package upload

import (
	"bytes"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"sort"
)

const (
	endOfCentralDirectorySize  = 22
	maximumDirectorySearchSize = 65 * 1024
	centralDirectoryHeaderSize = 46
	localFileHeaderSize        = 30
	dataDescriptorSize         = 12
	signedDataDescriptorSize   = 16
	zipStoreMethod             = 0
)

var (
	endOfCentralDirectorySignature = []byte{'P', 'K', 5, 6}
	centralDirectorySignature      = []byte{'P', 'K', 1, 2}
	localFileHeaderSignature       = []byte{'P', 'K', 3, 4}
	dataDescriptorSignature        = []byte{'P', 'K', 7, 8}
)

type archiveLayout struct {
	directoryOffset uint64
	directoryEnd    uint64
	records         []centralRecord
}

type centralRecord struct {
	name             string
	versionNeeded    uint16
	flags            uint16
	method           uint16
	modifiedTime     uint16
	modifiedDate     uint16
	crc32            uint32
	compressedSize   uint32
	uncompressedSize uint32
	localOffset      uint32
	extra            []byte
}

type memberRange struct {
	start          uint64
	end            uint64
	compressedSize uint64
}

// preflightCentralDirectory bounds archive/zip's allocations and selects EOCD
// candidates with the same "comment fits before EOF" rule as Go 1.27.1. A
// second viable EOCD is rejected because archive/zip would select the last one
// while a preflight might otherwise validate an earlier record.
func preflightCentralDirectory(file io.ReaderAt, size int64, maximumEntries int) (*archiveLayout, error) {
	if size < endOfCentralDirectorySize {
		return nil, ErrInvalidArchive
	}
	tailSize := int64(maximumDirectorySearchSize)
	if size < tailSize {
		tailSize = size
	}
	tail := make([]byte, tailSize)
	if _, err := file.ReadAt(tail, size-tailSize); err != nil {
		return nil, classifyStructureReadError("read end record", err)
	}
	candidates := archiveLevelEOCDCandidates(tail, uint64(size-tailSize), findEOCDCandidates(tail))
	if len(candidates) == 0 {
		return nil, fmt.Errorf("%w: missing end record", ErrInvalidArchive)
	}
	if len(candidates) != 1 {
		return nil, fmt.Errorf("%w: %w", ErrInvalidArchive, ErrAmbiguousEOCD)
	}
	eocdIndex := candidates[0]
	eocd := tail[eocdIndex:]
	if binary.LittleEndian.Uint16(eocd[4:6]) != 0 || binary.LittleEndian.Uint16(eocd[6:8]) != 0 {
		return nil, fmt.Errorf("%w: multi-disk ZIP", ErrInvalidArchive)
	}
	recordsOnDisk := binary.LittleEndian.Uint16(eocd[8:10])
	recordsTotal := binary.LittleEndian.Uint16(eocd[10:12])
	directorySize32 := binary.LittleEndian.Uint32(eocd[12:16])
	directoryOffset32 := binary.LittleEndian.Uint32(eocd[16:20])
	if recordsOnDisk == math.MaxUint16 || recordsTotal == math.MaxUint16 || directorySize32 == math.MaxUint32 || directoryOffset32 == math.MaxUint32 {
		return nil, fmt.Errorf("%w: %w", ErrInvalidArchive, ErrUnsupportedZIP64)
	}
	if int(recordsTotal) > maximumEntries {
		return nil, ErrEntryLimit
	}
	if recordsOnDisk != recordsTotal {
		return nil, fmt.Errorf("%w: inconsistent entry count", ErrInvalidArchive)
	}
	directorySize := uint64(directorySize32)
	directoryOffset := uint64(directoryOffset32)
	eocdOffset := uint64(size-tailSize) + uint64(eocdIndex)
	if directoryOffset > eocdOffset || directorySize > eocdOffset-directoryOffset || directoryOffset+directorySize != eocdOffset {
		return nil, fmt.Errorf("%w: invalid central directory bounds", ErrInvalidArchive)
	}

	layout := &archiveLayout{directoryOffset: directoryOffset, directoryEnd: eocdOffset, records: make([]centralRecord, 0, recordsTotal)}
	position := directoryOffset
	end := directoryOffset + directorySize
	var fixed [centralDirectoryHeaderSize]byte
	for index := 0; index < int(recordsTotal); index++ {
		if end-position < centralDirectoryHeaderSize {
			return nil, fmt.Errorf("%w: truncated central directory", ErrInvalidArchive)
		}
		if _, err := file.ReadAt(fixed[:], int64(position)); err != nil {
			return nil, classifyStructureReadError("read central directory", err)
		}
		if !bytes.Equal(fixed[:4], centralDirectorySignature) {
			return nil, fmt.Errorf("%w: invalid central directory signature", ErrInvalidArchive)
		}
		if binary.LittleEndian.Uint16(fixed[34:36]) != 0 {
			return nil, fmt.Errorf("%w: multi-disk entry", ErrInvalidArchive)
		}
		nameSize := uint64(binary.LittleEndian.Uint16(fixed[28:30]))
		extraSize := uint64(binary.LittleEndian.Uint16(fixed[30:32]))
		commentSize := uint64(binary.LittleEndian.Uint16(fixed[32:34]))
		recordSize := uint64(centralDirectoryHeaderSize) + nameSize + extraSize + commentSize
		if nameSize == 0 || nameSize > 255 || recordSize > end-position {
			return nil, fmt.Errorf("%w: invalid central directory record", ErrInvalidArchive)
		}
		variable := make([]byte, nameSize+extraSize)
		if _, err := file.ReadAt(variable, int64(position+centralDirectoryHeaderSize)); err != nil {
			return nil, classifyStructureReadError("read central entry", err)
		}
		if malformedOrContainsExtraField(variable[nameSize:], 0x0001) {
			return nil, fmt.Errorf("%w: %w", ErrInvalidArchive, ErrUnsupportedZIP64)
		}
		compressedSize := binary.LittleEndian.Uint32(fixed[20:24])
		uncompressedSize := binary.LittleEndian.Uint32(fixed[24:28])
		localOffset := binary.LittleEndian.Uint32(fixed[42:46])
		if compressedSize == math.MaxUint32 || uncompressedSize == math.MaxUint32 || localOffset == math.MaxUint32 {
			return nil, fmt.Errorf("%w: %w", ErrInvalidArchive, ErrUnsupportedZIP64)
		}
		layout.records = append(layout.records, centralRecord{
			name: string(variable[:nameSize]), versionNeeded: binary.LittleEndian.Uint16(fixed[6:8]),
			flags: binary.LittleEndian.Uint16(fixed[8:10]), method: binary.LittleEndian.Uint16(fixed[10:12]),
			modifiedTime: binary.LittleEndian.Uint16(fixed[12:14]), modifiedDate: binary.LittleEndian.Uint16(fixed[14:16]),
			crc32: binary.LittleEndian.Uint32(fixed[16:20]), compressedSize: compressedSize,
			uncompressedSize: uncompressedSize, localOffset: localOffset, extra: append([]byte(nil), variable[nameSize:]...),
		})
		position += recordSize
	}
	if position != end {
		return nil, fmt.Errorf("%w: undeclared central directory records", ErrInvalidArchive)
	}
	return layout, nil
}

