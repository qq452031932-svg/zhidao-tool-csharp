package compat

import (
	"context"
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/subtle"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"time"

	"golang.org/x/crypto/argon2"
	_ "modernc.org/sqlite"
)

const (
	argonTime    = 1
	argonMemory  = 32 * 1024
	argonThreads = 2
	argonKeyLen  = 32
)

// Checks records only boolean capability results so the probe never exposes
// paths, credentials, cryptographic material, or database contents.
type Checks struct {
	SQLite          bool `json:"sqlite"`
	Argon2id        bool `json:"argon2id"`
	RSAVerify       bool `json:"rsa_3072_pkcs1v15_sha256"`
	AtomicRename    bool `json:"same_filesystem_rename"`
	FileFsync       bool `json:"file_fsync"`
	DirectoryFsync  bool `json:"directory_fsync"`
	RelativeSymlink bool `json:"relative_symlink_replacement"`
	Health          bool `json:"loopback_health"`
}

// Report is the probe's complete, secret-free machine-readable result.
type Report struct {
	OK           bool     `json:"ok"`
	GoVersion    string   `json:"go_version"`
	GOOS         string   `json:"goos"`
	GOARCH       string   `json:"goarch"`
	Checks       Checks   `json:"checks"`
	FailureCodes []string `json:"failure_codes,omitempty"`
}

// Filesystem is the durability seam used by the probe. Tests can verify the
// required ordering without requiring symlink privileges on the test host.
type Filesystem interface {
	MkdirAll(path string, perm fs.FileMode) error
	WriteFileAndSync(path string, data []byte, perm fs.FileMode) error
	Rename(oldPath, newPath string) error
	SyncDirectory(path string) error
	Symlink(target, linkPath string) error
	Readlink(path string) (string, error)
	Remove(path string) error
}

// Run exercises every runtime primitive and returns only stable failure codes.
func Run(ctx context.Context, workDir string) Report {
	report := Report{
		GoVersion: runtime.Version(),
		GOOS:      runtime.GOOS,
		GOARCH:    runtime.GOARCH,
	}

	if err := os.MkdirAll(workDir, 0o700); err != nil {
		report.FailureCodes = append(report.FailureCodes, "workspace")
		return report
	}

	report.Checks.SQLite = recordCheck(&report, "sqlite", func() error {
		return exerciseSQLite(filepath.Join(workDir, "compat-probe.db"))
	})
	report.Checks.Argon2id = recordCheck(&report, "argon2id", exerciseArgon2id)
	report.Checks.RSAVerify = recordCheck(&report, "rsa_verify", exerciseRSA)

	filesystemChecks := &filesystemCheckState{}
	filesystemRoot := filepath.Join(workDir, "filesystem")
	if err := os.RemoveAll(filesystemRoot); err != nil {
		report.FailureCodes = append(report.FailureCodes, "filesystem_setup")
	} else if err := exerciseFilesystemWithState(osFilesystem{}, filesystemRoot, filesystemChecks); err != nil {
		report.FailureCodes = append(report.FailureCodes, "filesystem")
	}
	report.Checks.AtomicRename = filesystemChecks.atomicRename
	report.Checks.FileFsync = filesystemChecks.fileFsync
	report.Checks.DirectoryFsync = filesystemChecks.directoryFsync
	report.Checks.RelativeSymlink = filesystemChecks.relativeSymlink

	report.OK = primitiveChecksOK(report.Checks) && len(report.FailureCodes) == 0
	report.Checks.Health = exerciseHealth(ctx, report) == nil
	if !report.Checks.Health {
		report.FailureCodes = append(report.FailureCodes, "health")
	}
	report.OK = allChecksOK(report.Checks) && len(report.FailureCodes) == 0
	return report
}

// WriteReport emits exactly one compact JSON record.
func WriteReport(writer io.Writer, report Report) error {
	encoder := json.NewEncoder(writer)
	encoder.SetEscapeHTML(true)
	return encoder.Encode(report)
}

// StartHealthServer starts an HTTP server only when address names a loopback IP.
func StartHealthServer(report Report, address string) (*http.Server, string, error) {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return nil, "", errors.New("invalid listen address")
	}
	ip := net.ParseIP(host)
	if ip == nil || !ip.IsLoopback() {
		return nil, "", errors.New("listen address must be a loopback IP")
	}

	listener, err := net.Listen("tcp", address)
	if err != nil {
		return nil, "", fmt.Errorf("listen: %w", err)
	}
	server := &http.Server{
		Handler:           healthHandler(report),
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       30 * time.Second,
	}
	go func() {
		_ = server.Serve(listener)
	}()
	return server, listener.Addr().String(), nil
}

