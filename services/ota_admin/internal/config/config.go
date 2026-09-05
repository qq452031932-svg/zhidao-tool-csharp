package config

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"path/filepath"
	"strings"
	"time"
)

const (
	MaxZIPBytes          int64 = 2 << 30
	MaxExpandedBytes     int64 = 8 << 30
	MaxSingleFileBytes   int64 = 2 << 30
	MaxArchiveEntries          = 256
	MaxCompressionRatio        = 100
	MaxCandidateLifetime       = 24 * time.Hour
	MaxSessionIdle             = 30 * time.Minute
	MaxSessionLifetime         = 8 * time.Hour
	MaxLoginFailures           = 5
	MaxFailureWindow           = 15 * time.Minute
	MaxFailureLock             = 15 * time.Minute
)

type Config struct {
	DatabasePath string `json:"database_path"`
	Limits       Limits `json:"limits"`
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
	cfg := Default()
	decoder := json.NewDecoder(reader)
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

func Load(path string) (Config, error) {
	file, err := openTrustedConfig(path)
	if err != nil {
		return Config{}, err
	}
	defer file.Close()
	return Parse(file)
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

func ValidateRootOnlyMetadata(owner, group, expectedServiceGroup uint32, mode uint32) error {
	if owner != 0 {
		return errors.New("config must be owned by root")
	}
	permissions := mode & 0o777
	// 0640 is intentionally supported: deployment can give the unprivileged
	// service's dedicated group read-only access to a root-owned configuration.
	if permissions&0o027 != 0 || permissions&0o400 == 0 || permissions&0o100 != 0 {
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
