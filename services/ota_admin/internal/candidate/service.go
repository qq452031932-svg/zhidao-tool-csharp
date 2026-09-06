package candidate

import (
	"context"
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"sort"
	"sync"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

var (
	ErrDigestMismatch   = errors.New("candidate upload digest changed")
	ErrExpired          = errors.New("candidate expired")
	ErrUploadInProgress = errors.New("candidate upload is already in progress")
	canonicalUUID       = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)
)

const uploadLeaseDuration = 5 * time.Minute

type releaseValidator interface {
	Validate(context.Context, *upload.Envelope, string) (release.Report, error)
}

type Options struct {
	Store       *store.Store
	Validator   releaseValidator
	StagingRoot string
	Lifetime    time.Duration
	Now         func() time.Time
	Random      io.Reader
}

type Service struct {
	store       *store.Store
	validator   releaseValidator
	stagingRoot string
	lifetime    time.Duration
	now         func() time.Time
	random      io.Reader
	randomMu    sync.Mutex
}

type Summary struct {
	CandidateID string         `json:"candidate_id"`
	OperationID string         `json:"operation_id"`
	State       string         `json:"state"`
	CreatedAt   time.Time      `json:"created_at"`
	ExpiresAt   time.Time      `json:"expires_at"`
	Report      release.Report `json:"report"`
	ResultCode  string         `json:"result_code,omitempty"`
	LeaseOwner  string         `json:"-"`
}

type Prepared struct {
	Summary  Summary
	Report   release.Report
	Envelope *upload.Envelope
}

type candidatePaths struct{ archive, extraction string }

func NewService(options Options) (*Service, error) {
	if options.Store == nil || options.Validator == nil {
		return nil, errors.New("candidate store and validator are required")
	}
	if options.Lifetime <= 0 || options.Lifetime > 24*time.Hour {
		return nil, errors.New("candidate lifetime must be between zero and 24 hours")
	}
	if options.Now == nil {
		options.Now = time.Now
	}
	if options.Random == nil {
		options.Random = rand.Reader
	}
	root, err := secureStagingRoot(options.StagingRoot)
	if err != nil {
		return nil, err
	}
	return &Service{store: options.Store, validator: options.Validator, stagingRoot: root, lifetime: options.Lifetime, now: options.Now, random: options.Random}, nil
}

func secureStagingRoot(path string) (string, error) {
	if path == "" || !filepath.IsAbs(path) {
		return "", errors.New("staging root must be absolute")
	}
	root := filepath.Clean(path)
	info, err := os.Lstat(root)
	if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
		return "", errors.New("staging root must be a private non-linked directory")
	}
	if err := validateStagingRoot(info); err != nil {
		return "", fmt.Errorf("staging root security: %w", err)
	}
	resolved, err := filepath.EvalSymlinks(root)
	if err != nil || filepath.Clean(resolved) != root {
		return "", errors.New("staging root cannot contain symbolic-link aliases")
	}
	return root, nil
}

func (service *Service) AcceptNewEnvelopeForActiveSession(ctx context.Context, sessionID, operationID string, envelope *upload.Envelope) (Summary, error) {
	reserved, acquired, err := service.ReserveEnvelopeForActiveSession(ctx, sessionID, operationID)
	if err != nil {
		return Summary{}, err
	}
	if !acquired {
		_ = envelope.Cleanup()
		if reserved.State != string(store.CandidateUploading) {
			return reserved, nil
		}
		return Summary{}, ErrUploadInProgress
	}
	return service.AcceptEnvelopeForActiveSession(ctx, sessionID, operationID, envelope, reserved.LeaseOwner)
}

