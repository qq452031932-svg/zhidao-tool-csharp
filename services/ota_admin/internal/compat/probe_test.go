package compat

import (
	"context"
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/json"
	"io/fs"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"slices"
	"strings"
	"testing"
)

func TestSQLiteCreateWriteReadTransaction(t *testing.T) {
	t.Parallel()

	if err := exerciseSQLite(filepath.Join(t.TempDir(), "probe.db")); err != nil {
		t.Fatalf("exerciseSQLite() error = %v", err)
	}
}

func TestSQLiteCommitClosesThenReopensAndReadsSameDatabase(t *testing.T) {
	t.Parallel()

	databasePath := filepath.Join(t.TempDir(), "probe.db")
	if err := writeSQLiteProbe(databasePath); err != nil {
		t.Fatalf("writeSQLiteProbe() error = %v", err)
	}
	renamedPath := databasePath + ".closed"
	if err := os.Rename(databasePath, renamedPath); err != nil {
		t.Fatalf("rename committed database after close: %v", err)
	}
	if err := os.Rename(renamedPath, databasePath); err != nil {
		t.Fatalf("restore committed database for reopen: %v", err)
	}
	value, err := readSQLiteProbe(databasePath)
	if err != nil {
		t.Fatalf("readSQLiteProbe() error = %v", err)
	}
	if value != "round-trip" {
		t.Fatalf("reopened value = %q, want round-trip", value)
	}
}

func TestArgon2idHashAndVerify(t *testing.T) {
	t.Parallel()

	password := []byte("probe-password")
	salt := []byte("0123456789abcdef")
	digest := hashArgon2id(password, salt)
	if !verifyArgon2id(password, salt, digest) {
		t.Fatal("verifyArgon2id() rejected the original password")
	}
	if verifyArgon2id([]byte("wrong-password"), salt, digest) {
		t.Fatal("verifyArgon2id() accepted a different password")
	}
}

func TestRSA3072PKCS1v15SHA256Verification(t *testing.T) {
	t.Parallel()

	key, err := rsa.GenerateKey(rand.Reader, 3072)
	if err != nil {
		t.Fatalf("rsa.GenerateKey() error = %v", err)
	}
	message := []byte("probe manifest\n")
	digest := sha256.Sum256(message)
	signature, err := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, digest[:])
	if err != nil {
		t.Fatalf("rsa.SignPKCS1v15() error = %v", err)
	}

	if err := verifyRSA3072PKCS1v15SHA256(&key.PublicKey, message, signature); err != nil {
		t.Fatalf("verifyRSA3072PKCS1v15SHA256() error = %v", err)
	}
	message[0] ^= 1
	if err := verifyRSA3072PKCS1v15SHA256(&key.PublicKey, message, signature); err == nil {
		t.Fatal("verifyRSA3072PKCS1v15SHA256() accepted modified bytes")
	}
}

func TestFilesystemProbeUsesDurableAtomicRelativeSymlinkSwitch(t *testing.T) {
	t.Parallel()

	recorder := &recordingFilesystem{links: make(map[string]string)}
	if err := exerciseFilesystem(recorder, filepath.Join("probe", "root")); err != nil {
		t.Fatalf("exerciseFilesystem() error = %v", err)
	}

	for _, required := range []string{"write-sync", "rename", "symlink", "readlink"} {
		if !slices.Contains(recorder.calls, required) {
			t.Errorf("filesystem calls %v do not contain %q", recorder.calls, required)
		}
	}
	if count := countCall(recorder.calls, "sync-dir"); count < 3 {
		t.Errorf("sync-dir call count = %d, want at least 3", count)
	}
	if recorder.lastLinkTarget == "" || filepath.IsAbs(recorder.lastLinkTarget) {
		t.Errorf("symlink target = %q, want non-empty relative path", recorder.lastLinkTarget)
	}
	if got := recorder.links[filepath.Join("probe", "root", "stable")]; got != recorder.lastLinkTarget {
		t.Errorf("stable link target = %q, want %q", got, recorder.lastLinkTarget)
	}
}

