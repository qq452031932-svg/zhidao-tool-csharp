package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"io"
	"math/big"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/config"
	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

func TestRunCheckConfigValidatesProductionWithoutOpeningState(t *testing.T) {
	path := filepath.Join("..", "..", "..", "..", "deploy", "ota-admin", "config.example.json")
	file, err := os.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	cfg, err := config.Parse(file)
	file.Close()
	if err != nil {
		t.Fatal(err)
	}
	if err := validateExactProductionOTAConfig(cfg); err != nil {
		t.Fatalf("fixture is not exact production config: %v: %+v", err, cfg)
	}
	var stdout, stderr bytes.Buffer
	if code := run([]string{"check-config", "--config", path}, &stdout, &stderr); code != 0 {
		t.Fatalf("check-config code=%d stderr=%q", code, stderr.String())
	}
	if stdout.String() != "ota-admin: production configuration valid\n" {
		t.Fatalf("stdout=%q", stdout.String())
	}
	if _, err := os.Stat("/var/lib/superpan-ota-admin/state.db"); err == nil {
		t.Fatal("check-config opened production state")
	}
}

func TestRunRecognizesPublishBundleCommand(t *testing.T) {
	var stdout, stderr bytes.Buffer
	code := run([]string{"publish-bundle"}, &stdout, &stderr)
	if code != 2 {
		t.Fatalf("code = %d, want 2 for missing required flags", code)
	}
	if strings.Contains(stderr.String(), "usage: ota-admin <") || !strings.Contains(stderr.String(), "usage: ota-admin publish-bundle") {
		t.Fatalf("stderr = %q, want publish-bundle usage", stderr.String())
	}
}

type recordingBundleReceiver struct{}

func (recordingBundleReceiver) Receive(context.Context, io.Reader, int64) (*upload.Envelope, error) {
	return &upload.Envelope{}, nil
}

type recordingCandidateAcceptor struct{ sessionID string }

func (acceptor *recordingCandidateAcceptor) AcceptNewEnvelopeForActiveSession(_ context.Context, sessionID, operationID string, _ *upload.Envelope) (candidate.Summary, error) {
	acceptor.sessionID = sessionID
	return candidate.Summary{CandidateID: "11111111-1111-4111-8111-111111111111", OperationID: operationID, State: string(store.CandidateValidated)}, nil
}

type recordingBundlePublisher struct{ sessionID string }

func (publisher *recordingBundlePublisher) PublishForActiveSession(_ context.Context, sessionID, candidateID, operationID string) (deploy.PublishResult, error) {
	publisher.sessionID = sessionID
	return deploy.PublishResult{CandidateID: candidateID, OperationID: operationID, ReleaseID: operationID, Version: "1.1.0", State: "succeeded"}, nil
}

