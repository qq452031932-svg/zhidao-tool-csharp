package upload

import (
	"archive/zip"
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"io"
	"os"
	"strings"
	"testing"
)

func TestPreflightRejectsNestedAmbiguousEOCDSelectedByArchiveZip(t *testing.T) {
	fake := make([]byte, endOfCentralDirectorySize)
	copy(fake, endOfCentralDirectorySignature)
	binary.LittleEndian.PutUint16(fake[8:10], 0xffff)
	binary.LittleEndian.PutUint16(fake[10:12], 0xffff)
	archive := makeZIPWithComment(t, append(fake, 'x'))
	if _, err := preflightCentralDirectory(bytes.NewReader(archive), int64(len(archive)), DefaultLimits().Entries); !errors.Is(err, ErrAmbiguousEOCD) || !errors.Is(err, ErrInvalidArchive) {
		t.Fatalf("nested EOCD error=%v", err)
	}
}

func TestPreflightAllowsStoredZIPMemberEOCDSignature(t *testing.T) {
	inner := makeZIP(t, map[string]string{"upload-manifest.sha256": "inner"})
	var buffer bytes.Buffer
	outer := zip.NewWriter(&buffer)
	header := &zip.FileHeader{Name: "SuperPan.BaiduBatchTransfer-Portable.zip", Method: zip.Store}
	entry, err := outer.CreateHeader(header)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := entry.Write(inner); err != nil {
		t.Fatal(err)
	}
	if err := outer.Close(); err != nil {
		t.Fatal(err)
	}
	archive := buffer.Bytes()
	if _, err := zip.NewReader(bytes.NewReader(archive), int64(len(archive))); err != nil {
		t.Fatalf("Go parser rejected normal nested ZIP fixture: %v", err)
	}
	if _, err := preflightCentralDirectory(bytes.NewReader(archive), int64(len(archive)), DefaultLimits().Entries); err != nil {
		t.Fatalf("preflight rejected normal nested ZIP fixture: %v", err)
	}
}

func TestPreflightRejectsEveryZIP64Trigger(t *testing.T) {
	for _, field := range []struct {
		name  string
		start int
		size  int
	}{
		{"records", 10, 2},
		{"directory size", 12, 4},
		{"directory offset", 16, 4},
	} {
		t.Run(field.name, func(t *testing.T) {
			archive := makeZIP(t, map[string]string{"upload-manifest.sha256": "manifest"})
			eocd := bytes.LastIndex(archive, endOfCentralDirectorySignature)
			for index := 0; index < field.size; index++ {
				archive[eocd+field.start+index] = 0xff
			}
			if _, err := preflightCentralDirectory(bytes.NewReader(archive), int64(len(archive)), DefaultLimits().Entries); !errors.Is(err, ErrUnsupportedZIP64) || !errors.Is(err, ErrInvalidArchive) {
				t.Fatalf("ZIP64 error=%v", err)
			}
		})
	}
}

func TestArchiveStructureReadsPreserveFilesystemIOErrors(t *testing.T) {
	for _, failure := range []error{errors.New("filesystem read failure"), io.ErrUnexpectedEOF} {
		_, err := preflightCentralDirectory(failingReaderAt{err: &os.PathError{Op: "read", Path: "candidate.zip", Err: failure}}, endOfCentralDirectorySize, DefaultLimits().Entries)
		if !errors.Is(err, failure) || errors.Is(err, ErrInvalidArchive) {
			t.Fatalf("filesystem error was misclassified: %v", err)
		}
	}
}

func TestArchiveParserPreservesFilesystemIOErrors(t *testing.T) {
	failure := errors.New("filesystem read failure")
	err := &os.PathError{Op: "read", Path: "candidate.zip", Err: failure}
	classified := classifyArchiveParserError(err)
	if !errors.Is(classified, failure) {
		t.Fatalf("filesystem error was lost: %v", classified)
	}
	if errors.Is(classified, ErrInvalidArchive) {
		t.Fatalf("filesystem error was misclassified: %v", classified)
	}
}

