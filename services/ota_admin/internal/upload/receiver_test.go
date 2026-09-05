package upload

import (
	"archive/zip"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestReceiveStreamsValidZIPIntoPrivateRandomPaths(t *testing.T) {
	staging := t.TempDir()
	archive := makeZIP(t, map[string]string{
		"upload-manifest.sha256":                       "manifest",
		"upload-manifest.sha256.sig":                   "signature",
		"releases.win-x64-stable.json":                 "release",
		"assets.win-x64-stable.json":                   "assets",
		"RELEASES-win-x64-stable":                      "index",
		"SuperPan.BaiduBatchTransfer-Setup.exe":        "setup",
		"SuperPan.BaiduBatchTransfer-Portable.zip":     "portable",
		"SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg": "package",
	})
	receiver := newTestReceiver(t, staging, DefaultLimits(), func(string) (uint64, error) { return 1 << 40, nil })
	envelope, err := receiver.Receive(context.Background(), bytes.NewReader(archive), int64(len(archive)))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = envelope.Cleanup() })
	if envelope.Verified {
		t.Fatal("ZIP envelope must not be marked authenticity-verified")
	}
	if envelope.ArchivePath == "" || envelope.ExtractionRoot == "" || filepath.Dir(envelope.ArchivePath) != staging || filepath.Dir(envelope.ExtractionRoot) != staging {
		t.Fatalf("paths escaped staging root: %#v", envelope)
	}
	if filepath.Base(envelope.ArchivePath) == "candidate.zip" || filepath.Base(envelope.ExtractionRoot) == "candidate" {
		t.Fatalf("paths were not randomly named: %#v", envelope)
	}
	wantDigest := sha256.Sum256(archive)
	if envelope.SHA256 != hex.EncodeToString(wantDigest[:]) || envelope.CompressedBytes != int64(len(archive)) {
		t.Fatalf("wrong archive metadata: %#v", envelope)
	}
	if len(envelope.Entries) != 8 {
		t.Fatalf("entries=%d", len(envelope.Entries))
	}
	for _, entry := range envelope.Entries {
		data, readErr := os.ReadFile(entry.Path)
		if readErr != nil || int64(len(data)) != entry.Size {
			t.Fatalf("entry %q data=%q error=%v metadata=%#v", entry.Name, data, readErr, entry)
		}
		if filepath.Dir(entry.Path) != envelope.ExtractionRoot {
			t.Fatalf("entry escaped extraction root: %#v", entry)
		}
	}
	assertMode(t, envelope.ArchivePath, 0o600)
	assertMode(t, envelope.ExtractionRoot, 0o700)
}

func TestReceiveCleansEveryArtifactAfterInterruptedBody(t *testing.T) {
	staging := t.TempDir()
	receiver := newTestReceiver(t, staging, Limits{CompressedBytes: 64, ExpandedBytes: 64, SingleFileBytes: 64, Entries: 8, CompressionRatio: 100}, func(string) (uint64, error) { return 1024, nil })
	broken := &interruptingReader{data: []byte("not a complete zip"), err: errors.New("connection reset")}
	if _, err := receiver.Receive(context.Background(), broken, -1); err == nil || !strings.Contains(err.Error(), "connection reset") {
		t.Fatalf("interruption error=%v", err)
	}
	assertStagingEmpty(t, staging)
}

func TestReceiveRejectsStreamingOverflowAndCleansCandidate(t *testing.T) {
	staging := t.TempDir()
	limits := Limits{CompressedBytes: 8, ExpandedBytes: 64, SingleFileBytes: 64, Entries: 8, CompressionRatio: 100}
	receiver := newTestReceiver(t, staging, limits, func(string) (uint64, error) { return 1024, nil })
	if _, err := receiver.Receive(context.Background(), strings.NewReader("123456789"), -1); !errors.Is(err, ErrCompressedLimit) {
		t.Fatalf("overflow error=%v", err)
	}
	assertStagingEmpty(t, staging)
}

