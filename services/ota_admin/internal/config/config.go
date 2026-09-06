package config

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/url"
	"path/filepath"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

const (
	MaxZIPBytes           int64 = 2 << 30
	MaxExpandedBytes      int64 = 8 << 30
	MaxSingleFileBytes    int64 = 2 << 30
	MaxArchiveEntries           = 256
	MaxCompressionRatio         = 100
	MaxCandidateLifetime        = 24 * time.Hour
	MaxSessionIdle              = 30 * time.Minute
	MaxSessionLifetime          = 8 * time.Hour
	MaxLoginFailures            = 5
	MaxFailureWindow            = 15 * time.Minute
	MaxFailureLock              = 15 * time.Minute
	maxConfigurationBytes       = 64 << 10
)

type Config struct {
	DatabasePath             string `json:"database_path"`
	ListenAddress            string `json:"listen_address,omitempty"`
	PublicOrigin             string `json:"public_origin,omitempty"`
	StagingRoot              string `json:"staging_root,omitempty"`
	ReleaseRoot              string `json:"release_root,omitempty"`
	PublisherCertificatePath string `json:"publisher_certificate_path,omitempty"`
	PublisherSPKISHA256      string `json:"publisher_spki_sha256,omitempty"`
	SourceHMACKeyPath        string `json:"source_hmac_key_path,omitempty"`
	Limits                   Limits `json:"limits"`
}

type Limits struct {
	ZIPBytes          int64         `json:"zip_bytes"`
	ExpandedBytes     int64         `json:"expanded_bytes"`
	SingleFileBytes   int64         `json:"single_file_bytes"`
	ArchiveEntries    int           `json:"archive_entries"`
	CompressionRatio  int           `json:"compression_ratio"`
	CandidateLifetime time.Duration `json:"candidate_lifetime_ns"`
	SessionIdle       time.Duration `json:"session_idle_ns"`
	SessionLifetime   time.Duration `json:"session_lifetime_ns"`
	LoginFailures     int           `json:"login_failures"`
	FailureWindow     time.Duration `json:"failure_window_ns"`
	FailureLock       time.Duration `json:"failure_lock_ns"`
}

func Default() Config {
	return Config{Limits: Limits{
		ZIPBytes:          MaxZIPBytes,
		ExpandedBytes:     MaxExpandedBytes,
		SingleFileBytes:   MaxSingleFileBytes,
		ArchiveEntries:    MaxArchiveEntries,
		CompressionRatio:  MaxCompressionRatio,
		CandidateLifetime: MaxCandidateLifetime,
		SessionIdle:       MaxSessionIdle,
		SessionLifetime:   MaxSessionLifetime,
		LoginFailures:     MaxLoginFailures,
		FailureWindow:     MaxFailureWindow,
		FailureLock:       MaxFailureLock,
	}}
}

func Parse(reader io.Reader) (Config, error) {
	data, err := io.ReadAll(io.LimitReader(reader, maxConfigurationBytes+1))
	if err != nil {
		return Config{}, errors.New("read config")
	}
	if len(data) > maxConfigurationBytes {
		return Config{}, errors.New("config exceeds maximum size")
	}
	if !utf8.Valid(data) {
		return Config{}, errors.New("config must be valid UTF-8")
	}
	if err := validateConfigJSON(data); err != nil {
		return Config{}, err
	}
	cfg := Default()
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&cfg); err != nil {
		return Config{}, fmt.Errorf("decode config: %w", err)
	}
	if err := ensureSingleJSONValue(decoder); err != nil {
		return Config{}, err
	}
	if err := cfg.Validate(); err != nil {
		return Config{}, err
	}
	return cfg, nil
}

var topLevelConfigKeys = map[string]bool{
	"database_path": true, "listen_address": true, "public_origin": true,
	"staging_root": true, "release_root": true, "publisher_certificate_path": true,
	"publisher_spki_sha256": true, "source_hmac_key_path": true, "limits": true,
}

var limitConfigKeys = map[string]bool{
	"zip_bytes": true, "expanded_bytes": true, "single_file_bytes": true,
	"archive_entries": true, "compression_ratio": true, "candidate_lifetime_ns": true,
	"session_idle_ns": true, "session_lifetime_ns": true, "login_failures": true,
	"failure_window_ns": true, "failure_lock_ns": true,
}

func validateConfigJSON(data []byte) error {
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	if err := validateConfigObject(decoder, topLevelConfigKeys, true); err != nil {
		return err
	}
	if err := ensureSingleJSONValue(decoder); err != nil {
		return err
	}
	return nil
}

