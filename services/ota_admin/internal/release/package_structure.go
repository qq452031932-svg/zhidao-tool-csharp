package release

import (
	"archive/zip"
	"bytes"
	"encoding/binary"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"
	"unicode/utf8"
)

const (
	maxInnerEntries  = 4096
	maxInnerExpanded = int64(8 << 30)
	maxInnerEntry    = int64(2 << 30)
	maxNuspecBytes   = int64(1 << 20)
	nuspecNamespace  = "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd"
)

type nuspecDocument struct {
	XMLName  xml.Name         `xml:"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd package"`
	Metadata []nuspecMetadata `xml:"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd metadata"`
}

type nuspecMetadata struct {
	ID                  []string `xml:"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd id"`
	Version             []string `xml:"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd version"`
	Channel             []string `xml:"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd channel"`
	MainExecutable      []string `xml:"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd mainExe"`
	OS                  []string `xml:"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd os"`
	RID                 []string `xml:"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd rid"`
	MachineArchitecture []string `xml:"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd machineArchitecture"`
}

type innerArchive struct {
	archive *zip.Reader
	entries map[string]*zip.File
}

func validateSetupStructure(file *verifiedFile) error {
	header := make([]byte, 64)
	if _, err := file.file.ReadAt(header, 0); err != nil || header[0] != 'M' || header[1] != 'Z' {
		return errors.New("Setup is not a PE image")
	}
	peOffset := int64(binary.LittleEndian.Uint32(header[0x3c:]))
	if peOffset < 64 || peOffset > file.size-6 || peOffset > 1<<20 {
		return errors.New("Setup PE header offset is invalid")
	}
	pe := make([]byte, 6)
	if _, err := file.file.ReadAt(pe, peOffset); err != nil || !bytes.Equal(pe[:4], []byte{'P', 'E', 0, 0}) {
		return errors.New("Setup PE signature is invalid")
	}
	machine := binary.LittleEndian.Uint16(pe[4:])
	if machine != 0x014c && machine != 0x8664 {
		return errors.New("Setup PE machine is not Windows x86/x64")
	}
	return nil
}

func validatePackageStructure(file *verifiedFile, expectedVersion, expectedType string) error {
	inner, err := openInnerArchive(file)
	if err != nil {
		return err
	}
	for _, required := range []string{"[Content_Types].xml", "_rels/.rels", packageID + ".nuspec"} {
		if _, ok := inner.entries[required]; !ok {
			return fmt.Errorf("package is missing %s", required)
		}
	}
	if err := validateNuspec(inner.entries[packageID+".nuspec"], expectedVersion); err != nil {
		return err
	}
	if expectedType == "Full" {
		for _, required := range []string{
			"lib/app/BaiduBatchTransfer.App.dll",
			"lib/app/BaiduBatchTransfer.App.deps.json",
			"lib/app/BaiduBatchTransfer.App.runtimeconfig.json",
		} {
			if _, ok := inner.entries[required]; !ok {
				return fmt.Errorf("Full package is missing %s", required)
			}
		}
		return nil
	}
	patchBase := "lib/app/BaiduBatchTransfer.App.exe"
	_, diff := inner.entries[patchBase+".diff"]
	_, zsdiff := inner.entries[patchBase+".zsdiff"]
	if (!diff && !zsdiff) || inner.entries[patchBase+".shasum"] == nil {
		return errors.New("Delta package has no main executable patch and shasum")
	}
	for name := range inner.entries {
		base := ""
		if strings.HasPrefix(name, "lib/app/") && strings.HasSuffix(name, ".zsdiff") {
			base = strings.TrimSuffix(name, ".zsdiff")
		} else if strings.HasPrefix(name, "lib/app/") && strings.HasSuffix(name, ".diff") {
			base = strings.TrimSuffix(name, ".diff")
		}
		if base != "" && inner.entries[base+".shasum"] == nil {
			return fmt.Errorf("Delta patch has no shasum: %s", name)
		}
	}
	return nil
}

func validatePortableStructure(file *verifiedFile, expectedVersion string) error {
	inner, err := openInnerArchive(file)
	if err != nil {
		return err
	}
	for _, required := range []string{
		".portable",
		"current/sq.version",
		"current/BaiduBatchTransfer.App.dll",
		"current/BaiduBatchTransfer.App.deps.json",
		"current/BaiduBatchTransfer.App.runtimeconfig.json",
	} {
		if _, ok := inner.entries[required]; !ok {
			return fmt.Errorf("Portable archive is missing %s", required)
		}
	}
	return validateNuspec(inner.entries["current/sq.version"], expectedVersion)
}