func TestArchiveDataPreservesFilesystemIOErrors(t *testing.T) {
	failure := &os.PathError{Op: "read", Path: "candidate.zip", Err: io.ErrUnexpectedEOF}
	classified := classifyArchiveDataError(failure)
	if !errors.Is(classified, io.ErrUnexpectedEOF) || errors.Is(classified, ErrInvalidArchive) {
		t.Fatalf("filesystem error was misclassified: %v", classified)
	}
}

func TestReceiveRejectsAliasedLocalMemberRanges(t *testing.T) {
	archive := makeZIP(t, map[string]string{
		"upload-manifest.sha256":     "same bytes",
		"upload-manifest.sha256.sig": "same bytes",
	})
	records := centralRecordOffsets(t, archive)
	if len(records) != 2 {
		t.Fatalf("central records=%d", len(records))
	}
	firstLocal := binary.LittleEndian.Uint32(archive[records[0]+42 : records[0]+46])
	binary.LittleEndian.PutUint32(archive[records[1]+42:records[1]+46], firstLocal)
	assertRejectedInvalidArchive(t, archive)
}

func TestReceiveRejectsLocalAndCentralHeaderInconsistency(t *testing.T) {
	for _, test := range []struct {
		name   string
		mutate func([]byte, int, int)
	}{
		{"method", func(archive []byte, _, local int) {
			binary.LittleEndian.PutUint16(archive[local+8:local+10], zip.Store)
		}},
		{"modified time", func(archive []byte, _, local int) { archive[local+10] ^= 1 }},
		{"modified date", func(archive []byte, _, local int) { archive[local+12] ^= 1 }},
		{"extra", func(archive []byte, _, local int) {
			nameSize := int(binary.LittleEndian.Uint16(archive[local+26 : local+28]))
			archive[local+localFileHeaderSize+nameSize+4] ^= 1
		}},
	} {
		t.Run(test.name, func(t *testing.T) {
			archive := makeZIPWithExtra(t)
			record := centralRecordOffsets(t, archive)[0]
			local := int(binary.LittleEndian.Uint32(archive[record+42 : record+46]))
			test.mutate(archive, record, local)
			assertRejectedInvalidArchive(t, archive)
		})
	}
}

func TestReceiveClassifiesMalformedMemberDataAsInvalidArchive(t *testing.T) {
	tests := []struct {
		name   string
		mutate func([]byte)
	}{
		{"bad local header", func(archive []byte) {
			record := centralRecordOffsets(t, archive)[0]
			local := int(binary.LittleEndian.Uint32(archive[record+42 : record+46]))
			archive[local] ^= 0xff
		}},
		{"unsupported method", func(archive []byte) {
			record := centralRecordOffsets(t, archive)[0]
			local := int(binary.LittleEndian.Uint32(archive[record+42 : record+46]))
			binary.LittleEndian.PutUint16(archive[record+10:record+12], 99)
			binary.LittleEndian.PutUint16(archive[local+8:local+10], 99)
		}},
		{"bad descriptor", func(archive []byte) {
			record := centralRecordOffsets(t, archive)[0]
			local := int(binary.LittleEndian.Uint32(archive[record+42 : record+46]))
			nameSize := int(binary.LittleEndian.Uint16(archive[local+26 : local+28]))
			extraSize := int(binary.LittleEndian.Uint16(archive[local+28 : local+30]))
			compressed := int(binary.LittleEndian.Uint32(archive[record+20 : record+24]))
			descriptor := local + 30 + nameSize + extraSize + compressed
			if !bytes.Equal(archive[descriptor:descriptor+4], []byte{'P', 'K', 7, 8}) {
				t.Fatal("fixture has no signed data descriptor")
			}
			archive[descriptor+4] ^= 0xff
		}},
		{"bad checksum", func(archive []byte) {
			record := centralRecordOffsets(t, archive)[0]
			local := int(binary.LittleEndian.Uint32(archive[record+42 : record+46]))
			nameSize := int(binary.LittleEndian.Uint16(archive[local+26 : local+28]))
			extraSize := int(binary.LittleEndian.Uint16(archive[local+28 : local+30]))
			compressed := int(binary.LittleEndian.Uint32(archive[record+20 : record+24]))
			descriptor := local + localFileHeaderSize + nameSize + extraSize + compressed
			badCRC := binary.LittleEndian.Uint32(archive[record+16:record+20]) ^ 1
			binary.LittleEndian.PutUint32(archive[record+16:record+20], badCRC)
			binary.LittleEndian.PutUint32(archive[descriptor+4:descriptor+8], badCRC)
		}},
		{"corrupt deflate", func(archive []byte) {
			record := centralRecordOffsets(t, archive)[0]
			local := int(binary.LittleEndian.Uint32(archive[record+42 : record+46]))
			nameSize := int(binary.LittleEndian.Uint16(archive[local+26 : local+28]))
			extraSize := int(binary.LittleEndian.Uint16(archive[local+28 : local+30]))
			compressed := int(binary.LittleEndian.Uint32(archive[record+20 : record+24]))
			archive[local+30+nameSize+extraSize+compressed/2] ^= 0xff
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			archive := makeZIP(t, map[string]string{"upload-manifest.sha256": strings.Repeat("member data ", 100)})
			test.mutate(archive)
			assertRejectedInvalidArchive(t, archive)
		})
	}
}

