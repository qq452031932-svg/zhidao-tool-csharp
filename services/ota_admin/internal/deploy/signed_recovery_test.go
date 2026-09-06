package deploy

import (
	"archive/zip"
	"context"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"errors"
	"io"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

func TestStableRecoveryRunsTheFullSignedGenerationValidator(t *testing.T) {
	envelope, validator := signedGenerationFixture(t)
	report, err := validator.Validate(context.Background(), envelope, "")
	if err != nil {
		t.Fatal(err)
	}
	disk, err := NewDisk(publicationRoot(t), nil)
	if err != nil {
		t.Fatal(err)
	}
	generation, switched, err := disk.PublishGeneration(context.Background(), envelope, testReleaseID, report, validator)
	if symlinkUnavailable(err) {
		t.Skipf("symbolic links unavailable: %v", err)
	}
	if err != nil || !switched {
		t.Fatalf("generation=%+v switched=%v err=%v", generation, switched, err)
	}
	recovered, err := disk.RecoverGeneration(context.Background(), validator)
	if err != nil || recovered.Name != generation.Name || !reflect.DeepEqual(recovered.Report, report) || !reflect.DeepEqual(recovered.Assets, generation.Assets) {
		t.Fatalf("recovered=%+v err=%v", recovered, err)
	}
}

func signedGenerationFixture(t *testing.T) (*upload.Envelope, *release.Validator) {
	t.Helper()
	_, source, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("resolve test source path")
	}
	moduleRoot := filepath.Clean(filepath.Join(filepath.Dir(source), "..", ".."))
	fixtureRoot := filepath.Join(moduleRoot, "cmd", "ota-admin-acceptance", "testdata")
	certificateBytes, err := os.ReadFile(filepath.Join(fixtureRoot, "publisher.cer"))
	if err != nil {
		t.Fatal(err)
	}
	certificate, err := x509.ParseCertificate(certificateBytes)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256(certificate.RawSubjectPublicKeyInfo)
	trust, err := publisher.NewTrust(certificate.RawSubjectPublicKeyInfo, hex.EncodeToString(digest[:]))
	if err != nil {
		t.Fatal(err)
	}
	bundlePath := filepath.Join(fixtureRoot, "valid-upload.zip")
	archive, err := zip.OpenReader(bundlePath)
	if err != nil {
		t.Fatal(err)
	}
	defer archive.Close()
	extraction := t.TempDir()
	envelope := &upload.Envelope{ArchivePath: bundlePath, ExtractionRoot: extraction}
	for _, archived := range archive.File {
		if archived.Name != filepath.Base(archived.Name) {
			t.Fatalf("signed fixture entry is not flat: %q", archived.Name)
		}
		input, err := archived.Open()
		if err != nil {
			t.Fatal(err)
		}
		path := filepath.Join(extraction, archived.Name)
		output, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
		if err != nil {
			input.Close()
			t.Fatal(err)
		}
		_, copyErr := io.Copy(output, input)
		closeOutputErr := output.Close()
		closeInputErr := input.Close()
		if copyErr != nil || closeOutputErr != nil || closeInputErr != nil {
			t.Fatal(errors.Join(copyErr, closeOutputErr, closeInputErr))
		}
		envelope.Entries = append(envelope.Entries, upload.Entry{Name: archived.Name, Path: path, Size: int64(archived.UncompressedSize64)})
		envelope.ExpandedBytes += int64(archived.UncompressedSize64)
	}
	return envelope, release.NewValidator(trust)
}
