//go:build license_local_acceptance && windows

// Command license-admin-local runs the real license API and the real OTA admin
// login/proxy surface against disposable loopback-only state. It is intentionally
// excluded from normal builds.
package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/deploy"
	otahttp "fanfh.cn/superpan/ota-admin/internal/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/licenseadmin"
	licensehttp "fanfh.cn/superpan/ota-admin/internal/licensing/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/licensing/service"
	"fanfh.cn/superpan/ota-admin/internal/licensing/signing"
	licensestore "fanfh.cn/superpan/ota-admin/internal/licensing/store"
	"fanfh.cn/superpan/ota-admin/internal/session"
	otastore "fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
	"golang.org/x/sys/windows"
)

const (
	defaultAdminAddress   = "127.0.0.1:19090"
	defaultLicenseAddress = "127.0.0.1:19091"
	localSigningKeyID     = "local-acceptance-v1"
)

type systemClock struct{}

func (systemClock) Now() time.Time { return time.Now().UTC() }

var errLocalOTAReadOnly = errors.New("OTA publishing is disabled in the local license acceptance stack")

// localOTAStub keeps the real authenticated router usable without touching any
// OTA publication directory. Its mutation methods fail closed as read-only.
type localOTAStub struct{}

func (localOTAStub) ReleaseHistory(context.Context) ([]otastore.ReleaseHistoryItem, error) {
	return []otastore.ReleaseHistoryItem{}, nil
}
func (localOTAStub) ReadOnlyError() error { return errLocalOTAReadOnly }
func (localOTAStub) RollbackForActiveSession(context.Context, session.Principal, string, string, string) (deploy.RollbackResult, error) {
	return deploy.RollbackResult{}, deploy.ErrReadOnly
}
func (localOTAStub) PublishForActiveSession(context.Context, string, string, string) (deploy.PublishResult, error) {
	return deploy.PublishResult{}, deploy.ErrReadOnly
}
func (localOTAStub) ReserveEnvelopeForActiveSession(context.Context, string, string) (candidate.Summary, bool, error) {
	return candidate.Summary{}, false, errLocalOTAReadOnly
}
func (localOTAStub) RenewEnvelopeLeaseForActiveSession(context.Context, string, string, string) error {
	return errLocalOTAReadOnly
}
func (localOTAStub) FailEnvelopeForActiveSession(context.Context, string, string, string, string) (candidate.Summary, error) {
	return candidate.Summary{}, errLocalOTAReadOnly
}
func (localOTAStub) CandidateForOperationForActiveSession(context.Context, string, string) (candidate.Summary, bool, error) {
	return candidate.Summary{}, false, otastore.ErrNotFound
}
func (localOTAStub) AcceptEnvelopeForActiveSession(context.Context, string, string, *upload.Envelope, string) (candidate.Summary, error) {
	return candidate.Summary{}, errLocalOTAReadOnly
}
func (localOTAStub) CurrentForActiveSession(context.Context, string) (candidate.Summary, error) {
	return candidate.Summary{}, otastore.ErrNotFound
}
func (localOTAStub) StatusForActiveSession(context.Context, string, string) (candidate.Summary, error) {
	return candidate.Summary{}, otastore.ErrNotFound
}

