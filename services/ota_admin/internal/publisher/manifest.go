package publisher

import (
	"crypto"
	"crypto/rsa"
	"crypto/sha256"
	"errors"
	"fmt"
	"regexp"
	"strings"
	"unicode/utf8"
)

var (
	ErrManifest  = errors.New("manifest is not canonical")
	ErrSignature = errors.New("manifest signature is invalid")
)

const (
	manifestName  = "upload-manifest.sha256"
	signatureName = "upload-manifest.sha256.sig"
	maxManifest   = 81_788
)

var manifestLine = regexp.MustCompile(`^([0-9a-f]{64})  ([!-~]+)$`)

type ManifestEntry struct {
	SHA256 string `json:"sha256"`
	Name   string `json:"name"`
}

func (trust *Trust) VerifyManifest(manifest, signature []byte) ([]ManifestEntry, error) {
	if trust == nil || trust.publicKey == nil || len(signature) != 384 {
		return nil, ErrSignature
	}
	entries, err := ParseManifest(manifest)
	if err != nil {
		return nil, err
	}
	digest := sha256.Sum256(manifest)
	if err := rsa.VerifyPKCS1v15(trust.publicKey, crypto.SHA256, digest[:], signature); err != nil {
		return nil, ErrSignature
	}
	return entries, nil
}

func ParseManifest(manifest []byte) ([]ManifestEntry, error) {
	if len(manifest) == 0 || len(manifest) > maxManifest || !utf8.Valid(manifest) ||
		(len(manifest) >= 3 && string(manifest[:3]) == "\xef\xbb\xbf") ||
		strings.ContainsRune(string(manifest), '\r') || manifest[len(manifest)-1] != '\n' ||
		(len(manifest) >= 2 && manifest[len(manifest)-2] == '\n') {
		return nil, ErrManifest
	}
	lines := strings.Split(string(manifest[:len(manifest)-1]), "\n")
	if len(lines) == 0 || len(lines) > 254 {
		return nil, ErrManifest
	}
	entries := make([]ManifestEntry, 0, len(lines))
	seenFolded := make(map[string]struct{}, len(lines))
	previous := ""
	for index, line := range lines {
		match := manifestLine.FindStringSubmatch(line)
		if match == nil || !validFlatASCIIName(match[2]) || match[2] == manifestName || match[2] == signatureName {
			return nil, fmt.Errorf("%w: line %d", ErrManifest, index+1)
		}
		if index > 0 && previous >= match[2] {
			return nil, fmt.Errorf("%w: entries are not ordered", ErrManifest)
		}
		folded := strings.ToLower(match[2])
		if _, exists := seenFolded[folded]; exists {
			return nil, fmt.Errorf("%w: duplicate name", ErrManifest)
		}
		seenFolded[folded] = struct{}{}
		entries = append(entries, ManifestEntry{SHA256: match[1], Name: match[2]})
		previous = match[2]
	}
	return entries, nil
}

func validFlatASCIIName(name string) bool {
	if name == "" || len(name) > 255 || name == "." || name == ".." || strings.ContainsAny(name, `/\\<>:"|?*`) || strings.HasSuffix(name, " ") || strings.HasSuffix(name, ".") {
		return false
	}
	for index := 0; index < len(name); index++ {
		if name[index] < 0x21 || name[index] > 0x7e {
			return false
		}
	}
	return true
}