func TestPublishBundleWithComponentsRevokesTemporarySession(t *testing.T) {
	stateRoot := t.TempDir()
	if err := os.Chmod(stateRoot, 0o700); err != nil {
		t.Fatal(err)
	}
	database, err := store.Open(context.Background(), filepath.Join(stateRoot, "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	bundlePath := filepath.Join(stateRoot, "bundle.zip")
	if err := os.WriteFile(bundlePath, []byte("test bundle"), 0o600); err != nil {
		t.Fatal(err)
	}
	acceptor := &recordingCandidateAcceptor{}
	publisher := &recordingBundlePublisher{}
	random := bytes.NewReader(bytes.Repeat([]byte{0x42}, 128))
	now := time.Unix(1_800_000_000, 0).UTC()
	result, err := publishBundleWithComponents(context.Background(), bundlePath, database, recordingBundleReceiver{}, acceptor, publisher, random, func() time.Time { return now })
	if err != nil || result.Version != "1.1.0" || acceptor.sessionID == "" || publisher.sessionID != acceptor.sessionID {
		t.Fatalf("result=%+v acceptor=%q publisher=%q err=%v", result, acceptor.sessionID, publisher.sessionID, err)
	}
	persisted, err := database.Session(context.Background(), acceptor.sessionID)
	if err != nil || persisted.State != store.SessionRevoked {
		t.Fatalf("temporary session=%+v err=%v, want revoked", persisted, err)
	}
}

func TestDecodeSourceHMACKeyRequiresOneLowercaseHexKey(t *testing.T) {
	t.Parallel()
	text := strings.Repeat("01", 32) + "\n"
	key, err := decodeSourceHMACKey([]byte(text))
	if err != nil || len(key) != 32 || key[0] != 1 || key[31] != 1 {
		t.Fatalf("decodeSourceHMACKey() = %x, %v", key, err)
	}
	for _, invalid := range [][]byte{
		[]byte(strings.Repeat("01", 31)),
		[]byte(strings.ToUpper(strings.Repeat("ab", 32))),
		[]byte(strings.Repeat("01", 32) + "\r\n"),
		[]byte(strings.Repeat("01", 32) + "\nextra"),
	} {
		if _, err := decodeSourceHMACKey(invalid); err == nil {
			t.Fatalf("decodeSourceHMACKey accepted %q", invalid)
		}
	}
}

func TestInitializeAdminIfMissingIsIdempotentAgainstActualAdministratorRow(t *testing.T) {
	t.Parallel()
	stateRoot := t.TempDir()
	if err := os.Chmod(stateRoot, 0o700); err != nil {
		t.Fatal(err)
	}
	database, err := store.Open(context.Background(), filepath.Join(stateRoot, "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	var first, replay bytes.Buffer
	random := bytes.NewReader(bytes.Repeat([]byte{0x42}, 128))
	if err := initializeAdmin(database, &first, random, true); err != nil || strings.TrimSpace(first.String()) == "" {
		t.Fatalf("first initialize = %q, %v", first.String(), err)
	}
	if err := initializeAdmin(database, &replay, random, true); err != nil || replay.Len() != 0 {
		t.Fatalf("idempotent initialize = %q, %v", replay.String(), err)
	}
}

func TestValidatePublisherCertificateBindsExactSPKI(t *testing.T) {
	t.Parallel()
	privateKey, err := rsa.GenerateKey(rand.Reader, 1024)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{
		SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "test publisher"},
		NotBefore: time.Unix(1_700_000_000, 0), NotAfter: time.Unix(1_800_000_000, 0),
		KeyUsage: x509.KeyUsageDigitalSignature,
	}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &privateKey.PublicKey, privateKey)
	if err != nil {
		t.Fatal(err)
	}
	certificate, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256(certificate.RawSubjectPublicKeyInfo)
	if err := validatePublisherCertificate(der, hex.EncodeToString(digest[:])); err != nil {
		t.Fatalf("validatePublisherCertificate() error = %v", err)
	}
	if err := validatePublisherCertificate(der, strings.Repeat("0", 64)); err == nil {
		t.Fatal("validatePublisherCertificate accepted the wrong SPKI pin")
	}
	if err := validatePublisherCertificate([]byte("not a certificate"), hex.EncodeToString(digest[:])); err == nil {
		t.Fatal("validatePublisherCertificate accepted invalid DER")
	}
}

func TestServeRequiresConfigPathWithoutPrintingSecrets(t *testing.T) {
	t.Parallel()
	var stdout, stderr bytes.Buffer
	if code := run([]string{"serve"}, &stdout, &stderr); code != 2 {
		t.Fatalf("run(serve) code = %d, stderr = %q", code, stderr.String())
	}
	if stdout.Len() != 0 || !strings.Contains(stderr.String(), "serve --config PATH") {
		t.Fatalf("stdout=%q stderr=%q", stdout.String(), stderr.String())
	}
}

func TestBuildLicenseAdminClientFailsClosedWithoutExactRawSecret(t *testing.T) {
	t.Parallel()
	cfg := config.Default()
	cfg.LicenseAdminBaseURL = "http://127.0.0.1:19091"
	if _, err := buildLicenseAdminClient(cfg, make([]byte, 31)); err == nil {
		t.Fatal("buildLicenseAdminClient accepted a short secret")
	}
	client, err := buildLicenseAdminClient(cfg, make([]byte, 32))
	if err != nil || client == nil {
		t.Fatalf("buildLicenseAdminClient() = %v, %v", client, err)
	}
}