type localRuntime struct {
	root                  string
	licenseDatabasePath   string
	adminDatabasePath     string
	encryptionKeyPath     string
	lookupKeyPath         string
	signingPrivateKeyPath string
	signingPublicKeyPath  string
	internalSecretPath    string
	sourceHMACKeyPath     string
	encryptionKey         []byte
	lookupKey             []byte
	internalSecret        []byte
	sourceHMACKey         []byte
	privateKey            *ecdsa.PrivateKey
}

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(arguments []string, stdout, stderr io.Writer) int {
	flags := flag.NewFlagSet("license-admin-local", flag.ContinueOnError)
	flags.SetOutput(stderr)
	root := flags.String("root", "", "absolute disposable .tmp/license-local directory")
	adminAddress := flags.String("admin-listen", defaultAdminAddress, "literal admin loopback address")
	licenseAddress := flags.String("license-listen", defaultLicenseAddress, "literal license loopback address")
	instanceToken := flags.String("instance-token", "", "single-use local process identity token")
	if err := flags.Parse(arguments); err != nil {
		return 2
	}
	if flags.NArg() != 0 || *root == "" {
		fmt.Fprintln(stderr, "usage: license-admin-local --root ABSOLUTE_PATH")
		return 2
	}
	if err := validateLoopbackAddress(*adminAddress); err != nil {
		fmt.Fprintln(stderr, "local admin address rejected")
		return 1
	}
	if err := validateLoopbackAddress(*licenseAddress); err != nil || *licenseAddress == *adminAddress {
		fmt.Fprintln(stderr, "local license address rejected")
		return 1
	}
	if err := validateInstanceToken(*instanceToken); err != nil {
		fmt.Fprintln(stderr, "local instance token rejected")
		return 1
	}
	runtime, err := prepareRuntime(*root, *adminAddress, *licenseAddress)
	if err != nil {
		fmt.Fprintln(stderr, "could not prepare disposable local state")
		return 1
	}
	defer runtime.clear()
	if err := writeFileAtomic(filepath.Join(runtime.root, "instance-token"), []byte(*instanceToken+"\n"), 0o600); err != nil {
		_ = os.RemoveAll(runtime.root)
		fmt.Fprintln(stderr, "could not record local runtime ownership")
		return 1
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	password, err := serveLocalStack(ctx, runtime, *adminAddress, *licenseAddress, *instanceToken, stdout, stderr)
	if err != nil {
		fmt.Fprintln(stderr, "local acceptance stack stopped with an error")
		return 1
	}
	_ = password
	return 0
}

func validateInstanceToken(token string) error {
	if len(token) != 32 {
		return errors.New("instance token must contain 16 lowercase-hex bytes")
	}
	for _, value := range token {
		if value < '0' || value > '9' {
			if value < 'a' || value > 'f' {
				return errors.New("instance token must contain 16 lowercase-hex bytes")
			}
		}
	}
	return nil
}

func validateLoopbackAddress(address string) error {
	host, port, err := net.SplitHostPort(address)
	if err != nil || host != "127.0.0.1" || address != host+":"+port {
		return errors.New("address must use literal 127.0.0.1")
	}
	number, err := strconv.ParseUint(port, 10, 16)
	if err != nil || number == 0 {
		return errors.New("address must include a fixed non-zero port")
	}
	return nil
}

func writeFileAtomic(path string, contents []byte, permissions os.FileMode) error {
	temporary, err := os.CreateTemp(filepath.Dir(path), "."+filepath.Base(path)+"-*.tmp")
	if err != nil {
		return err
	}
	temporaryPath := temporary.Name()
	defer os.Remove(temporaryPath)
	closed := false
	defer func() {
		if !closed {
			_ = temporary.Close()
		}
	}()
	if err := temporary.Chmod(permissions); err != nil {
		return err
	}
	if _, err := temporary.Write(contents); err != nil {
		return err
	}
	if err := temporary.Sync(); err != nil {
		return err
	}
	if err := temporary.Close(); err != nil {
		return err
	}
	closed = true
	return os.Rename(temporaryPath, path)
}

func secureLocalRuntimeDirectory(path string) error {
	token, err := windows.OpenCurrentProcessToken()
	if err != nil {
		return err
	}
	defer token.Close()
	user, err := token.GetTokenUser()
	if err != nil {
		return err
	}
	system, err := windows.CreateWellKnownSid(windows.WinLocalSystemSid)
	if err != nil {
		return err
	}
	administrators, err := windows.CreateWellKnownSid(windows.WinBuiltinAdministratorsSid)
	if err != nil {
		return err
	}
	entries := []windows.EXPLICIT_ACCESS{
		localFullControlEntry(user.User.Sid, windows.TRUSTEE_IS_USER),
		localFullControlEntry(system, windows.TRUSTEE_IS_USER),
		localFullControlEntry(administrators, windows.TRUSTEE_IS_GROUP),
	}
	dacl, err := windows.ACLFromEntries(entries, nil)
	if err != nil {
		return err
	}
	return windows.SetNamedSecurityInfo(
		path,
		windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION,
		nil,
		nil,
		dacl,
		nil,
	)
}

func localFullControlEntry(sid *windows.SID, trusteeType windows.TRUSTEE_TYPE) windows.EXPLICIT_ACCESS {
	return windows.EXPLICIT_ACCESS{
		AccessPermissions: windows.GENERIC_ALL,
		AccessMode:        windows.SET_ACCESS,
		Inheritance:       windows.SUB_CONTAINERS_AND_OBJECTS_INHERIT,
		Trustee: windows.TRUSTEE{
			TrusteeForm:  windows.TRUSTEE_IS_SID,
			TrusteeType:  trusteeType,
			TrusteeValue: windows.TrusteeValueFromSID(sid),
		},
	}
}

func prepareRuntime(root, adminAddress, licenseAddress string) (*localRuntime, error) {
	if !filepath.IsAbs(root) || filepath.Base(root) != "license-local" || filepath.Base(filepath.Dir(root)) != ".tmp" {
		return nil, errors.New("runtime root must be an absolute .tmp/license-local path")
	}
	if err := validateLoopbackAddress(adminAddress); err != nil {
		return nil, err
	}
	if err := validateLoopbackAddress(licenseAddress); err != nil {
		return nil, err
	}
	if err := os.MkdirAll(filepath.Dir(root), 0o700); err != nil {
		return nil, err
	}
	if err := os.Mkdir(root, 0o700); err != nil {
		return nil, errors.New("runtime root must not already exist")
	}
	prepared := false
	defer func() {
		if !prepared {
			_ = os.RemoveAll(root)
		}
	}()
	if err := secureLocalRuntimeDirectory(root); err != nil {
		return nil, err
	}
	absRoot, err := filepath.Abs(root)
	if err != nil {
		return nil, err
	}
	runtime := &localRuntime{
		root:                  absRoot,
		licenseDatabasePath:   filepath.Join(absRoot, "license.db"),
		adminDatabasePath:     filepath.Join(absRoot, "admin.db"),
		encryptionKeyPath:     filepath.Join(absRoot, "card-encryption.key"),
		lookupKeyPath:         filepath.Join(absRoot, "card-lookup.key"),
		signingPrivateKeyPath: filepath.Join(absRoot, "signing-private.pem"),
		signingPublicKeyPath:  filepath.Join(absRoot, "signing-public.pem"),
		internalSecretPath:    filepath.Join(absRoot, "internal-admin.key"),
		sourceHMACKeyPath:     filepath.Join(absRoot, "session-source.key"),
	}
	for _, destination := range []*[]byte{&runtime.encryptionKey, &runtime.lookupKey, &runtime.internalSecret, &runtime.sourceHMACKey} {
		*destination = make([]byte, 32)
		if _, err := io.ReadFull(rand.Reader, *destination); err != nil {
			runtime.clear()
			return nil, err
		}
	}
	runtime.privateKey, err = ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		runtime.clear()
		return nil, err
	}
	privateDER, err := x509.MarshalECPrivateKey(runtime.privateKey)
	if err != nil {
		runtime.clear()
		return nil, err
	}
	publicDER, err := x509.MarshalPKIXPublicKey(&runtime.privateKey.PublicKey)
	if err != nil {
		runtime.clear()
		return nil, err
	}
	files := []struct {
		path string
		data []byte
	}{
		{runtime.encryptionKeyPath, runtime.encryptionKey},
		{runtime.lookupKeyPath, runtime.lookupKey},
		{runtime.internalSecretPath, runtime.internalSecret},
		{runtime.sourceHMACKeyPath, []byte(hex.EncodeToString(runtime.sourceHMACKey))},
		{runtime.signingPrivateKeyPath, pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: privateDER})},
		{runtime.signingPublicKeyPath, pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: publicDER})},
		{runtime.licenseDatabasePath, nil},
		{runtime.adminDatabasePath, nil},
	}
	for _, file := range files {
		if err := os.WriteFile(file.path, file.data, 0o600); err != nil {
			runtime.clear()
			return nil, err
		}
	}
	clientSettings := struct {
		LicenseBaseURL      string `json:"license_base_url"`
		SigningKeyID        string `json:"signing_key_id"`
		SigningPublicKeyPEM string `json:"signing_public_key_pem"`
		AcceptanceOnly      bool   `json:"acceptance_only"`
	}{"http://" + licenseAddress, localSigningKeyID, "signing-public.pem", true}
	clientJSON, err := json.MarshalIndent(clientSettings, "", "  ")
	if err != nil {
		runtime.clear()
		return nil, err
	}
	clientJSON = append(clientJSON, '\n')
	if err := writeFileAtomic(filepath.Join(absRoot, "client.json"), clientJSON, 0o600); err != nil {
		runtime.clear()
		return nil, err
	}
	prepared = true
	return runtime, nil
}

