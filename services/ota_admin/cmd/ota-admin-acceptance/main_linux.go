//go:build linux && ota_admin_acceptance

package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
	"golang.org/x/sys/unix"
)

const (
	acceptanceSessionID   = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	acceptanceCandidateID = "55555555-5555-4555-8555-555555555555"
	acceptanceUploadID    = "66666666-6666-4666-8666-666666666666"
	acceptancePublishID   = "77777777-7777-4777-8777-777777777777"
	productionRoot        = "/www/wwwroot/ota.fanfh.cn"
	productionServiceRoot = "/opt/superpan-ota-admin"
)

var publishBoundaries = map[string]deploy.Boundary{
	string(deploy.BoundaryFileSynced):        deploy.BoundaryFileSynced,
	string(deploy.BoundaryCandidateSynced):   deploy.BoundaryCandidateSynced,
	string(deploy.BoundaryGenerationNamed):   deploy.BoundaryGenerationNamed,
	string(deploy.BoundaryGenerationsSynced): deploy.BoundaryGenerationsSynced,
	string(deploy.BoundaryStableTemp):        deploy.BoundaryStableTemp,
	string(deploy.BoundaryStableReplaced):    deploy.BoundaryStableReplaced,
	string(deploy.BoundaryParentSynced):      deploy.BoundaryParentSynced,
}

type evidence struct {
	SchemaVersion  int    `json:"schema_version"`
	FailurePoint   string `json:"failure_point"`
	FaultObserved  bool   `json:"fault_observed"`
	CommitExpected bool   `json:"commit_expected"`
	StableTarget   string `json:"stable_target,omitempty"`
	CurrentVersion string `json:"current_version,omitempty"`
	OperationState string `json:"operation_state,omitempty"`
	RecoveryRuns   int    `json:"recovery_runs"`
	Passed         bool   `json:"passed"`
	Error          string `json:"error,omitempty"`
}

type fixtureValidator struct{ report release.Report }

func (validator fixtureValidator) Validate(_ context.Context, envelope *upload.Envelope, _ string) (release.Report, error) {
	if envelope == nil || len(envelope.Entries) != len(validator.report.Files) {
		return release.Report{}, errors.New("fixture generation closure mismatch")
	}
	expected := make(map[string]release.ReportFile, len(validator.report.Files))
	for _, file := range validator.report.Files {
		expected[file.Name] = file
	}
	seen := make(map[string]bool, len(envelope.Entries))
	for _, entry := range envelope.Entries {
		want, ok := expected[entry.Name]
		if !ok || seen[entry.Name] {
			return release.Report{}, errors.New("fixture generation member mismatch")
		}
		seen[entry.Name] = true
		content, err := os.ReadFile(entry.Path)
		if err != nil {
			return release.Report{}, err
		}
		digest := sha256.Sum256(content)
		if int64(len(content)) != want.Size || hex.EncodeToString(digest[:]) != want.SHA256 {
			return release.Report{}, errors.New("fixture generation hash mismatch")
		}
	}
	return validator.report, nil
}

type fixturePreparer struct{ prepared candidate.Prepared }

func (preparer fixturePreparer) RevalidateForConfirm(context.Context, string, string) (candidate.Prepared, error) {
	return preparer.prepared, nil
}

func main() {
	rootFlag := flag.String("root", "", "caller-created empty isolated publication root")
	failureFlag := flag.String("failure-point", "", "enumerated durability boundary")
	flag.Parse()
	result := evidence{SchemaVersion: 1, FailurePoint: *failureFlag}
	if flag.NArg() != 0 {
		emitAndExit(result, errors.New("positional arguments are not accepted"))
	}
	boundary, ok := publishBoundaries[*failureFlag]
	if !ok {
		emitAndExit(result, errors.New("failure point is not enumerated"))
	}
	root, err := validateIsolatedRoot(*rootFlag)
	if err != nil {
		emitAndExit(result, err)
	}
	result.CommitExpected = boundary == deploy.BoundaryStableReplaced || boundary == deploy.BoundaryParentSynced
	result, err = exercise(root, boundary, result)
	emitAndExit(result, err)
}

