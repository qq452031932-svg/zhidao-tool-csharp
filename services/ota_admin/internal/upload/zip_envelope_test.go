package upload

import (
	"errors"
	"fmt"
	"testing"
)

func TestEnvelopeMetadataAcceptsEveryInclusiveBoundary(t *testing.T) {
	limits := Limits{CompressedBytes: 200, ExpandedBytes: 800, SingleFileBytes: 200, Entries: 256, CompressionRatio: 100}
	entries := make([]entryHeader, 0, 256)
	for index := range 256 {
		entries = append(entries, entryHeader{
			Name:    fmt.Sprintf("SuperPan.BaiduBatchTransfer-1.0.%d-full.nupkg", index),
			Regular: true,
		})
	}
	// Exercise the independent byte ceilings at their inclusive boundaries.
	for index := range 4 {
		entries[index].CompressedSize = 2
		entries[index].UncompressedSize = 200
	}
	if err := validateHeaders(entries, 200, limits); err != nil {
		t.Fatalf("inclusive boundary rejected: %v", err)
	}
}

func TestEnvelopeMetadataRejectsEveryResourceLimitWithoutLargeAllocations(t *testing.T) {
	limits := Limits{CompressedBytes: 10, ExpandedBytes: 20, SingleFileBytes: 15, Entries: 2, CompressionRatio: 100}
	tests := []struct {
		name    string
		archive int64
		entries []entryHeader
		want    error
		limits  Limits
	}{
		{"compressed bytes", 11, []entryHeader{{Name: "upload-manifest.sha256", Regular: true}}, ErrCompressedLimit, Limits{}},
		{"declared compressed bytes", 10, []entryHeader{{Name: "upload-manifest.sha256", CompressedSize: 11, Regular: true}}, ErrCompressedLimit, Limits{}},
		{"expanded bytes", 10, []entryHeader{{Name: "upload-manifest.sha256", CompressedSize: 1, UncompressedSize: 11, Regular: true}, {Name: "upload-manifest.sha256.sig", CompressedSize: 1, UncompressedSize: 10, Regular: true}}, ErrExpandedLimit, Limits{}},
		{"single file", 10, []entryHeader{{Name: "upload-manifest.sha256", CompressedSize: 1, UncompressedSize: 16, Regular: true}}, ErrSingleFileLimit, Limits{}},
		{"entries", 10, []entryHeader{{Name: "upload-manifest.sha256", Regular: true}, {Name: "upload-manifest.sha256.sig", Regular: true}, {Name: "RELEASES-win-x64-stable", Regular: true}}, ErrEntryLimit, Limits{}},
		{"entry ratio", 10, []entryHeader{{Name: "upload-manifest.sha256", CompressedSize: 1, UncompressedSize: 3, Regular: true}}, ErrCompressionRatio, Limits{CompressedBytes: 10, ExpandedBytes: 20, SingleFileBytes: 15, Entries: 2, CompressionRatio: 2}},
		{"total ratio", 10, []entryHeader{{Name: "upload-manifest.sha256", CompressedSize: 1, UncompressedSize: 2, Regular: true}, {Name: "upload-manifest.sha256.sig", CompressedSize: 1, UncompressedSize: 3, Regular: true}}, ErrCompressionRatio, Limits{CompressedBytes: 10, ExpandedBytes: 20, SingleFileBytes: 15, Entries: 2, CompressionRatio: 2}},
		{"zero compressed nonempty", 10, []entryHeader{{Name: "upload-manifest.sha256", CompressedSize: 0, UncompressedSize: 1, Regular: true}}, ErrCompressionRatio, Limits{}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			activeLimits := test.limits
			if activeLimits == (Limits{}) {
				activeLimits = limits
			}
			if err := validateHeaders(test.entries, test.archive, activeLimits); !errors.Is(err, test.want) {
				t.Fatalf("error=%v want=%v", err, test.want)
			}
		})
	}
}

func TestEnvelopeRejectsUnsafeUnknownAndCollidingNames(t *testing.T) {
	unsafeNames := []string{
		"", ".", "..", "../evil", "a/evil", `a\\evil`, "/absolute", `\\\\server\\share`,
		`C:\\evil`, "C:evil", "releases.win-x64-stable.json/", "trailing. ", "CON",
		"r\u00e9leases.win-x64-stable.json", "releases\uff0ewin-x64-stable.json", "unknown.bin",
		"SuperPan.BaiduBatchTransfer-01.2.3-full.nupkg", "SuperPan.BaiduBatchTransfer-65535.0.0-full.nupkg",
	}
	for _, name := range unsafeNames {
		t.Run(fmt.Sprintf("%q", name), func(t *testing.T) {
			if err := validateEntryName(name); !errors.Is(err, ErrUnsafeName) && !errors.Is(err, ErrUnknownName) {
				t.Fatalf("name %q error=%v", name, err)
			}
		})
	}

	for _, entries := range [][]entryHeader{
		{{Name: "upload-manifest.sha256", Regular: true}, {Name: "upload-manifest.sha256", Regular: true}},
		{{Name: "upload-manifest.sha256", Regular: true}, {Name: "UPLOAD-MANIFEST.SHA256", Regular: true}},
	} {
		if err := validateHeaders(entries, 1, DefaultLimits()); !errors.Is(err, ErrDuplicateName) {
			t.Fatalf("collision error=%v", err)
		}
	}
}

func TestEnvelopeRejectsDirectoriesSymlinksAndNonRegularModes(t *testing.T) {
	for _, entry := range []entryHeader{
		{Name: "upload-manifest.sha256", Directory: true},
		{Name: "upload-manifest.sha256", Symlink: true},
		{Name: "upload-manifest.sha256", Regular: false},
		{Name: "upload-manifest.sha256", Regular: true, Encrypted: true},
	} {
		if err := validateHeaders([]entryHeader{entry}, 1, DefaultLimits()); !errors.Is(err, ErrUnsafeEntry) {
			t.Fatalf("entry=%#v error=%v", entry, err)
		}
	}
}