func (layout *archiveLayout) validateLocalRecords(file io.ReaderAt) (uint64, error) {
	ranges := make([]memberRange, 0, len(layout.records))
	var fixed [localFileHeaderSize]byte
	for _, record := range layout.records {
		start := uint64(record.localOffset)
		if start > layout.directoryOffset || layout.directoryOffset-start < localFileHeaderSize {
			return 0, fmt.Errorf("%w: local header outside member area", ErrInvalidArchive)
		}
		if _, err := file.ReadAt(fixed[:], int64(start)); err != nil {
			return 0, classifyStructureReadError("read local header", err)
		}
		if !bytes.Equal(fixed[:4], localFileHeaderSignature) {
			return 0, fmt.Errorf("%w: invalid local header signature", ErrInvalidArchive)
		}
		flags := binary.LittleEndian.Uint16(fixed[6:8])
		method := binary.LittleEndian.Uint16(fixed[8:10])
		nameSize := uint64(binary.LittleEndian.Uint16(fixed[26:28]))
		extraSize := uint64(binary.LittleEndian.Uint16(fixed[28:30]))
		if binary.LittleEndian.Uint16(fixed[4:6]) != record.versionNeeded || flags != record.flags || method != record.method ||
			binary.LittleEndian.Uint16(fixed[10:12]) != record.modifiedTime || binary.LittleEndian.Uint16(fixed[12:14]) != record.modifiedDate ||
			nameSize == 0 || nameSize > 255 {
			return 0, fmt.Errorf("%w: local and central headers disagree", ErrInvalidArchive)
		}
		headerEnd := start + localFileHeaderSize + nameSize + extraSize
		if headerEnd < start || headerEnd > layout.directoryOffset {
			return 0, fmt.Errorf("%w: invalid local header bounds", ErrInvalidArchive)
		}
		variable := make([]byte, nameSize+extraSize)
		if _, err := file.ReadAt(variable, int64(start+localFileHeaderSize)); err != nil {
			return 0, classifyStructureReadError("read local entry", err)
		}
		if string(variable[:nameSize]) != record.name || !bytes.Equal(variable[nameSize:], record.extra) || malformedOrContainsExtraField(variable[nameSize:], 0x0001) {
			return 0, fmt.Errorf("%w: local entry metadata mismatch", ErrInvalidArchive)
		}
		localCRC := binary.LittleEndian.Uint32(fixed[14:18])
		localCompressed := binary.LittleEndian.Uint32(fixed[18:22])
		localExpanded := binary.LittleEndian.Uint32(fixed[22:26])
		if localCompressed == math.MaxUint32 || localExpanded == math.MaxUint32 {
			return 0, fmt.Errorf("%w: %w", ErrInvalidArchive, ErrUnsupportedZIP64)
		}
		usesDescriptor := flags&0x8 != 0
		if usesDescriptor {
			if (localCRC != 0 && localCRC != record.crc32) || (localCompressed != 0 && localCompressed != record.compressedSize) || (localExpanded != 0 && localExpanded != record.uncompressedSize) {
				return 0, fmt.Errorf("%w: local descriptor placeholders disagree", ErrInvalidArchive)
			}
		} else if localCRC != record.crc32 || localCompressed != record.compressedSize || localExpanded != record.uncompressedSize {
			return 0, fmt.Errorf("%w: local sizes or CRC disagree", ErrInvalidArchive)
		}
		if method == zipStoreMethod && record.compressedSize != record.uncompressedSize {
			return 0, fmt.Errorf("%w: stored member sizes disagree", ErrInvalidArchive)
		}
		dataEnd := headerEnd + uint64(record.compressedSize)
		if dataEnd < headerEnd || dataEnd > layout.directoryOffset {
			return 0, fmt.Errorf("%w: compressed member crosses central directory", ErrInvalidArchive)
		}
		memberEnd := dataEnd
		if usesDescriptor {
			descriptorEnd, err := validateDataDescriptor(file, dataEnd, layout.directoryOffset, record)
			if err != nil {
				return 0, err
			}
			memberEnd = descriptorEnd
		}
		ranges = append(ranges, memberRange{start: start, end: memberEnd, compressedSize: uint64(record.compressedSize)})
	}
	sort.Slice(ranges, func(left, right int) bool { return ranges[left].start < ranges[right].start })
	var actualCompressed uint64
	for index, member := range ranges {
		if index > 0 && member.start < ranges[index-1].end {
			return 0, fmt.Errorf("%w: overlapping or aliased member ranges", ErrInvalidArchive)
		}
		if math.MaxUint64-actualCompressed < member.compressedSize {
			return 0, fmt.Errorf("%w: compressed size overflow", ErrInvalidArchive)
		}
		actualCompressed += member.compressedSize
	}
	return actualCompressed, nil
}