func healthHandler(report Report) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/admin/health", func(writer http.ResponseWriter, request *http.Request) {
		if request.Method != http.MethodGet {
			writer.Header().Set("Allow", http.MethodGet)
			http.Error(writer, http.StatusText(http.StatusMethodNotAllowed), http.StatusMethodNotAllowed)
			return
		}
		writer.Header().Set("Cache-Control", "no-store")
		writer.Header().Set("Content-Type", "application/json")
		if !report.OK {
			writer.WriteHeader(http.StatusServiceUnavailable)
		}
		_ = WriteReport(writer, report)
	})
	return mux
}

func exerciseSQLite(databasePath string) (resultErr error) {
	if err := cleanupSQLiteProbe(databasePath); err != nil {
		return err
	}
	defer func() {
		if err := cleanupSQLiteProbe(databasePath); resultErr == nil && err != nil {
			resultErr = err
		}
	}()

	if err := writeSQLiteProbe(databasePath); err != nil {
		return err
	}
	value, err := readSQLiteProbe(databasePath)
	if err != nil {
		return err
	}
	if value != "round-trip" {
		return errors.New("sqlite value mismatch")
	}
	return nil
}

func cleanupSQLiteProbe(databasePath string) error {
	for _, path := range []string{databasePath, databasePath + "-shm", databasePath + "-wal"} {
		if err := os.Remove(path); err != nil && !errors.Is(err, fs.ErrNotExist) {
			return err
		}
	}
	return nil
}

func writeSQLiteProbe(databasePath string) error {
	database, err := sql.Open("sqlite", databasePath)
	if err != nil {
		return err
	}
	defer database.Close()

	transaction, err := database.Begin()
	if err != nil {
		return err
	}
	defer transaction.Rollback()
	if _, err := transaction.Exec(`CREATE TABLE compat_probe (id INTEGER PRIMARY KEY, value TEXT NOT NULL)`); err != nil {
		return err
	}
	if _, err := transaction.Exec(`INSERT INTO compat_probe (value) VALUES (?)`, "round-trip"); err != nil {
		return err
	}
	if err := transaction.Commit(); err != nil {
		return err
	}
	return database.Close()
}

func readSQLiteProbe(databasePath string) (string, error) {
	database, err := sql.Open("sqlite", databasePath)
	if err != nil {
		return "", err
	}
	defer database.Close()
	transaction, err := database.Begin()
	if err != nil {
		return "", err
	}
	defer transaction.Rollback()
	var value string
	if err := transaction.QueryRow(`SELECT value FROM compat_probe WHERE id = 1`).Scan(&value); err != nil {
		return "", err
	}
	if err := transaction.Commit(); err != nil {
		return "", err
	}
	if err := database.Close(); err != nil {
		return "", err
	}
	return value, nil
}

func hashArgon2id(password, salt []byte) []byte {
	return argon2.IDKey(password, salt, argonTime, argonMemory, argonThreads, argonKeyLen)
}

func verifyArgon2id(password, salt, expected []byte) bool {
	actual := hashArgon2id(password, salt)
	return len(actual) == len(expected) && subtle.ConstantTimeCompare(actual, expected) == 1
}

func exerciseArgon2id() error {
	password := make([]byte, 24)
	salt := make([]byte, 16)
	if _, err := rand.Read(password); err != nil {
		return err
	}
	if _, err := rand.Read(salt); err != nil {
		return err
	}
	digest := hashArgon2id(password, salt)
	if !verifyArgon2id(password, salt, digest) {
		return errors.New("argon2id verification failed")
	}
	password[0] ^= 1
	if verifyArgon2id(password, salt, digest) {
		return errors.New("argon2id accepted modified password")
	}
	return nil
}

func exerciseRSA() error {
	key, err := rsa.GenerateKey(rand.Reader, 3072)
	if err != nil {
		return err
	}
	message := []byte("compatibility probe manifest\n")
	digest := sha256.Sum256(message)
	signature, err := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, digest[:])
	if err != nil {
		return err
	}
	return verifyRSA3072PKCS1v15SHA256(&key.PublicKey, message, signature)
}

func verifyRSA3072PKCS1v15SHA256(publicKey *rsa.PublicKey, message, signature []byte) error {
	if publicKey == nil || publicKey.N.BitLen() != 3072 {
		return errors.New("RSA public key must be 3072 bits")
	}
	digest := sha256.Sum256(message)
	return rsa.VerifyPKCS1v15(publicKey, crypto.SHA256, digest[:], signature)
}

type filesystemCheckState struct {
	atomicRename    bool
	fileFsync       bool
	directoryFsync  bool
	relativeSymlink bool
}

