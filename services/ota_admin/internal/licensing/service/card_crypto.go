package service

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base32"
	"encoding/hex"
	"errors"
	"io"
	"strings"
	"unicode"
)

const (
	cardRandomBytes = 20
	cardGroupSize   = 8
	cardGroupCount  = 4
)

var cardEncoding = base32.StdEncoding.WithPadding(base32.NoPadding)

type cardProtector struct {
	gcm       cipher.AEAD
	lookupKey []byte
	random    io.Reader
}

type protectedCard struct {
	ciphertext []byte
	digest     string
}

func newCardProtector(encryptionKey, lookupKey []byte, random io.Reader) (*cardProtector, error) {
	if len(encryptionKey) != 32 || len(lookupKey) != 32 || random == nil {
		return nil, errors.New("card protection requires two 32-byte keys and a random source")
	}
	if hmac.Equal(encryptionKey, lookupKey) {
		return nil, errors.New("card encryption and lookup keys must be distinct")
	}
	block, err := aes.NewCipher(encryptionKey)
	if err != nil {
		return nil, err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	return &cardProtector{gcm: gcm, lookupKey: append([]byte(nil), lookupKey...), random: random}, nil
}

func (p *cardProtector) generate() (string, error) {
	raw := make([]byte, cardRandomBytes)
	defer zeroBytes(raw)
	if _, err := io.ReadFull(p.random, raw); err != nil {
		return "", err
	}
	encoded := cardEncoding.EncodeToString(raw)
	groups := make([]string, cardGroupCount)
	for i := range groups {
		start := i * cardGroupSize
		groups[i] = encoded[start : start+cardGroupSize]
	}
	return strings.Join(groups, "-"), nil
}

func (p *cardProtector) seal(cardText string) (protectedCard, error) {
	canonical, compact, err := normalizeCard(cardText)
	if err != nil {
		return protectedCard{}, err
	}
	plaintext := []byte(canonical)
	defer zeroBytes(plaintext)
	nonce := make([]byte, p.gcm.NonceSize())
	if _, err := io.ReadFull(p.random, nonce); err != nil {
		return protectedCard{}, err
	}
	ciphertext := make([]byte, 0, len(nonce)+len(plaintext)+p.gcm.Overhead())
	ciphertext = append(ciphertext, nonce...)
	ciphertext = p.gcm.Seal(ciphertext, nonce, plaintext, nil)
	return protectedCard{ciphertext: ciphertext, digest: p.digestCompact(compact)}, nil
}

func (p *cardProtector) open(ciphertext []byte) (string, error) {
	if len(ciphertext) < p.gcm.NonceSize()+p.gcm.Overhead() {
		return "", errors.New("invalid encrypted card")
	}
	nonce := ciphertext[:p.gcm.NonceSize()]
	plaintext, err := p.gcm.Open(nil, nonce, ciphertext[p.gcm.NonceSize():], nil)
	if err != nil {
		return "", errors.New("invalid encrypted card")
	}
	defer zeroBytes(plaintext)
	return string(plaintext), nil
}

func (p *cardProtector) digest(cardText string) string {
	_, compact, err := normalizeCard(cardText)
	if err != nil {
		return ""
	}
	return p.digestCompact(compact)
}

func (p *cardProtector) digestCompact(compact string) string {
	input := []byte(compact)
	defer zeroBytes(input)
	mac := hmac.New(sha256.New, p.lookupKey)
	_, _ = mac.Write(input)
	return hex.EncodeToString(mac.Sum(nil))
}

func normalizeCard(cardText string) (canonical, compact string, err error) {
	var builder strings.Builder
	builder.Grow(cardGroupCount * cardGroupSize)
	for _, r := range strings.TrimSpace(cardText) {
		switch {
		case r == '-' || unicode.IsSpace(r):
			continue
		case r >= 'a' && r <= 'z':
			r -= 'a' - 'A'
		}
		if !((r >= 'A' && r <= 'Z') || (r >= '2' && r <= '7')) {
			return "", "", errors.New("invalid card format")
		}
		builder.WriteRune(r)
	}
	compact = builder.String()
	if len(compact) != cardGroupCount*cardGroupSize {
		return "", "", errors.New("invalid card format")
	}
	decoded, decodeErr := cardEncoding.DecodeString(compact)
	if decodeErr != nil || len(decoded) != cardRandomBytes {
		zeroBytes(decoded)
		return "", "", errors.New("invalid card format")
	}
	zeroBytes(decoded)
	groups := make([]string, cardGroupCount)
	for i := range groups {
		start := i * cardGroupSize
		groups[i] = compact[start : start+cardGroupSize]
	}
	return strings.Join(groups, "-"), compact, nil
}

func zeroBytes(value []byte) {
	for i := range value {
		value[i] = 0
	}
}
