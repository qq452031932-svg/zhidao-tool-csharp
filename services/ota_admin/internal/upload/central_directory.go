package upload

import (
	"encoding/binary"
	"fmt"
	"io"
)

const (
	endOfCentralDirectorySize  = 22
	maximumZIPCommentSize      = 1<<16 - 1
	centralDirectoryHeaderSize = 46
)

var (
	endOfCentralDirectorySignature = []byte{'P', 'K', 5, 6}
	centralDirectorySignature      = []byte{'P', 'K', 1, 2}
)

// preflightCentralDirectory bounds archive/zip's allocations. archive/zip parses
// every central-directory record before returning, so the entry ceiling must be
// enforced independently first. OTA archives never need ZIP64: their compressed
// size and entry count are both below classic ZIP limits.
func preflightCentralDirectory(file io.ReaderAt, size int64, maximumEntries int) error {
	if size < endOfCentralDirectorySize {
		return ErrInvalidArchive
	}
	tailSize := int64(endOfCentralDirectorySize + maximumZIPCommentSize)
	if size < tailSize {
		tailSize = size
	}
	tail := make([]byte, tailSize)
	if _, err := file.ReadAt(tail, size-tailSize); err != nil {
		return fmt.Errorf("%w: read end record: %v", ErrInvalidArchive, err)
	}
	eocdIndex := findEOCD(tail)
	if eocdIndex < 0 {
		return fmt.Errorf("%w: missing end record", ErrInvalidArchive)
	}
	eocd := tail[eocdIndex:]
	if binary.LittleEndian.Uint16(eocd[4:6]) != 0 || binary.LittleEndian.Uint16(eocd[6:8]) != 0 {
		return fmt.Errorf("%w: multi-disk ZIP", ErrInvalidArchive)
	}
	recordsOnDisk := binary.LittleEndian.Uint16(eocd[8:10])
	recordsTotal := binary.LittleEndian.Uint16(eocd[10:12])
	if recordsOnDisk == 0xffff || recordsTotal == 0xffff || int(recordsTotal) > maximumEntries {
		return ErrEntryLimit
	}
	if recordsOnDisk != recordsTotal {
		return fmt.Errorf("%w: inconsistent entry count", ErrInvalidArchive)
	}
	directorySize := uint64(binary.LittleEndian.Uint32(eocd[12:16]))
	directoryOffset := uint64(binary.LittleEndian.Uint32(eocd[16:20]))
	eocdOffset := uint64(size-tailSize) + uint64(eocdIndex)
	if directoryOffset > eocdOffset || directorySize > eocdOffset-directoryOffset || directoryOffset+directorySize != eocdOffset {
		return fmt.Errorf("%w: invalid central directory bounds", ErrInvalidArchive)
	}

	position := directoryOffset
	end := directoryOffset + directorySize
	var fixed [centralDirectoryHeaderSize]byte
	for index := 0; index < int(recordsTotal); index++ {
		if end-position < centralDirectoryHeaderSize {
			return fmt.Errorf("%w: truncated central directory", ErrInvalidArchive)
		}
		if _, err := file.ReadAt(fixed[:], int64(position)); err != nil {
			return fmt.Errorf("%w: read central directory: %v", ErrInvalidArchive, err)
		}
		if string(fixed[:4]) != string(centralDirectorySignature) {
			return fmt.Errorf("%w: invalid central directory signature", ErrInvalidArchive)
		}
		if binary.LittleEndian.Uint16(fixed[34:36]) != 0 {
			return fmt.Errorf("%w: multi-disk entry", ErrInvalidArchive)
		}
		nameSize := uint64(binary.LittleEndian.Uint16(fixed[28:30]))
		extraSize := uint64(binary.LittleEndian.Uint16(fixed[30:32]))
		commentSize := uint64(binary.LittleEndian.Uint16(fixed[32:34]))
		recordSize := uint64(centralDirectoryHeaderSize) + nameSize + extraSize + commentSize
		if nameSize == 0 || nameSize > 255 || recordSize > end-position {
			return fmt.Errorf("%w: invalid central directory record", ErrInvalidArchive)
		}
		position += recordSize
	}
	if position != end {
		return fmt.Errorf("%w: undeclared central directory records", ErrInvalidArchive)
	}
	return nil
}

func findEOCD(tail []byte) int {
	for index := len(tail) - endOfCentralDirectorySize; index >= 0; index-- {
		if string(tail[index:index+4]) != string(endOfCentralDirectorySignature) {
			continue
		}
		commentSize := int(binary.LittleEndian.Uint16(tail[index+20 : index+22]))
		if index+endOfCentralDirectorySize+commentSize == len(tail) {
			return index
		}
	}
	return -1
}
