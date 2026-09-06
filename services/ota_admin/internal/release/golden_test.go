package release

import (
	"archive/zip"
	"context"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"io"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

func TestCommittedPowerShellProductionGoldenBundle(t *testing.T) {
	_, source, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("resolve test source path")
	}
	moduleRoot := filepath.Clean(filepath.Join(filepath.Dir(source), "..", ".."))
	fixtureRoot := filepath.Join(moduleRoot, "cmd", "ota-admin-acceptance", "testdata")
	bundlePath := filepath.Join(fixtureRoot, "valid-upload.zip")
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
	validatePowerShellGolden(t, bundlePath, trust, "1.2.3")
}

func TestOptionalLargeProductionCandidate(t *testing.T) {
	bundlePath := os.Getenv("SUPERPAN_OTA_GOLDEN_ZIP")
	if bundlePath == "" {
		t.Skip("SUPERPAN_OTA_GOLDEN_ZIP is not set")
	}
	trust, err := publisher.ProductionTrust()
	if err != nil {
		t.Fatal(err)
	}
	validatePowerShellGolden(t, bundlePath, trust, "")
}

func validatePowerShellGolden(t *testing.T, bundlePath string, trust *publisher.Trust, expectedVersion string) {
	t.Helper()
	archive, err := zip.OpenReader(bundlePath)
	if err != nil {
		t.Fatal(err)
	}
	defer archive.Close()
	root := t.TempDir()
	envelope := &upload.Envelope{ArchivePath: bundlePath, ExtractionRoot: root}
	for _, archived := range archive.File {
		if archived.Name != filepath.Base(archived.Name) {
			t.Fatalf("golden entry is not flat: %q", archived.Name)
		}
		input, err := archived.Open()
		if err != nil {
			t.Fatal(err)
		}
		path := filepath.Join(root, archived.Name)
		output, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
		if err != nil {
			input.Close()
			t.Fatal(err)
		}
		_, copyErr := io.Copy(output, input)
		closeErr := output.Close()
		inputErr := input.Close()
		if copyErr != nil || closeErr != nil || inputErr != nil {
			t.Fatalf("extract %q: copy=%v close=%v input=%v", archived.Name, copyErr, closeErr, inputErr)
		}
		envelope.Entries = append(envelope.Entries, upload.Entry{Name: archived.Name, Path: path, Size: int64(archived.UncompressedSize64)})
	}
	report, err := NewValidator(trust).Validate(context.Background(), envelope, "")
	if err != nil {
		t.Fatal(err)
	}
	rootDescriptor, err := os.Open(root)
	if err != nil {
		t.Fatal(err)
	}
	descriptorReport, descriptorErr := NewValidator(trust).ValidateOpenedDirectory(context.Background(), envelope, rootDescriptor, "")
	closeErr := rootDescriptor.Close()
	if descriptorErr != nil || closeErr != nil {
		t.Fatalf("descriptor validation=%v close=%v", descriptorErr, closeErr)
	}
	if !reflect.DeepEqual(descriptorReport, report) {
		t.Fatalf("descriptor report differs from path report")
	}
	if !report.Verified || report.PackageID != packageID || report.Channel != channel {
		t.Fatalf("report = %#v", report)
	}
	if expectedVersion != "" && report.Version != expectedVersion {
		t.Fatalf("version = %q, want %q", report.Version, expectedVersion)
	}
}