func TestReceiveRejectsAdvertisedOverflowBeforeReadingOrWriting(t *testing.T) {
	staging := t.TempDir()
	limits := Limits{CompressedBytes: 8, ExpandedBytes: 64, SingleFileBytes: 64, Entries: 8, CompressionRatio: 100}
	receiver := newTestReceiver(t, staging, limits, func(string) (uint64, error) { return 1024, nil })
	reader := &countingReader{}
	if _, err := receiver.Receive(context.Background(), reader, 9); !errors.Is(err, ErrCompressedLimit) {
		t.Fatalf("overflow error=%v", err)
	}
	if reader.reads != 0 {
		t.Fatalf("body read %d times", reader.reads)
	}
	assertStagingEmpty(t, staging)
}

func TestReceiveRejectsCentralDirectoryEntryBombBeforeZIPParser(t *testing.T) {
	staging := t.TempDir()
	archive := makeZIP(t, map[string]string{"upload-manifest.sha256": "manifest"})
	eocd := bytes.LastIndex(archive, []byte{'P', 'K', 5, 6})
	if eocd < 0 {
		t.Fatal("test ZIP has no EOCD")
	}
	binary.LittleEndian.PutUint16(archive[eocd+8:eocd+10], 0xffff)
	binary.LittleEndian.PutUint16(archive[eocd+10:eocd+12], 0xffff)
	receiver := newTestReceiver(t, staging, DefaultLimits(), func(string) (uint64, error) { return 1 << 40, nil })
	if _, err := receiver.Receive(context.Background(), bytes.NewReader(archive), int64(len(archive))); !errors.Is(err, ErrEntryLimit) {
		t.Fatalf("entry bomb error=%v", err)
	}
	assertStagingEmpty(t, staging)
}

func TestReceiveChecksDiskBeforeCreatingExtractionRootAndCleansZIP(t *testing.T) {
	staging := t.TempDir()
	archive := makeZIP(t, map[string]string{"upload-manifest.sha256": "123456789"})
	probes := 0
	receiver := newTestReceiver(t, staging, DefaultLimits(), func(path string) (uint64, error) {
		probes++
		if path != staging {
			t.Fatalf("disk probe path=%q", path)
		}
		return 8, nil
	})
	if _, err := receiver.Receive(context.Background(), bytes.NewReader(archive), int64(len(archive))); !errors.Is(err, ErrInsufficientDisk) {
		t.Fatalf("disk error=%v", err)
	}
	if probes != 1 {
		t.Fatalf("disk probes=%d", probes)
	}
	assertStagingEmpty(t, staging)
}

func TestReceiveHonorsCancellationAndCleansArtifacts(t *testing.T) {
	staging := t.TempDir()
	receiver := newTestReceiver(t, staging, DefaultLimits(), func(string) (uint64, error) { return 1 << 40, nil })
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := receiver.Receive(ctx, strings.NewReader("zip"), -1); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation error=%v", err)
	}
	assertStagingEmpty(t, staging)
}

func TestReceiveRejectsActualAdversarialZIPsAndCleansArtifacts(t *testing.T) {
	tests := []struct {
		name    string
		archive func(*testing.T) []byte
		limits  Limits
		want    error
	}{
		{"traversal", func(t *testing.T) []byte { return makeZIP(t, map[string]string{"../evil": "x"}) }, DefaultLimits(), ErrUnsafeName},
		{"nested", func(t *testing.T) []byte { return makeZIP(t, map[string]string{"a/evil": "x"}) }, DefaultLimits(), ErrUnsafeName},
		{"directory", func(t *testing.T) []byte { return makeSpecialZIP(t, "directory") }, DefaultLimits(), ErrUnsafeEntry},
		{"symlink", func(t *testing.T) []byte { return makeSpecialZIP(t, "symlink") }, DefaultLimits(), ErrUnsafeEntry},
		{"duplicate", func(t *testing.T) []byte { return makeSpecialZIP(t, "duplicate") }, DefaultLimits(), ErrDuplicateName},
		{"case collision", func(t *testing.T) []byte { return makeSpecialZIP(t, "case-collision") }, DefaultLimits(), ErrDuplicateName},
		{"unknown", func(t *testing.T) []byte { return makeZIP(t, map[string]string{"unknown.bin": "x"}) }, DefaultLimits(), ErrUnknownName},
		{"compression ratio", func(t *testing.T) []byte {
			return makeZIP(t, map[string]string{"upload-manifest.sha256": strings.Repeat("a", 10_000)})
		}, DefaultLimits(), ErrCompressionRatio},
		{"entry count", func(t *testing.T) []byte {
			return makeZIP(t, map[string]string{"upload-manifest.sha256": "a", "upload-manifest.sha256.sig": "b"})
		}, Limits{CompressedBytes: 1 << 20, ExpandedBytes: 1 << 20, SingleFileBytes: 1 << 20, Entries: 1, CompressionRatio: 100}, ErrEntryLimit},
		{"corrupt", func(*testing.T) []byte { return []byte("not a zip") }, DefaultLimits(), ErrInvalidArchive},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			staging := t.TempDir()
			archive := test.archive(t)
			receiver := newTestReceiver(t, staging, test.limits, func(string) (uint64, error) { return 1 << 40, nil })
			if _, err := receiver.Receive(context.Background(), bytes.NewReader(archive), int64(len(archive))); !errors.Is(err, test.want) {
				t.Fatalf("error=%v want=%v", err, test.want)
			}
			assertStagingEmpty(t, staging)
		})
	}
}