func assertRejectedInvalidArchive(t *testing.T, archive []byte) {
	t.Helper()
	receiver := newTestReceiver(t, t.TempDir(), DefaultLimits(), func(string) (uint64, error) { return 1 << 40, nil })
	if _, err := receiver.Receive(context.Background(), bytes.NewReader(archive), int64(len(archive))); !errors.Is(err, ErrInvalidArchive) {
		t.Fatalf("error=%v want ErrInvalidArchive", err)
	}
}

func centralRecordOffsets(t *testing.T, archive []byte) []int {
	t.Helper()
	eocd := bytes.LastIndex(archive, endOfCentralDirectorySignature)
	if eocd < 0 {
		t.Fatal("fixture has no EOCD")
	}
	position := int(binary.LittleEndian.Uint32(archive[eocd+16 : eocd+20]))
	end := position + int(binary.LittleEndian.Uint32(archive[eocd+12:eocd+16]))
	var offsets []int
	for position < end {
		if !bytes.Equal(archive[position:position+4], centralDirectorySignature) {
			t.Fatalf("bad central signature at %d", position)
		}
		offsets = append(offsets, position)
		nameSize := int(binary.LittleEndian.Uint16(archive[position+28 : position+30]))
		extraSize := int(binary.LittleEndian.Uint16(archive[position+30 : position+32]))
		commentSize := int(binary.LittleEndian.Uint16(archive[position+32 : position+34]))
		position += centralDirectoryHeaderSize + nameSize + extraSize + commentSize
	}
	return offsets
}

func makeZIPWithComment(t *testing.T, comment []byte) []byte {
	t.Helper()
	var buffer bytes.Buffer
	archive := zip.NewWriter(&buffer)
	entry, err := archive.Create("upload-manifest.sha256")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := io.WriteString(entry, "manifest"); err != nil {
		t.Fatal(err)
	}
	if err := archive.SetComment(string(comment)); err != nil {
		t.Fatal(err)
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return buffer.Bytes()
}

func makeZIPWithExtra(t *testing.T) []byte {
	t.Helper()
	var buffer bytes.Buffer
	archive := zip.NewWriter(&buffer)
	header := &zip.FileHeader{Name: "upload-manifest.sha256", Method: zip.Deflate, Extra: []byte{0xfe, 0xca, 1, 0, 7}}
	entry, err := archive.CreateHeader(header)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := io.WriteString(entry, "manifest"); err != nil {
		t.Fatal(err)
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return buffer.Bytes()
}

type failingReaderAt struct{ err error }

func (reader failingReaderAt) ReadAt([]byte, int64) (int, error) {
	return 0, reader.err
}