func (service *Service) AcceptEnvelopeForActiveSession(ctx context.Context, sessionID, operationID string, envelope *upload.Envelope, leaseOwner string) (Summary, error) {
	if service == nil || envelope == nil || envelope.Verified || !canonicalUUID.MatchString(operationID) {
		return Summary{}, errors.New("candidate request is invalid")
	}
	if !canonicalUUID.MatchString(leaseOwner) {
		return Summary{}, errors.New("candidate upload lease owner is invalid")
	}
	now := service.now().UTC()
	existing, found, err := service.store.CandidateByOperationForActiveSession(ctx, sessionID, operationID, now)
	if err != nil {
		return Summary{}, mapStoreError(err)
	}
	if found && existing.State != store.CandidateUploading {
		_ = envelope.Cleanup()
		return summaryFromRecord(existing)
	}
	if !found {
		return Summary{}, errors.New("candidate reservation disappeared")
	}
	if err := service.RenewEnvelopeLeaseForActiveSession(ctx, sessionID, operationID, leaseOwner); err != nil {
		return Summary{}, err
	}
	floor, _, err := service.store.HighestCommittedVersion(ctx)
	if err != nil {
		service.failBestEffort(sessionID, operationID, leaseOwner, "internal_failure")
		return Summary{}, err
	}
	report, err := service.validator.Validate(ctx, envelope, floor)
	if err != nil {
		service.failBestEffort(sessionID, operationID, leaseOwner, validationFailureCode(err))
		return Summary{}, err
	}
	reportJSON, err := json.Marshal(report)
	if err != nil {
		service.failBestEffort(sessionID, operationID, leaseOwner, "internal_failure")
		return Summary{}, errors.New("encode candidate validation report")
	}
	if err := service.RenewEnvelopeLeaseForActiveSession(ctx, sessionID, operationID, leaseOwner); err != nil {
		return Summary{}, err
	}
	candidateID := existing.ID
	paths := service.paths(candidateID)
	if err := service.adoptEnvelope(envelope, paths); err != nil {
		service.failBestEffort(sessionID, operationID, leaseOwner, "internal_failure")
		return Summary{}, err
	}
	cleanupAfterAdoption := func(cause error) (Summary, error) {
		service.failBestEffort(sessionID, operationID, leaseOwner, validationFailureCode(cause))
		return Summary{}, errors.Join(cause, deleteCandidatePaths(service.stagingRoot, candidateID))
	}
	if err := syncStagingRoot(service.stagingRoot); err != nil {
		return cleanupAfterAdoption(err)
	}
	adoptedDigest, adoptedSize, err := hashCandidateArchive(service.stagingRoot, filepath.Base(paths.archive))
	if err != nil || adoptedDigest != envelope.SHA256 || adoptedSize != envelope.CompressedBytes {
		return cleanupAfterAdoption(ErrDigestMismatch)
	}
	adoptedReport, err := service.validator.Validate(ctx, envelope, floor)
	if err != nil {
		return cleanupAfterAdoption(err)
	}
	if !reflect.DeepEqual(adoptedReport, report) {
		return cleanupAfterAdoption(ErrDigestMismatch)
	}
	if err := service.RenewEnvelopeLeaseForActiveSession(ctx, sessionID, operationID, leaseOwner); err != nil {
		return cleanupAfterAdoption(err)
	}
	accepted, created, err := service.store.AcceptCandidateForActiveSession(ctx, store.CandidateAcceptance{
		CandidateID: candidateID, SessionID: sessionID, OperationID: operationID,
		UploadDigest: envelope.SHA256, ValidationReport: string(reportJSON), Version: report.Version,
		CreatedAt: existing.CreatedAt, ExpiresAt: existing.ExpiresAt, CommittedAt: service.now().UTC(), LeaseOwner: leaseOwner,
	})
	if err != nil || !created {
		cleanupErr := deleteCandidatePaths(service.stagingRoot, candidateID)
		if err != nil {
			code := validationFailureCode(err)
			if errors.Is(err, store.ErrSessionInactive) || errors.Is(err, store.ErrCandidateExpired) {
				code = "expired"
			}
			service.failBestEffort(sessionID, operationID, leaseOwner, code)
			return Summary{}, errors.Join(mapStoreError(err), cleanupErr)
		}
		if cleanupErr != nil {
			return Summary{}, cleanupErr
		}
		return summaryFromRecord(accepted)
	}
	return summaryFromRecord(accepted)
}

func (service *Service) FailEnvelopeForActiveSession(ctx context.Context, sessionID, operationID, code, leaseOwner string) (Summary, error) {
	record, err := service.store.FailReservedCandidate(ctx, sessionID, operationID, code, service.now().UTC(), leaseOwner)
	if err != nil {
		return Summary{}, mapStoreError(err)
	}
	return summaryFromRecord(record)
}