func exercise(root string, failure deploy.Boundary, result evidence) (evidence, error) {
	stateRoot, err := os.MkdirTemp("", "superpan-ota-acceptance-state-")
	if err != nil {
		return result, err
	}
	defer os.RemoveAll(stateRoot)
	if err := os.Chmod(stateRoot, 0o700); err != nil {
		return result, err
	}
	sourceRoot, err := os.MkdirTemp("", "superpan-ota-acceptance-source-")
	if err != nil {
		return result, err
	}
	defer os.RemoveAll(sourceRoot)
	if err := os.Chmod(sourceRoot, 0o700); err != nil {
		return result, err
	}
	envelope, report, err := newFixture(sourceRoot)
	if err != nil {
		return result, err
	}
	validator := fixtureValidator{report: report}
	databasePath := filepath.Join(stateRoot, "state.db")
	database, err := store.Open(context.Background(), databasePath)
	if err != nil {
		return result, err
	}
	now := time.Now().UTC().Truncate(time.Second)
	if err := seedCandidate(database, report, now); err != nil {
		database.Close()
		return result, err
	}
	var faultMu sync.Mutex
	faultObserved := false
	disk, err := deploy.NewDisk(root, func(actual deploy.Boundary) error {
		faultMu.Lock()
		defer faultMu.Unlock()
		if actual == failure && !faultObserved {
			faultObserved = true
			return errors.Join(deploy.ErrCrashSimulation, errors.New("injected acceptance fault"))
		}
		return nil
	})
	if err != nil {
		database.Close()
		return result, err
	}
	preparer := fixturePreparer{prepared: candidate.Prepared{Summary: candidate.Summary{CandidateID: acceptanceCandidateID}, Report: report, Envelope: envelope}}
	publisher, err := deploy.NewPublisher(deploy.PublisherOptions{Disk: disk, Store: database, Candidates: preparer, Validator: validator, Now: func() time.Time { return now }})
	if err != nil {
		database.Close()
		return result, err
	}
	if err := publisher.Recover(context.Background()); err != nil {
		database.Close()
		return result, err
	}
	if _, publishErr := publisher.PublishForActiveSession(context.Background(), acceptanceSessionID, acceptanceCandidateID, acceptancePublishID); publishErr == nil {
		database.Close()
		return result, errors.New("faulted publish unexpectedly succeeded")
	}
	faultMu.Lock()
	result.FaultObserved = faultObserved
	faultMu.Unlock()
	if !result.FaultObserved {
		database.Close()
		return result, errors.New("requested failure point was not reached")
	}
	if err := database.Close(); err != nil {
		return result, err
	}
	database, err = store.Open(context.Background(), databasePath)
	if err != nil {
		return result, err
	}
	defer database.Close()
	restartedDisk, err := deploy.NewDisk(root, nil)
	if err != nil {
		return result, err
	}
	restarted, err := deploy.NewPublisher(deploy.PublisherOptions{Disk: restartedDisk, Store: database, Candidates: preparer, Validator: validator, Now: func() time.Time { return now.Add(time.Minute) }})
	if err != nil {
		return result, err
	}
	for range 2 {
		if err := restarted.Recover(context.Background()); err != nil {
			return result, err
		}
		result.RecoveryRuns++
	}
	current, _, haveCurrent, err := database.CurrentRelease(context.Background())
	if err != nil {
		return result, err
	}
	operation, found, err := database.PublishOperationForActiveSession(context.Background(), acceptanceSessionID, acceptancePublishID, now.Add(time.Minute))
	if err != nil || !found {
		return result, errors.Join(errors.New("publish operation missing after recovery"), err)
	}
	result.OperationState = string(operation.State)
	stable, stableErr := restartedDisk.StableTarget()
	if result.CommitExpected {
		if stableErr != nil || stable != "release-"+acceptancePublishID || !haveCurrent || current.ID != acceptancePublishID || current.Version != report.Version || operation.State != store.OperationSucceeded {
			return result, errors.New("post-commit recovery did not converge")
		}
		result.StableTarget = stable
		result.CurrentVersion = current.Version
	} else {
		if !errors.Is(stableErr, os.ErrNotExist) || haveCurrent || operation.State != store.OperationFailed {
			return result, errors.New("pre-commit recovery changed the public release")
		}
	}
	result.Passed = true
	return result, nil
}