func TestReceiveCleansZIPWhenDiskProbeFails(t *testing.T) {
	staging := t.TempDir()
	archive := makeZIP(t, map[string]string{"upload-manifest.sha256": "manifest"})
	receiver := newTestReceiver(t, staging, DefaultLimits(), func(string) (uint64, error) { return 0, errors.New("statfs failed") })
	if _, err := receiver.Receive(context.Background(), bytes.NewReader(archive), int64(len(archive))); err == nil || !strings.Contains(err.Error(), "statfs failed") {
		t.Fatalf("disk probe error=%v", err)
	}
	assertStagingEmpty(t, staging)
}

func TestEnvelopeCleanupIsIdempotentAndUsesImmutableCreatedPaths(t *testing.T) {
	staging := t.TempDir()
	archive := makeZIP(t, map[string]string{"upload-manifest.sha256": "manifest"})
	receiver := newTestReceiver(t, staging, DefaultLimits(), func(string) (uint64, error) { return 1 << 40, nil })
	envelope, err := receiver.Receive(context.Background(), bytes.NewReader(archive), int64(len(archive)))
	if err != nil {
		t.Fatal(err)
	}
	envelope.ArchivePath = filepath.Join(t.TempDir(), "must-not-delete")
	envelope.ExtractionRoot = filepath.Join(t.TempDir(), "must-not-delete")
	if err := envelope.Cleanup(); err != nil {
		t.Fatal(err)
	}
	if err := envelope.Cleanup(); err != nil {
		t.Fatal(err)
	}
	assertStagingEmpty(t, staging)
}

func TestConcurrentReceivesNeverShareCandidateOrExtractionPaths(t *testing.T) {
	staging := t.TempDir()
	archive := makeZIP(t, map[string]string{"upload-manifest.sha256": "manifest"})
	receiver := newTestReceiver(t, staging, DefaultLimits(), func(string) (uint64, error) { return 1 << 40, nil })
	const count = 24
	results := make(chan *Envelope, count)
	errorsFound := make(chan error, count)
	var group sync.WaitGroup
	for range count {
		group.Add(1)
		go func() {
			defer group.Done()
			envelope, err := receiver.Receive(context.Background(), bytes.NewReader(archive), int64(len(archive)))
			if err != nil {
				errorsFound <- err
				return
			}
			results <- envelope
		}()
	}
	group.Wait()
	close(results)
	close(errorsFound)
	for err := range errorsFound {
		t.Errorf("receive: %v", err)
	}
	seen := map[string]bool{}
	for result := range results {
		for _, path := range []string{result.ArchivePath, result.ExtractionRoot} {
			if seen[path] {
				t.Errorf("path reused: %s", path)
			}
			seen[path] = true
		}
		if err := result.Cleanup(); err != nil {
			t.Errorf("cleanup: %v", err)
		}
	}
}

