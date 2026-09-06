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
	"math/big"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/store"
)

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
