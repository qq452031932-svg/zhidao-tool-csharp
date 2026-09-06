//go:build linux

package deploy

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"os"
	"path/filepath"
	"reflect"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

func TestRealSignedGenerationPublishesAndRecoversThroughBoundDescriptor(t *testing.T) {
	fixtureRoot := filepath.Join("..", "..", "cmd", "ota-admin-acceptance", "testdata")
	bundle, err := os.ReadFile(filepath.Join(fixtureRoot, "valid-upload.zip"))
	if err != nil {
		t.Fatal(err)
	}
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
	validator := release.NewValidator(trust)
	staging := filepath.Join(t.TempDir(), "staging")
	if err := os.Mkdir(staging, 0o700); err != nil {
		t.Fatal(err)
	}
	receiver, err := upload.NewReceiver(upload.ReceiverOptions{StagingRoot: staging, Limits: upload.DefaultLimits()})
	if err != nil {
		t.Fatal(err)
	}
	envelope, err := receiver.Receive(context.Background(), bytes.NewReader(bundle), int64(len(bundle)))
	if err != nil {
		t.Fatal(err)
	}
	defer envelope.Cleanup()
	report, err := validator.Validate(context.Background(), envelope, "")
	if err != nil {
		t.Fatal(err)
	}
	root := publicationRoot(t)
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	generation, switched, err := disk.PublishGeneration(context.Background(), envelope, testReleaseID, report, validator)
	if err != nil || !switched || generation.Report.Version != "1.2.3" {
		t.Fatalf("publish switched=%v generation=%+v err=%v", switched, generation, err)
	}
	restarted, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	recovered, err := restarted.RecoverGeneration(context.Background(), validator)
	if err != nil {
		t.Fatal(err)
	}
	if recovered.Name != generation.Name || !reflect.DeepEqual(recovered.Report, report) {
		t.Fatalf("recovered=%+v, want generation=%+v", recovered, generation)
	}
}
