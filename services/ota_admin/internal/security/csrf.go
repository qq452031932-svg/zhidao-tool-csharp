package security

import (
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"io"
)

func NewRandomToken(random io.Reader) (string, error) {
	if random == nil {
		return "", errors.New("token randomness is required")
	}
	value := make([]byte, 32)
	if _, err := io.ReadFull(random, value); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(value), nil
}

func HashToken(token string) string {
	digest := sha256.Sum256([]byte(token))
	return hex.EncodeToString(digest[:])
}

func TokenMatchesHash(token, encodedHash string) bool {
	actual := sha256.Sum256([]byte(token))
	expected, err := hex.DecodeString(encodedHash)
	if err != nil || len(expected) != sha256.Size {
		return false
	}
	return subtle.ConstantTimeCompare(actual[:], expected) == 1
}

func ConstantTimeStringEqual(left, right string) bool {
	leftHash := sha256.Sum256([]byte(left))
	rightHash := sha256.Sum256([]byte(right))
	return subtle.ConstantTimeCompare(leftHash[:], rightHash[:]) == 1
}
