package config

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
)

const (
	PublicAPIBasePath  = "/license-api/v1"
	maximumConfigBytes = 64 << 10
)

type Limits struct {
	MaxBodyBytes               int64 `json:"max_body_bytes"`
	MaxHeaderBytes             int   `json:"max_header_bytes"`
	TimestampWindowSeconds     int   `json:"timestamp_window_seconds"`
	NonceLifetimeSeconds       int   `json:"nonce_lifetime_seconds"`
	MaxReplayNonces            int   `json:"max_replay_nonces"`
	MaxAdminIdempotencyEntries int   `json:"max_admin_idempotency_entries"`
	MaxAdminIdempotencyBytes   int64 `json:"max_admin_idempotency_bytes"`
	ActivationRequests         int   `json:"activation_requests"`
	ValidationRequests         int   `json:"validation_requests"`
	AdminRequests              int   `json:"admin_requests"`
	RateWindowSeconds          int   `json:"rate_window_seconds"`
	ReadHeaderTimeoutSeconds   int   `json:"read_header_timeout_seconds"`
	ReadTimeoutSeconds         int   `json:"read_timeout_seconds"`
	WriteTimeoutSeconds        int   `json:"write_timeout_seconds"`
	IdleTimeoutSeconds         int   `json:"idle_timeout_seconds"`
	ShutdownTimeoutSeconds     int   `json:"shutdown_timeout_seconds"`
}

type Config struct {
	ListenAddress              string   `json:"listen_address"`
	LicenseDatabasePath        string   `json:"license_database_path"`
	EncryptionKeyPath          string   `json:"encryption_key_path"`
	PreviousEncryptionKeyPaths []string `json:"previous_encryption_key_paths"`
	LookupHMACKeyPath          string   `json:"lookup_hmac_key_path"`
	SigningPrivateKeyPath      string   `json:"signing_private_key_path"`
	InternalSecretPath         string   `json:"internal_secret_path"`
	LoadTestEnabled            bool     `json:"load_test_enabled"`
	LoadTestSecretPath         string   `json:"load_test_secret_path"`
	SigningKeyID               string   `json:"signing_key_id"`
	PublicAPIBasePath          string   `json:"public_api_base_path"`
	Limits                     Limits   `json:"limits"`
}

func Load(path string) (Config, error) {
	if strings.TrimSpace(path) == "" {
		return Config{}, errors.New("configuration path is required")
	}
	data, err := readConfigFile(path, maximumConfigBytes)
	if err != nil {
		return Config{}, errors.New("read license service configuration")
	}
	defer clear(data)
	cfg, err := Parse(data)
	if err != nil {
		return Config{}, err
	}
	if err := cfg.Validate(); err != nil {
		return Config{}, err
	}
	return cfg, nil
}