func TestHealthServerIsLoopbackOnlyAndReturnsJSON(t *testing.T) {
	t.Parallel()

	report := Report{OK: true, Checks: Checks{SQLite: true, Argon2id: true}}
	server, address, err := StartHealthServer(report, "127.0.0.1:0")
	if err != nil {
		t.Fatalf("startHealthServer() error = %v", err)
	}
	t.Cleanup(func() {
		if err := server.Shutdown(context.Background()); err != nil {
			t.Errorf("server.Shutdown() error = %v", err)
		}
	})

	host, _, err := net.SplitHostPort(address)
	if err != nil {
		t.Fatalf("net.SplitHostPort(%q) error = %v", address, err)
	}
	if ip := net.ParseIP(host); ip == nil || !ip.IsLoopback() {
		t.Fatalf("listener host = %q, want loopback IP", host)
	}

	response, err := http.Get("http://" + address + "/admin/health")
	if err != nil {
		t.Fatalf("GET /admin/health error = %v", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatalf("GET /admin/health status = %d, want %d", response.StatusCode, http.StatusOK)
	}
	if got := response.Header.Get("Content-Type"); got != "application/json" {
		t.Errorf("Content-Type = %q, want application/json", got)
	}
	var decoded Report
	if err := json.NewDecoder(response.Body).Decode(&decoded); err != nil {
		t.Fatalf("decode health JSON: %v", err)
	}
	if !decoded.OK || !decoded.Checks.SQLite || !decoded.Checks.Argon2id {
		t.Fatalf("decoded report = %+v", decoded)
	}
}

func TestHealthServerRejectsNonLoopbackListenAddress(t *testing.T) {
	t.Parallel()

	if _, _, err := StartHealthServer(Report{OK: true}, "0.0.0.0:0"); err == nil {
		t.Fatal("startHealthServer() accepted a non-loopback address")
	}
}

func TestDeploymentScriptsPinRemoteVerificationInputs(t *testing.T) {
	t.Parallel()

	buildScript := readRepositoryFile(t, "scripts", "build_linux_amd64.ps1")
	for _, required := range []string{
		"git", "ls-files", "superpan-ota-admin-source.zip",
		"superpan-ota-admin-source.zip.sha256", "superpan-ota-admin-linux-amd64.sha256",
		"1980-01-01", "StringComparer]::Ordinal",
		"EndsWith('.ps1'", "EndsWith('.sh'", "EndsWith('.service'",
	} {
		if !strings.Contains(buildScript, required) {
			t.Errorf("build script does not contain %q", required)
		}
	}

	runner := readRepositoryFile(t, "deploy", "probe", "run-centos7-probe.sh")
	for _, required := range []string{
		"go1.27.1.linux-amd64.tar.gz",
		"63d339f0da5ab53635a56f2490a7984dfe12dfcff22ad749f63edaf590168445",
		"GO_ARCHIVE_PARTIAL",
		"if [[ -e \"$GO_ARCHIVE\" || -L \"$GO_ARCHIVE\" ]]",
		"if [[ ! -f \"$GO_ARCHIVE\" || -L \"$GO_ARCHIVE\" ]]",
		"--output \"$GO_ARCHIVE_PARTIAL\"",
		"mv -- \"$GO_ARCHIVE_PARTIAL\" \"$GO_ARCHIVE\"",
		"superpan-ota-admin-source.zip.sha256",
		"superpan-ota-admin-linux-amd64.sha256",
		"sha256sum -c",
		"CGO_ENABLED=0",
		"GOTOOLCHAIN=local",
		"GOCACHE=\"$GO_CACHE\"",
		"GOMODCACHE=\"$GO_MOD_CACHE\"",
		"test -shuffle=on -count=10 ./...",
		"x86-64",
		"statically linked",
		"stripped",
	} {
		if !strings.Contains(runner, required) {
			t.Errorf("CentOS runner does not contain %q", required)
		}
	}
}

func TestStaticBinaryFilePatternRejectsNotStripped(t *testing.T) {
	t.Parallel()

	runner := readRepositoryFile(t, "deploy", "probe", "run-centos7-probe.sh")
	declaration := regexp.MustCompile(`(?m)^readonly STATIC_STRIPPED_FILE_PATTERN='([^']+)'$`)
	match := declaration.FindStringSubmatch(runner)
	if len(match) != 2 {
		t.Fatal("CentOS runner does not declare STATIC_STRIPPED_FILE_PATTERN")
	}
	pattern, err := regexp.Compile(match[1])
	if err != nil {
		t.Fatalf("STATIC_STRIPPED_FILE_PATTERN is invalid: %v", err)
	}

	valid := "ELF 64-bit LSB executable, x86-64, version 1 (SYSV), statically linked, stripped"
	if !pattern.MatchString(valid) {
		t.Fatalf("static stripped pattern rejected representative output %q", valid)
	}
	notStripped := "ELF 64-bit LSB executable, x86-64, version 1 (SYSV), statically linked, not stripped"
	if pattern.MatchString(notStripped) {
		t.Fatalf("static stripped pattern accepted representative output %q", notStripped)
	}
	if !strings.Contains(runner, "grep -Fq 'not stripped'") {
		t.Fatal("CentOS runner does not explicitly reject the 'not stripped' token")
	}
}

func TestRunnerOwnsCleanupAndUsesOnlyVerifiedBundledDeploymentFiles(t *testing.T) {
	t.Parallel()

	runner := readRepositoryFile(t, "deploy", "probe", "run-centos7-probe.sh")
	for _, required := range []string{
		"SCRIPT_PATH", "BUNDLED_RUNNER", "BUNDLED_UNIT", "cmp -s --",
		"LoadState=not-found", "OWNER_MARKER", "OWNER_TOKEN_FILE",
		"ownership_marker_is_intact", "unit_installed=false", "unit_started=false",
		"global_unit_is_absent", "GO_ROOT", "UNIT_REMOVAL_PATH", "cleanup_failed",
	} {
		if !strings.Contains(runner, required) {
			t.Errorf("CentOS runner does not contain ownership/canonical-source contract %q", required)
		}
	}
	requireSubstringsInOrder(t, runner,
		`if [[ -e "$GO_ROOT" || -L "$GO_ROOT" ]]`,
		`if [[ -e "$SOURCE_ROOT" || -L "$SOURCE_ROOT" ]]`,
		`if [[ -e "$UNIT_TARGET" || -L "$UNIT_TARGET" ]]`,
		`mkdir -- "$OWNER_MARKER"`,
		"trap cleanup EXIT INT TERM",
		`cmp -s -- "$SCRIPT_PATH" "$BUNDLED_RUNNER"`,
		`$BUNDLED_UNIT`,
		`unit_installed=true`,
	)
	cleanupOffset := strings.Index(runner, "cleanup() {")
	if cleanupOffset < 0 {
		t.Fatal("CentOS runner does not define cleanup")
	}
	requireSubstringsInOrder(t, runner[cleanupOffset:],
		`if [[ "$unit_installed" == true ]]`,
		`cmp -s -- "$UNIT_TARGET" "$BUNDLED_UNIT"`,
		`systemctl stop "$UNIT_NAME"`,
		`systemctl is-active --quiet "$UNIT_NAME"`,
		`mv -- "$UNIT_TARGET" "$UNIT_REMOVAL_PATH"`,
		`systemctl daemon-reload`,
		`rm -f -- "$UNIT_REMOVAL_PATH"`,
		`ownership_marker_is_intact`,
		`rm -rf -- "$VERIFY_ROOT"`,
	)
	cleanupText := runner[cleanupOffset : strings.Index(runner[cleanupOffset:], "trap cleanup EXIT INT TERM")+cleanupOffset]
	if strings.Contains(cleanupText, "|| true") {
		t.Fatal("owned cleanup still suppresses a failure")
	}
}

func TestBuildPinsToolchainUsesCleanInputsAndStagesCompleteOutputSet(t *testing.T) {
	t.Parallel()

	buildScript := readRepositoryFile(t, "scripts", "build_linux_amd64.ps1")
	for _, required := range []string{
		"a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d",
		"go version go1.27.1 windows/amd64", "GOTOOLCHAIN", "GOCACHE", "GOMODCACHE", "local", "-buildvcs=false",
		"git", "diff", "--quiet", "HEAD", "ls-tree", "ls-files", "--others",
		"archive", "--format=zip", "HEAD:services/ota_admin", "core.autocrlf=false", "core.eol=lf",
		"SnapshotDirectory", "Get-SnapshotFingerprint", "Publish-StagedArtifacts",
		"Substring(0, 12)", `Join-Path $ArtifactsRoot ".o-$stagingId"`, "Join-Path $StagingDirectory 's'",
		"bootstrap_go_toolchain.ps1", "vet ./...", "backup preserved at",
		"EndsWith('.html'", "EndsWith('.css'", "EndsWith('.js'", "EndsWith('.sql'",
	} {
		if !strings.Contains(buildScript, required) {
			t.Errorf("build script does not contain pinned/reproducible contract %q", required)
		}
	}
	requireSubstringsInOrder(t, buildScript,
		"GOTOOLCHAIN",
		"GOCACHE",
		"GOMODCACHE",
		`& powershell -NoProfile -ExecutionPolicy Bypass -File $BootstrapScript`,
		"ExpectedArchiveSha256",
		"go version go1.27.1 windows/amd64",
		"mod verify",
		"test -p=1 ./...",
		"vet ./...",
		"build -buildvcs=false",
		"Get-SnapshotFingerprint",
		"New-DeterministicSourceBundle -SnapshotRoot $SnapshotDirectory",
		"Write-Sha256CheckFile",
		"Publish-StagedArtifacts",
	)
	finallyOffset := strings.LastIndex(buildScript, "finally {")
	if finallyOffset < 0 {
		t.Fatal("build script does not contain final cleanup")
	}
	if strings.Contains(buildScript[finallyOffset:], "BackupDirectory") {
		t.Fatal("final cleanup can delete a rollback backup that was not recovered")
	}
	if strings.Contains(buildScript, "Copy-Item -LiteralPath $sourcePath") {
		t.Fatal("build snapshot still copies mutable working-tree bytes")
	}
}

func TestBuildRejectsAnyUntrackedModuleFile(t *testing.T) {
	powerShell, err := exec.LookPath("powershell")
	if err != nil {
		t.Skip("PowerShell is unavailable on this compatibility-test host")
	}

	moduleRoot := testModuleRoot(t)
	repositoryRoot := filepath.Clean(filepath.Join(moduleRoot, "..", ".."))
	expectedModuleRoot := filepath.Clean(filepath.Join(repositoryRoot, "services", "ota_admin"))
	if !strings.EqualFold(moduleRoot, expectedModuleRoot) {
		t.Skip("test is running from a flattened source snapshot")
	}
	if _, err := os.Stat(filepath.Join(repositoryRoot, ".git")); err != nil {
		t.Skip("test is running from a source bundle without repository metadata")
	}
	fixtureName := ".untracked-build-rejection-test.go"
	fixturePath := filepath.Join(moduleRoot, fixtureName)
	if err := os.WriteFile(fixturePath, []byte("package rejectedfixture\n"), 0o600); err != nil {
		t.Fatalf("create untracked fixture: %v", err)
	}
	t.Cleanup(func() {
		if err := os.Remove(fixturePath); err != nil && !os.IsNotExist(err) {
			t.Errorf("remove untracked fixture: %v", err)
		}
	})

	buildScript := filepath.Join(moduleRoot, "scripts", "build_linux_amd64.ps1")
	output, runErr := exec.Command(powerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", buildScript).CombinedOutput()
	if runErr == nil {
		t.Fatalf("build unexpectedly accepted untracked module file; output:\n%s", output)
	}
	outputText := string(output)
	if !strings.Contains(outputText, "Untracked files exist under services/ota_admin") || !strings.Contains(outputText, fixtureName) {
		t.Fatalf("build failed for the wrong reason; output:\n%s", outputText)
	}
}

func TestRunnerCleanupFailsClosedAndPreservesOwnedResources(t *testing.T) {
	bash, err := exec.LookPath("bash")
	if err != nil && runtime.GOOS == "windows" {
		candidate := `C:\Program Files\Git\bin\bash.exe`
		if _, statErr := os.Stat(candidate); statErr == nil {
			bash, err = candidate, nil
		}
	}
	if err != nil {
		t.Skip("bash is unavailable on this compatibility-test host")
	}
	runner := readRepositoryFile(t, "deploy", "probe", "run-centos7-probe.sh")
	cleanupStart := strings.Index(runner, "cleanup() {")
	if cleanupStart < 0 {
		t.Fatal("could not find runner cleanup function")
	}
	cleanupEndRelative := strings.Index(runner[cleanupStart:], "\n}\ntrap cleanup EXIT INT TERM")
	if cleanupEndRelative < 0 {
		t.Fatal("could not isolate runner cleanup function")
	}
	cleanupFunction := runner[cleanupStart : cleanupStart+cleanupEndRelative+2]

	for _, scenario := range []string{"changed-owner", "stop-failure"} {
		t.Run(scenario, func(t *testing.T) {
			workRoot := filepath.Join(testLocalTempDir(t), "probe-root")
			harnessPath := filepath.Join(testLocalTempDir(t), "cleanup-harness.sh")
			harness := `#!/usr/bin/env bash
set -u
VERIFY_ROOT="$1"
scenario="$2"
UNIT_NAME=probe.service
UNIT_TARGET="$VERIFY_ROOT/unit"
UNIT_REMOVAL_PATH="$VERIFY_ROOT/unit.removing"
BUNDLED_UNIT="$VERIFY_ROOT/bundled-unit"
OWNER_MARKER="$VERIFY_ROOT/owner"
OWNER_TOKEN_FILE="$OWNER_MARKER/token"
owner_token=expected-owner
unit_installed=true
unit_started=true
cleanup_failed=false
global_unit_is_absent() { return 0; }
ownership_marker_is_intact() {
  [[ -d "$OWNER_MARKER" && -f "$OWNER_TOKEN_FILE" && "$(<"$OWNER_TOKEN_FILE")" == "$owner_token" ]]
}
systemctl() {
  printf '%s\n' "$*" >>"$VERIFY_ROOT/systemctl.log"
  if [[ "$scenario" == stop-failure && "$1" == stop ]]; then return 1; fi
  if [[ "$1" == is-active ]]; then return 0; fi
  return 0
}
mkdir -p "$OWNER_MARKER"
printf '%s\n' expected-owner >"$OWNER_TOKEN_FILE"
printf '%s\n' canonical >"$BUNDLED_UNIT"
cp "$BUNDLED_UNIT" "$UNIT_TARGET"
if [[ "$scenario" == changed-owner ]]; then printf '%s\n' changed-owner >"$OWNER_TOKEN_FILE"; fi
` + cleanupFunction + `
cleanup
`
			if err := os.WriteFile(harnessPath, []byte(harness), 0o700); err != nil {
				t.Fatalf("write cleanup harness: %v", err)
			}
			output, runErr := exec.Command(bash, filepath.ToSlash(harnessPath), filepath.ToSlash(workRoot), scenario).CombinedOutput()
			if runErr == nil {
				t.Fatalf("cleanup succeeded for %s; output:\n%s", scenario, output)
			}
			if _, err := os.Stat(workRoot); err != nil {
				t.Fatalf("cleanup did not preserve verification root for %s: %v; output:\n%s", scenario, err, output)
			}
			if _, err := os.Stat(filepath.Join(workRoot, "unit")); err != nil {
				t.Fatalf("cleanup did not preserve unit for %s: %v; output:\n%s", scenario, err, output)
			}
		})
	}
}

func TestRunnerRejectsPreexistingExtractionTargets(t *testing.T) {
	bash, err := exec.LookPath("bash")
	if err != nil && runtime.GOOS == "windows" {
		candidate := `C:\Program Files\Git\bin\bash.exe`
		if _, statErr := os.Stat(candidate); statErr == nil {
			bash, err = candidate, nil
		}
	}
	if err != nil {
		t.Skip("bash is unavailable on this compatibility-test host")
	}
	runner := readRepositoryFile(t, "deploy", "probe", "run-centos7-probe.sh")
	guardStart := strings.Index(runner, `if [[ -e "$GO_ROOT" || -L "$GO_ROOT" ]]`)
	guardEnd := strings.Index(runner, "\nglobal_unit_is_absent()")
	if guardStart < 0 || guardEnd < guardStart {
		t.Fatal("could not isolate extraction-target guards")
	}
	guards := runner[guardStart:guardEnd]

	for _, target := range []string{"go", "source"} {
		t.Run(target, func(t *testing.T) {
			workRoot := testLocalTempDir(t)
			if err := os.Mkdir(filepath.Join(workRoot, target), 0o700); err != nil {
				t.Fatalf("create preexisting target: %v", err)
			}
			harnessPath := filepath.Join(testLocalTempDir(t), "extraction-guard.sh")
			harness := "#!/usr/bin/env bash\nset -u\nGO_ROOT=\"$1/go\"\nSOURCE_ROOT=\"$1/source\"\n" + guards + "\n"
			if err := os.WriteFile(harnessPath, []byte(harness), 0o700); err != nil {
				t.Fatalf("write extraction harness: %v", err)
			}
			output, runErr := exec.Command(bash, filepath.ToSlash(harnessPath), filepath.ToSlash(workRoot)).CombinedOutput()
			if runErr == nil {
				t.Fatalf("runner accepted preexisting %s extraction target; output:\n%s", target, output)
			}
			if !strings.Contains(string(output), "pre-existing") {
				t.Fatalf("runner failed for the wrong reason; output:\n%s", output)
			}
		})
	}
}

func TestArtifactRollbackFailurePreservesBackup(t *testing.T) {
	powerShell, err := exec.LookPath("powershell")
	if err != nil {
		t.Skip("PowerShell is unavailable on this compatibility-test host")
	}
	moduleRoot := testModuleRoot(t)
	buildScript := filepath.Join(moduleRoot, "scripts", "build_linux_amd64.ps1")
	harnessPath := filepath.Join(testLocalTempDir(t), "rollback-harness.ps1")
	artifactRoot := filepath.Join(testLocalTempDir(t), "artifacts")
	harness := `param([string] $BuildScript, [string] $TestArtifactRoot)
$ErrorActionPreference = 'Stop'
. $BuildScript -FunctionsOnly
$ArtifactsRoot = [System.IO.Path]::GetFullPath($TestArtifactRoot)
$published = Join-Path $ArtifactsRoot 'published'
$staged = Join-Path $ArtifactsRoot 'staged'
$backup = Join-Path $ArtifactsRoot 'backup'
New-Item -ItemType Directory -Path $published, $staged -Force | Out-Null
Set-Content -LiteralPath (Join-Path $published 'old.txt') -Value old
$script:moveCount = 0
function Move-Item {
  param([string] $LiteralPath, [string] $Destination)
  $script:moveCount++
  if ($script:moveCount -ge 2) { throw "injected move failure $script:moveCount" }
  Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
}
try {
  Publish-StagedArtifacts -StagedPath $staged -PublishedPath $published -PreviousPath $backup
  throw 'publication unexpectedly succeeded'
}
catch {
  if ($_.Exception.Message -notlike '*backup preserved at*') { throw }
  if (-not (Test-Path -LiteralPath (Join-Path $backup 'old.txt') -PathType Leaf)) { throw 'backup was not preserved' }
}
`
	if err := os.WriteFile(harnessPath, []byte(harness), 0o600); err != nil {
		t.Fatalf("write rollback harness: %v", err)
	}
	output, runErr := exec.Command(powerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath, buildScript, artifactRoot).CombinedOutput()
	if runErr != nil {
		t.Fatalf("rollback harness failed: %v; output:\n%s", runErr, output)
	}
}

func TestGitObjectSnapshotIgnoresWorkingTreeMutation(t *testing.T) {
	powerShell, err := exec.LookPath("powershell")
	if err != nil {
		t.Skip("PowerShell is unavailable on this compatibility-test host")
	}
	root := testLocalTempDir(t)
	repositoryRoot := filepath.Join(root, "repository")
	moduleRoot := filepath.Join(repositoryRoot, "services", "ota_admin")
	if err := os.MkdirAll(filepath.Join(moduleRoot, "internal", "compat"), 0o700); err != nil {
		t.Fatalf("create fixture module: %v", err)
	}
	goModPath := filepath.Join(moduleRoot, "go.mod")
	probePath := filepath.Join(moduleRoot, "internal", "compat", "probe.go")
	if err := os.WriteFile(goModPath, []byte("module example.test/probe\n"), 0o600); err != nil {
		t.Fatalf("write fixture go.mod: %v", err)
	}
	if err := os.WriteFile(probePath, []byte("package compat\nconst source = \"committed\"\n"), 0o600); err != nil {
		t.Fatalf("write committed fixture: %v", err)
	}
	for _, args := range [][]string{
		{"init"},
		{"config", "user.email", "compat@example.invalid"},
		{"config", "user.name", "Compatibility Test"},
		{"add", "services/ota_admin"},
		{"commit", "-m", "fixture"},
	} {
		command := exec.Command("git", append([]string{"-C", repositoryRoot}, args...)...)
		if output, err := command.CombinedOutput(); err != nil {
			t.Fatalf("git %v failed: %v; output:\n%s", args, err, output)
		}
	}
	if err := os.WriteFile(probePath, []byte("package compat\nconst source = \"working-tree\"\n"), 0o600); err != nil {
		t.Fatalf("mutate working-tree fixture: %v", err)
	}

	buildScript := filepath.Join(testModuleRoot(t), "scripts", "build_linux_amd64.ps1")
	harnessPath := filepath.Join(root, "snapshot-harness.ps1")
	snapshotRoot := filepath.Join(root, "snapshot")
	harness := `param([string] $BuildScript, [string] $FixtureRepository, [string] $SnapshotRoot)
$ErrorActionPreference = 'Stop'
. $BuildScript -FunctionsOnly
$RepositoryRoot = [System.IO.Path]::GetFullPath($FixtureRepository)
[string[]] $paths = @('services/ota_admin/go.mod', 'services/ota_admin/internal/compat/probe.go')
New-GitObjectSourceSnapshot -SourcePaths $paths -DestinationRoot $SnapshotRoot
`
	if err := os.WriteFile(harnessPath, []byte(harness), 0o600); err != nil {
		t.Fatalf("write snapshot harness: %v", err)
	}
	if output, err := exec.Command(powerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath, buildScript, repositoryRoot, snapshotRoot).CombinedOutput(); err != nil {
		t.Fatalf("snapshot harness failed: %v; output:\n%s", err, output)
	}
	snapshotProbe, err := os.ReadFile(filepath.Join(snapshotRoot, "internal", "compat", "probe.go"))
	if err != nil {
		t.Fatalf("read snapshot probe: %v", err)
	}
	if strings.Contains(string(snapshotProbe), "working-tree") || !strings.Contains(string(snapshotProbe), "committed") {
		t.Fatalf("snapshot did not come exclusively from HEAD: %q", snapshotProbe)
	}
	if strings.Contains(string(snapshotProbe), "\r") {
		t.Fatalf("Git-object snapshot applied working-tree CRLF conversion: %q", snapshotProbe)
	}
}

func TestModuleRootLocatorFindsSnapshotFixtures(t *testing.T) {
	moduleRoot := testModuleRoot(t)
	goMod, err := os.ReadFile(filepath.Join(moduleRoot, "go.mod"))
	if err != nil {
		t.Fatalf("read located go.mod: %v", err)
	}
	if !strings.Contains(string(goMod), "module fanfh.cn/superpan/ota-admin") {
		t.Fatalf("located the wrong module root %q", moduleRoot)
	}
	for _, fixture := range []string{
		filepath.Join("deploy", "probe", "run-centos7-probe.sh"),
		filepath.Join("scripts", "build_linux_amd64.ps1"),
	} {
		info, err := os.Stat(filepath.Join(moduleRoot, fixture))
		if err != nil || !info.Mode().IsRegular() {
			t.Fatalf("located module root lacks fixture %q: %v", fixture, err)
		}
	}
}

func requireSubstringsInOrder(t *testing.T, text string, required ...string) {
	t.Helper()
	offset := 0
	for _, substring := range required {
		index := strings.Index(text[offset:], substring)
		if index < 0 {
			t.Fatalf("text does not contain %q after byte offset %d", substring, offset)
		}
		offset += index + len(substring)
	}
}

func testLocalTempDir(t *testing.T) string {
	t.Helper()
	path, err := os.MkdirTemp(filepath.Join(testModuleRoot(t), "internal", "compat"), ".compat-test-")
	if err != nil {
		t.Fatalf("create package-local temporary directory: %v", err)
	}
	t.Cleanup(func() {
		if err := os.RemoveAll(path); err != nil {
			t.Errorf("remove package-local temporary directory: %v", err)
		}
	})
	return path
}

func testModuleRoot(t *testing.T) string {
	t.Helper()
	currentPath, err := os.Getwd()
	if err != nil {
		t.Fatalf("get test working directory: %v", err)
	}
	for {
		goModPath := filepath.Join(currentPath, "go.mod")
		if info, statErr := os.Stat(goModPath); statErr == nil && info.Mode().IsRegular() {
			contents, readErr := os.ReadFile(goModPath)
			if readErr != nil {
				t.Fatalf("read candidate module file %q: %v", goModPath, readErr)
			}
			if strings.Contains(string(contents), "module fanfh.cn/superpan/ota-admin") {
				return currentPath
			}
		}
		parentPath := filepath.Dir(currentPath)
		if parentPath == currentPath {
			t.Fatalf("could not locate module fanfh.cn/superpan/ota-admin above test working directory")
		}
		currentPath = parentPath
	}
}

func readRepositoryFile(t *testing.T, pathParts ...string) string {
	t.Helper()
	moduleRoot := testModuleRoot(t)
	path := filepath.Join(append([]string{moduleRoot}, pathParts...)...)
	contents, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("os.ReadFile(%q) error = %v", path, err)
	}
	return string(contents)
}

func countCall(calls []string, want string) int {
	count := 0
	for _, call := range calls {
		if call == want {
			count++
		}
	}
	return count
}

type recordingFilesystem struct {
	calls          []string
	links          map[string]string
	lastLinkTarget string
}

func (f *recordingFilesystem) MkdirAll(string, fs.FileMode) error {
	f.calls = append(f.calls, "mkdir")
	return nil
}

func (f *recordingFilesystem) WriteFileAndSync(string, []byte, fs.FileMode) error {
	f.calls = append(f.calls, "write-sync")
	return nil
}

func (f *recordingFilesystem) Rename(oldPath, newPath string) error {
	f.calls = append(f.calls, "rename")
	if target, ok := f.links[oldPath]; ok {
		delete(f.links, oldPath)
		f.links[newPath] = target
	}
	return nil
}

func (f *recordingFilesystem) SyncDirectory(string) error {
	f.calls = append(f.calls, "sync-dir")
	return nil
}

func (f *recordingFilesystem) Symlink(target, linkPath string) error {
	f.calls = append(f.calls, "symlink")
	f.links[linkPath] = target
	f.lastLinkTarget = target
	return nil
}

func (f *recordingFilesystem) Readlink(path string) (string, error) {
	f.calls = append(f.calls, "readlink")
	return f.links[path], nil
}

func (f *recordingFilesystem) Remove(path string) error {
	f.calls = append(f.calls, "remove")
	delete(f.links, path)
	return nil
}