func (runtime *localRuntime) clear() {
	if runtime == nil {
		return
	}
	clear(runtime.encryptionKey)
	clear(runtime.lookupKey)
	clear(runtime.internalSecret)
	clear(runtime.sourceHMACKey)
	if runtime.privateKey != nil {
		runtime.privateKey.D.SetInt64(0)
	}
}

func serveLocalStack(ctx context.Context, runtime *localRuntime, adminAddress, licenseAddress, instanceToken string, stdout, stderr io.Writer) (string, error) {
	licenseListener, err := net.Listen("tcp", licenseAddress)
	if err != nil {
		return "", err
	}
	defer licenseListener.Close()
	adminListener, err := net.Listen("tcp", adminAddress)
	if err != nil {
		return "", err
	}
	defer adminListener.Close()

	licenseDB, err := licensestore.Open(ctx, runtime.licenseDatabasePath)
	if err != nil {
		return "", err
	}
	defer licenseDB.Close()
	licenses, err := service.New(licenseDB, systemClock{}, rand.Reader, runtime.encryptionKey, runtime.lookupKey)
	if err != nil {
		return "", err
	}
	signer, err := signing.New(localSigningKeyID, runtime.privateKey, rand.Reader)
	if err != nil {
		return "", err
	}
	licenseHandler, err := licensehttp.NewRouter(licensehttp.Options{
		Service: licenses, ReplayStore: licenseDB, Signer: signer, Clock: systemClock{},
		MaxBodyBytes: 8192, MaxHeaderBytes: 16384, TimestampWindow: 5 * time.Minute,
		NonceLifetime: 10 * time.Minute, MaxReplayNonces: 10000,
		ActivationRate: licensehttp.RateLimit{Requests: 120, Window: time.Minute},
		ValidationRate: licensehttp.RateLimit{Requests: 300, Window: time.Minute}, MaxLimiterEntries: 4096,
		AdminService: licenses, AdminIdempotencyStore: licenseDB, InternalSecret: runtime.internalSecret,
		AdminRate: licensehttp.RateLimit{Requests: 300, Window: time.Minute}, MaxAdminPageSize: 100,
		MaxExportCount: 1000, MaxAdminIdempotencyEntries: 1000, MaxAdminIdempotencyBytes: 16 << 20,
	})
	if err != nil {
		return "", err
	}

	adminDB, err := otastore.Open(ctx, runtime.adminDatabasePath)
	if err != nil {
		return "", err
	}
	defer adminDB.Close()
	var passwordOutput strings.Builder
	if err := adminDB.InitializeAdminWithGeneratedPassword(ctx, &passwordOutput, rand.Reader); err != nil {
		return "", err
	}
	password := strings.TrimSpace(passwordOutput.String())
	if password == "" {
		return "", errors.New("generated password was empty")
	}
	if err := writeFileAtomic(filepath.Join(runtime.root, "admin-password.txt"), []byte(password+"\n"), 0o600); err != nil {
		return "", err
	}
	sessions, err := session.NewService(adminDB, session.Options{
		SourceHMACKey: runtime.sourceHMACKey, PreauthTTL: 5 * time.Minute,
		IdleTimeout: 30 * time.Minute, AbsoluteExpiry: 8 * time.Hour,
		FailureLimit: 10, FailureWindow: 10 * time.Minute, FailureLock: 5 * time.Minute,
	})
	if err != nil {
		return "", err
	}
	licenseClient, err := licenseadmin.NewClient(licenseadmin.Options{
		BaseURL: "http://" + licenseAddress, Secret: runtime.internalSecret,
		MaxRequestBytes: 64 << 10, MaxResponseBytes: 8 << 20,
	})
	if err != nil {
		return "", err
	}
	adminHandler, err := otahttp.NewRouter(otahttp.Options{
		Sessions: sessions, PublicOrigin: "http://" + adminAddress,
		MaxJSONBytes: 64 << 10, LicenseAdmin: licenseClient,
		UploadEnvelopes: localOTAStub{}, Publisher: localOTAStub{}, ReleaseOperations: localOTAStub{},
	})
	if err != nil {
		return "", err
	}

	licenseServer := localHTTPServer(withHealth(licenseHandler, "/health"), stderr)
	adminServer := localHTTPServer(allowLoopbackHTTPCookies(withHealth(adminHandler, "/admin/health")), stderr)
	done := make(chan error, 2)
	go func() { done <- normalizeServeError(licenseServer.Serve(licenseListener)) }()
	go func() { done <- normalizeServeError(adminServer.Serve(adminListener)) }()

	ready := struct {
		AdminURL      string `json:"admin_url"`
		LicenseURL    string `json:"license_url"`
		PID           int    `json:"pid"`
		InstanceToken string `json:"instance_token"`
	}{"http://" + adminAddress + "/admin/", "http://" + licenseAddress, os.Getpid(), instanceToken}
	readyJSON, err := json.MarshalIndent(ready, "", "  ")
	if err != nil {
		return "", err
	}
	if err := writeFileAtomic(filepath.Join(runtime.root, "ready.json"), append(readyJSON, '\n'), 0o600); err != nil {
		return "", err
	}
	fmt.Fprintf(stdout, "本地卡密后台：http://%s/admin/\n", adminAddress)
	fmt.Fprintln(stdout, "管理员：admin")
	fmt.Fprintf(stdout, "一次性密码文件：%s\n", filepath.Join(runtime.root, "admin-password.txt"))

	select {
	case <-ctx.Done():
		shutdownContext, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		first := licenseServer.Shutdown(shutdownContext)
		second := adminServer.Shutdown(shutdownContext)
		return password, errors.Join(first, second)
	case err := <-done:
		shutdownContext, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = licenseServer.Shutdown(shutdownContext)
		_ = adminServer.Shutdown(shutdownContext)
		return password, err
	}
}

