package config_test

import (
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/config"
)

func TestCompiledHardMaxima(t *testing.T) {
	t.Parallel()

	if config.MaxZIPBytes != 2<<30 {
		t.Fatalf("MaxZIPBytes = %d, want %d", config.MaxZIPBytes, int64(2<<30))
	}
	if config.MaxExpandedBytes != 8<<30 {
		t.Fatalf("MaxExpandedBytes = %d, want %d", config.MaxExpandedBytes, int64(8<<30))
	}
	if config.MaxSingleFileBytes != 2<<30 {
		t.Fatalf("MaxSingleFileBytes = %d, want %d", config.MaxSingleFileBytes, int64(2<<30))
	}
	if config.MaxArchiveEntries != 256 || config.MaxCompressionRatio != 100 {
		t.Fatalf("archive maxima = (%d, %d), want (256, 100)", config.MaxArchiveEntries, config.MaxCompressionRatio)
	}
	if config.MaxCandidateLifetime != 24*time.Hour {
		t.Fatalf("MaxCandidateLifetime = %s, want 24h", config.MaxCandidateLifetime)
	}
	if config.MaxSessionIdle != 30*time.Minute || config.MaxSessionLifetime != 8*time.Hour {
		t.Fatalf("session maxima = (%s, %s), want (30m, 8h)", config.MaxSessionIdle, config.MaxSessionLifetime)
	}
	if config.MaxLoginFailures != 5 || config.MaxFailureWindow != 15*time.Minute || config.MaxFailureLock != 15*time.Minute {
		t.Fatalf("failure maxima = (%d, %s, %s), want (5, 15m, 15m)", config.MaxLoginFailures, config.MaxFailureWindow, config.MaxFailureLock)
	}
}

func TestParseDefaultsToHardMaxima(t *testing.T) {
	t.Parallel()

	cfg, err := config.Parse(strings.NewReader(`{"database_path":"/var/lib/superpan-ota-admin/state.db"}`))
	if err != nil {
		t.Fatalf("Parse() error = %v", err)
	}
	if cfg.Limits.ZIPBytes != config.MaxZIPBytes || cfg.Limits.ExpandedBytes != config.MaxExpandedBytes ||
		cfg.Limits.SingleFileBytes != config.MaxSingleFileBytes || cfg.Limits.ArchiveEntries != config.MaxArchiveEntries ||
		cfg.Limits.CompressionRatio != config.MaxCompressionRatio || cfg.Limits.CandidateLifetime != config.MaxCandidateLifetime ||
		cfg.Limits.SessionIdle != config.MaxSessionIdle || cfg.Limits.SessionLifetime != config.MaxSessionLifetime ||
		cfg.Limits.LoginFailures != config.MaxLoginFailures || cfg.Limits.FailureWindow != config.MaxFailureWindow ||
		cfg.Limits.FailureLock != config.MaxFailureLock {
		t.Fatalf("Parse() limits = %+v, want compiled maxima", cfg.Limits)
	}
}

func TestConfigRejectsAnyLimitAboveCompiledMaximum(t *testing.T) {
	t.Parallel()

	tests := []struct {
		name string
		edit func(*config.Config)
	}{
		{"zip bytes", func(c *config.Config) { c.Limits.ZIPBytes++ }},
		{"expanded bytes", func(c *config.Config) { c.Limits.ExpandedBytes++ }},
		{"single file bytes", func(c *config.Config) { c.Limits.SingleFileBytes++ }},
		{"archive entries", func(c *config.Config) { c.Limits.ArchiveEntries++ }},
		{"compression ratio", func(c *config.Config) { c.Limits.CompressionRatio++ }},
		{"candidate lifetime", func(c *config.Config) { c.Limits.CandidateLifetime++ }},
		{"session idle", func(c *config.Config) { c.Limits.SessionIdle++ }},
		{"session lifetime", func(c *config.Config) { c.Limits.SessionLifetime++ }},
		{"login failures", func(c *config.Config) { c.Limits.LoginFailures++ }},
		{"failure window", func(c *config.Config) { c.Limits.FailureWindow++ }},
		{"failure lock", func(c *config.Config) { c.Limits.FailureLock++ }},
	}
	for _, test := range tests {
		test := test
		t.Run(test.name, func(t *testing.T) {
			t.Parallel()
			cfg := config.Default()
			cfg.DatabasePath = "/var/lib/superpan-ota-admin/state.db"
			test.edit(&cfg)
			if err := cfg.Validate(); err == nil {
				t.Fatal("Validate() accepted a value above its compiled maximum")
			}
		})
	}
}