func (service *Service) failBestEffort(sessionID, operationID, leaseOwner, code string) {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_, _ = service.FailEnvelopeForActiveSession(ctx, sessionID, operationID, code, leaseOwner)
}

func validationFailureCode(err error) string {
	switch {
	case errors.Is(err, release.ErrSignature):
		return "signature_mismatch"
	case errors.Is(err, release.ErrVersionFloor):
		return "version_floor"
	case errors.Is(err, ErrDigestMismatch):
		return "hash_mismatch"
	case errors.Is(err, release.ErrManifest), errors.Is(err, release.ErrMetadata), errors.Is(err, release.ErrCatalog), errors.Is(err, release.ErrReleasesIndex):
		return "release_validation_failed"
	default:
		return "internal_failure"
	}
}

// ReserveEnvelopeForActiveSession atomically binds an idempotency key before
// the HTTP handler consumes any request bytes.
func (service *Service) ReserveEnvelopeForActiveSession(ctx context.Context, sessionID, operationID string) (Summary, bool, error) {
	if service == nil || !canonicalUUID.MatchString(operationID) {
		return Summary{}, false, errors.New("candidate request is invalid")
	}
	now := service.now().UTC()
	existing, found, err := service.store.CandidateByOperationForActiveSession(ctx, sessionID, operationID, now)
	if err != nil {
		return Summary{}, false, mapStoreError(err)
	}
	if found && existing.State != store.CandidateUploading {
		summary, err := summaryFromRecord(existing)
		return summary, false, err
	}
	candidateID := existing.ID
	if !found {
		candidateID, err = service.nextCandidateID()
		if err != nil {
			return Summary{}, false, err
		}
	}
	leaseOwner, err := service.nextOwnerID()
	if err != nil {
		return Summary{}, false, err
	}
	record, created, err := service.store.ReserveCandidateForActiveSession(ctx, store.CandidateReservation{CandidateID: candidateID, SessionID: sessionID, OperationID: operationID, LeaseOwner: leaseOwner, CreatedAt: now, ExpiresAt: now.Add(service.lifetime), LeaseExpiresAt: now.Add(uploadLeaseDuration)})
	if errors.Is(err, store.ErrUploadLeaseRecoveryRequired) {
		record, err = service.store.RecoverUnleasedCandidateForActiveSession(ctx, sessionID, operationID, now)
		created = false
	}
	if err != nil {
		return Summary{}, false, mapStoreError(err)
	}
	summary, err := summaryFromRecord(record)
	return summary, created, err
}

func (service *Service) RenewEnvelopeLeaseForActiveSession(ctx context.Context, sessionID, operationID, leaseOwner string) error {
	if service == nil || !canonicalUUID.MatchString(operationID) || !canonicalUUID.MatchString(leaseOwner) {
		return errors.New("candidate upload lease request is invalid")
	}
	now := service.now().UTC()
	return mapStoreError(service.store.RenewCandidateUploadLease(ctx, sessionID, operationID, leaseOwner, now, now.Add(uploadLeaseDuration)))
}

func (service *Service) CandidateForOperationForActiveSession(ctx context.Context, sessionID, operationID string) (Summary, bool, error) {
	if !canonicalUUID.MatchString(operationID) {
		return Summary{}, false, errors.New("operation ID is invalid")
	}
	record, found, err := service.store.CandidateByOperationForActiveSession(ctx, sessionID, operationID, service.now().UTC())
	if err != nil {
		return Summary{}, false, mapStoreError(err)
	}
	if !found {
		return Summary{}, false, nil
	}
	summary, err := summaryFromRecord(record)
	return summary, true, err
}

func (service *Service) CurrentForActiveSession(ctx context.Context, sessionID string) (Summary, error) {
	record, err := service.store.CurrentCandidateForActiveSession(ctx, sessionID, service.now().UTC())
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return Summary{}, store.ErrNotFound
		}
		return Summary{}, mapStoreError(err)
	}
	return summaryFromRecord(record)
}

