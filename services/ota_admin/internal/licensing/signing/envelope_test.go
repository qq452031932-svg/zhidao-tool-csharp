package signing

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"os"
	"os/user"
	"path/filepath"
	"runtime"
	"strconv"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
)

func TestSignClaimsPreservesExactSerializedBytes(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := New("local-key-1", key, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	claims := model.Claims{
		ProtocolVersion: model.ProtocolVersion,
		RequestID:       "request-1",
		ServerTime:      time.Date(2026, 9, 9, 1, 2, 3, 0, time.UTC),
		Decision:        model.DecisionDeny,
		Code:            model.CodeLicenseNotFound,
	}

	envelope, err := signer.Sign(claims)
	if err != nil {
		t.Fatal(err)
	}
	payload, err := base64.StdEncoding.DecodeString(envelope.Payload)
	if err != nil {
		t.Fatal(err)
	}
	want := []byte(`{"protocol_version":1,"request_id":"request-1","server_time":"2026-09-09T01:02:03Z","decision":"deny","code":"license_not_found","card_id":null,"software_id":null,"machine_hash":null,"package_name":null,"expires_at":null,"lease_until":null}`)
	if !bytes.Equal(payload, want) {
		t.Fatalf("decoded payload = %s, want byte-exact %s", payload, want)
	}
	signature, err := base64.StdEncoding.DecodeString(envelope.Signature)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256(payload)
	if !ecdsa.VerifyASN1(&key.PublicKey, digest[:], signature) {
		t.Fatal("signature does not verify over decoded payload bytes")
	}
	encoded, err := json.Marshal(envelope)
	if err != nil {
		t.Fatal(err)
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(encoded, &fields); err != nil {
		t.Fatal(err)
	}
	if len(fields) != 3 || fields["key_id"] == nil || fields["payload"] == nil || fields["signature"] == nil {
		t.Fatalf("outer envelope fields = %s", encoded)
	}
}

func TestNewRejectsWrongCurveAndInvalidKeyID(t *testing.T) {
	p384, err := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := New("key", p384, rand.Reader); err == nil {
		t.Fatal("New accepted a non-P-256 key")
	}
	p256, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := New("", p256, rand.Reader); err == nil {
		t.Fatal("New accepted an empty key ID")
	}
}

func TestLoadPrivateKeyAcceptsOnlySingleP256ECPEM(t *testing.T) {
	if runtime.GOOS == "linux" {
		t.Skip("Linux acceptance requires installed production ownership")
	}
	p256, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	der, err := x509.MarshalECPrivateKey(p256)
	if err != nil {
		t.Fatal(err)
	}
	validPath := writePEM(t, "valid.pem", &pem.Block{Type: "EC PRIVATE KEY", Bytes: der}, 0o600)
	loaded, err := LoadPrivateKey(validPath)
	if err != nil {
		t.Fatal(err)
	}
	if loaded.Curve != elliptic.P256() {
		t.Fatal("loaded key curve is not P-256")
	}

	p384, err := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	p384DER, _ := x509.MarshalECPrivateKey(p384)
	wrongCurve := writePEM(t, "p384.pem", &pem.Block{Type: "EC PRIVATE KEY", Bytes: p384DER}, 0o600)
	if _, err := LoadPrivateKey(wrongCurve); err == nil {
		t.Fatal("LoadPrivateKey accepted P-384")
	}

	pkcs8, err := x509.MarshalPKCS8PrivateKey(p256)
	if err != nil {
		t.Fatal(err)
	}
	wrongFormat := writePEM(t, "pkcs8.pem", &pem.Block{Type: "PRIVATE KEY", Bytes: pkcs8}, 0o600)
	if _, err := LoadPrivateKey(wrongFormat); err == nil {
		t.Fatal("LoadPrivateKey accepted PKCS#8 instead of EC PRIVATE KEY")
	}

	trailing := filepath.Join(t.TempDir(), "trailing.pem")
	if err := os.WriteFile(trailing, append(pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: der}), []byte("not-whitespace")...), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := LoadPrivateKey(trailing); err == nil {
		t.Fatal("LoadPrivateKey accepted trailing non-whitespace data")
	}
}

func TestLoadPrivateKeyUnixPermissionsAllowOnly0600Or0640(t *testing.T) {
	if runtime.GOOS == "windows" || runtime.GOOS == "linux" {
		t.Skip("platform uses a separate trusted-file policy")
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	der, _ := x509.MarshalECPrivateKey(key)
	for _, mode := range []os.FileMode{0o600, 0o640, 0o400, 0o440, 0o700, 0o650, 0o660, 0o604, 0o644} {
		path := writePEM(t, "permissions.pem", &pem.Block{Type: "EC PRIVATE KEY", Bytes: der}, mode)
		_, err := LoadPrivateKey(path)
		wantOK := mode == 0o600 || mode == 0o640
		if (err == nil) != wantOK {
			t.Fatalf("mode %o error = %v, wantOK=%v", mode, err, wantOK)
		}
	}
}

func TestLoadPrivateKeyRejectsSymlinkOnLinux(t *testing.T) {
	if runtime.GOOS != "linux" || os.Geteuid() != 0 {
		t.Skip("Linux root-only trust fixture")
	}
	group, err := user.LookupGroup("superpan-license")
	if err != nil {
		t.Skip("superpan-license group is not installed")
	}
	gid, err := strconv.Atoi(group.Gid)
	if err != nil {
		t.Fatal(err)
	}
	root, err := os.MkdirTemp("/root", "signing-trust-")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(root) })
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	der, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	target := filepath.Join(root, "real.pem")
	if err := os.WriteFile(target, pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: der}), 0o640); err != nil {
		t.Fatal(err)
	}
	if err := os.Chown(target, 0, gid); err != nil {
		t.Fatal(err)
	}
	link := filepath.Join(root, "link.pem")
	if err := os.Symlink(target, link); err != nil {
		t.Fatal(err)
	}
	if _, err := LoadPrivateKey(link); err == nil {
		t.Fatal("LoadPrivateKey accepted symlink")
	}
}

func writePEM(t *testing.T, name string, block *pem.Block, mode os.FileMode) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), name)
	if err := os.WriteFile(path, pem.EncodeToMemory(block), mode); err != nil {
		t.Fatal(err)
	}
	return path
}
