package upload

import (
	"archive/zip"
	"fmt"
	"math"
	"os"
	"regexp"
	"strconv"
	"strings"
)

const (
	packageID = "SuperPan.BaiduBatchTransfer"
	channel   = "win-x64-stable"
)

var versionedPackageName = regexp.MustCompile(`^SuperPan\.BaiduBatchTransfer-(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})-(full|delta)\.nupkg$`)

var fixedNames = map[string]struct{}{
	"upload-manifest.sha256":        {},
	"upload-manifest.sha256.sig":    {},
	"releases." + channel + ".json": {},
	"assets." + channel + ".json":   {},
	"RELEASES-" + channel:           {},
	packageID + "-Setup.exe":        {},
	packageID + "-Portable.zip":     {},
}

type entryHeader struct {
	Name             string
	CompressedSize   uint64
	UncompressedSize uint64
	Directory        bool
	Symlink          bool
	Regular          bool
	Encrypted        bool
}

func headerFor(file *zip.File) entryHeader {
	mode := file.Mode()
	return entryHeader{
		Name:             file.Name,
		CompressedSize:   file.CompressedSize64,
		UncompressedSize: file.UncompressedSize64,
		Directory:        file.FileInfo().IsDir() || strings.HasSuffix(file.Name, "/") || strings.HasSuffix(file.Name, `\`),
		Symlink:          mode&os.ModeSymlink != 0,
		Regular:          mode.IsRegular(),
		Encrypted:        file.Flags&1 != 0,
	}
}

func validateHeaders(entries []entryHeader, archiveBytes int64, actualCompressed uint64, limits Limits) error {
	if err := limits.Validate(); err != nil {
		return err
	}
	if archiveBytes < 0 || archiveBytes > limits.CompressedBytes {
		return ErrCompressedLimit
	}
	if len(entries) == 0 {
		return fmt.Errorf("%w: archive contains no files", ErrInvalidArchive)
	}
	if len(entries) > limits.Entries {
		return ErrEntryLimit
	}

	seen := make(map[string]string, len(entries))
	var totalCompressed uint64
	var totalExpanded uint64
	for _, entry := range entries {
		if entry.Directory || entry.Symlink || !entry.Regular || entry.Encrypted {
			return fmt.Errorf("%w: %q", ErrUnsafeEntry, entry.Name)
		}
		folded := strings.ToLower(entry.Name)
		if previous, duplicate := seen[folded]; duplicate {
			return fmt.Errorf("%w: %q collides with %q", ErrDuplicateName, entry.Name, previous)
		}
		seen[folded] = entry.Name
		if err := validateEntryName(entry.Name); err != nil {
			return err
		}

		if entry.UncompressedSize > uint64(limits.SingleFileBytes) {
			return fmt.Errorf("%w: %q", ErrSingleFileLimit, entry.Name)
		}
		if entry.CompressedSize > uint64(limits.CompressedBytes) || totalCompressed > uint64(limits.CompressedBytes)-entry.CompressedSize {
			return ErrCompressedLimit
		}
		if exceedsRatio(entry.UncompressedSize, entry.CompressedSize, uint64(limits.CompressionRatio)) {
			return fmt.Errorf("%w: %q", ErrCompressionRatio, entry.Name)
		}
		if math.MaxUint64-totalCompressed < entry.CompressedSize || math.MaxUint64-totalExpanded < entry.UncompressedSize {
			return ErrExpandedLimit
		}
		totalCompressed += entry.CompressedSize
		totalExpanded += entry.UncompressedSize
		if totalExpanded > uint64(limits.ExpandedBytes) {
			return ErrExpandedLimit
		}
	}
	if actualCompressed != totalCompressed {
		return fmt.Errorf("%w: compressed byte accounting mismatch", ErrInvalidArchive)
	}
	if exceedsRatio(totalExpanded, actualCompressed, uint64(limits.CompressionRatio)) {
		return ErrCompressionRatio
	}
	return nil
}

func exceedsRatio(expanded, compressed, ratio uint64) bool {
	if expanded == 0 {
		return false
	}
	if compressed == 0 {
		return true
	}
	return expanded/compressed > ratio || (expanded/compressed == ratio && expanded%compressed != 0)
}

func validateEntryName(name string) error {
	if name == "" || len(name) > 255 || name == "." || name == ".." || strings.ContainsAny(name, `/\\`) ||
		strings.HasSuffix(name, " ") || strings.HasSuffix(name, ".") {
		return fmt.Errorf("%w: %q", ErrUnsafeName, name)
	}
	for _, character := range []byte(name) {
		if character < 0x21 || character > 0x7e {
			return fmt.Errorf("%w: %q", ErrUnsafeName, name)
		}
	}
	if strings.ContainsAny(name, `<>:"|?*`) {
		return fmt.Errorf("%w: %q", ErrUnsafeName, name)
	}
	stem := strings.ToUpper(strings.SplitN(name, ".", 2)[0])
	if isReservedDOSName(stem) {
		return fmt.Errorf("%w: %q", ErrUnsafeName, name)
	}
	if _, ok := fixedNames[name]; ok {
		return nil
	}
	matches := versionedPackageName.FindStringSubmatch(name)
	if matches == nil {
		return fmt.Errorf("%w: %q", ErrUnknownName, name)
	}
	for _, component := range matches[1:4] {
		value, err := strconv.Atoi(component)
		if err != nil || value > 65534 {
			return fmt.Errorf("%w: %q", ErrUnknownName, name)
		}
	}
	return nil
}

func isReservedDOSName(stem string) bool {
	switch stem {
	case "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9":
		return true
	default:
		return false
	}
}
