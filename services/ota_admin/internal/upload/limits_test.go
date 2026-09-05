package upload

import (
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/config"
)

func TestDefaultLimitsMatchCompiledConfigurationMaxima(t *testing.T) {
	limits := DefaultLimits()
	if limits.CompressedBytes != config.MaxZIPBytes || limits.ExpandedBytes != config.MaxExpandedBytes ||
		limits.SingleFileBytes != config.MaxSingleFileBytes || limits.Entries != config.MaxArchiveEntries ||
		limits.CompressionRatio != config.MaxCompressionRatio {
		t.Fatalf("unexpected defaults: %#v", limits)
	}
	if err := limits.Validate(); err != nil {
		t.Fatalf("default limits rejected: %v", err)
	}
}

func TestLimitsCannotBeDisabledOrRaisedAboveCompiledMaxima(t *testing.T) {
	base := DefaultLimits()
	tests := []struct {
		name   string
		mutate func(*Limits)
	}{
		{"zero compressed", func(value *Limits) { value.CompressedBytes = 0 }},
		{"raised compressed", func(value *Limits) { value.CompressedBytes++ }},
		{"zero expanded", func(value *Limits) { value.ExpandedBytes = 0 }},
		{"raised expanded", func(value *Limits) { value.ExpandedBytes++ }},
		{"zero file", func(value *Limits) { value.SingleFileBytes = 0 }},
		{"raised file", func(value *Limits) { value.SingleFileBytes++ }},
		{"zero entries", func(value *Limits) { value.Entries = 0 }},
		{"raised entries", func(value *Limits) { value.Entries++ }},
		{"zero ratio", func(value *Limits) { value.CompressionRatio = 0 }},
		{"raised ratio", func(value *Limits) { value.CompressionRatio++ }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			limits := base
			test.mutate(&limits)
			if err := limits.Validate(); err == nil {
				t.Fatalf("invalid limits accepted: %#v", limits)
			}
		})
	}
}
