package authwire

import (
	"bytes"
	"encoding/base64"
	"testing"
)

func TestEncodeInternalTokenIsCanonicalBase64URLForRawBinarySecret(t *testing.T) {
	secret := append([]byte{0x00, 0x0a, 0x0d, 0x7f, 0x80, 0xff}, bytes.Repeat([]byte{0x42}, 26)...)
	want := base64.RawURLEncoding.EncodeToString(secret)
	if got := EncodeInternalToken(secret); got != want {
		t.Fatalf("EncodeInternalToken()=%q want=%q", got, want)
	}
	if len(want) != 43 || bytes.ContainsAny([]byte(want), "=+/\r\n\x00") {
		t.Fatalf("encoded token is not canonical base64url: %q", want)
	}
}