func TestConcurrentReceivesSerializeDiskPreflightThroughExtraction(t *testing.T) {
	staging := t.TempDir()
	archive := makeZIP(t, map[string]string{"upload-manifest.sha256": "123456789"})
	limits := Limits{CompressedBytes: 1 << 20, ExpandedBytes: 9, SingleFileBytes: 9, Entries: 1, CompressionRatio: 100}
	available := func(root string) (uint64, error) {
		var used uint64
		roots, err := filepath.Glob(filepath.Join(root, ".extract-*"))
		if err != nil {
			return 0, err
		}
		for _, extractionRoot := range roots {
			err = filepath.Walk(extractionRoot, func(path string, info os.FileInfo, walkErr error) error {
				if walkErr == nil && info.Mode().IsRegular() {
					used += uint64(info.Size())
				}
				return walkErr
			})
			if err != nil {
				return 0, err
			}
		}
		// Make concurrent unsynchronized probes reliably overlap.
		time.Sleep(25 * time.Millisecond)
		if used >= 9 {
			return 0, nil
		}
		return 9 - used, nil
	}
	receiver := newTestReceiver(t, staging, limits, available)
	start := make(chan struct{})
	results := make(chan *Envelope, 2)
	errorsFound := make(chan error, 2)
	var group sync.WaitGroup
	for range 2 {
		group.Add(1)
		go func() {
			defer group.Done()
			<-start
			envelope, err := receiver.Receive(context.Background(), bytes.NewReader(archive), int64(len(archive)))
			if err != nil {
				errorsFound <- err
				return
			}
			results <- envelope
		}()
	}
	close(start)
	group.Wait()
	close(results)
	close(errorsFound)
	var successes, diskFailures int
	for result := range results {
		successes++
		if err := result.Cleanup(); err != nil {
			t.Fatal(err)
		}
	}
	for err := range errorsFound {
		if errors.Is(err, ErrInsufficientDisk) {
			diskFailures++
		} else {
			t.Fatalf("unexpected error: %v", err)
		}
	}
	if successes != 1 || diskFailures != 1 {
		t.Fatalf("successes=%d disk failures=%d", successes, diskFailures)
	}
}

func makeZIP(t *testing.T, files map[string]string) []byte {
	t.Helper()
	var buffer bytes.Buffer
	archive := zip.NewWriter(&buffer)
	for name, value := range files {
		entry, err := archive.Create(name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := io.WriteString(entry, value); err != nil {
			t.Fatal(err)
		}
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return buffer.Bytes()
}

func makeSpecialZIP(t *testing.T, kind string) []byte {
	t.Helper()
	var buffer bytes.Buffer
	archive := zip.NewWriter(&buffer)
	names := []string{"upload-manifest.sha256"}
	if kind == "duplicate" {
		names = append(names, "upload-manifest.sha256")
	}
	if kind == "case-collision" {
		names = append(names, "UPLOAD-MANIFEST.SHA256")
	}
	for _, name := range names {
		header := &zip.FileHeader{Name: name, Method: zip.Store}
		switch kind {
		case "directory":
			header.Name = "directory/"
			header.SetMode(os.ModeDir | 0o700)
		case "symlink":
			header.SetMode(os.ModeSymlink | 0o777)
		default:
			header.SetMode(0o600)
		}
		entry, err := archive.CreateHeader(header)
		if err != nil {
			t.Fatal(err)
		}
		if kind != "directory" {
			_, err = io.WriteString(entry, "x")
		}
		if err != nil {
			t.Fatal(err)
		}
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return buffer.Bytes()
}

func newTestReceiver(t *testing.T, staging string, limits Limits, disk func(string) (uint64, error)) *Receiver {
	t.Helper()
	receiver, err := NewReceiver(ReceiverOptions{StagingRoot: staging, Limits: limits, AvailableBytes: disk})
	if err != nil {
		t.Fatal(err)
	}
	return receiver
}

func assertStagingEmpty(t *testing.T, root string) {
	t.Helper()
	entries, err := os.ReadDir(root)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 0 {
		t.Fatalf("staging artifacts remain: %v", entries)
	}
}

func assertMode(t *testing.T, path string, want os.FileMode) {
	t.Helper()
	if runtime.GOOS == "windows" {
		return
	}
	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if got := info.Mode().Perm(); got != want {
		t.Fatalf("%s mode=%#o want=%#o", path, got, want)
	}
}

type interruptingReader struct {
	data []byte
	err  error
	done bool
}

func (reader *interruptingReader) Read(buffer []byte) (int, error) {
	if !reader.done {
		reader.done = true
		return copy(buffer, reader.data), nil
	}
	return 0, reader.err
}

type countingReader struct{ reads int }

func (reader *countingReader) Read([]byte) (int, error) {
	reader.reads++
	return 0, io.EOF
}
