package authwire

import "encoding/base64"

// EncodeInternalToken converts the exact 32 raw secret bytes stored on disk to
// the only representation accepted on the HTTP Authorization wire.
func EncodeInternalToken(secret []byte) string {
	return base64.RawURLEncoding.EncodeToString(secret)
}