func (service *Service) StatusForActiveSession(ctx context.Context, sessionID, candidateID string) (Summary, error) {
	if !canonicalUUID.MatchString(candidateID) {
		return Summary{}, session.ErrUnauthorized
	}
	record, err := service.store.CandidateForActiveSession(ctx, sessionID, candidateID, service.now().UTC())
	if err != nil {
		return Summary{}, mapStoreError(err)
	}
	return summaryFromRecord(record)
}

func (service *Service) RevalidateForConfirm(ctx context.Context, sessionID, candidateID string) (Prepared, error) {
	if !canonicalUUID.MatchString(candidateID) {
		return Prepared{}, session.ErrUnauthorized
	}
	now := service.now().UTC()
	record, err := service.store.CandidateForConfirmForActiveSession(ctx, sessionID, candidateID, now)
	if err != nil {
		return Prepared{}, mapStoreError(err)
	}
	paths := service.paths(candidateID)
	digest, archiveSize, err := hashCandidateArchive(service.stagingRoot, filepath.Base(paths.archive))
	if err != nil || digest != record.UploadDigest {
		return Prepared{}, ErrDigestMismatch
	}
	envelope, err := envelopeFromCandidate(paths, digest, archiveSize)
	if err != nil {
		return Prepared{}, err
	}
	floor, _, err := service.store.HighestCommittedVersion(ctx)
	if err != nil {
		return Prepared{}, err
	}
	report, err := service.validator.Validate(ctx, envelope, floor)
	if err != nil {
		return Prepared{}, err
	}
	var expected release.Report
	if json.Unmarshal([]byte(record.ValidationReport), &expected) != nil || !reflect.DeepEqual(report, expected) {
		return Prepared{}, ErrDigestMismatch
	}
	latest, err := service.store.CandidateForConfirmForActiveSession(ctx, sessionID, candidateID, service.now().UTC())
	if err != nil {
		return Prepared{}, mapStoreError(err)
	}
	if latest.UploadDigest != record.UploadDigest || latest.ValidationReport != record.ValidationReport {
		return Prepared{}, ErrDigestMismatch
	}
	summary, err := summaryFromRecord(latest)
	if err != nil {
		return Prepared{}, err
	}
	return Prepared{Summary: summary, Report: report, Envelope: envelope}, nil
}

func (service *Service) paths(candidateID string) candidatePaths {
	base := ".candidate-" + candidateID
	return candidatePaths{archive: filepath.Join(service.stagingRoot, base+".zip"), extraction: filepath.Join(service.stagingRoot, base)}
}

func (service *Service) nextCandidateID() (string, error) {
	service.randomMu.Lock()
	defer service.randomMu.Unlock()
	for range 8 {
		id, err := readUUID(service.random)
		if err != nil {
			return "", fmt.Errorf("generate candidate ID: %w", err)
		}
		paths := service.paths(id)
		if _, err := os.Lstat(paths.archive); !errors.Is(err, os.ErrNotExist) {
			continue
		}
		if _, err := os.Lstat(paths.extraction); !errors.Is(err, os.ErrNotExist) {
			continue
		}
		return id, nil
	}
	return "", errors.New("candidate ID collisions exhausted")
}

func (service *Service) nextOwnerID() (string, error) {
	service.randomMu.Lock()
	defer service.randomMu.Unlock()
	id, err := readUUID(service.random)
	if err != nil {
		return "", fmt.Errorf("generate upload lease owner: %w", err)
	}
	return id, nil
}

func (service *Service) adoptEnvelope(envelope *upload.Envelope, destination candidatePaths) error {
	_, err := validateDirectChild(envelope.ArchivePath, service.stagingRoot, true, ".upload-")
	if err != nil {
		return err
	}
	_, err = validateDirectChild(envelope.ExtractionRoot, service.stagingRoot, false, ".extract-")
	if err != nil {
		return err
	}
	entryNames := make([]string, 0, len(envelope.Entries))
	for _, entry := range envelope.Entries {
		if filepath.Dir(filepath.Clean(entry.Path)) != envelope.ExtractionRoot || filepath.Base(entry.Path) != entry.Name {
			return errors.New("candidate extraction entry is outside its root")
		}
		entryNames = append(entryNames, entry.Name)
	}
	if err := adoptCandidatePaths(service.stagingRoot, filepath.Base(envelope.ArchivePath), filepath.Base(envelope.ExtractionRoot), filepath.Base(destination.archive), filepath.Base(destination.extraction), entryNames); err != nil {
		return err
	}
	envelope.ArchivePath = destination.archive
	envelope.ExtractionRoot = destination.extraction
	for index := range envelope.Entries {
		envelope.Entries[index].Path = filepath.Join(destination.extraction, envelope.Entries[index].Name)
	}
	return nil
}

