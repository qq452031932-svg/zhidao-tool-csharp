package publisher

import (
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"testing"
)

func TestProductionTrustPinsEnrolledRSA3072SPKI(t *testing.T) {
	trust, err := ProductionTrust()
	if err != nil {
		t.Fatal(err)
	}
	if trust.SPKISHA256() != ProductionSPKISHA256 {
		t.Fatalf("SPKI SHA-256 = %q", trust.SPKISHA256())
	}
	if trust.publicKey.N.BitLen() != 3072 {
		t.Fatalf("RSA bits = %d", trust.publicKey.N.BitLen())
	}
}

func TestNewTrustRejectsWrongPinAndNonRSA3072(t *testing.T) {
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	spki, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256(spki)
	if _, err := NewTrust(spki, hex.EncodeToString(digest[:])); err == nil {
		t.Fatal("accepted RSA-2048 key")
	}
	if _, err := NewTrust(spki, ProductionSPKISHA256); err == nil {
		t.Fatal("accepted wrong SPKI pin")
	}
}