func validateConfigObject(decoder *json.Decoder, allowed map[string]bool, topLevel bool) error {
	token, err := decoder.Token()
	if err != nil || token != json.Delim('{') {
		return errors.New("config must be a JSON object")
	}
	seen := make(map[string]bool, len(allowed))
	seenFolded := make(map[string]bool, len(allowed))
	for decoder.More() {
		token, err := decoder.Token()
		key, ok := token.(string)
		if err != nil || !ok || !allowed[key] {
			return errors.New("config contains an unknown or misspelled field")
		}
		folded := strings.ToLower(key)
		if seen[key] || seenFolded[folded] {
			return errors.New("config contains duplicate fields")
		}
		seen[key], seenFolded[folded] = true, true
		if topLevel && key == "limits" {
			if err := validateConfigObject(decoder, limitConfigKeys, false); err != nil {
				return err
			}
			continue
		}
		var value json.RawMessage
		if err := decoder.Decode(&value); err != nil {
			return errors.New("config field value is invalid")
		}
	}
	closing, err := decoder.Token()
	if err != nil || closing != json.Delim('}') {
		return errors.New("config object is incomplete")
	}
	return nil
}

func Load(path string) (Config, error) {
	file, err := openTrustedConfig(path)
	if err != nil {
		return Config{}, err
	}
	defer file.Close()
	return Parse(file)
}

// ReadTrustedFile opens a small root-owned service resource with the same
// no-follow and metadata policy as the configuration file. It is intended for
// public trust material and service-local secret bytes, never user uploads.
func ReadTrustedFile(path string, maximumBytes int64) ([]byte, error) {
	if maximumBytes <= 0 {
		return nil, errors.New("trusted file size limit must be positive")
	}
	file, err := openTrustedConfig(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, maximumBytes+1))
	if err != nil {
		return nil, errors.New("read trusted service file")
	}
	if int64(len(data)) > maximumBytes {
		return nil, errors.New("trusted service file exceeds size limit")
	}
	return data, nil
}

func (c Config) Validate() error {
	if strings.TrimSpace(c.DatabasePath) == "" || !isAbsolutePath(c.DatabasePath) {
		return errors.New("database_path must be absolute")
	}
	if strings.HasPrefix(strings.ToLower(c.DatabasePath), "file:") || strings.ContainsAny(c.DatabasePath, "?#\x00") {
		return errors.New("database_path must be a literal path without URI or DSN metacharacters")
	}
	checks := []struct {
		name  string
		value int64
		max   int64
	}{
		{"zip_bytes", c.Limits.ZIPBytes, MaxZIPBytes},
		{"expanded_bytes", c.Limits.ExpandedBytes, MaxExpandedBytes},
		{"single_file_bytes", c.Limits.SingleFileBytes, MaxSingleFileBytes},
		{"archive_entries", int64(c.Limits.ArchiveEntries), MaxArchiveEntries},
		{"compression_ratio", int64(c.Limits.CompressionRatio), MaxCompressionRatio},
		{"candidate_lifetime_ns", int64(c.Limits.CandidateLifetime), int64(MaxCandidateLifetime)},
		{"session_idle_ns", int64(c.Limits.SessionIdle), int64(MaxSessionIdle)},
		{"session_lifetime_ns", int64(c.Limits.SessionLifetime), int64(MaxSessionLifetime)},
		{"login_failures", int64(c.Limits.LoginFailures), MaxLoginFailures},
		{"failure_window_ns", int64(c.Limits.FailureWindow), int64(MaxFailureWindow)},
		{"failure_lock_ns", int64(c.Limits.FailureLock), int64(MaxFailureLock)},
	}
	for _, check := range checks {
		if check.value <= 0 || check.value > check.max {
			return fmt.Errorf("%s must be between 1 and compiled maximum %d", check.name, check.max)
		}
	}
	if c.Limits.SessionIdle > c.Limits.SessionLifetime {
		return errors.New("session idle timeout cannot exceed absolute lifetime")
	}
	return nil
}

