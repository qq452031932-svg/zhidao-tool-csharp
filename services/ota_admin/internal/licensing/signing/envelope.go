package signing

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"errors"
	"io"
	"strings"

	licenseconfig "fanfh.cn/superpan/ota-admin/internal/licensing/config"
	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
)

const maximumPrivateKeyBytes = 16 << 10

type Signer struct {
	keyID  string
	key    *ecdsa.PrivateKey
	random io.Reader
}

func New(keyID string, key *ecdsa.PrivateKey, random io.Reader) (*Signer, error) {
	if strings.TrimSpace(keyID) != keyID || keyID == "" || len(keyID) > model.MaxIdentifierLength {
		return nil, errors.New("invalid signing key ID")
	}
	if key == nil || key.Curve != elliptic.P256() || key.D == nil || key.X == nil || key.Y == nil || !key.Curve.IsOnCurve(key.X, key.Y) {
		return nil, errors.New("signing key must be ECDSA P-256")
	}
	if random == nil {
		return nil, errors.New("signing randomness is required")
	}
	return &Signer{keyID: keyID, key: key, random: random}, nil
}

func (s *Signer) Sign(claims model.Claims) (model.SignedEnvelope, error) {
	if s == nil || s.key == nil || s.random == nil {
		return model.SignedEnvelope{}, errors.New("signer is not initialized")
	}
	if err := claims.Validate(); err != nil {
		return model.SignedEnvelope{}, errors.New("invalid claims")
	}
	payload, err := json.Marshal(claims)
	if err != nil {
		return model.SignedEnvelope{}, errors.New("serialize claims")
	}
	digest := sha256.Sum256(payload)
	signature, err := ecdsa.SignASN1(s.random, s.key, digest[:])
	if err != nil {
		return model.SignedEnvelope{}, errors.New("sign claims")
	}
	return model.SignedEnvelope{
		KeyID:     s.keyID,
		Payload:   base64.StdEncoding.EncodeToString(payload),
		Signature: base64.StdEncoding.EncodeToString(signature),
	}, nil
}

// Verify authenticates the exact canonical claims representation produced by Sign.
func Verify(envelope model.SignedEnvelope, expectedKeyID string, publicKey *ecdsa.PublicKey) (model.Claims, error) {
	if envelope.Validate() != nil || envelope.KeyID != expectedKeyID || publicKey == nil || publicKey.Curve != elliptic.P256() || !publicKey.Curve.IsOnCurve(publicKey.X, publicKey.Y) {
		return model.Claims{}, errors.New("invalid signed envelope identity")
	}
	payload, err := base64.StdEncoding.Strict().DecodeString(envelope.Payload)
	if err != nil || base64.StdEncoding.EncodeToString(payload) != envelope.Payload {
		return model.Claims{}, errors.New("invalid signed payload encoding")
	}
	var claims model.Claims
	if json.Unmarshal(payload, &claims) != nil || claims.Validate() != nil {
		return model.Claims{}, errors.New("invalid signed claims")
	}
	canonical, err := json.Marshal(claims)
	if err != nil || !bytes.Equal(canonical, payload) {
		return model.Claims{}, errors.New("noncanonical signed claims")
	}
	signature, err := base64.StdEncoding.Strict().DecodeString(envelope.Signature)
	if err != nil || base64.StdEncoding.EncodeToString(signature) != envelope.Signature {
		return model.Claims{}, errors.New("invalid signature encoding")
	}
	digest := sha256.Sum256(payload)
	if !ecdsa.VerifyASN1(publicKey, digest[:], signature) {
		return model.Claims{}, errors.New("signature verification failed")
	}
	return claims, nil
}

func LoadPrivateKey(path string) (*ecdsa.PrivateKey, error) {
	data, err := licenseconfig.ReadLicenseFile(path, maximumPrivateKeyBytes)
	if err != nil {
		return nil, errors.New("read signing private key")
	}
	block, rest := pem.Decode(data)
	clear(data)
	if block == nil || block.Type != "EC PRIVATE KEY" || len(strings.TrimSpace(string(rest))) != 0 {
		return nil, errors.New("signing private key must be one EC PRIVATE KEY PEM block")
	}
	key, err := x509.ParseECPrivateKey(block.Bytes)
	clear(block.Bytes)
	if err != nil || key.Curve != elliptic.P256() {
		return nil, errors.New("signing private key must be ECDSA P-256")
	}
	return key, nil
}
