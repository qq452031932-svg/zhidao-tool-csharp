package upload

import (
	"archive/zip"
	"compress/flate"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"hash/crc32"
	"io"
	"os"
	"path/filepath"
	"sync"
)

type AvailableBytesFunc func(path string) (uint64, error)

type ReceiverOptions struct {
	StagingRoot    string
	Limits         Limits
	AvailableBytes AvailableBytesFunc
}

type Receiver struct {
	stagingRoot    string
	limits         Limits
	availableBytes AvailableBytesFunc
	spaceMu        sync.Mutex
}

type Entry struct {
	Name           string
	Path           string
	Size           int64
	CompressedSize int64
}

type Envelope struct {
	ArchivePath     string
	ExtractionRoot  string
	SHA256          string
	CompressedBytes int64
	ExpandedBytes   int64
	Entries         []Entry
	Verified        bool

	cleanupRoot       string
	cleanupArchive    string
	cleanupExtraction string
}

func NewReceiver(options ReceiverOptions) (*Receiver, error) {
	if err := options.Limits.Validate(); err != nil {
		return nil, fmt.Errorf("upload limits: %w", err)
	}
	if options.AvailableBytes == nil {
		options.AvailableBytes = filesystemAvailableBytes
	}
	if options.StagingRoot == "" || !filepath.IsAbs(options.StagingRoot) {
		return nil, errors.New("staging root must be an existing absolute directory")
	}
	absRoot := filepath.Clean(options.StagingRoot)
	providedInfo, err := os.Lstat(absRoot)
	if err != nil || !providedInfo.IsDir() {
		return nil, errors.New("staging root must be an existing directory")
	}
	if providedInfo.Mode()&os.ModeSymlink != 0 {
		return nil, errors.New("staging root cannot be a symbolic link")
	}
	resolvedRoot, err := filepath.EvalSymlinks(absRoot)
	if err != nil {
		return nil, fmt.Errorf("resolve staging root: %w", err)
	}
	info, err := os.Lstat(resolvedRoot)
	if err != nil || !info.IsDir() {
		return nil, errors.New("staging root must be an existing directory")
	}
	if info.Mode()&os.ModeSymlink != 0 {
		return nil, errors.New("resolved staging root cannot be a symbolic link")
	}
	if err := validateStagingRoot(info); err != nil {
		return nil, fmt.Errorf("insecure staging root: %w", err)
	}
	return &Receiver{stagingRoot: filepath.Clean(resolvedRoot), limits: options.Limits, availableBytes: options.AvailableBytes}, nil
}

func validatePrivateStagingRootMetadata(owner, currentUID uint32, mode os.FileMode) error {
	if owner != currentUID {
		return errors.New("staging root must be owned by the service user")
	}
	if mode.Perm() != 0o700 {
		return errors.New("staging root permissions must be 0700")
	}
	return nil
}

