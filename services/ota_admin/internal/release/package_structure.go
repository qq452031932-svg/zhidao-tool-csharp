package release

import (
	"archive/zip"
	"bytes"
	"encoding/binary"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"strings"
	"unicode/utf16"
	"unicode/utf8"

	"golang.org/x/text/unicode/norm"
)

const (
	maxInnerEntries  = 4096
	maxInnerExpanded = int64(8 << 30)
	maxInnerEntry    = int64(2 << 30)
	maxInnerCentral  = uint64(64 << 20)
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
	if err := validateNuspec(inner.entries[packageID+".nuspec"], expectedVersion, false); err != nil {
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
		return validateExecutableSet(inner, []string{
			"lib/app/BaiduBatchTransfer.App_ExecutionStub.exe",
			"lib/app/BaiduBatchTransfer.App.exe",
			"lib/app/Squirrel.exe",
		}, []string{
			"lib/app/managed-browser-helper/python.exe",
			"lib/app/managed-browser-helper/pythonw.exe",
		})
	}
	if err := validateExecutableSet(inner, nil, nil); err != nil {
		return err
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
	if err := validateNuspec(inner.entries["current/sq.version"], expectedVersion, true); err != nil {
		return err
	}
	return validateExecutableSet(inner, []string{
		packageID + ".exe",
		"Update.exe",
		"current/BaiduBatchTransfer.App.exe",
	}, []string{
		"current/managed-browser-helper/python.exe",
		"current/managed-browser-helper/pythonw.exe",
	})
}

func validateExecutableSet(inner *innerArchive, required, allowed []string) error {
	// The signed manifest is the server-side authenticity root. Windows clients
	// separately enforce Authenticode before executing these role-bound files.
	expected := make(map[string]struct{}, len(required)+len(allowed))
	for _, name := range required {
		entry, ok := inner.entries[name]
		if !ok || entry.UncompressedSize64 == 0 {
			return fmt.Errorf("package is missing required publisher executable %s", name)
		}
		expected[name] = struct{}{}
	}
	for _, name := range allowed {
		expected[name] = struct{}{}
	}
	for name := range inner.entries {
		if !strings.HasSuffix(strings.ToLower(name), ".exe") {
			continue
		}
		if _, ok := expected[name]; !ok {
			return fmt.Errorf("package contains unexpected executable %s", name)
		}
	}
	return nil
}

func preflightInnerArchive(reader io.ReaderAt, size int64) (uint64, error) {
	if reader == nil || size < 22 {
		return 0, errors.New("inner package has no valid ZIP end record")
	}
	tailSize := size
	if tailSize > 22+math.MaxUint16 {
		tailSize = 22 + math.MaxUint16
	}
	tail := make([]byte, int(tailSize))
	if _, err := reader.ReadAt(tail, size-tailSize); err != nil {
		return 0, errors.New("inner package ZIP end record is unreadable")
	}
	eocdIndex := -1
	for index := len(tail) - 22; index >= 0; index-- {
		if binary.LittleEndian.Uint32(tail[index:]) == 0x06054b50 &&
			index+22+int(binary.LittleEndian.Uint16(tail[index+20:])) == len(tail) {
			eocdIndex = index
			break
		}
	}
	if eocdIndex < 0 {
		return 0, errors.New("inner package has no valid ZIP end record")
	}
	eocd := tail[eocdIndex : eocdIndex+22]
	if binary.LittleEndian.Uint16(eocd[4:]) != 0 || binary.LittleEndian.Uint16(eocd[6:]) != 0 {
		return 0, errors.New("inner package uses a multi-disk ZIP")
	}
	eocdOffset := uint64(size-tailSize) + uint64(eocdIndex)
	entryCount := uint64(binary.LittleEndian.Uint16(eocd[10:]))
	centralSize := uint64(binary.LittleEndian.Uint32(eocd[12:]))
	centralOffset := uint64(binary.LittleEndian.Uint32(eocd[16:]))
	zip64 := binary.LittleEndian.Uint16(eocd[8:]) == math.MaxUint16 ||
		binary.LittleEndian.Uint16(eocd[10:]) == math.MaxUint16 ||
		binary.LittleEndian.Uint32(eocd[12:]) == math.MaxUint32 ||
		binary.LittleEndian.Uint32(eocd[16:]) == math.MaxUint32
	centralTerminus := eocdOffset
	if zip64 {
		if eocdOffset < 20 {
			return 0, errors.New("inner package ZIP64 locator is missing")
		}
		locator := make([]byte, 20)
		if _, err := reader.ReadAt(locator, int64(eocdOffset-20)); err != nil || binary.LittleEndian.Uint32(locator) != 0x07064b50 ||
			binary.LittleEndian.Uint32(locator[4:]) != 0 || binary.LittleEndian.Uint32(locator[16:]) != 1 {
			return 0, errors.New("inner package ZIP64 locator is invalid")
		}
		zip64Offset := binary.LittleEndian.Uint64(locator[8:])
		if zip64Offset > math.MaxInt64 || zip64Offset+56 < zip64Offset || zip64Offset+56 > eocdOffset-20 {
			return 0, errors.New("inner package ZIP64 end record offset is invalid")
		}
		zip64End := make([]byte, 56)
		if _, err := reader.ReadAt(zip64End, int64(zip64Offset)); err != nil || binary.LittleEndian.Uint32(zip64End) != 0x06064b50 ||
			binary.LittleEndian.Uint64(zip64End[4:]) < 44 || binary.LittleEndian.Uint32(zip64End[16:]) != 0 || binary.LittleEndian.Uint32(zip64End[20:]) != 0 {
			return 0, errors.New("inner package ZIP64 end record is invalid")
		}
		recordSize := binary.LittleEndian.Uint64(zip64End[4:])
		if recordSize > math.MaxInt64 || zip64Offset+12 < zip64Offset || zip64Offset+12+recordSize < zip64Offset+12 ||
			zip64Offset+12+recordSize != eocdOffset-20 {
			return 0, errors.New("inner package ZIP64 end record size is invalid")
		}
		if binary.LittleEndian.Uint64(zip64End[24:]) != binary.LittleEndian.Uint64(zip64End[32:]) {
			return 0, errors.New("inner package ZIP64 entry counts differ")
		}
		entryCount = binary.LittleEndian.Uint64(zip64End[32:])
		centralSize = binary.LittleEndian.Uint64(zip64End[40:])
		centralOffset = binary.LittleEndian.Uint64(zip64End[48:])
		centralTerminus = zip64Offset
	} else if binary.LittleEndian.Uint16(eocd[8:]) != binary.LittleEndian.Uint16(eocd[10:]) {
		return 0, errors.New("inner package ZIP entry counts differ")
	}
	if entryCount == 0 || entryCount > maxInnerEntries {
		return 0, errors.New("inner package entry count exceeds the limit")
	}
	if centralOffset > math.MaxInt64 || centralSize > math.MaxInt64 || centralSize > maxInnerCentral ||
		centralOffset+centralSize < centralOffset || centralOffset+centralSize != centralTerminus {
		return 0, errors.New("inner package central directory is invalid or excessive")
	}
	if err := preflightCentralDirectory(reader, centralOffset, centralSize, entryCount); err != nil {
		return 0, err
	}
	return entryCount, nil
}

func preflightCentralDirectory(reader io.ReaderAt, offset, size, entryCount uint64) error {
	end := offset + size
	current := offset
	for index := uint64(0); index < entryCount; index++ {
		if current > end || current > math.MaxInt64 || end-current < 46 {
			return errors.New("inner package central directory is truncated")
		}
		header := make([]byte, 46)
		if _, err := reader.ReadAt(header, int64(current)); err != nil || binary.LittleEndian.Uint32(header) != 0x02014b50 {
			return errors.New("inner package central directory entry is invalid")
		}
		nameLength := uint64(binary.LittleEndian.Uint16(header[28:]))
		extraLength := uint64(binary.LittleEndian.Uint16(header[30:]))
		commentLength := uint64(binary.LittleEndian.Uint16(header[32:]))
		recordSize := uint64(46) + nameLength + extraLength + commentLength
		if recordSize < 46 || recordSize > end-current {
			return errors.New("inner package central directory entry exceeds its bounds")
		}
		extra := make([]byte, int(extraLength))
		if extraLength > 0 {
			if _, err := reader.ReadAt(extra, int64(current+46+nameLength)); err != nil {
				return errors.New("inner package central directory extra data is unreadable")
			}
		}
		if err := validateCentralZIP64Sizes(header, extra); err != nil {
			return err
		}
		current += recordSize
	}
	if current != end {
		return errors.New("inner package central directory count does not match its size")
	}
	return nil
}

func validateCentralZIP64Sizes(header, extra []byte) error {
	needUncompressed := binary.LittleEndian.Uint32(header[24:]) == math.MaxUint32
	needCompressed := binary.LittleEndian.Uint32(header[20:]) == math.MaxUint32
	needOffset := binary.LittleEndian.Uint32(header[42:]) == math.MaxUint32
	needDisk := binary.LittleEndian.Uint16(header[34:]) == math.MaxUint16
	if !needUncompressed && !needCompressed && !needOffset && !needDisk {
		if binary.LittleEndian.Uint16(header[34:]) != 0 {
			return errors.New("inner package central entry uses another disk")
		}
		return nil
	}
	var zip64 []byte
	for cursor := 0; cursor+4 <= len(extra); {
		fieldSize := int(binary.LittleEndian.Uint16(extra[cursor+2:]))
		if fieldSize > len(extra)-cursor-4 {
			return errors.New("inner package central extra field is truncated")
		}
		if binary.LittleEndian.Uint16(extra[cursor:]) == 0x0001 {
			zip64 = extra[cursor+4 : cursor+4+fieldSize]
			break
		}
		cursor += 4 + fieldSize
	}
	read64 := func(required bool) (uint64, bool) {
		if !required {
			return 0, true
		}
		if len(zip64) < 8 {
			return 0, false
		}
		value := binary.LittleEndian.Uint64(zip64)
		zip64 = zip64[8:]
		return value, true
	}
	for _, required := range []bool{needUncompressed, needCompressed, needOffset} {
		value, ok := read64(required)
		if !ok || required && value > math.MaxInt64 {
			return errors.New("inner package ZIP64 entry size or offset is invalid")
		}
	}
	if needDisk {
		if len(zip64) < 4 || binary.LittleEndian.Uint32(zip64) != 0 {
			return errors.New("inner package ZIP64 entry uses another disk")
		}
	} else if binary.LittleEndian.Uint16(header[34:]) != 0 {
		return errors.New("inner package central entry uses another disk")
	}
	return nil
}

func openInnerArchive(file *verifiedFile) (*innerArchive, error) {
	preflightCount, err := preflightInnerArchive(file.file, file.size)
	if err != nil {
		return nil, err
	}
	archive, err := zip.NewReader(file.file, file.size)
	if err != nil || len(archive.File) == 0 || len(archive.File) > maxInnerEntries || uint64(len(archive.File)) != preflightCount {
		return nil, errors.New("inner package is not a bounded ZIP archive")
	}
	entries := make(map[string]*zip.File, len(archive.File))
	folded := make(map[string]struct{}, len(archive.File))
	var expanded int64
	var compressed int64
	for _, entry := range archive.File {
		canonicalName, safe := canonicalInnerWindowsPath(entry.Name)
		if !safe || entry.FileInfo().IsDir() || entry.Mode()&os.ModeSymlink != 0 || entry.ExternalAttrs&0x400 != 0 {
			return nil, fmt.Errorf("inner package has unsafe path %q", entry.Name)
		}
		foldedName := strings.ToUpper(canonicalName)
		if _, duplicate := folded[foldedName]; duplicate {
			return nil, errors.New("inner package has duplicate or case-colliding paths")
		}
		if entry.UncompressedSize64 > uint64(maxInnerEntry) || entry.UncompressedSize64 > math.MaxInt64 || entry.CompressedSize64 > math.MaxInt64 {
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

func canonicalInnerWindowsPath(name string) (string, bool) {
	if name == "" || len(name) > 4096 || !utf8.ValidString(name) || len(utf16.Encode([]rune(name))) > 1024 ||
		strings.HasPrefix(name, "/") || strings.HasPrefix(name, `\`) ||
		strings.Contains(name, `\`) || strings.ContainsRune(name, 0) || (len(name) >= 2 && name[1] == ':' && isASCIIAlpha(name[0])) {
		return "", false
	}
	parts := strings.Split(name, "/")
	canonicalParts := make([]string, 0, len(parts))
	for _, rawPart := range parts {
		part := norm.NFC.String(rawPart)
		if part == "" || part == "." || part == ".." || len(utf16.Encode([]rune(part))) > 255 || strings.HasSuffix(part, " ") || strings.HasSuffix(part, ".") ||
			strings.ContainsAny(part, `<>:"|?*`) {
			return "", false
		}
		for _, character := range part {
			if character < 0x20 || character == 0x7f {
				return "", false
			}
		}
		stem := strings.ToUpper(strings.SplitN(part, ".", 2)[0])
		switch stem {
		case "CON", "PRN", "AUX", "NUL", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9":
			return "", false
		}
		canonicalParts = append(canonicalParts, part)
	}
	canonical := strings.Join(canonicalParts, "/")
	if len(utf16.Encode([]rune(canonical))) > 240 {
		return "", false
	}
	return canonical, true
}

func isASCIIAlpha(value byte) bool {
	return value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z'
}

func validateNuspec(entry *zip.File, expectedVersion string, requireMachineArchitecture bool) error {
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
	if requireMachineArchitecture && !exactSingle(metadata.MachineArchitecture, "x64") ||
		!requireMachineArchitecture && (len(metadata.MachineArchitecture) > 1 || len(metadata.MachineArchitecture) == 1 && metadata.MachineArchitecture[0] != "x64") {
		return errors.New("package metadata machine architecture differs")
	}
	return nil
}

func exactSingle(values []string, expected string) bool {
	return len(values) == 1 && values[0] == expected
}

func readZipEntry(entry *zip.File, limit int64) ([]byte, error) {
	if entry == nil || entry.UncompressedSize64 > math.MaxInt64 || entry.UncompressedSize64 > uint64(limit) {
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
