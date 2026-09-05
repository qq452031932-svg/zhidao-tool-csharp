package config

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"reflect"
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
	info, err := os.Lstat(path)
	if err != nil {
		return Config{}, fmt.Errorf("stat config: %w", err)
	}
	if !info.Mode().IsRegular() {
		return Config{}, errors.New("config must be a regular file")
	}
	uid, ok := ownerUID(info.Sys())
	if !ok {
		return Config{}, errors.New("cannot verify root ownership of config")
	}
	if err := ValidateRootOnlyMetadata(uid, uint32(info.Mode().Perm())); err != nil {
		return Config{}, err
	}
	file, err := os.Open(path)
	if err != nil {
		return Config{}, fmt.Errorf("open config: %w", err)
	}
	defer file.Close()
	return Parse(file)
}

func (c Config) Validate() error {
	if strings.TrimSpace(c.DatabasePath) == "" || !isAbsolutePath(c.DatabasePath) {
		return errors.New("database_path must be absolute")
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

func ValidateRootOnlyMetadata(owner uint32, mode uint32) error {
	if owner != 0 {
		return errors.New("config must be owned by root")
	}
	permissions := mode & 0o777
	if permissions&0o077 != 0 || permissions&0o400 == 0 || permissions&0o100 != 0 {
		return errors.New("config permissions must be root-readable and inaccessible to group and others")
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

func ownerUID(value any) (uint32, bool) {
	reflected := reflect.ValueOf(value)
	if !reflected.IsValid() {
		return 0, false
	}
	if reflected.Kind() == reflect.Pointer {
		if reflected.IsNil() {
			return 0, false
		}
		reflected = reflected.Elem()
	}
	if reflected.Kind() != reflect.Struct {
		return 0, false
	}
	field := reflected.FieldByName("Uid")
	if !field.IsValid() || !field.CanUint() {
		return 0, false
	}
	return uint32(field.Uint()), true
}

func isAbsolutePath(path string) bool {
	return filepath.IsAbs(path) || strings.HasPrefix(path, "/")
}