func validateDataDescriptor(file io.ReaderAt, offset, boundary uint64, record centralRecord) (uint64, error) {
	if offset > boundary || boundary-offset < dataDescriptorSize {
		return 0, fmt.Errorf("%w: truncated data descriptor", ErrInvalidArchive)
	}
	var descriptor [signedDataDescriptorSize]byte
	readSize := dataDescriptorSize
	if boundary-offset >= signedDataDescriptorSize {
		readSize = signedDataDescriptorSize
	}
	if _, err := file.ReadAt(descriptor[:readSize], int64(offset)); err != nil {
		return 0, classifyStructureReadError("read data descriptor", err)
	}
	position := 0
	length := uint64(dataDescriptorSize)
	if bytes.Equal(descriptor[:4], dataDescriptorSignature) {
		if readSize < signedDataDescriptorSize {
			return 0, fmt.Errorf("%w: truncated signed data descriptor", ErrInvalidArchive)
		}
		position = 4
		length = signedDataDescriptorSize
	}
	if binary.LittleEndian.Uint32(descriptor[position:position+4]) != record.crc32 ||
		binary.LittleEndian.Uint32(descriptor[position+4:position+8]) != record.compressedSize ||
		binary.LittleEndian.Uint32(descriptor[position+8:position+12]) != record.uncompressedSize {
		return 0, fmt.Errorf("%w: data descriptor disagrees with central directory", ErrInvalidArchive)
	}
	return offset + length, nil
}

