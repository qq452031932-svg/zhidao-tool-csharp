package upload

import (
	"errors"
	"fmt"

	"fanfh.cn/superpan/ota-admin/internal/config"
)

var (
	ErrCompressedLimit  = errors.New("compressed upload limit exceeded")
	ErrExpandedLimit    = errors.New("expanded upload limit exceeded")
	ErrSingleFileLimit  = errors.New("single file limit exceeded")
	ErrEntryLimit       = errors.New("archive entry limit exceeded")
	ErrCompressionRatio = errors.New("compression ratio limit exceeded")
	ErrUnsafeName       = errors.New("unsafe archive entry name")
	ErrUnknownName      = errors.New("unknown archive entry name")
	ErrDuplicateName    = errors.New("duplicate archive entry name")
	ErrUnsafeEntry      = errors.New("unsafe archive entry type")
	ErrInvalidArchive   = errors.New("invalid ZIP archive")
	ErrAmbiguousEOCD    = errors.New("ambiguous ZIP end records")
	ErrUnsupportedZIP64 = errors.New("ZIP64 archives are not accepted")
	ErrInsufficientDisk = errors.New("insufficient disk space")
)

type Limits struct {
	CompressedBytes  int64
	ExpandedBytes    int64
	SingleFileBytes  int64
	Entries          int
	CompressionRatio int
}

func DefaultLimits() Limits {
	return Limits{
		CompressedBytes:  config.MaxZIPBytes,
		ExpandedBytes:    config.MaxExpandedBytes,
		SingleFileBytes:  config.MaxSingleFileBytes,
		Entries:          config.MaxArchiveEntries,
		CompressionRatio: config.MaxCompressionRatio,
	}
}

func LimitsFromConfig(configured config.Limits) Limits {
	return Limits{
		CompressedBytes:  configured.ZIPBytes,
		ExpandedBytes:    configured.ExpandedBytes,
		SingleFileBytes:  configured.SingleFileBytes,
		Entries:          configured.ArchiveEntries,
		CompressionRatio: configured.CompressionRatio,
	}
}

func (limits Limits) Validate() error {
	checks := []struct {
		name  string
		value int64
		max   int64
	}{
		{"compressed bytes", limits.CompressedBytes, config.MaxZIPBytes},
		{"expanded bytes", limits.ExpandedBytes, config.MaxExpandedBytes},
		{"single file bytes", limits.SingleFileBytes, config.MaxSingleFileBytes},
		{"entries", int64(limits.Entries), config.MaxArchiveEntries},
		{"compression ratio", int64(limits.CompressionRatio), config.MaxCompressionRatio},
	}
	for _, check := range checks {
		if check.value <= 0 || check.value > check.max {
			return fmt.Errorf("%s must be between 1 and %d", check.name, check.max)
		}
	}
	return nil
}
