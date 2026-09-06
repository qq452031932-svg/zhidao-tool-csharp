//go:build linux && ota_admin_acceptance

package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/x509"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
	"golang.org/x/sys/unix"
)

// These are exact copies of the tracked PowerShell production golden. The
// certificate is public; private signing material is never embedded here.
//
//go:embed testdata/valid-upload.zip
var goldenUpload []byte

//go:embed testdata/publisher.cer
var goldenCertificate []byte

const (
	acceptanceSessionID   = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
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
	if err := refuseServiceManager(); err != nil {
		emitAndExit(result, err)
	}
	root, err := validateIsolatedRoot(*rootFlag)
	if err != nil {
		emitAndExit(result, err)
	}
	result.CommitExpected = boundary == deploy.BoundaryStableReplaced || boundary == deploy.BoundaryParentSynced
	result, err = exercise(root, boundary, result)
	emitAndExit(result, err)
}

func exercise(root isolatedRoot, failure deploy.Boundary, result evidence) (evidence, error) {
	stateRoot, err := os.MkdirTemp("", "superpan-ota-acceptance-state-")
	if err != nil {
		return result, err
	}
	defer os.RemoveAll(stateRoot)
	if err := os.Chmod(stateRoot, 0o700); err != nil {
		return result, err
	}
	stagingRoot := filepath.Join(stateRoot, "staging")
	if err := os.Mkdir(stagingRoot, 0o700); err != nil {
		return result, err
	}
	certificate, err := x509.ParseCertificate(goldenCertificate)
	if err != nil {
		return result, err
	}
	spkiDigest := sha256.Sum256(certificate.RawSubjectPublicKeyInfo)
	trust, err := publisher.NewTrust(certificate.RawSubjectPublicKeyInfo, hex.EncodeToString(spkiDigest[:]))
	if err != nil {
		return result, err
	}
	validator := release.NewValidator(trust)
	receiver, err := upload.NewReceiver(upload.ReceiverOptions{StagingRoot: stagingRoot, Limits: upload.DefaultLimits()})
	if err != nil {
		return result, err
	}
	envelope, err := receiver.Receive(context.Background(), bytes.NewReader(goldenUpload), int64(len(goldenUpload)))
	if err != nil {
		return result, err
	}
	defer envelope.Cleanup()
	databasePath := filepath.Join(stateRoot, "state.db")
	database, err := store.Open(context.Background(), databasePath)
	if err != nil {
		return result, err
	}
	now := time.Now().UTC().Truncate(time.Second)
	if err := seedSession(database, now); err != nil {
		database.Close()
		return result, err
	}
	candidates, err := candidate.NewService(candidate.Options{Store: database, Validator: validator, StagingRoot: stagingRoot, Lifetime: 24 * time.Hour, Now: func() time.Time { return now }})
	if err != nil {
		database.Close()
		return result, err
	}
	reserved, acquired, err := candidates.ReserveEnvelopeForActiveSession(context.Background(), acceptanceSessionID, acceptanceUploadID)
	if err != nil || !acquired {
		database.Close()
		return result, errors.Join(errors.New("candidate reservation failed"), err)
	}
	accepted, err := candidates.AcceptEnvelopeForActiveSession(context.Background(), acceptanceSessionID, acceptanceUploadID, envelope, reserved.LeaseOwner)
	if err != nil {
		database.Close()
		return result, err
	}
	if !accepted.Report.Verified || accepted.Report.Version != "1.2.3" {
		database.Close()
		return result, errors.New("production golden did not produce its expected verified report")
	}
	prepared, err := candidates.RevalidateForConfirm(context.Background(), acceptanceSessionID, accepted.CandidateID)
	if err != nil || prepared.Report.Version != accepted.Report.Version {
		database.Close()
		return result, errors.Join(errors.New("real candidate confirmation revalidation failed"), err)
	}
	var faultMu sync.Mutex
	faultObserved := false
	disk, err := deploy.NewDisk(root.path, func(actual deploy.Boundary) error {
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
	if err := verifyIsolatedRoot(root); err != nil {
		database.Close()
		return result, err
	}
	otaPublisher, err := deploy.NewPublisher(deploy.PublisherOptions{Disk: disk, Store: database, Candidates: candidates, Validator: validator, Now: func() time.Time { return now }})
	if err != nil {
		database.Close()
		return result, err
	}
	if err := otaPublisher.Recover(context.Background()); err != nil {
		database.Close()
		return result, err
	}
	_, publishErr := otaPublisher.PublishForActiveSession(context.Background(), acceptanceSessionID, accepted.CandidateID, acceptancePublishID)
	if publishErr == nil {
		database.Close()
		return result, errors.New("faulted publish unexpectedly succeeded")
	}
	faultMu.Lock()
	result.FaultObserved = faultObserved
	faultMu.Unlock()
	if !result.FaultObserved {
		database.Close()
		return result, fmt.Errorf("requested failure point was not reached: %w", publishErr)
	}
	if err := database.Close(); err != nil {
		return result, err
	}
	database, err = store.Open(context.Background(), databasePath)
	if err != nil {
		return result, err
	}
	defer database.Close()
	restartedDisk, err := deploy.NewDisk(root.path, nil)
	if err != nil {
		return result, err
	}
	restartedCandidates, err := candidate.NewService(candidate.Options{Store: database, Validator: validator, StagingRoot: stagingRoot, Lifetime: 24 * time.Hour, Now: func() time.Time { return now.Add(time.Minute) }})
	if err != nil {
		return result, err
	}
	restarted, err := deploy.NewPublisher(deploy.PublisherOptions{Disk: restartedDisk, Store: database, Candidates: restartedCandidates, Validator: validator, Now: func() time.Time { return now.Add(time.Minute) }})
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
		if stableErr != nil || stable != "release-"+acceptancePublishID || !haveCurrent || current.ID != acceptancePublishID || current.Version != accepted.Report.Version || operation.State != store.OperationSucceeded {
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

func seedSession(database *store.Store, now time.Time) error {
	ctx := context.Background()
	return database.CreateSession(ctx, store.Session{IDHash: acceptanceSessionID, State: store.SessionActive, CreatedAt: now, LastSeenAt: now, IdleExpiresAt: now.Add(30 * time.Minute), AbsoluteExpiresAt: now.Add(2 * time.Hour)})
}

type isolatedRoot struct {
	path   string
	device uint64
	inode  uint64
}

func refuseServiceManager() error {
	environment := os.Getenv("INVOCATION_ID") != "" || os.Getenv("SYSTEMD_EXEC_PID") != "" || os.Getenv("NOTIFY_SOCKET") != ""
	parentPID := os.Getppid()
	parentComm, _ := os.ReadFile(fmt.Sprintf("/proc/%d/comm", parentPID))
	initComm, _ := os.ReadFile("/proc/1/comm")
	cgroup, _ := os.ReadFile("/proc/self/cgroup")
	if systemdExecution(environment, parentPID, string(parentComm), string(initComm), string(cgroup)) {
		return errors.New("acceptance harness refuses systemd service execution")
	}
	return nil
}

func systemdExecution(environment bool, parentPID int, parentComm, initComm, cgroup string) bool {
	if environment || parentPID == 1 || strings.EqualFold(strings.TrimSpace(parentComm), "systemd") {
		return true
	}
	if !strings.EqualFold(strings.TrimSpace(initComm), "systemd") {
		return false
	}
	cgroup = strings.ToLower(cgroup)
	return strings.Contains(cgroup, "/system.slice/") || strings.Contains(cgroup, ".service")
}

func validateIsolatedRoot(input string) (isolatedRoot, error) {
	if input == "" || !filepath.IsAbs(input) {
		return isolatedRoot{}, errors.New("an absolute isolated root is required")
	}
	executable, err := os.Executable()
	if err != nil {
		return isolatedRoot{}, err
	}
	resolvedExecutable, err := filepath.EvalSymlinks(executable)
	if err != nil {
		return isolatedRoot{}, err
	}
	if pathWithin(resolvedExecutable, productionServiceRoot) {
		return isolatedRoot{}, errors.New("acceptance harness refuses the production service directory")
	}
	clean := filepath.Clean(input)
	if pathWithin(clean, productionRoot) || pathWithin(productionRoot, clean) {
		return isolatedRoot{}, errors.New("acceptance harness refuses the production publication tree")
	}
	resolved, err := filepath.EvalSymlinks(clean)
	if err != nil || filepath.Clean(resolved) != clean {
		return isolatedRoot{}, errors.New("isolated root cannot use symbolic-link aliases")
	}
	fd, err := unix.Open(clean, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return isolatedRoot{}, errors.New("isolated root must be a caller-created private directory")
	}
	handle := os.NewFile(uintptr(fd), clean)
	defer handle.Close()
	var stat unix.Stat_t
	if err := unix.Fstat(fd, &stat); err != nil || stat.Mode&unix.S_IFMT != unix.S_IFDIR || stat.Mode&0o777 != 0o700 || int(stat.Uid) != os.Geteuid() {
		return isolatedRoot{}, errors.New("isolated root must be owned by the caller and mode 0700")
	}
	entries, readErr := handle.ReadDir(1)
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return isolatedRoot{}, readErr
	}
	if len(entries) != 0 {
		return isolatedRoot{}, errors.New("isolated root must be empty")
	}
	root := isolatedRoot{path: clean, device: uint64(stat.Dev), inode: stat.Ino}
	if err := verifyIsolatedRoot(root); err != nil {
		return isolatedRoot{}, err
	}
	return root, nil
}

func verifyIsolatedRoot(root isolatedRoot) error {
	var stat unix.Stat_t
	if err := unix.Lstat(root.path, &stat); err != nil || stat.Mode&unix.S_IFMT != unix.S_IFDIR || uint64(stat.Dev) != root.device || stat.Ino != root.inode {
		return errors.New("isolated root identity changed")
	}
	return nil
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