func (receiver *Receiver) Receive(ctx context.Context, body io.Reader, contentLength int64) (*Envelope, error) {
	if body == nil {
		return nil, fmt.Errorf("%w: missing body", ErrInvalidArchive)
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if contentLength > receiver.limits.CompressedBytes {
		return nil, ErrCompressedLimit
	}
	if contentLength < -1 {
		return nil, fmt.Errorf("%w: invalid content length", ErrInvalidArchive)
	}
	receiver.spaceMu.Lock()
	defer receiver.spaceMu.Unlock()
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	archiveReservation, extractionReservation := receiver.reservations(contentLength)
	available, err := receiver.availableBytes(receiver.stagingRoot)
	if err != nil {
		return nil, fmt.Errorf("check disk space before upload: %w", err)
	}
	if archiveReservation > ^uint64(0)-extractionReservation || available < archiveReservation+extractionReservation {
		return nil, ErrInsufficientDisk
	}

	candidate, err := os.CreateTemp(receiver.stagingRoot, ".upload-*.zip")
	if err != nil {
		return nil, fmt.Errorf("create upload candidate: %w", err)
	}
	archivePath := candidate.Name()
	cleanupFailure := func(extractionRoot string) error {
		var closeErr error
		if candidate != nil {
			closeErr = candidate.Close()
			candidate = nil
		}
		var extractionErr error
		if extractionRoot != "" {
			extractionErr = os.RemoveAll(extractionRoot)
		}
		return errors.Join(closeErr, extractionErr, removeFileIfPresent(archivePath))
	}
	fail := func(primary error, extractionRoot string) (*Envelope, error) {
		return nil, errors.Join(primary, cleanupFailure(extractionRoot))
	}
	if err := candidate.Chmod(0o600); err != nil {
		return fail(fmt.Errorf("secure upload candidate: %w", err), "")
	}

	hash := sha256.New()
	limited := io.LimitReader(&contextReader{ctx: ctx, reader: body}, receiver.limits.CompressedBytes+1)
	diskGuard := &diskGuardWriter{writer: candidate, root: receiver.stagingRoot, availableBytes: receiver.availableBytes, safetyFloor: extractionReservation}
	written, copyErr := io.CopyBuffer(io.MultiWriter(diskGuard, hash), limited, make([]byte, 64<<10))
	if copyErr == nil && written <= receiver.limits.CompressedBytes {
		copyErr = candidate.Sync()
	}
	if copyErr == nil {
		copyErr = ctx.Err()
	}
	if copyErr != nil {
		return fail(fmt.Errorf("receive upload: %w", copyErr), "")
	}
	if written > receiver.limits.CompressedBytes {
		return fail(ErrCompressedLimit, "")
	}
	if contentLength >= 0 && written != contentLength {
		return fail(fmt.Errorf("%w: content length mismatch", ErrInvalidArchive), "")
	}
	layout, err := preflightCentralDirectory(candidate, written, receiver.limits.Entries)
	if err != nil {
		return fail(err, "")
	}
	actualCompressed, err := layout.validateLocalRecords(candidate)
	if err != nil {
		return fail(err, "")
	}

	archive, err := zip.NewReader(candidate, written)
	if err != nil {
		return fail(fmt.Errorf("open ZIP parser: %w", classifyArchiveParserError(err)), "")
	}
	if len(archive.File) != len(layout.records) {
		return fail(fmt.Errorf("%w: parsed central directory count changed", ErrInvalidArchive), "")
	}
	headers := make([]entryHeader, len(archive.File))
	for index, file := range archive.File {
		record := layout.records[index]
		if file.Name != record.name || file.Flags != record.flags || file.Method != record.method || file.CRC32 != record.crc32 ||
			file.CompressedSize64 != uint64(record.compressedSize) || file.UncompressedSize64 != uint64(record.uncompressedSize) {
			return fail(fmt.Errorf("%w: central directory parser disagreement", ErrInvalidArchive), "")
		}
		headers[index] = headerFor(file)
	}
	if err := validateHeaders(headers, written, actualCompressed, receiver.limits); err != nil {
		return fail(err, "")
	}
	var declaredExpanded uint64
	for _, header := range headers {
		declaredExpanded += header.UncompressedSize
	}
	available, err = receiver.availableBytes(receiver.stagingRoot)
	if err != nil {
		return fail(fmt.Errorf("check disk space: %w", err), "")
	}
	if available < declaredExpanded {
		return fail(ErrInsufficientDisk, "")
	}
	if err := ctx.Err(); err != nil {
		return fail(err, "")
	}

	extractionRoot, err := os.MkdirTemp(receiver.stagingRoot, ".extract-")
	if err != nil {
		return fail(fmt.Errorf("create extraction root: %w", err), "")
	}
	if err := os.Chmod(extractionRoot, 0o700); err != nil {
		return fail(fmt.Errorf("secure extraction root: %w", err), extractionRoot)
	}
	entries, expanded, extractErr := receiver.extract(ctx, archive.File, extractionRoot)
	if extractErr != nil {
		return fail(extractErr, extractionRoot)
	}
	if err := ctx.Err(); err != nil {
		return fail(err, extractionRoot)
	}
	if err := candidate.Close(); err != nil {
		return fail(fmt.Errorf("close upload: %w", err), extractionRoot)
	}
	candidate = nil
	if err := ctx.Err(); err != nil {
		return fail(err, extractionRoot)
	}

	return &Envelope{
		ArchivePath: archivePath, ExtractionRoot: extractionRoot,
		SHA256: hex.EncodeToString(hash.Sum(nil)), CompressedBytes: written, ExpandedBytes: expanded,
		Entries: entries, Verified: false, cleanupRoot: receiver.stagingRoot,
		cleanupArchive: archivePath, cleanupExtraction: extractionRoot,
	}, nil
}

func (receiver *Receiver) reservations(contentLength int64) (uint64, uint64) {
	if contentLength < 0 {
		return uint64(receiver.limits.CompressedBytes), uint64(receiver.limits.ExpandedBytes)
	}
	archive := uint64(contentLength)
	expanded := archive * uint64(receiver.limits.CompressionRatio)
	if archive != 0 && expanded/archive != uint64(receiver.limits.CompressionRatio) {
		expanded = uint64(receiver.limits.ExpandedBytes)
	}
	if expanded > uint64(receiver.limits.ExpandedBytes) {
		expanded = uint64(receiver.limits.ExpandedBytes)
	}
	return archive, expanded
}

type diskGuardWriter struct {
	writer         io.Writer
	root           string
	availableBytes AvailableBytesFunc
	safetyFloor    uint64
}

func (writer *diskGuardWriter) Write(data []byte) (int, error) {
	available, err := writer.availableBytes(writer.root)
	if err != nil {
		return 0, fmt.Errorf("check upload disk floor: %w", err)
	}
	if uint64(len(data)) > ^uint64(0)-writer.safetyFloor || available < writer.safetyFloor+uint64(len(data)) {
		return 0, ErrInsufficientDisk
	}
	return writer.writer.Write(data)
}

func (receiver *Receiver) extract(ctx context.Context, files []*zip.File, root string) ([]Entry, int64, error) {
	entries := make([]Entry, 0, len(files))
	var total int64
	for _, archived := range files {
		if err := ctx.Err(); err != nil {
			return nil, 0, err
		}
		input, err := archived.Open()
		if err != nil {
			return nil, 0, fmt.Errorf("open ZIP entry %q: %w", archived.Name, classifyArchiveDataError(err))
		}
		output, path, err := createOutputFile(root, archived.Name)
		if err != nil {
			_ = input.Close()
			return nil, 0, fmt.Errorf("create ZIP entry %q: %w", archived.Name, err)
		}
		maximum := int64(archived.UncompressedSize64)
		checksum := crc32.NewIEEE()
		copied, copyErr := io.CopyBuffer(io.MultiWriter(output, checksum), io.LimitReader(&contextReader{ctx: ctx, reader: input}, maximum+1), make([]byte, 64<<10))
		if copyErr == nil && copied <= maximum {
			copyErr = output.Sync()
		}
		outputCloseErr := output.Close()
		inputCloseErr := input.Close()
		if copyErr != nil {
			return nil, 0, fmt.Errorf("extract ZIP entry %q: %w", archived.Name, classifyArchiveDataError(copyErr))
		}
		if outputCloseErr != nil {
			return nil, 0, fmt.Errorf("close extracted file %q: %w", archived.Name, outputCloseErr)
		}
		if inputCloseErr != nil {
			return nil, 0, fmt.Errorf("close ZIP entry %q: %w", archived.Name, classifyArchiveDataError(inputCloseErr))
		}
		if copied != maximum {
			return nil, 0, fmt.Errorf("%w: ZIP entry %q size mismatch", ErrInvalidArchive, archived.Name)
		}
		if checksum.Sum32() != archived.CRC32 {
			return nil, 0, fmt.Errorf("%w: ZIP entry %q checksum mismatch", ErrInvalidArchive, archived.Name)
		}
		if copied > receiver.limits.SingleFileBytes || total > receiver.limits.ExpandedBytes-copied {
			return nil, 0, ErrExpandedLimit
		}
		total += copied
		entries = append(entries, Entry{Name: archived.Name, Path: path, Size: copied, CompressedSize: int64(archived.CompressedSize64)})
	}
	return entries, total, nil
}

func classifyArchiveParserError(err error) error {
	if err == nil || errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return err
	}
	var pathError *os.PathError
	if errors.As(err, &pathError) {
		return err
	}
	return fmt.Errorf("%w: %v", ErrInvalidArchive, err)
}