// Parse performs the same bounded, strict JSON decoding used by Load without
// applying OS-specific path and listener validation.
func Parse(data []byte) (Config, error) {
	if len(data) > maximumConfigBytes {
		return Config{}, errors.New("license service configuration exceeds maximum size")
	}
	if err := validateConfigJSONShape(data); err != nil {
		return Config{}, err
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	var cfg Config
	if err := decoder.Decode(&cfg); err != nil {
		return Config{}, errors.New("decode license service configuration")
	}
	var trailing any
	if err := decoder.Decode(&trailing); err != io.EOF {
		return Config{}, errors.New("configuration must contain exactly one JSON object")
	}
	return cfg, nil
}

func validateConfigJSONShape(data []byte) error {
	topFields := map[string]struct{}{
		"listen_address": {}, "license_database_path": {}, "encryption_key_path": {}, "previous_encryption_key_paths": {}, "lookup_hmac_key_path": {},
		"signing_private_key_path": {}, "internal_secret_path": {}, "load_test_enabled": {}, "load_test_secret_path": {}, "signing_key_id": {}, "public_api_base_path": {}, "limits": {},
	}
	values, err := strictJSONObject(data, topFields)
	if err != nil {
		return errors.New("configuration keys must be exact and unique")
	}
	limits, ok := values["limits"]
	if !ok {
		return errors.New("configuration limits are required")
	}
	limitFields := map[string]struct{}{
		"max_body_bytes": {}, "max_header_bytes": {}, "timestamp_window_seconds": {}, "nonce_lifetime_seconds": {}, "max_replay_nonces": {}, "max_admin_idempotency_entries": {}, "max_admin_idempotency_bytes": {},
		"activation_requests": {}, "validation_requests": {}, "admin_requests": {}, "rate_window_seconds": {}, "read_header_timeout_seconds": {},
		"read_timeout_seconds": {}, "write_timeout_seconds": {}, "idle_timeout_seconds": {}, "shutdown_timeout_seconds": {},
	}
	if _, err := strictJSONObject(limits, limitFields); err != nil {
		return errors.New("configuration limit keys must be exact and unique")
	}
	return nil
}

func strictJSONObject(data []byte, allowed map[string]struct{}) (map[string]json.RawMessage, error) {
	decoder := json.NewDecoder(bytes.NewReader(data))
	token, err := decoder.Token()
	if err != nil || token != json.Delim('{') {
		return nil, errors.New("JSON object required")
	}
	values := make(map[string]json.RawMessage)
	for decoder.More() {
		token, err := decoder.Token()
		if err != nil {
			return nil, err
		}
		name, ok := token.(string)
		if !ok {
			return nil, errors.New("JSON field name required")
		}
		if _, exists := values[name]; exists {
			return nil, errors.New("duplicate JSON field")
		}
		if _, ok := allowed[name]; !ok {
			return nil, errors.New("unknown JSON field")
		}
		var value json.RawMessage
		if err := decoder.Decode(&value); err != nil {
			return nil, err
		}
		values[name] = value
	}
	if _, err := decoder.Token(); err != nil {
		return nil, err
	}
	var trailing any
	if err := decoder.Decode(&trailing); err != io.EOF {
		return nil, errors.New("trailing JSON value")
	}
	return values, nil
}

func (c Config) Validate() error {
	host, portText, err := net.SplitHostPort(c.ListenAddress)
	if err != nil {
		return errors.New("listen address must include a loopback IP and port")
	}
	ip := net.ParseIP(host)
	port, portErr := strconv.Atoi(portText)
	if ip == nil || !ip.IsLoopback() || portErr != nil || port < 1 || port > 65535 {
		return errors.New("listen address must use a literal loopback IP and nonzero port")
	}
	if c.PublicAPIBasePath != PublicAPIBasePath {
		return fmt.Errorf("public API base path must be %s", PublicAPIBasePath)
	}
	if strings.TrimSpace(c.SigningKeyID) != c.SigningKeyID || c.SigningKeyID == "" || len(c.SigningKeyID) > model.MaxIdentifierLength {
		return errors.New("signing key ID is invalid")
	}
	if len(c.PreviousEncryptionKeyPaths) > 2 {
		return errors.New("too many previous encryption keys")
	}
	if c.LoadTestEnabled != (c.LoadTestSecretPath != "") {
		return errors.New("load-test mode requires exactly one explicit secret path")
	}
	paths := []string{c.LicenseDatabasePath, c.EncryptionKeyPath, c.LookupHMACKeyPath, c.SigningPrivateKeyPath, c.InternalSecretPath}
	paths = append(paths, c.PreviousEncryptionKeyPaths...)
	if c.LoadTestEnabled {
		paths = append(paths, c.LoadTestSecretPath)
	}
	normalized := make([]string, 0, len(paths))
	for _, path := range paths {
		if path == "" || !filepath.IsAbs(path) || filepath.Clean(path) != path {
			return errors.New("state and key paths must be canonical absolute paths")
		}
		identity := path
		if runtime.GOOS == "windows" {
			identity = strings.ToLower(identity)
		}
		for _, existing := range normalized {
			if pathsOverlap(existing, identity) {
				return errors.New("state and key paths must not overlap")
			}
		}
		normalized = append(normalized, identity)
	}
	if c.Limits.MaxBodyBytes < 1024 || c.Limits.MaxBodyBytes > 1<<20 {
		return errors.New("max body bytes is outside the safe range")
	}
	if c.Limits.MaxHeaderBytes < 1024 || c.Limits.MaxHeaderBytes > 64<<10 {
		return errors.New("max header bytes is outside the safe range")
	}
	if c.Limits.TimestampWindowSeconds < 1 || c.Limits.TimestampWindowSeconds > 3600 {
		return errors.New("timestamp window is outside the safe range")
	}
	if c.Limits.NonceLifetimeSeconds < c.Limits.TimestampWindowSeconds || c.Limits.NonceLifetimeSeconds > 24*60*60 {
		return errors.New("nonce lifetime is outside the safe range")
	}
	if c.Limits.MaxReplayNonces < 1 || c.Limits.MaxReplayNonces > 1_000_000 {
		return errors.New("replay nonce cap is outside the safe range")
	}
	if c.Limits.MaxAdminIdempotencyEntries < 1 || c.Limits.MaxAdminIdempotencyEntries > 10_000 {
		return errors.New("admin idempotency entry cap is outside the safe range")
	}
	if c.Limits.MaxAdminIdempotencyBytes < 1<<20 || c.Limits.MaxAdminIdempotencyBytes > 64<<20 {
		return errors.New("admin idempotency byte cap is outside the safe range")
	}
	if c.Limits.ActivationRequests < 1 || c.Limits.ActivationRequests > 10000 || c.Limits.ValidationRequests < 1 || c.Limits.ValidationRequests > 10000 || c.Limits.AdminRequests < 1 || c.Limits.AdminRequests > 10000 {
		return errors.New("rate limits are outside the safe range")
	}
	if c.Limits.RateWindowSeconds < 1 || c.Limits.RateWindowSeconds > 3600 {
		return errors.New("rate window is outside the safe range")
	}
	seconds := []int{c.Limits.ReadHeaderTimeoutSeconds, c.Limits.ReadTimeoutSeconds, c.Limits.WriteTimeoutSeconds, c.Limits.ShutdownTimeoutSeconds}
	for _, value := range seconds {
		if value < 1 || value > 300 {
			return errors.New("server timeout is outside the safe range")
		}
	}
	if c.Limits.IdleTimeoutSeconds < 1 || c.Limits.IdleTimeoutSeconds > 3600 {
		return errors.New("idle timeout is outside the safe range")
	}
	return nil
}

func pathsOverlap(first, second string) bool {
	for _, pair := range [][2]string{{first, second}, {second, first}} {
		relative, err := filepath.Rel(pair[0], pair[1])
		if err != nil {
			continue
		}
		if relative == "." || (relative != ".." && !strings.HasPrefix(relative, ".."+string(filepath.Separator))) {
			return true
		}
	}
	return false
}

func ReadSecret32(path string) ([]byte, error) {
	data, err := readLicenseFile(path, 32, 32)
	if err != nil {
		clear(data)
		return nil, errors.New("read 32-byte secret file")
	}
	return data, nil
}

// ReadSharedSecret32 reads the one credential shared with the OTA admin. On
// Linux it is restricted to the process's single supplementary group rather
// than the license service's primary group.
func ReadSharedSecret32(path string) ([]byte, error) {
	data, err := readSharedFile(path, 32, 32)
	if err != nil {
		clear(data)
		return nil, errors.New("read shared 32-byte secret file")
	}
	return data, nil
}

// ReadLicenseFile applies the same runtime trust boundary used by the service
// configuration and license-only keys.
func ReadLicenseFile(path string, maximum int64) ([]byte, error) {
	if maximum < 1 {
		return nil, errors.New("trusted file size limit must be positive")
	}
	return readLicenseFile(path, 0, maximum)
}

func sharedFilePolicy(groups []int, exactSize, maximumSize int64) (trustedFilePolicy, error) {
	if len(groups) != 1 || groups[0] < 0 {
		return trustedFilePolicy{}, errors.New("exactly one supplementary group is required")
	}
	return trustedFilePolicy{
		allowedGIDs:  map[uint32]struct{}{uint32(groups[0]): {}},
		exactSize:    exactSize,
		maximumSize:  maximumSize,
		allowedModes: map[os.FileMode]struct{}{0o600: {}, 0o640: {}},
	}, nil
}