func classifyStructureReadError(operation string, err error) error {
	var pathError *os.PathError
	if errors.As(err, &pathError) {
		return fmt.Errorf("%s: %w", operation, err)
	}
	if errors.Is(err, io.EOF) || errors.Is(err, io.ErrUnexpectedEOF) {
		return fmt.Errorf("%w: %s: %v", ErrInvalidArchive, operation, err)
	}
	return fmt.Errorf("%s: %w", operation, err)
}

func malformedOrContainsExtraField(extra []byte, wanted uint16) bool {
	for len(extra) >= 4 {
		fieldID := binary.LittleEndian.Uint16(extra[:2])
		length := int(binary.LittleEndian.Uint16(extra[2:4]))
		extra = extra[4:]
		if length > len(extra) || fieldID == wanted {
			return true
		}
		extra = extra[length:]
	}
	return len(extra) != 0
}

func findEOCDCandidates(tail []byte) []int {
	var candidates []int
	for index := len(tail) - endOfCentralDirectorySize; index >= 0; index-- {
		if !bytes.Equal(tail[index:index+4], endOfCentralDirectorySignature) {
			continue
		}
		commentSize := int(binary.LittleEndian.Uint16(tail[index+20 : index+22]))
		if index+endOfCentralDirectorySize+commentSize <= len(tail) {
			candidates = append(candidates, index)
		}
	}
	return candidates
}

func archiveLevelEOCDCandidates(tail []byte, tailOffset uint64, raw []int) []int {
	if len(raw) < 2 {
		return raw
	}
	candidates := []int{raw[0]}
	for _, index := range raw[1:] {
		record := tail[index : index+endOfCentralDirectorySize]
		if binary.LittleEndian.Uint16(record[4:6]) != 0 || binary.LittleEndian.Uint16(record[6:8]) != 0 {
			continue
		}
		directorySize := uint64(binary.LittleEndian.Uint32(record[12:16]))
		directoryOffset := uint64(binary.LittleEndian.Uint32(record[16:20]))
		absoluteOffset := tailOffset + uint64(index)
		if directoryOffset <= absoluteOffset && directorySize == absoluteOffset-directoryOffset {
			candidates = append(candidates, index)
		}
	}
	return candidates
}
