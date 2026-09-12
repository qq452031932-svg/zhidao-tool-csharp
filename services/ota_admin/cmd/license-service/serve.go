package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"runtime"
	"syscall"
	"time"

	licenseconfig "fanfh.cn/superpan/ota-admin/internal/licensing/config"
	licensehttp "fanfh.cn/superpan/ota-admin/internal/licensing/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/licensing/service"
	"fanfh.cn/superpan/ota-admin/internal/licensing/signing"
	licensestore "fanfh.cn/superpan/ota-admin/internal/licensing/store"
)

const maximumLimiterEntries = 4096
const maximumAdminPageSize = 100
const maximumAdminExportCount = 1000

type systemClock struct{}

func (systemClock) Now() time.Time { return time.Now().UTC() }

func run(arguments []string, stdout, stderr io.Writer) int {
	if len(arguments) == 0 || (arguments[0] != "serve" && arguments[0] != "check-config") {
		writeUsage(stderr)
		return 2
	}
	command := arguments[0]
	flags := flag.NewFlagSet("license-service "+command, flag.ContinueOnError)
	flags.SetOutput(stderr)
	configPath := flags.String("config", "", "local license service configuration file")
	if err := flags.Parse(arguments[1:]); err != nil {
		return 2
	}
	if *configPath == "" || flags.NArg() != 0 {
		writeUsage(stderr)
		return 2
	}
	var cfg licenseconfig.Config
	var err error
	if command == "check-config" && runtime.GOOS == "windows" {
		var body []byte
		body, err = os.ReadFile(*configPath)
		if err == nil {
			cfg, err = licenseconfig.Parse(body)
		}
	} else {
		cfg, err = licenseconfig.Load(*configPath)
	}
	if err != nil {
		fmt.Fprintln(stderr, "license-service: invalid configuration")
		return 1
	}
	if command == "check-config" {
		if err := validateProductionConfig(cfg); err != nil {
			fmt.Fprintln(stderr, "license-service: invalid production configuration")
			return 1
		}
		fmt.Fprintln(stdout, "license-service: production configuration valid")
		return 0
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	if err := serve(ctx, cfg, stdout, stderr); err != nil {
		fmt.Fprintln(stderr, "license-service: service stopped with an error")
		return 1
	}
	return 0
}

func validateProductionConfig(cfg licenseconfig.Config) error {
	if cfg.ListenAddress != "127.0.0.1:18092" || cfg.LicenseDatabasePath != "/var/lib/superpan-license/state.db" ||
		cfg.EncryptionKeyPath != "/etc/superpan-license/encryption.key" || len(cfg.PreviousEncryptionKeyPaths) != 0 ||
		cfg.LookupHMACKeyPath != "/etc/superpan-license/lookup.key" || cfg.SigningPrivateKeyPath != "/etc/superpan-license/response-signing.key" ||
		cfg.InternalSecretPath != "/etc/superpan-license/admin.secret" || cfg.LoadTestEnabled || cfg.LoadTestSecretPath != "" ||
		cfg.PublicAPIBasePath != "/license-api/v1" {
		return errors.New("production identity, listener, or paths differ")
	}
	want := licenseconfig.Limits{MaxBodyBytes: 65536, MaxHeaderBytes: 16384, TimestampWindowSeconds: 300, NonceLifetimeSeconds: 600, MaxReplayNonces: 131072, MaxAdminIdempotencyEntries: 1024, MaxAdminIdempotencyBytes: 10485760, ActivationRequests: 30, ValidationRequests: 60, AdminRequests: 120, RateWindowSeconds: 60, ReadHeaderTimeoutSeconds: 5, ReadTimeoutSeconds: 15, WriteTimeoutSeconds: 15, IdleTimeoutSeconds: 60, ShutdownTimeoutSeconds: 30}
	if cfg.Limits != want {
		return errors.New("production limits differ")
	}
	return nil
}

func serve(ctx context.Context, cfg licenseconfig.Config, stdout, stderr io.Writer) error {
	if err := cfg.Validate(); err != nil {
		return errors.New("license service configuration rejected")
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	encryptionKey, err := licenseconfig.ReadSecret32(cfg.EncryptionKeyPath)
	if err != nil {
		return errors.New("load card encryption key")
	}
	defer clear(encryptionKey)
	previousEncryptionKeys := make([][]byte, len(cfg.PreviousEncryptionKeyPaths))
	for index, path := range cfg.PreviousEncryptionKeyPaths {
		previousEncryptionKeys[index], err = licenseconfig.ReadSecret32(path)
		if err != nil {
			clearPreviousEncryptionKeys(previousEncryptionKeys)
			return errors.New("load previous card encryption key")
		}
	}
	defer clearPreviousEncryptionKeys(previousEncryptionKeys)
	lookupKey, err := licenseconfig.ReadSecret32(cfg.LookupHMACKeyPath)
	if err != nil {
		return errors.New("load card lookup key")
	}
	defer clear(lookupKey)
	internalSecret, err := licenseconfig.ReadSharedSecret32(cfg.InternalSecretPath)
	if err != nil {
		return errors.New("load internal API secret")
	}
	defer clear(internalSecret)
	var loadTestSecret []byte
	if cfg.LoadTestEnabled {
		loadTestSecret, err = licenseconfig.ReadSecret32(cfg.LoadTestSecretPath)
		if err != nil {
			return errors.New("load load-test secret")
		}
		defer clear(loadTestSecret)
		symmetricSecrets := make([][]byte, 0, 3+len(previousEncryptionKeys))
		symmetricSecrets = append(symmetricSecrets, encryptionKey)
		symmetricSecrets = append(symmetricSecrets, previousEncryptionKeys...)
		symmetricSecrets = append(symmetricSecrets, lookupKey, internalSecret)
		if !loadSecretIsIndependent(loadTestSecret, symmetricSecrets) {
			return errors.New("load-test secret must be independent")
		}
	}
	privateKey, err := signing.LoadPrivateKey(cfg.SigningPrivateKeyPath)
	if err != nil {
		return errors.New("load signing private key")
	}
	signer, err := signing.New(cfg.SigningKeyID, privateKey, rand.Reader)
	if err != nil {
		return errors.New("initialize claims signer")
	}

	listener, err := net.Listen("tcp", cfg.ListenAddress)
	if err != nil {
		return errors.New("bind configured loopback listener")
	}
	defer listener.Close()
	bound, ok := listener.Addr().(*net.TCPAddr)
	if !ok || bound.IP == nil || !bound.IP.IsLoopback() {
		return errors.New("listener did not bind to loopback")
	}
	database, err := licensestore.Open(ctx, cfg.LicenseDatabasePath)
	if err != nil {
		return errors.New("open license state")
	}
	defer database.Close()
	clock := systemClock{}
	licenses, err := service.NewWithPreviousEncryptionKeys(database, clock, rand.Reader, encryptionKey, previousEncryptionKeys, lookupKey)
	clear(encryptionKey)
	clearPreviousEncryptionKeys(previousEncryptionKeys)
	clear(lookupKey)
	if err != nil {
		return errors.New("initialize license service")
	}
	var storeMetrics licensehttp.StoreMetrics
	if cfg.LoadTestEnabled {
		storeMetrics = database
	}
	handler, err := licensehttp.NewRouter(licensehttp.Options{
		Service: licenses, ReplayStore: database, Signer: signer, Clock: clock,
		MaxBodyBytes: cfg.Limits.MaxBodyBytes, MaxHeaderBytes: cfg.Limits.MaxHeaderBytes,
		TimestampWindow: time.Duration(cfg.Limits.TimestampWindowSeconds) * time.Second,
		NonceLifetime:   time.Duration(cfg.Limits.NonceLifetimeSeconds) * time.Second,
		MaxReplayNonces: cfg.Limits.MaxReplayNonces,
		ActivationRate:  licensehttp.RateLimit{Requests: cfg.Limits.ActivationRequests, Window: time.Duration(cfg.Limits.RateWindowSeconds) * time.Second},
		ValidationRate:  licensehttp.RateLimit{Requests: cfg.Limits.ValidationRequests, Window: time.Duration(cfg.Limits.RateWindowSeconds) * time.Second},
		AdminService:    licenses, AdminIdempotencyStore: database, InternalSecret: internalSecret,
		AdminRate:        licensehttp.RateLimit{Requests: cfg.Limits.AdminRequests, Window: time.Duration(cfg.Limits.RateWindowSeconds) * time.Second},
		MaxAdminPageSize: maximumAdminPageSize, MaxExportCount: maximumAdminExportCount,
		MaxAdminIdempotencyEntries: cfg.Limits.MaxAdminIdempotencyEntries,
		MaxAdminIdempotencyBytes:   cfg.Limits.MaxAdminIdempotencyBytes,
		MaxLimiterEntries:          maximumLimiterEntries,
		LoadTestSecret:             loadTestSecret,
		StoreMetrics:               storeMetrics,
	})
	clear(internalSecret)
	clear(loadTestSecret)
	if err != nil {
		return errors.New("initialize license API")
	}

	serverContext, stopServer := context.WithCancel(ctx)
	defer stopServer()
	server := &http.Server{
		Handler:           handler,
		ReadHeaderTimeout: time.Duration(cfg.Limits.ReadHeaderTimeoutSeconds) * time.Second,
		ReadTimeout:       time.Duration(cfg.Limits.ReadTimeoutSeconds) * time.Second,
		WriteTimeout:      time.Duration(cfg.Limits.WriteTimeoutSeconds) * time.Second,
		IdleTimeout:       time.Duration(cfg.Limits.IdleTimeoutSeconds) * time.Second,
		MaxHeaderBytes:    cfg.Limits.MaxHeaderBytes,
		ErrorLog:          log.New(stderr, "http: ", 0),
		BaseContext:       func(net.Listener) context.Context { return serverContext },
	}
	shutdownDone := make(chan struct{})
	go func() {
		defer close(shutdownDone)
		<-serverContext.Done()
		shutdownContext, cancel := context.WithTimeout(context.Background(), time.Duration(cfg.Limits.ShutdownTimeoutSeconds)*time.Second)
		defer cancel()
		_ = server.Shutdown(shutdownContext)
	}()
	fmt.Fprintln(stdout, "License service listening on configured loopback address")
	serveErr := server.Serve(listener)
	stopServer()
	<-shutdownDone
	if errors.Is(serveErr, http.ErrServerClosed) && ctx.Err() != nil {
		return nil
	}
	if serveErr != nil {
		return errors.New("license HTTP server stopped")
	}
	return nil
}

func loadSecretIsIndependent(loadSecret []byte, symmetricSecrets [][]byte) bool {
	loadDigest := sha256.Sum256(loadSecret)
	duplicate := 0
	for _, candidate := range symmetricSecrets {
		candidateDigest := sha256.Sum256(candidate)
		sameContent := subtle.ConstantTimeCompare(loadDigest[:], candidateDigest[:])
		sameLength := subtle.ConstantTimeEq(int32(len(loadSecret)), int32(len(candidate)))
		duplicate |= sameContent & sameLength
	}
	return duplicate == 0
}

func clearPreviousEncryptionKeys(keys [][]byte) {
	for _, key := range keys {
		clear(key)
	}
}

func writeUsage(writer io.Writer) {
	fmt.Fprintln(writer, "usage: license-service (serve|check-config) --config PATH")
}