func withHealth(next http.Handler, path string) http.Handler {
	return http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		if request.Method == http.MethodGet && request.URL.Path == path {
			response.Header().Set("Content-Type", "application/json")
			response.WriteHeader(http.StatusOK)
			_, _ = io.WriteString(response, `{"ok":true}`+"\n")
			return
		}
		next.ServeHTTP(response, request)
	})
}

type loopbackCookieResponseWriter struct {
	http.ResponseWriter
	wroteHeader bool
}

func (writer *loopbackCookieResponseWriter) WriteHeader(statusCode int) {
	if writer.wroteHeader {
		return
	}
	writer.wroteHeader = true
	cookies := writer.Header().Values("Set-Cookie")
	if len(cookies) != 0 {
		writer.Header().Del("Set-Cookie")
		for _, cookie := range cookies {
			writer.Header().Add("Set-Cookie", strings.ReplaceAll(cookie, "; Secure", ""))
		}
	}
	writer.ResponseWriter.WriteHeader(statusCode)
}

func (writer *loopbackCookieResponseWriter) Write(contents []byte) (int, error) {
	if !writer.wroteHeader {
		writer.WriteHeader(http.StatusOK)
	}
	return writer.ResponseWriter.Write(contents)
}

func allowLoopbackHTTPCookies(next http.Handler) http.Handler {
	return http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		next.ServeHTTP(&loopbackCookieResponseWriter{ResponseWriter: response}, request)
	})
}

func localHTTPServer(handler http.Handler, stderr io.Writer) *http.Server {
	return &http.Server{
		Handler: handler, ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 15 * time.Second,
		WriteTimeout: 15 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 32 << 10,
		ErrorLog: log.New(stderr, "local-http: ", 0),
	}
}

func normalizeServeError(err error) error {
	if errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return err
}