func TestConfigRejectsZeroLimitsAndIdleLongerThanAbsoluteLifetime(t *testing.T) {
	t.Parallel()

	cfg := config.Default()
	cfg.DatabasePath = "/var/lib/superpan-ota-admin/state.db"
	cfg.Limits.ZIPBytes = 0
	if err := cfg.Validate(); err == nil {
		t.Fatal("Validate() accepted a disabled ZIP limit")
	}

	cfg = config.Default()
	cfg.DatabasePath = "/var/lib/superpan-ota-admin/state.db"
	cfg.Limits.SessionLifetime = 10 * time.Minute
	if err := cfg.Validate(); err == nil {
		t.Fatal("Validate() accepted idle expiry after absolute expiry")
	}
}

func TestValidateRootOnlyMetadata(t *testing.T) {
	t.Parallel()

	if err := config.ValidateRootOnlyMetadata(0, 1000, 1000, 0o600); err != nil {
		t.Fatalf("ValidateRootOnlyMetadata(root, 0600) error = %v", err)
	}
	if err := config.ValidateRootOnlyMetadata(0, 1000, 1000, 0o640); err != nil {
		t.Fatalf("ValidateRootOnlyMetadata(root, 0640) error = %v; service group read must be supported", err)
	}
	for _, test := range []struct {
		name string
		uid  uint32
		mode uint32
	}{
		{"non-root owner", 1000, 0o600},
		{"group writable", 0, 0o660},
		{"world readable", 0, 0o604},
		{"world writable", 0, 0o602},
		{"executable", 0, 0o700},
		{"group executable", 0, 0o650},
	} {
		t.Run(test.name, func(t *testing.T) {
			t.Parallel()
			if err := config.ValidateRootOnlyMetadata(test.uid, 1000, 1000, test.mode); err == nil {
				t.Fatal("ValidateRootOnlyMetadata() accepted insecure metadata")
			}
		})
	}
	if err := config.ValidateRootOnlyMetadata(0, 2000, 1000, 0o640); err == nil {
		t.Fatal("ValidateRootOnlyMetadata accepted group-readable config owned by another group")
	}
}

func TestValidateTrustedParentMetadata(t *testing.T) {
	t.Parallel()
	if err := config.ValidateTrustedParentMetadata(0, false, 0o755); err != nil {
		t.Fatalf("ValidateTrustedParentMetadata(0755) error = %v", err)
	}
	for _, test := range []struct {
		name    string
		symlink bool
		mode    uint32
	}{
		{"symlink", true, 0o755},
		{"group writable", false, 0o775},
		{"world writable", false, 0o757},
	} {
		t.Run(test.name, func(t *testing.T) {
			t.Parallel()
			if err := config.ValidateTrustedParentMetadata(0, test.symlink, test.mode); err == nil {
				t.Fatal("ValidateTrustedParentMetadata() accepted unsafe parent")
			}
		})
	}
	if err := config.ValidateTrustedParentMetadata(1000, false, 0o755); err == nil {
		t.Fatal("ValidateTrustedParentMetadata accepted non-root ancestor")
	}
}

func TestConfigRejectsDatabaseDSNMetacharacters(t *testing.T) {
	t.Parallel()
	for _, path := range []string{"/var/lib/state.db?_pragma=foreign_keys(0)", "/var/lib/state.db#fragment", "file:/var/lib/state.db"} {
		cfg := config.Default()
		cfg.DatabasePath = path
		if err := cfg.Validate(); err == nil {
			t.Errorf("Validate() accepted database path %q", path)
		}
	}
}

func TestParseRejectsUnknownFields(t *testing.T) {
	t.Parallel()

	if _, err := config.Parse(strings.NewReader(`{"database_path":"/state.db","disable_limits":true}`)); err == nil {
		t.Fatal("Parse() accepted an unknown field")
	}
}
