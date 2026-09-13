package main

import (
	"context"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/config"
	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/licenseadmin"
	licenseconfig "fanfh.cn/superpan/ota-admin/internal/licensing/config"
	"fanfh.cn/superpan/ota-admin/internal/operation"
	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

const (
	maximumTrustedCertificateBytes   = 1 << 20
	maximumSourceHMACKeyBytes        = 65
	maximumAPIJSONBytes              = 64 << 10
	maximumLicenseAdminResponseBytes = 8 << 20
	loginPreauthorizationLifetime    = 5 * time.Minute
)

func runServe(arguments []string, stdout, stderr io.Writer) int {
	flags := flag.NewFlagSet("serve", flag.ContinueOnError)
	flags.SetOutput(stderr)
	configPath := flags.String("config", "", "root-owned service configuration file")
	if err := flags.Parse(arguments); err != nil {
		return 2
	}
	if *configPath == "" || flags.NArg() != 0 {
		fmt.Fprintln(stderr, "usage: ota-admin serve --config PATH")
		return 2
	}
	cfg, err := config.Load(*configPath)
	if err != nil {
		fmt.Fprintln(stderr, "serve: invalid root-owned configuration")
		return 1
	}
	if err := cfg.ValidateServe(); err != nil {
		fmt.Fprintln(stderr, "serve: production configuration rejected")
		return 1
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	if err := serve(ctx, cfg, stdout, stderr); err != nil {
		fmt.Fprintln(stderr, "serve: service stopped with an error")
		return 1
	}
	return 0
}

func serve(ctx context.Context, cfg config.Config, stdout, stderr io.Writer) error {
	if err := cfg.ValidateServe(); err != nil {
		return err
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	if cfg.PublisherSPKISHA256 != publisher.ProductionSPKISHA256 {
		return errors.New("publisher pin does not match the compiled production identity")
	}
	certificateDER, err := config.ReadTrustedFile(cfg.PublisherCertificatePath, maximumTrustedCertificateBytes)
	if err != nil {
		return errors.New("load publisher certificate")
	}
	if err := validatePublisherCertificate(certificateDER, cfg.PublisherSPKISHA256); err != nil {
		return err
	}
	trust, err := publisher.ProductionTrust()
	if err != nil {
		return errors.New("initialize compiled publisher trust")
	}
	keyText, err := config.ReadTrustedFile(cfg.SourceHMACKeyPath, maximumSourceHMACKeyBytes)
	if err != nil {
		return errors.New("load source audit key")
	}
	sourceHMACKey, err := decodeSourceHMACKey(keyText)
	if err != nil {
		return err
	}
	defer clear(sourceHMACKey)
	licenseSecret, err := licenseconfig.ReadSharedSecret32(cfg.LicenseAdminSecretPath)
	if err != nil {
		return errors.New("load license admin credential")
	}
	licenseClient, err := buildLicenseAdminClient(cfg, licenseSecret)
	clear(licenseSecret)
	if err != nil {
		return errors.New("initialize license admin client")
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

	database, err := store.Open(ctx, cfg.DatabasePath)
	if err != nil {
		return errors.New("open service state")
	}
	defer database.Close()
	sessions, err := session.NewService(database, session.Options{
		SourceHMACKey: sourceHMACKey, PreauthTTL: loginPreauthorizationLifetime,
		IdleTimeout: cfg.Limits.SessionIdle, AbsoluteExpiry: cfg.Limits.SessionLifetime,
		FailureLimit: cfg.Limits.LoginFailures, FailureWindow: cfg.Limits.FailureWindow, FailureLock: cfg.Limits.FailureLock,
	})
	if err != nil {
		return errors.New("initialize session service")
	}
	clear(sourceHMACKey)

	receiver, err := upload.NewReceiver(upload.ReceiverOptions{StagingRoot: cfg.StagingRoot, Limits: upload.LimitsFromConfig(cfg.Limits)})
	if err != nil {
		return errors.New("initialize upload receiver")
	}
	validator := release.NewValidator(trust)
	candidates, err := candidate.NewService(candidate.Options{
		Store: database, Validator: validator, StagingRoot: cfg.StagingRoot, Lifetime: cfg.Limits.CandidateLifetime,
	})
	if err != nil {
		return errors.New("initialize candidate service")
	}
	janitor, err := candidate.NewJanitor(candidate.JanitorOptions{
		Store: database, StagingRoot: cfg.StagingRoot, Interval: time.Hour,
		OnError: func(error) { fmt.Fprintln(stderr, "candidate cleanup will retry") },
	})
	if err != nil {
		return errors.New("initialize candidate cleanup")
	}
	disk, err := deploy.NewDisk(cfg.ReleaseRoot, nil)
	if err != nil {
		return errors.New("initialize publication storage")
	}
	publication, err := deploy.NewPublisher(deploy.PublisherOptions{Disk: disk, Store: database, Candidates: candidates, Validator: validator})
	if err != nil {
		return errors.New("initialize publisher")
	}
	if err := publication.Recover(ctx); err != nil {
		// Recovery intentionally leaves the publisher in read-only protection.
		// The console remains available for diagnosis without exposing paths or
		// exception details through journald or the browser.
		fmt.Fprintln(stderr, "publisher recovery requires read-only protection")
	}
	retention, err := deploy.NewRetention(deploy.RetentionOptions{
		Store: database, Generations: disk, Interval: time.Hour, AttemptTimeout: time.Minute,
		OnError: func(error) { fmt.Fprintln(stderr, "release retention will retry") },
	})
	if err != nil {
		return errors.New("initialize release retention")
	}
	operations, err := operation.NewService(operation.Options{Publisher: publication, Passwords: sessions, History: database, Retention: retention})
	if err != nil {
		return errors.New("initialize release operations")
	}
	handler, err := httpapi.NewRouter(httpapi.Options{
		Sessions: sessions, PublicOrigin: cfg.PublicOrigin, MaxJSONBytes: maximumAPIJSONBytes,
		Uploads: receiver, UploadEnvelopes: candidates, Publisher: operations, ReleaseOperations: operations,
		LicenseAdmin: licenseClient,
	})
	if err != nil {
		return errors.New("initialize HTTP API")
	}

	workerContext, stopWorkers := context.WithCancel(ctx)
	defer stopWorkers()
	janitor.Start(workerContext)
	retention.Start(workerContext)
	server := &http.Server{
		Handler: handler, ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 60 * time.Second,
		MaxHeaderBytes: 32 << 10, ErrorLog: log.New(stderr, "http: ", 0),
		BaseContext: func(net.Listener) context.Context { return workerContext },
	}
	shutdownDone := make(chan struct{})
	go func() {
		defer close(shutdownDone)
		<-workerContext.Done()
		shutdownContext, cancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cancel()
		_ = server.Shutdown(shutdownContext)
	}()
	fmt.Fprintln(stdout, "OTA admin listening on configured loopback address")
	serveErr := server.Serve(listener)
	stopWorkers()
	<-shutdownDone
	if errors.Is(serveErr, http.ErrServerClosed) && ctx.Err() != nil {
		return nil
	}
	return serveErr
}

func buildLicenseAdminClient(cfg config.Config, rawSecret []byte) (*licenseadmin.Client, error) {
	return licenseadmin.NewClient(licenseadmin.Options{
		BaseURL:          cfg.LicenseAdminBaseURL,
		Secret:           rawSecret,
		MaxRequestBytes:  maximumAPIJSONBytes,
		MaxResponseBytes: maximumLicenseAdminResponseBytes,
	})
}

func decodeSourceHMACKey(data []byte) ([]byte, error) {
	if len(data) == 65 && data[64] == '\n' {
		data = data[:64]
	}
	if len(data) != 64 {
		return nil, errors.New("source audit key must contain exactly 32 lowercase-hex bytes")
	}
	for _, value := range data {
		if !strings.ContainsRune("0123456789abcdef", rune(value)) {
			return nil, errors.New("source audit key must contain exactly 32 lowercase-hex bytes")
		}
	}
	key := make([]byte, 32)
	if _, err := hex.Decode(key, data); err != nil {
		return nil, errors.New("source audit key is invalid")
	}
	return key, nil
}

func validatePublisherCertificate(der []byte, expectedSPKI string) error {
	certificate, err := x509.ParseCertificate(der)
	if err != nil {
		return errors.New("publisher certificate is invalid DER")
	}
	digest := sha256.Sum256(certificate.RawSubjectPublicKeyInfo)
	if hex.EncodeToString(digest[:]) != expectedSPKI {
		return errors.New("publisher certificate SPKI does not match its pin")
	}
	return nil
}
