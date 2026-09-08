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
	MaxBodyBytes             int64 `json:"max_body_bytes"`
	MaxHeaderBytes           int   `json:"max_header_bytes"`
	TimestampWindowSeconds   int   `json:"timestamp_window_seconds"`
	NonceLifetimeSeconds     int   `json:"nonce_lifetime_seconds"`
	MaxReplayNonces          int   `json:"max_replay_nonces"`
	ActivationRequests       int   `json:"activation_requests"`
	ValidationRequests       int   `json:"validation_requests"`
	RateWindowSeconds        int   `json:"rate_window_seconds"`
	ReadHeaderTimeoutSeconds int   `json:"read_header_timeout_seconds"`
	ReadTimeoutSeconds       int   `json:"read_timeout_seconds"`
	WriteTimeoutSeconds      int   `json:"write_timeout_seconds"`
	IdleTimeoutSeconds       int   `json:"idle_timeout_seconds"`
	ShutdownTimeoutSeconds   int   `json:"shutdown_timeout_seconds"`
}

type Config struct {
	ListenAddress         string `json:"listen_address"`
	LicenseDatabasePath   string `json:"license_database_path"`
	EncryptionKeyPath     string `json:"encryption_key_path"`
	LookupHMACKeyPath     string `json:"lookup_hmac_key_path"`
	SigningPrivateKeyPath string `json:"signing_private_key_path"`
	InternalSecretPath    string `json:"internal_secret_path"`
	SigningKeyID          string `json:"signing_key_id"`
	PublicAPIBasePath     string `json:"public_api_base_path"`
	Limits                Limits `json:"limits"`
}

func Load(path string) (Config, error) {
	if strings.TrimSpace(path) == "" {
		return Config{}, errors.New("configuration path is required")
	}
	file, err := os.Open(path)
	if err != nil {
		return Config{}, errors.New("open license service configuration")
	}
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, maximumConfigBytes+1))
	if err != nil || len(data) > maximumConfigBytes {
		return Config{}, errors.New("read license service configuration")
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
	if err := cfg.Validate(); err != nil {
		return Config{}, err
	}
	return cfg, nil
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
	paths := []string{c.LicenseDatabasePath, c.EncryptionKeyPath, c.LookupHMACKeyPath, c.SigningPrivateKeyPath, c.InternalSecretPath}
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
	if c.Limits.ActivationRequests < 1 || c.Limits.ActivationRequests > 10000 || c.Limits.ValidationRequests < 1 || c.Limits.ValidationRequests > 10000 {
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
	info, err := os.Stat(path)
	if err != nil || !info.Mode().IsRegular() || info.Size() != 32 {
		return nil, errors.New("secret file must contain exactly 32 raw bytes")
	}
	if runtime.GOOS != "windows" && info.Mode().Perm()&0o077 != 0 {
		return nil, errors.New("secret file permissions are too broad")
	}
	data, err := os.ReadFile(path)
	if err != nil || len(data) != 32 {
		clear(data)
		return nil, errors.New("read 32-byte secret file")
	}
	return data, nil
}