// ValidateServe applies the production-only configuration contract. Parse and
// Validate intentionally continue to accept the minimal database-only shape
// used by init-admin; the long-running service always calls this stricter gate
// before opening a listener or persistent state.
func (c Config) ValidateServe() error {
	if err := c.Validate(); err != nil {
		return err
	}
	host, portText, err := net.SplitHostPort(c.ListenAddress)
	if err != nil {
		return errors.New("listen_address must be an explicit loopback IP and port")
	}
	ip := net.ParseIP(host)
	port, portErr := strconv.ParseUint(portText, 10, 16)
	if ip == nil || !ip.IsLoopback() || portErr != nil || port == 0 {
		return errors.New("listen_address must be an explicit loopback IP and nonzero port")
	}
	origin, err := url.Parse(c.PublicOrigin)
	if err != nil || origin.Scheme != "https" || origin.Host != "ota.fanfh.cn" || origin.User != nil || origin.Path != "" || origin.RawPath != "" || origin.RawQuery != "" || origin.Fragment != "" || origin.String() != c.PublicOrigin {
		return errors.New("public_origin must be exactly https://ota.fanfh.cn")
	}
	paths := []struct {
		name  string
		value string
	}{
		{"staging_root", c.StagingRoot},
		{"release_root", c.ReleaseRoot},
		{"publisher_certificate_path", c.PublisherCertificatePath},
		{"source_hmac_key_path", c.SourceHMACKeyPath},
	}
	for _, item := range paths {
		if err := validateLiteralAbsolutePath(item.name, item.value); err != nil {
			return err
		}
	}
	if pathsOverlap(c.StagingRoot, c.ReleaseRoot) {
		return errors.New("staging_root and release_root must be disjoint")
	}
	for _, privatePath := range []struct {
		name string
		path string
	}{
		{"database_path", c.DatabasePath},
		{"publisher_certificate_path", c.PublisherCertificatePath},
		{"source_hmac_key_path", c.SourceHMACKeyPath},
	} {
		if pathWithin(c.ReleaseRoot, privatePath.path) {
			return fmt.Errorf("%s cannot be inside release_root", privatePath.name)
		}
	}
	if samePath(c.PublisherCertificatePath, c.SourceHMACKeyPath) {
		return errors.New("publisher certificate and source HMAC key must be different files")
	}
	if len(c.PublisherSPKISHA256) != 64 {
		return errors.New("publisher_spki_sha256 must be 64 lowercase hexadecimal characters")
	}
	for _, value := range c.PublisherSPKISHA256 {
		if !strings.ContainsRune("0123456789abcdef", value) {
			return errors.New("publisher_spki_sha256 must be 64 lowercase hexadecimal characters")
		}
	}
	return nil
}

func validateLiteralAbsolutePath(name, value string) error {
	if strings.TrimSpace(value) == "" || !isAbsolutePath(value) {
		return fmt.Errorf("%s must be absolute", name)
	}
	if strings.HasPrefix(strings.ToLower(value), "file:") || strings.ContainsAny(value, "?#\x00") {
		return fmt.Errorf("%s must be a literal path without URI metacharacters", name)
	}
	return nil
}

func samePath(left, right string) bool {
	return strings.EqualFold(filepath.Clean(left), filepath.Clean(right))
}

func pathsOverlap(left, right string) bool {
	return pathWithin(left, right) || pathWithin(right, left)
}

func pathWithin(parent, child string) bool {
	relative, err := filepath.Rel(filepath.Clean(parent), filepath.Clean(child))
	if err != nil {
		return false
	}
	return relative == "." || relative != ".." && !strings.HasPrefix(relative, ".."+string(filepath.Separator))
}

func ValidateRootOnlyMetadata(owner, group, expectedServiceGroup uint32, mode uint32) error {
	if owner != 0 {
		return errors.New("config must be owned by root")
	}
	permissions := mode & 0o777
	// 0640 is intentionally supported: deployment can give the unprivileged
	// service's dedicated group read-only access to a root-owned configuration.
	if permissions&0o026 != 0 || permissions&0o400 == 0 || permissions&0o111 != 0 {
		return errors.New("config must be root-readable, optionally group-readable, and never executable or group/other writable")
	}
	if permissions&0o040 != 0 && group != expectedServiceGroup {
		return errors.New("group-readable config must belong to the service's effective group")
	}
	return nil
}

func ValidateTrustedParentMetadata(owner uint32, symlink bool, mode uint32) error {
	if owner != 0 {
		return errors.New("config ancestors must be root-owned")
	}
	if symlink {
		return errors.New("config parent cannot be a symlink")
	}
	if mode&0o022 != 0 {
		return errors.New("config parent cannot be group or other writable")
	}
	return nil
}

func ensureSingleJSONValue(decoder *json.Decoder) error {
	var extra any
	if err := decoder.Decode(&extra); !errors.Is(err, io.EOF) {
		if err == nil {
			return errors.New("config contains multiple JSON values")
		}
		return fmt.Errorf("decode trailing config data: %w", err)
	}
	return nil
}

func isAbsolutePath(path string) bool {
	return filepath.IsAbs(path) || strings.HasPrefix(path, "/")
}