func classifyArchiveDataError(err error) error {
	if err == nil || errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return err
	}
	var pathError *os.PathError
	if errors.As(err, &pathError) {
		return err
	}
	var corrupt flate.CorruptInputError
	if errors.Is(err, zip.ErrFormat) || errors.Is(err, zip.ErrAlgorithm) || errors.Is(err, zip.ErrChecksum) ||
		errors.Is(err, io.ErrUnexpectedEOF) || errors.As(err, &corrupt) {
		return fmt.Errorf("%w: %v", ErrInvalidArchive, err)
	}
	return err
}

func (envelope *Envelope) Cleanup() error {
	if envelope == nil || envelope.cleanupRoot == "" {
		return nil
	}
	if !isDirectChild(envelope.cleanupRoot, envelope.cleanupArchive) || !isDirectChild(envelope.cleanupRoot, envelope.cleanupExtraction) {
		return errors.New("refusing to clean paths outside staging root")
	}
	return errors.Join(os.RemoveAll(envelope.cleanupExtraction), removeFileIfPresent(envelope.cleanupArchive))
}

func removeFileIfPresent(path string) error {
	err := os.Remove(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}

func isDirectChild(root, path string) bool {
	return path != "" && filepath.Dir(filepath.Clean(path)) == filepath.Clean(root)
}

type contextReader struct {
	ctx    context.Context
	reader io.Reader
}

func (reader *contextReader) Read(buffer []byte) (int, error) {
	if err := reader.ctx.Err(); err != nil {
		return 0, err
	}
	read, err := reader.reader.Read(buffer)
	if err == nil {
		if contextErr := reader.ctx.Err(); contextErr != nil {
			return read, contextErr
		}
	}
	return read, err
}
