package publisher

import (
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"errors"
	"strings"
	"testing"
)

func testTrust(t *testing.T) (*Trust, *rsa.PrivateKey) {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 3072)
	if err != nil {
		t.Fatal(err)
	}
	spki, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256(spki)
	trust, err := NewTrust(spki, hex.EncodeToString(digest[:]))
	if err != nil {
		t.Fatal(err)
	}
	return trust, key
}

func TestVerifyManifestAcceptsExactCanonicalSignedBytes(t *testing.T) {
	trust, key := testTrust(t)
	manifest := []byte(strings.Repeat("a", 64) + "  RELEASES-win-x64-stable\n" + strings.Repeat("b", 64) + "  SuperPan.BaiduBatchTransfer-1.0.1-full.nupkg\n")
	digest := sha256.Sum256(manifest)
	signature, err := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, digest[:])
	if err != nil {
		t.Fatal(err)
	}
	entries, err := trust.VerifyManifest(manifest, signature)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 2 || entries[1].Name != "SuperPan.BaiduBatchTransfer-1.0.1-full.nupkg" {
		t.Fatalf("entries = %#v", entries)
	}
}

func TestVerifyManifestRejectsNonCanonicalOrTamperedInput(t *testing.T) {
	trust, key := testTrust(t)
	canonical := []byte(strings.Repeat("a", 64) + "  a.bin\n")
	digest := sha256.Sum256(canonical)
	signature, err := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, digest[:])
	if err != nil {
		t.Fatal(err)
	}

	tests := []struct {
		name      string
		manifest  []byte
		signature []byte
		want      error
	}{
		{"tampered", []byte(strings.Repeat("b", 64) + "  a.bin\n"), signature, ErrSignature},
		{"wrong signature size", canonical, signature[:len(signature)-1], ErrSignature},
		{"bom", append([]byte{0xef, 0xbb, 0xbf}, canonical...), signature, ErrManifest},
		{"crlf", []byte(strings.Repeat("a", 64) + "  a.bin\r\n"), signature, ErrManifest},
		{"no final LF", canonical[:len(canonical)-1], signature, ErrManifest},
		{"double final LF", append(append([]byte(nil), canonical...), '\n'), signature, ErrManifest},
		{"uppercase hash", []byte(strings.Repeat("A", 64) + "  a.bin\n"), signature, ErrManifest},
		{"excluded manifest", []byte(strings.Repeat("a", 64) + "  upload-manifest.sha256\n"), signature, ErrManifest},
		{"nested name", []byte(strings.Repeat("a", 64) + "  a/b\n"), signature, ErrManifest},
		{"unsorted", []byte(strings.Repeat("a", 64) + "  b.bin\n" + strings.Repeat("a", 64) + "  a.bin\n"), signature, ErrManifest},
		{"case collision", []byte(strings.Repeat("a", 64) + "  A.bin\n" + strings.Repeat("a", 64) + "  a.bin\n"), signature, ErrManifest},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			_, err := trust.VerifyManifest(test.manifest, test.signature)
			if !errors.Is(err, test.want) {
				t.Fatalf("error = %v, want %v", err, test.want)
			}
		})
	}
}
