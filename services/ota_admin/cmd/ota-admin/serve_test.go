package main

import (
	"bytes"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"math/big"
	"strings"
	"testing"
	"time"
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