func openInnerArchive(file *verifiedFile) (*innerArchive, error) {
	archive, err := zip.NewReader(file.file, file.size)
	if err != nil || len(archive.File) == 0 || len(archive.File) > maxInnerEntries {
		return nil, errors.New("inner package is not a bounded ZIP archive")
	}
	entries := make(map[string]*zip.File, len(archive.File))
	folded := make(map[string]struct{}, len(archive.File))
	var expanded int64
	var compressed int64
	for _, entry := range archive.File {
		if !validInnerWindowsPath(entry.Name) || entry.FileInfo().IsDir() || entry.Mode()&os.ModeSymlink != 0 {
			return nil, fmt.Errorf("inner package has unsafe path %q", entry.Name)
		}
		foldedName := strings.ToLower(entry.Name)
		if _, duplicate := folded[foldedName]; duplicate {
			return nil, errors.New("inner package has duplicate or case-colliding paths")
		}
		if entry.UncompressedSize64 > uint64(maxInnerEntry) || entry.UncompressedSize64 > uint64(^uint64(0)>>1) {
			return nil, errors.New("inner package entry is oversized")
		}
		size := int64(entry.UncompressedSize64)
		if size > maxInnerExpanded-expanded {
			return nil, errors.New("inner package expanded size is excessive")
		}
		expanded += size
		compressedSize := int64(entry.CompressedSize64)
		if size > 0 && (compressedSize <= 0 || float64(size)/float64(compressedSize) > 100) {
			return nil, errors.New("inner package compression ratio is excessive")
		}
		if compressedSize > maxInnerExpanded-compressed {
			return nil, errors.New("inner package compressed size is excessive")
		}
		compressed += compressedSize
		folded[foldedName] = struct{}{}
		entries[entry.Name] = entry
	}
	if compressed > 0 && float64(expanded)/float64(compressed) > 100 {
		return nil, errors.New("inner package aggregate compression ratio is excessive")
	}
	return &innerArchive{archive: archive, entries: entries}, nil
}

func validInnerWindowsPath(name string) bool {
	if name == "" || len(name) > 1024 || !utf8.ValidString(name) || strings.HasPrefix(name, "/") || strings.HasPrefix(name, `\`) ||
		strings.Contains(name, `\`) || strings.ContainsRune(name, 0) || (len(name) >= 2 && name[1] == ':' && isASCIIAlpha(name[0])) {
		return false
	}
	for _, part := range strings.Split(name, "/") {
		if part == "" || part == "." || part == ".." || len(part) > 255 || strings.HasSuffix(part, " ") || strings.HasSuffix(part, ".") ||
			strings.ContainsAny(part, `<>:"|?*`) {
			return false
		}
		for _, character := range part {
			if character < 0x20 || character == 0x7f {
				return false
			}
		}
		stem := strings.ToUpper(strings.SplitN(part, ".", 2)[0])
		switch stem {
		case "CON", "PRN", "AUX", "NUL", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9":
			return false
		}
	}
	return len(name) <= 240
}

func isASCIIAlpha(value byte) bool {
	return value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z'
}

func validateNuspec(entry *zip.File, expectedVersion string) error {
	data, err := readZipEntry(entry, maxNuspecBytes)
	if err != nil {
		return errors.New("package metadata is oversized or unreadable")
	}
	tokens := xml.NewDecoder(bytes.NewReader(data))
	for {
		token, tokenErr := tokens.Token()
		if errors.Is(tokenErr, io.EOF) {
			break
		}
		if tokenErr != nil {
			return errors.New("package metadata XML is invalid")
		}
		if _, directive := token.(xml.Directive); directive {
			return errors.New("package metadata XML directives are forbidden")
		}
	}
	var document nuspecDocument
	decoder := xml.NewDecoder(bytes.NewReader(data))
	decoder.Strict = true
	if err := decoder.Decode(&document); err != nil || document.XMLName.Local != "package" || document.XMLName.Space != nuspecNamespace || len(document.Metadata) != 1 {
		return errors.New("package metadata identity is invalid")
	}
	metadata := document.Metadata[0]
	if !exactSingle(metadata.ID, packageID) || !exactSingle(metadata.Version, expectedVersion) || !exactSingle(metadata.Channel, channel) ||
		!exactSingle(metadata.MainExecutable, "BaiduBatchTransfer.App.exe") || !exactSingle(metadata.OS, "win") || !exactSingle(metadata.RID, "win-x64") {
		return errors.New("package metadata identity, version, channel, or RID differs")
	}
	if len(metadata.MachineArchitecture) > 1 || len(metadata.MachineArchitecture) == 1 && metadata.MachineArchitecture[0] != "x64" {
		return errors.New("package metadata machine architecture differs")
	}
	return nil
}

func exactSingle(values []string, expected string) bool {
	return len(values) == 1 && values[0] == expected
}

func readZipEntry(entry *zip.File, limit int64) ([]byte, error) {
	if entry == nil || int64(entry.UncompressedSize64) > limit {
		return nil, errors.New("ZIP entry exceeds limit")
	}
	stream, err := entry.Open()
	if err != nil {
		return nil, err
	}
	defer stream.Close()
	data, err := io.ReadAll(io.LimitReader(stream, limit+1))
	if err != nil || int64(len(data)) > limit {
		return nil, errors.New("ZIP entry exceeds limit")
	}
	return data, nil
}
