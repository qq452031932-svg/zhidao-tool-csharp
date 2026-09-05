package publisher

import (
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"errors"
)

const ProductionSPKISHA256 = "f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff"

const productionSPKIBase64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAvWrtg8OCYPysm7veFAbUHBj0Jv5lfCsnUZB0//REAhwl6diO3KYS5Ht66wlIdKvOQ/IyD45HV7gIkdYADgu4Y4B/ZzoW1uwnaul80eLRo2gCiWO2GS/VKDgtQpXkyEovHXWZ7qdNc0U1PYiZTMSUma6581yUAQ48ETfm60SJ9EDaYhgZugGbFRzY4RwV4H8ajEXp25ljKd5+xsT54MQ5iyt6fWbDZzLL0TziW30nDXocLp93Ucgic0wLrB1kmj9kfQ1DYzsTRF9OK18JXy1cxbJeP9Z9dgg1vGtvnbLqJweUO+SggHTnPlDO9NGQbZxoJxpcqCmRcPzrZaqYGcOx9YE06jQVfeHcI67PQLmH9+cmtn/9UaKL0VKPXf9J5lH5Z48f7tHbkgZH8sHF4xoNLdrLpD8fYKtc0+h6KxH5MnyDedQfuQpjZlhE4z7BxEfU7GGpIQ7a3p8B8yB+KraXgLWaUJlwsrWoQ9TPXGMUuE8ISIitZNdVfaFvOvllVMdBAgMBAAE="

type Trust struct {
	publicKey *rsa.PublicKey
	spkiHash  string
}

func ProductionTrust() (*Trust, error) {
	spki, err := base64.StdEncoding.Strict().DecodeString(productionSPKIBase64)
	if err != nil {
		return nil, errors.New("decode enrolled publisher key")
	}
	return NewTrust(spki, ProductionSPKISHA256)
}

func NewTrust(spki []byte, expectedSHA256 string) (*Trust, error) {
	if len(spki) == 0 || len(expectedSHA256) != 64 {
		return nil, errors.New("publisher trust is incomplete")
	}
	digest := sha256.Sum256(spki)
	actual := hex.EncodeToString(digest[:])
	if actual != expectedSHA256 {
		return nil, errors.New("publisher SPKI pin mismatch")
	}
	parsed, err := x509.ParsePKIXPublicKey(spki)
	if err != nil {
		return nil, errors.New("publisher SPKI is invalid")
	}
	publicKey, ok := parsed.(*rsa.PublicKey)
	if !ok || publicKey.N.BitLen() != 3072 || publicKey.E != 65537 {
		return nil, errors.New("publisher key must be RSA-3072 with the expected exponent")
	}
	return &Trust{publicKey: publicKey, spkiHash: actual}, nil
}

func (trust *Trust) SPKISHA256() string {
	if trust == nil {
		return ""
	}
	return trust.spkiHash
}
