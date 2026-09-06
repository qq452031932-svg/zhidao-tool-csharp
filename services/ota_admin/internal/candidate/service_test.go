package candidate

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

const (
	testSessionID   = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
	testOperationID = "22222222-2222-4222-8222-222222222222"
)

func TestAcceptEnvelopeValidatesAdoptsAndPersistsForActiveSession(t *testing.T) {
	service, database, staging, now := newCandidateHarness(t)
	envelope := receiveGolden(t, staging)
	originalArchive, originalExtraction := envelope.ArchivePath, envelope.ExtractionRoot

	summary, err := service.AcceptEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, envelope)
	if err != nil {
		t.Fatal(err)
	}
	if summary.CandidateID == "" || summary.OperationID != testOperationID || summary.State != string(store.CandidateValidated) ||
		summary.Report.Version != "1.2.3" || !summary.Report.Verified || !summary.ExpiresAt.Equal(now.Add(24*time.Hour)) {
		t.Fatalf("summary=%+v", summary)
	}
	paths := service.paths(summary.CandidateID)
	if envelope.ArchivePath != paths.archive || envelope.ExtractionRoot != paths.extraction || envelope.Verified {
		t.Fatalf("adopted envelope=%+v paths=%+v", envelope, paths)
	}
	if _, err := os.Stat(paths.archive); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(paths.extraction); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(originalArchive); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("original archive still present: %v", err)
	}
	if _, err := os.Stat(originalExtraction); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("original extraction still present: %v", err)
	}
	record, found, err := database.CandidateByOperationForActiveSession(context.Background(), testSessionID, testOperationID, now)
	if err != nil || !found || record.ID != summary.CandidateID || record.UploadDigest != envelope.SHA256 {
		t.Fatalf("record=%+v found=%v err=%v", record, found, err)
	}
}

func TestCandidateOperationLookupIsIdempotentAndSessionBound(t *testing.T) {
	service, database, staging, now := newCandidateHarness(t)
	envelope := receiveGolden(t, staging)
	accepted, err := service.AcceptEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, envelope)
	if err != nil {
		t.Fatal(err)
	}
	replayed, found, err := service.CandidateForOperationForActiveSession(context.Background(), testSessionID, testOperationID)
	if err != nil || !found || replayed.CandidateID != accepted.CandidateID {
		t.Fatalf("replayed=%+v found=%v err=%v", replayed, found, err)
	}
	retryEnvelope := receiveGolden(t, staging)
	retryArchive, retryExtraction := retryEnvelope.ArchivePath, retryEnvelope.ExtractionRoot
	retried, err := service.AcceptEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, retryEnvelope)
	if err != nil || retried.CandidateID != accepted.CandidateID {
		t.Fatalf("retried=%+v err=%v", retried, err)
	}
	for _, path := range []string{retryArchive, retryExtraction} {
		if _, err := os.Lstat(path); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("idempotent retry envelope was not cleaned: %s err=%v", path, err)
		}
	}
	other := strings.Repeat("b", 64)
	if err := database.CreateSession(context.Background(), store.Session{IDHash: other, State: store.SessionActive, CreatedAt: now, LastSeenAt: now, IdleExpiresAt: now.Add(time.Hour), AbsoluteExpiresAt: now.Add(2 * time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if _, _, err := service.CandidateForOperationForActiveSession(context.Background(), other, testOperationID); !errors.Is(err, session.ErrUnauthorized) {
		t.Fatalf("cross-session err=%v", err)
	}
}

func TestCurrentCandidateDistinguishesAnEmptyActiveSession(t *testing.T) {
	service, _, _, _ := newCandidateHarness(t)
	if _, err := service.CurrentForActiveSession(context.Background(), testSessionID); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("err=%v, want not found", err)
	}
}

func TestAcceptEnvelopeRechecksSessionTransactionallyAndDoesNotAdoptOnKnownRevocation(t *testing.T) {
	service, database, staging, _ := newCandidateHarness(t)
	envelope := receiveGolden(t, staging)
	if err := database.TransitionSession(context.Background(), testSessionID, store.SessionRevoked); err != nil {
		t.Fatal(err)
	}
	if _, err := service.AcceptEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, envelope); !errors.Is(err, session.ErrUnauthorized) {
		t.Fatalf("err=%v", err)
	}
	if _, err := os.Stat(envelope.ArchivePath); err != nil {
		t.Fatalf("caller-owned envelope was removed: %v", err)
	}
}