func seedCandidate(database *store.Store, report release.Report, now time.Time) error {
	ctx := context.Background()
	if err := database.CreateSession(ctx, store.Session{IDHash: acceptanceSessionID, State: store.SessionActive, CreatedAt: now, LastSeenAt: now, IdleExpiresAt: now.Add(30 * time.Minute), AbsoluteExpiresAt: now.Add(2 * time.Hour)}); err != nil {
		return err
	}
	if _, acquired, err := database.ReserveCandidateForActiveSession(ctx, store.CandidateReservation{
		CandidateID: acceptanceCandidateID, SessionID: acceptanceSessionID, OperationID: acceptanceUploadID, LeaseOwner: acceptanceCandidateID,
		CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour), LeaseExpiresAt: now.Add(5 * time.Minute),
	}); err != nil || !acquired {
		return errors.Join(errors.New("candidate reservation failed"), err)
	}
	reportJSON, err := json.Marshal(report)
	if err != nil {
		return err
	}
	_, created, err := database.AcceptCandidateForActiveSession(ctx, store.CandidateAcceptance{
		CandidateID: acceptanceCandidateID, SessionID: acceptanceSessionID, OperationID: acceptanceUploadID, LeaseOwner: acceptanceCandidateID,
		UploadDigest: strings.Repeat("c", 64), ValidationReport: string(reportJSON), Version: report.Version,
		CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour), CommittedAt: now,
	})
	if err != nil || !created {
		return errors.Join(errors.New("candidate acceptance failed"), err)
	}
	return nil
}

func newFixture(root string) (*upload.Envelope, release.Report, error) {
	contents := map[string][]byte{
		"SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg": []byte("acceptance full package\n"),
		"releases.win-x64-stable.json":                 []byte("acceptance signed release metadata\n"),
	}
	names := make([]string, 0, len(contents))
	for name := range contents {
		names = append(names, name)
	}
	sort.Strings(names)
	report := release.Report{SchemaVersion: 1, Verified: true, PackageID: "SuperPan.BaiduBatchTransfer", Channel: "win-x64-stable", Version: "1.2.3", FileCount: len(names)}
	envelope := &upload.Envelope{ExtractionRoot: root}
	for _, name := range names {
		path := filepath.Join(root, name)
		content := contents[name]
		if err := os.WriteFile(path, content, 0o600); err != nil {
			return nil, release.Report{}, err
		}
		digest := sha256.Sum256(content)
		file := release.ReportFile{Name: name, Size: int64(len(content)), SHA256: hex.EncodeToString(digest[:])}
		report.Files = append(report.Files, file)
		envelope.Entries = append(envelope.Entries, upload.Entry{Name: name, Path: path, Size: file.Size})
		envelope.ExpandedBytes += file.Size
	}
	return envelope, report, nil
}

func validateIsolatedRoot(input string) (string, error) {
	if input == "" || !filepath.IsAbs(input) {
		return "", errors.New("an absolute isolated root is required")
	}
	if os.Getenv("INVOCATION_ID") != "" || os.Getenv("SYSTEMD_EXEC_PID") != "" || os.Getenv("NOTIFY_SOCKET") != "" {
		return "", errors.New("acceptance harness refuses systemd service execution")
	}
	executable, err := os.Executable()
	if err != nil {
		return "", err
	}
	resolvedExecutable, err := filepath.EvalSymlinks(executable)
	if err != nil {
		return "", err
	}
	if pathWithin(resolvedExecutable, productionServiceRoot) {
		return "", errors.New("acceptance harness refuses the production service directory")
	}
	clean := filepath.Clean(input)
	resolved, err := filepath.EvalSymlinks(clean)
	if err != nil || filepath.Clean(resolved) != clean {
		return "", errors.New("isolated root cannot use symbolic-link aliases")
	}
	if pathWithin(clean, productionRoot) || pathWithin(productionRoot, clean) {
		return "", errors.New("acceptance harness refuses the production publication tree")
	}
	info, err := os.Lstat(clean)
	if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 || info.Mode().Perm() != 0o700 {
		return "", errors.New("isolated root must be a caller-created private directory")
	}
	stat, ok := info.Sys().(*unix.Stat_t)
	if !ok || int(stat.Uid) != os.Geteuid() {
		return "", errors.New("isolated root must be owned by the caller")
	}
	handle, err := os.Open(clean)
	if err != nil {
		return "", err
	}
	entries, readErr := handle.ReadDir(1)
	closeErr := handle.Close()
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return "", readErr
	}
	if closeErr != nil {
		return "", closeErr
	}
	if len(entries) != 0 {
		return "", errors.New("isolated root must be empty")
	}
	return clean, nil
}

func pathWithin(path, root string) bool {
	path = filepath.Clean(path)
	root = filepath.Clean(root)
	return path == root || strings.HasPrefix(path, root+string(os.PathSeparator))
}

func emitAndExit(result evidence, err error) {
	if err != nil {
		result.Passed = false
		result.Error = err.Error()
		_ = json.NewEncoder(os.Stdout).Encode(result)
		os.Exit(1)
	}
	if encodeErr := json.NewEncoder(os.Stdout).Encode(result); encodeErr != nil {
		_, _ = fmt.Fprintln(os.Stderr, "could not encode acceptance evidence")
		os.Exit(1)
	}
}