func validateDirectChild(path, root string, regular bool, prefix string) (os.FileInfo, error) {
	clean := filepath.Clean(path)
	if !filepath.IsAbs(clean) || filepath.Dir(clean) != root || !regexp.MustCompile(`^[A-Za-z0-9._-]+$`).MatchString(filepath.Base(clean)) ||
		!filepathHasPrefix(filepath.Base(clean), prefix) {
		return nil, errors.New("candidate path is outside the staging root")
	}
	info, err := os.Lstat(clean)
	if err != nil || info.Mode()&os.ModeSymlink != 0 || regular && !info.Mode().IsRegular() || !regular && !info.IsDir() {
		return nil, errors.New("candidate path type changed")
	}
	return info, nil
}

func filepathHasPrefix(name, prefix string) bool {
	return len(name) > len(prefix) && name[:len(prefix)] == prefix
}

func envelopeFromCandidate(paths candidatePaths, digest string, archiveSize int64) (*upload.Envelope, error) {
	rootInfo, err := os.Lstat(paths.extraction)
	if err != nil || !rootInfo.IsDir() || rootInfo.Mode()&os.ModeSymlink != 0 {
		return nil, errors.New("candidate extraction root changed")
	}
	handle, err := os.Open(paths.extraction)
	if err != nil {
		return nil, errors.New("candidate extraction is unreadable")
	}
	defer handle.Close()
	directory, err := handle.ReadDir(257)
	if err != nil && !errors.Is(err, io.EOF) || len(directory) == 0 || len(directory) > 256 {
		return nil, errors.New("candidate extraction is unreadable")
	}
	sort.Slice(directory, func(left, right int) bool { return directory[left].Name() < directory[right].Name() })
	entries := make([]upload.Entry, 0, len(directory))
	var expanded int64
	for _, child := range directory {
		path := filepath.Join(paths.extraction, child.Name())
		info, err := os.Lstat(path)
		if err != nil || !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 || info.Size() < 0 || info.Size() > (2<<30) || expanded > (8<<30)-info.Size() {
			return nil, errors.New("candidate extraction member changed")
		}
		expanded += info.Size()
		entries = append(entries, upload.Entry{Name: child.Name(), Path: path, Size: info.Size()})
	}
	return &upload.Envelope{ArchivePath: paths.archive, ExtractionRoot: paths.extraction, SHA256: digest, CompressedBytes: archiveSize, ExpandedBytes: expanded, Entries: entries}, nil
}

func summaryFromRecord(record store.CandidateRecord) (Summary, error) {
	var report release.Report
	if record.ValidationReport != "" {
		if err := json.Unmarshal([]byte(record.ValidationReport), &report); err != nil || !report.Verified || report.Version != record.Version {
			return Summary{}, errors.New("stored candidate report is invalid")
		}
	} else if record.State == store.CandidateValidated || record.State == store.CandidatePublishing || record.State == store.CandidatePublished {
		return Summary{}, errors.New("stored candidate report is invalid")
	}
	return Summary{CandidateID: record.ID, OperationID: record.OperationID, State: string(record.State), CreatedAt: record.CreatedAt, ExpiresAt: record.ExpiresAt, Report: report, ResultCode: record.ResultCode, LeaseOwner: record.LeaseOwner}, nil
}

func mapStoreError(err error) error {
	switch {
	case errors.Is(err, store.ErrSessionInactive), errors.Is(err, store.ErrNotFound):
		return session.ErrUnauthorized
	case errors.Is(err, store.ErrCandidateExpired):
		return ErrExpired
	case errors.Is(err, store.ErrVersionNotIncreasing):
		return release.ErrVersionFloor
	default:
		return err
	}
}