func TestRevalidateForConfirmRehashesArchiveAndFullExtractedClosure(t *testing.T) {
	t.Run("accepted", func(t *testing.T) {
		service, _, staging, _ := newCandidateHarness(t)
		summary, err := service.AcceptEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
		if err != nil {
			t.Fatal(err)
		}
		prepared, err := service.RevalidateForConfirm(context.Background(), testSessionID, summary.CandidateID)
		if err != nil || prepared.Summary.CandidateID != summary.CandidateID || prepared.Report.Version != "1.2.3" {
			t.Fatalf("prepared=%+v err=%v", prepared, err)
		}
	})
	t.Run("archive replaced", func(t *testing.T) {
		service, _, staging, _ := newCandidateHarness(t)
		summary, err := service.AcceptEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(service.paths(summary.CandidateID).archive, []byte("replaced"), 0o600); err != nil {
			t.Fatal(err)
		}
		if _, err := service.RevalidateForConfirm(context.Background(), testSessionID, summary.CandidateID); !errors.Is(err, ErrDigestMismatch) {
			t.Fatalf("err=%v, want %v", err, ErrDigestMismatch)
		}
	})
	t.Run("extracted file changed", func(t *testing.T) {
		service, _, staging, _ := newCandidateHarness(t)
		summary, err := service.AcceptEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
		if err != nil {
			t.Fatal(err)
		}
		manifest := filepath.Join(service.paths(summary.CandidateID).extraction, "upload-manifest.sha256")
		if err := os.WriteFile(manifest, []byte("changed"), 0o600); err != nil {
			t.Fatal(err)
		}
		if _, err := service.RevalidateForConfirm(context.Background(), testSessionID, summary.CandidateID); err == nil || errors.Is(err, ErrDigestMismatch) {
			t.Fatalf("err=%v, want release validation failure", err)
		}
	})
}

func newCandidateHarness(t *testing.T) (*Service, *store.Store, string, time.Time) {
	t.Helper()
	staging := t.TempDir()
	if err := os.Chmod(staging, 0o700); err != nil {
		t.Fatal(err)
	}
	state := t.TempDir()
	if err := os.Chmod(state, 0o700); err != nil {
		t.Fatal(err)
	}
	database, err := store.Open(context.Background(), filepath.Join(state, "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	now := time.Unix(2_000_000_000, 0).UTC()
	if err := database.CreateSession(context.Background(), store.Session{IDHash: testSessionID, State: store.SessionActive, CreatedAt: now, LastSeenAt: now, IdleExpiresAt: now.Add(time.Hour), AbsoluteExpiresAt: now.Add(2 * time.Hour)}); err != nil {
		t.Fatal(err)
	}
	validator := goldenValidator(t)
	service, err := NewService(Options{Store: database, Validator: validator, StagingRoot: staging, Lifetime: 24 * time.Hour, Now: func() time.Time { return now }, Random: bytes.NewReader(bytes.Repeat([]byte{0x51}, 1024))})
	if err != nil {
		t.Fatal(err)
	}
	return service, database, staging, now
}

func receiveGolden(t *testing.T, staging string) *upload.Envelope {
	t.Helper()
	path := goldenFixturePath(t, "valid-upload.zip")
	file, err := os.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		t.Fatal(err)
	}
	receiver, err := upload.NewReceiver(upload.ReceiverOptions{StagingRoot: staging, Limits: upload.DefaultLimits(), AvailableBytes: func(string) (uint64, error) { return 20 << 30, nil }})
	if err != nil {
		t.Fatal(err)
	}
	envelope, err := receiver.Receive(context.Background(), file, info.Size())
	if err != nil {
		t.Fatal(err)
	}
	return envelope
}

func goldenValidator(t *testing.T) *release.Validator {
	t.Helper()
	certificateBytes, err := os.ReadFile(goldenFixturePath(t, "publisher.cer"))
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
	return release.NewValidator(trust)
}

func goldenFixturePath(t *testing.T, name string) string {
	t.Helper()
	_, source, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("resolve source path")
	}
	return filepath.Join(filepath.Dir(source), "..", "..", "..", "..", "tests", "BaiduBatchTransfer.App.Tests", "Updates", "Fixtures", "OtaProduction", name)
}
