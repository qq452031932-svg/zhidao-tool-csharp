package release

import (
	"archive/zip"
	"context"
	"io"
	"os"
	"path/filepath"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

func TestPowerShellProductionGoldenBundle(t *testing.T) {
	bundlePath := os.Getenv("SUPERPAN_OTA_GOLDEN_ZIP")
	if bundlePath == "" {
		t.Skip("SUPERPAN_OTA_GOLDEN_ZIP is not set")
	}
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
	trust, err := publisher.ProductionTrust()
	if err != nil {
		t.Fatal(err)
	}
	report, err := NewValidator(trust).Validate(context.Background(), envelope, "")
	if err != nil {
		t.Fatal(err)
	}
	if !report.Verified || report.PackageID != packageID || report.Channel != channel {
		t.Fatalf("report = %#v", report)
	}
}