func exerciseFilesystem(filesystem Filesystem, root string) error {
	return exerciseFilesystemWithState(filesystem, root, &filesystemCheckState{})
}

func exerciseFilesystemWithState(filesystem Filesystem, root string, state *filesystemCheckState) error {
	generations := filepath.Join(root, "generations")
	previous := filepath.Join(generations, "generation-0")
	pending := filepath.Join(generations, ".generation-1.pending")
	final := filepath.Join(generations, "generation-1")
	if err := filesystem.MkdirAll(previous, 0o700); err != nil {
		return err
	}
	if err := filesystem.MkdirAll(pending, 0o700); err != nil {
		return err
	}
	if err := filesystem.WriteFileAndSync(filepath.Join(pending, "probe"), []byte("durable"), 0o600); err != nil {
		return err
	}
	state.fileFsync = true
	if err := filesystem.SyncDirectory(pending); err != nil {
		return err
	}
	if err := filesystem.Rename(pending, final); err != nil {
		return err
	}
	state.atomicRename = true
	if err := filesystem.SyncDirectory(generations); err != nil {
		return err
	}

	stable := filepath.Join(root, "stable")
	nextStable := filepath.Join(root, ".stable.next")
	previousTarget := filepath.ToSlash(filepath.Join("generations", "generation-0"))
	nextTarget := filepath.ToSlash(filepath.Join("generations", "generation-1"))
	if err := filesystem.Symlink(previousTarget, stable); err != nil {
		return err
	}
	_ = filesystem.Remove(nextStable)
	if err := filesystem.Symlink(nextTarget, nextStable); err != nil {
		return err
	}
	if filepath.IsAbs(nextTarget) {
		return errors.New("symlink target is absolute")
	}
	if target, err := filesystem.Readlink(nextStable); err != nil || target != nextTarget {
		return errors.New("temporary symlink target mismatch")
	}
	if err := filesystem.Rename(nextStable, stable); err != nil {
		return err
	}
	if target, err := filesystem.Readlink(stable); err != nil || target != nextTarget {
		return errors.New("stable symlink target mismatch")
	}
	state.relativeSymlink = true
	if err := filesystem.SyncDirectory(root); err != nil {
		return err
	}
	state.directoryFsync = true
	return nil
}

type osFilesystem struct{}

func (osFilesystem) MkdirAll(path string, perm fs.FileMode) error {
	return os.MkdirAll(path, perm)
}

func (osFilesystem) WriteFileAndSync(path string, data []byte, perm fs.FileMode) error {
	file, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, perm)
	if err != nil {
		return err
	}
	if _, err := file.Write(data); err != nil {
		_ = file.Close()
		return err
	}
	if err := file.Sync(); err != nil {
		_ = file.Close()
		return err
	}
	return file.Close()
}

func (osFilesystem) Rename(oldPath, newPath string) error {
	return os.Rename(oldPath, newPath)
}

func (osFilesystem) SyncDirectory(path string) error {
	directory, err := os.Open(path)
	if err != nil {
		return err
	}
	defer directory.Close()
	return directory.Sync()
}

func (osFilesystem) Symlink(target, linkPath string) error {
	return os.Symlink(target, linkPath)
}

func (osFilesystem) Readlink(path string) (string, error) {
	return os.Readlink(path)
}

func (osFilesystem) Remove(path string) error {
	err := os.Remove(path)
	if errors.Is(err, fs.ErrNotExist) {
		return nil
	}
	return err
}

func exerciseHealth(ctx context.Context, report Report) error {
	server, address, err := StartHealthServer(report, "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer server.Shutdown(context.Background())

	request, err := http.NewRequestWithContext(ctx, http.MethodGet, "http://"+address+"/admin/health", nil)
	if err != nil {
		return err
	}
	client := &http.Client{Timeout: 5 * time.Second}
	response, err := client.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return errors.New("health endpoint was not healthy")
	}
	var received Report
	if err := json.NewDecoder(response.Body).Decode(&received); err != nil {
		return err
	}
	if !received.OK || received.GoVersion != report.GoVersion || received.Checks != report.Checks {
		return errors.New("health report mismatch")
	}
	return nil
}

func recordCheck(report *Report, code string, operation func() error) bool {
	if err := operation(); err != nil {
		report.FailureCodes = append(report.FailureCodes, code)
		return false
	}
	return true
}

func primitiveChecksOK(checks Checks) bool {
	return checks.SQLite && checks.Argon2id && checks.RSAVerify && checks.AtomicRename &&
		checks.FileFsync && checks.DirectoryFsync && checks.RelativeSymlink
}

func allChecksOK(checks Checks) bool {
	return primitiveChecksOK(checks) && checks.Health
}
