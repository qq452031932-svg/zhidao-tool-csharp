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
	normalizedRunner := strings.ReplaceAll(runner, "\r\n", "\n")
	if !strings.HasPrefix(normalizedRunner, "#!/usr/bin/env bash\nset -euo pipefail\numask 077\n") {
		t.Fatal("CentOS runner does not establish a private umask before Linux Go tests")
	}
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
		"Substring(0, 12)", `[System.IO.Path]::GetTempPath()`, `Join-Path $SystemTempRoot "spota-$stagingId"`, "Join-Path $StagingDirectory 's'",
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
	if strings.Contains(buildScript, `Join-Path $ArtifactsRoot ".o-$stagingId"`) {
		t.Fatal("build staging root still inherits the long repository path")
	}
	for _, artifactContract := range []string{
		`$OutputDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot 'ota-admin'))`,
		`$BackupDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot ".ota-admin-backup-$invocationId"))`,
	} {
		if !strings.Contains(buildScript, artifactContract) {
			t.Errorf("build output moved outside the artifact root; missing %q", artifactContract)
		}
	}
}

func TestBuildCreatesAndExportsPrivateTemporaryDirectoryBeforeBootstrap(t *testing.T) {
	t.Parallel()

	buildScript := readRepositoryFile(t, "scripts", "build_linux_amd64.ps1")
	for _, required := range []string{
		`$TestTempDirectory = Join-Path $StagingDirectory 't'`,
		`New-Item -ItemType Directory -Path $TestTempDirectory`,
		`$env:TEMP = $TestTempDirectory`,
		`$env:TMP = $TestTempDirectory`,
		`$env:GOTMPDIR = $TestTempDirectory`,
	} {
		if !strings.Contains(buildScript, required) {
			t.Errorf("build script does not contain isolated temporary-directory contract %q", required)
		}
	}
	requireSubstringsInOrder(t, buildScript,
		`$TestTempDirectory = Join-Path $StagingDirectory 't'`,
		`New-SafeStagingDirectory -Path $StagingDirectory`,
		`New-Item -ItemType Directory -Path $TestTempDirectory`,
		`$env:TEMP = $TestTempDirectory`,
		`$env:TMP = $TestTempDirectory`,
		`$env:GOTMPDIR = $TestTempDirectory`,
		`& powershell -NoProfile -ExecutionPolicy Bypass -File $BootstrapScript`,
		`test -p=1 ./...`,
	)
}

func TestLicenseBundleBuilderDeclaresImmutableProductionContract(t *testing.T) {
	t.Parallel()

	buildScript := strings.ReplaceAll(readRepositoryFile(t, "scripts", "build_linux_license_bundle.ps1"), "\r\n", "\n")
	for _, required := range []string{
		"param(", "[string] $SourceCommit", "[switch] $FunctionsOnly",
		"$LicenseFunctionsOnly = [bool] $FunctionsOnly", "$FunctionsOnly = $LicenseFunctionsOnly",
		"Assert-LicenseBundleSourceState", "Get-LicenseBundleSourcePaths", "New-LicenseBundleSourceSnapshot",
		"Assert-PinnedGoToolchain", "go version go1.27.1 windows/amd64", "GOTOOLCHAIN", "local",
		"CGO_ENABLED", "GOOS", "linux", "GOARCH", "amd64",
		"build -buildvcs=false -trimpath -ldflags '-s -w'", "./cmd/ota-admin", "./cmd/license-service",
		"ota-admin", "ota-admin.sha256", "superpan-license", "superpan-license.sha256",
		"source-manifest.sha256", "source-manifest.sha256.sha256", "source-commit.txt",
		"Write-DeterministicSourceManifest", "Write-LicenseSha256CheckFile", "New-LicenseBundleExpectedState", "Assert-LicenseBundleArtifacts", "Publish-VerifiedLicenseBundle",
	} {
		if !strings.Contains(buildScript, required) {
			t.Errorf("license bundle builder does not contain %q", required)
		}
	}
	requireSubstringsInOrder(t, buildScript,
		"Assert-LicenseBundleSourceState -SourceCommit $SourceCommit",
		"New-LicenseBundleSourceSnapshot",
		"mod verify",
		"build -buildvcs=false -trimpath -ldflags '-s -w'",
		"./cmd/ota-admin",
		"build -buildvcs=false -trimpath -ldflags '-s -w'",
		"./cmd/license-service",
		"Write-DeterministicSourceManifest",
		"Assert-LicenseBundleArtifacts",
		"Publish-VerifiedLicenseBundle",
	)
	snapshotOffset := strings.Index(buildScript, "New-LicenseBundleSourceSnapshot")
	if snapshotOffset < 0 {
		t.Fatal("license bundle builder does not create a Git-object snapshot")
	}
	afterSnapshot := buildScript[snapshotOffset:]
	for _, prohibited := range []string{"Copy-Item -LiteralPath $RepositoryRoot", "Get-Content -LiteralPath $RepositoryRoot"} {
		if strings.Contains(afterSnapshot, prohibited) {
			t.Errorf("license bundle builder reads mutable source after snapshot: %q", prohibited)
		}
	}
}

func TestLicenseBundleBootstrapsPinnedToolchainBeforeSnapshot(t *testing.T) {
	powerShell, err := exec.LookPath("powershell")
	if err != nil {
		t.Skip("PowerShell is unavailable on this compatibility-test host")
	}

	buildScript := strings.ReplaceAll(readRepositoryFile(t, "scripts", "build_linux_license_bundle.ps1"), "\r\n", "\n")
	entryOffset := strings.LastIndex(buildScript, "if ($FunctionsOnly)")
	if entryOffset < 0 {
		t.Fatal("license bundle builder does not define a FunctionsOnly entry boundary")
	}
	entryPoint := buildScript[entryOffset:]
	for _, required := range []string{
		"function Invoke-LicenseToolchainBootstrap",
		"scripts\\bootstrap_go_toolchain.ps1",
		"rev-parse --show-object-format",
		"blob $($Bytes.Length)",
		"[System.IO.FileShare]::Read",
		"bootstrapStream.Dispose()",
		"does not match its committed Git blob",
		"& powershell -NoProfile -ExecutionPolicy Bypass -File $LicenseBootstrapScript",
		"$LASTEXITCODE",
		"Pinned Go bootstrap failed",
	} {
		if !strings.Contains(buildScript, required) {
			t.Errorf("license bundle bootstrap contract does not contain %q", required)
		}
	}
	requireSubstringsInOrder(t, entryPoint,
		"Assert-LicenseBundleSourceState -SourceCommit $SourceCommit",
		"Set-LicensePreBootstrapEnvironment",
		"Invoke-LicenseToolchainBootstrap -SourceCommit $SourceCommit",
		"Test-Path -LiteralPath $GoExe -PathType Leaf",
		"Set-SealedLicenseBuildEnvironment",
		"Assert-PinnedLicenseGoToolchain",
		"New-LicenseBundleSourceSnapshot",
	)
	snapshotOffset := strings.Index(entryPoint, "New-LicenseBundleSourceSnapshot")
	if snapshotOffset < 0 {
		t.Fatal("license bundle entry point does not create a source snapshot")
	}
	if strings.Contains(entryPoint[snapshotOffset:], "Invoke-LicenseToolchainBootstrap") {
		t.Fatal("license bundle entry point reads the mutable bootstrap script after snapshot creation")
	}
	manifestOffset := strings.LastIndex(entryPoint, "Write-DeterministicSourceManifest -SnapshotRoot $SnapshotDirectory")
	if manifestOffset < 0 {
		t.Fatal("license bundle entry point does not generate a deterministic source manifest")
	}
	requireSubstringsInOrder(t, entryPoint[manifestOffset:],
		"Write-DeterministicSourceManifest -SnapshotRoot $SnapshotDirectory",
		"Get-SnapshotFingerprint -SnapshotRoot $SnapshotDirectory",
		"New-LicenseBundleExpectedState",
	)
	root := testLocalTempDir(t)
	fixtureRepository := filepath.Join(root, "repository")
	bootstrapPath := filepath.Join(fixtureRepository, "services", "ota_admin", "scripts", "bootstrap_go_toolchain.ps1")
	if err := os.MkdirAll(filepath.Dir(bootstrapPath), 0o700); err != nil {
		t.Fatalf("create bootstrap fixture directory: %v", err)
	}
	successMarker := filepath.Join(root, "success.marker")
	failureMarker := filepath.Join(root, "failure.marker")
	replacementMarker := filepath.Join(root, "replacement.marker")
	snapshotRoot := filepath.Join(root, "snapshot-must-not-exist")
	if err := os.WriteFile(bootstrapPath, []byte("param([string] $Marker)\n[IO.File]::WriteAllText($Marker, 'success')\nexit 0\n"), 0o600); err != nil {
		t.Fatalf("write success bootstrap fixture: %v", err)
	}
	for _, args := range [][]string{
		{"init"},
		{"config", "user.email", "compat@example.invalid"},
		{"config", "user.name", "Compatibility Test"},
		{"add", "services/ota_admin/scripts/bootstrap_go_toolchain.ps1"},
		{"commit", "-m", "bootstrap success"},
	} {
		command := exec.Command("git", append([]string{"-C", fixtureRepository}, args...)...)
		if output, commandErr := command.CombinedOutput(); commandErr != nil {
			t.Fatalf("git %v failed: %v; output:\n%s", args, commandErr, output)
		}
	}
	commitOutput, err := exec.Command("git", "-C", fixtureRepository, "rev-parse", "HEAD").Output()
	if err != nil {
		t.Fatalf("resolve success bootstrap commit: %v", err)
	}
	successCommit := strings.TrimSpace(string(commitOutput))
	harnessPath := filepath.Join(root, "bootstrap-contract.ps1")
	harness := `param([string] $BuildScript, [string] $FixtureRepository, [string] $BootstrapPath, [string] $SourceCommit, [string] $SuccessMarker, [string] $ReplacementMarker, [string] $SnapshotRoot)
$ErrorActionPreference = 'Stop'
$FixtureSourceCommit = $SourceCommit
. $BuildScript -FunctionsOnly
$RepositoryRoot = [IO.Path]::GetFullPath($FixtureRepository)
$LicenseBootstrapScript = [IO.Path]::GetFullPath($BootstrapPath)

Invoke-LicenseToolchainBootstrap -SourceCommit $FixtureSourceCommit -Arguments @('-Marker', $SuccessMarker)
if (-not (Test-Path -LiteralPath $SuccessMarker -PathType Leaf)) { throw 'success bootstrap was not invoked' }

[IO.File]::WriteAllText($BootstrapPath, ('param([string] $Marker)' + [char]10 + '[IO.File]::WriteAllText($Marker, ''replacement'')' + [char]10 + 'exit 23' + [char]10), [Text.UTF8Encoding]::new($false))
try {
  Invoke-LicenseToolchainBootstrap -SourceCommit $FixtureSourceCommit -Arguments @('-Marker', $ReplacementMarker)
  New-LicenseBundleSourceSnapshot -SourcePaths @('services/ota_admin/go.mod') -DestinationRoot $SnapshotRoot -SourceCommit ('0' * 40)
  throw 'replaced bootstrap unexpectedly executed or continued to snapshot creation'
}
catch {
  if ($_.Exception.Message -eq 'replaced bootstrap unexpectedly executed or continued to snapshot creation') { throw }
  if ($_.Exception.Message -notlike '*does not match its committed Git blob*') { throw }
}
if (Test-Path -LiteralPath $ReplacementMarker) { throw 'uncommitted replacement bootstrap executed' }
if (Test-Path -LiteralPath $SnapshotRoot) { throw 'snapshot exists after bootstrap failure' }
`
	if err := os.WriteFile(harnessPath, []byte(harness), 0o600); err != nil {
		t.Fatalf("write bootstrap harness: %v", err)
	}
	buildScriptPath := filepath.Join(testModuleRoot(t), "scripts", "build_linux_license_bundle.ps1")
	output, runErr := exec.Command(powerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath, buildScriptPath, fixtureRepository, bootstrapPath, successCommit, successMarker, replacementMarker, snapshotRoot).CombinedOutput()
	if runErr != nil {
		t.Fatalf("license bootstrap harness failed: %v; output:\n%s", runErr, output)
	}

	for _, args := range [][]string{
		{"add", "services/ota_admin/scripts/bootstrap_go_toolchain.ps1"},
		{"commit", "-m", "bootstrap failure"},
	} {
		command := exec.Command("git", append([]string{"-C", fixtureRepository}, args...)...)
		if output, commandErr := command.CombinedOutput(); commandErr != nil {
			t.Fatalf("git %v failed: %v; output:\n%s", args, commandErr, output)
		}
	}
	failureCommitOutput, err := exec.Command("git", "-C", fixtureRepository, "rev-parse", "HEAD").Output()
	if err != nil {
		t.Fatalf("resolve failure bootstrap commit: %v", err)
	}
	failureCommit := strings.TrimSpace(string(failureCommitOutput))
	failureHarnessPath := filepath.Join(root, "bootstrap-failure-contract.ps1")
	failureHarness := `param([string] $BuildScript, [string] $FixtureRepository, [string] $BootstrapPath, [string] $SourceCommit, [string] $FailureMarker, [string] $SnapshotRoot)
$ErrorActionPreference = 'Stop'
$FixtureSourceCommit = $SourceCommit
. $BuildScript -FunctionsOnly
$RepositoryRoot = [IO.Path]::GetFullPath($FixtureRepository)
$LicenseBootstrapScript = [IO.Path]::GetFullPath($BootstrapPath)
try {
  Invoke-LicenseToolchainBootstrap -SourceCommit $FixtureSourceCommit -Arguments @('-Marker', $FailureMarker)
  New-LicenseBundleSourceSnapshot -SourcePaths @('services/ota_admin/go.mod') -DestinationRoot $SnapshotRoot -SourceCommit ('0' * 40)
  throw 'failed bootstrap unexpectedly continued to snapshot creation'
}
catch {
  if ($_.Exception.Message -eq 'failed bootstrap unexpectedly continued to snapshot creation') { throw }
  if ($_.Exception.Message -notlike '*Pinned Go bootstrap failed*') { throw }
}
if (-not (Test-Path -LiteralPath $FailureMarker -PathType Leaf)) { throw 'committed failure bootstrap was not invoked' }
if (Test-Path -LiteralPath $SnapshotRoot) { throw 'snapshot exists after committed bootstrap failure' }
`
	if err := os.WriteFile(failureHarnessPath, []byte(failureHarness), 0o600); err != nil {
		t.Fatalf("write failure bootstrap harness: %v", err)
	}
	output, runErr = exec.Command(powerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", failureHarnessPath, buildScriptPath, fixtureRepository, bootstrapPath, failureCommit, failureMarker, snapshotRoot).CombinedOutput()
	if runErr != nil {
		t.Fatalf("license failure bootstrap harness failed: %v; output:\n%s", runErr, output)
	}
}

func TestLicenseBundleFreshBootstrapDoesNotInheritFinalGOROOT(t *testing.T) {
	powerShell, err := exec.LookPath("powershell")
	if err != nil {
		t.Skip("PowerShell is unavailable on this compatibility-test host")
	}

	root := testLocalTempDir(t)
	fixtureRepository := filepath.Join(root, "repository")
	bootstrapPath := filepath.Join(fixtureRepository, "services", "ota_admin", "scripts", "bootstrap_go_toolchain.ps1")
	if err := os.MkdirAll(filepath.Dir(bootstrapPath), 0o700); err != nil {
		t.Fatalf("create fresh bootstrap fixture directory: %v", err)
	}
	bootstrap := `$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$installRoot = Join-Path $repositoryRoot '.tools\go-1.27.1'
$stagingRoot = Join-Path $repositoryRoot '.tools\go-1.27.1.extracting'
if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
$stagedBin = Join-Path $stagingRoot 'go\bin'
New-Item -ItemType Directory -Path $stagedBin -Force | Out-Null
$stagedGo = Join-Path $stagedBin 'go.cmd'
$cmd = '@echo off' + [char]13 + [char]10 +
  'if "%~1 %~2"=="env GOROOT" (' + [char]13 + [char]10 +
  '  echo %GOROOT%' + [char]13 + [char]10 +
  '  exit /b 0' + [char]13 + [char]10 + ')' + [char]13 + [char]10 +
  'if not "%GOROOT%"=="" if not exist "%GOROOT%" exit /b 2' + [char]13 + [char]10 +
  'if not "%GOENV%"=="off" exit /b 3' + [char]13 + [char]10 +
  'if not "%GOTOOLCHAIN%"=="local" exit /b 4' + [char]13 + [char]10 +
  'if not "%GOEXPERIMENT%"=="" exit /b 5' + [char]13 + [char]10 +
  'if not "%GOFIPS140%"=="off" exit /b 6' + [char]13 + [char]10 +
  'if defined GOFLAGS exit /b 7' + [char]13 + [char]10 +
  'if "%~1"=="version" echo go version go1.27.1 windows/amd64' + [char]13 + [char]10 +
  'exit /b 0' + [char]13 + [char]10
[IO.File]::WriteAllText($stagedGo, $cmd, [Text.ASCIIEncoding]::new())
& $stagedGo version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
Move-Item -LiteralPath $stagingRoot -Destination $installRoot
exit 0
`
	if err := os.WriteFile(bootstrapPath, []byte(bootstrap), 0o600); err != nil {
		t.Fatalf("write fresh bootstrap fixture: %v", err)
	}
	for _, args := range [][]string{
		{"init"},
		{"config", "user.email", "compat@example.invalid"},
		{"config", "user.name", "Compatibility Test"},
		{"add", "services/ota_admin/scripts/bootstrap_go_toolchain.ps1"},
		{"commit", "-m", "fresh staged toolchain"},
	} {
		command := exec.Command("git", append([]string{"-C", fixtureRepository}, args...)...)
		if output, commandErr := command.CombinedOutput(); commandErr != nil {
			t.Fatalf("git %v failed: %v; output:\n%s", args, commandErr, output)
		}
	}
	commitOutput, err := exec.Command("git", "-C", fixtureRepository, "rev-parse", "HEAD").Output()
	if err != nil {
		t.Fatalf("resolve fresh bootstrap commit: %v", err)
	}
	commit := strings.TrimSpace(string(commitOutput))

	harnessPath := filepath.Join(root, "fresh-bootstrap-contract.ps1")
	finalGo := filepath.Join(fixtureRepository, ".tools", "go-1.27.1", "go", "bin", "go.cmd")
	harness := `param([string] $BuildScript, [string] $FixtureRepository, [string] $BootstrapPath, [string] $SourceCommit, [string] $FinalGo)
$ErrorActionPreference = 'Stop'
$FixtureSourceCommit = $SourceCommit
. $BuildScript -FunctionsOnly
$RepositoryRoot = [IO.Path]::GetFullPath($FixtureRepository)
$LicenseBootstrapScript = [IO.Path]::GetFullPath($BootstrapPath)
$GoExe = [IO.Path]::GetFullPath($FinalGo)
$external = @{
  GOROOT='C:\attacker\compatible-go';
  GOENV='C:\attacker\go.env';
  GOTOOLCHAIN='go1.99.0';
  GOEXPERIMENT='attacker';
  GOFIPS140='latest';
  GOFLAGS='"'
}
foreach ($name in $external.Keys) { [Environment]::SetEnvironmentVariable($name, $external[$name], 'Process') }

function Invoke-TestBootstrap([string] $Commit) {
  $saved = Save-LicenseBuildEnvironment
  try {
    if (Get-Command Set-LicensePreBootstrapEnvironment -ErrorAction SilentlyContinue) {
      Set-LicensePreBootstrapEnvironment
      if ($null -ne [Environment]::GetEnvironmentVariable('GOROOT', 'Process')) { throw 'pre-bootstrap GOROOT was not removed' }
    }
    else {
      Set-SealedLicenseBuildEnvironment
    }
    Invoke-LicenseToolchainBootstrap -SourceCommit $Commit
    if (-not (Test-Path -LiteralPath $GoExe -PathType Leaf)) { throw 'fresh bootstrap did not install final go executable' }
    Set-SealedLicenseBuildEnvironment
    $expectedGoRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $GoExe))).TrimEnd([IO.Path]::DirectorySeparatorChar)
    Assert-LicenseGoRoot -GoExecutable $GoExe -ExpectedGoRoot $expectedGoRoot
  }
  finally {
    Restore-LicenseBuildEnvironment -SavedEnvironment $saved
  }
  foreach ($name in $external.Keys) {
    if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne $external[$name]) { throw "caller $name was not restored" }
  }
}

if (Test-Path -LiteralPath (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $GoExe)))) { throw 'fresh final toolchain unexpectedly exists before bootstrap' }
Invoke-TestBootstrap -Commit $FixtureSourceCommit
Invoke-TestBootstrap -Commit $FixtureSourceCommit

$failureMarker = Join-Path $FixtureRepository 'failure.marker'
$failureScript = '[IO.File]::WriteAllText(''' + $failureMarker.Replace("'", "''") + ''',''ran''); exit 23' + [char]10
[IO.File]::WriteAllText($BootstrapPath, $failureScript, [Text.UTF8Encoding]::new($false))
& git -C $FixtureRepository add services/ota_admin/scripts/bootstrap_go_toolchain.ps1
if ($LASTEXITCODE -ne 0) { throw 'could not stage failure bootstrap' }
& git -C $FixtureRepository commit -m 'failing bootstrap'
if ($LASTEXITCODE -ne 0) { throw 'could not commit failure bootstrap' }
$failureCommit = (& git -C $FixtureRepository rev-parse HEAD) -join ''
$saved = Save-LicenseBuildEnvironment
try {
  Set-LicensePreBootstrapEnvironment
  try { Invoke-LicenseToolchainBootstrap -SourceCommit $failureCommit; throw 'nonzero bootstrap unexpectedly succeeded' }
  catch {
    if ($_.Exception.Message -eq 'nonzero bootstrap unexpectedly succeeded') { throw }
    if ($_.Exception.Message -notlike '*Pinned Go bootstrap failed*') { throw }
  }
}
finally { Restore-LicenseBuildEnvironment -SavedEnvironment $saved }
if (-not (Test-Path -LiteralPath $failureMarker -PathType Leaf)) { throw 'nonzero bootstrap was not executed' }
foreach ($name in $external.Keys) {
  if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne $external[$name]) { throw "caller $name was not restored after bootstrap failure" }
}
`
	if err := os.WriteFile(harnessPath, []byte(harness), 0o600); err != nil {
		t.Fatalf("write fresh bootstrap harness: %v", err)
	}
	buildScriptPath := filepath.Join(testModuleRoot(t), "scripts", "build_linux_license_bundle.ps1")
	output, runErr := exec.Command(powerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath, buildScriptPath, fixtureRepository, bootstrapPath, commit, finalGo).CombinedOutput()
	if runErr != nil {
		t.Fatalf("fresh license toolchain bootstrap harness failed: %v; output:\n%s", runErr, output)
	}
}

func TestLicenseBundleSealsAndRestoresGoEnvironment(t *testing.T) {
	powerShell, err := exec.LookPath("powershell")
	if err != nil {
		t.Skip("PowerShell is unavailable on this compatibility-test host")
	}

	buildScript := strings.ReplaceAll(readRepositoryFile(t, "scripts", "build_linux_license_bundle.ps1"), "\r\n", "\n")
	for _, required := range []string{
		"GOENV", "off", "GOFLAGS", "GOWORK", "GOEXPERIMENT", "GOAMD64", "v1", "GOTOOLCHAIN", "local",
		"GOROOT", "GOFIPS140", "GODEBUG", "GO111MODULE", "GOCACHEPROG", "GO_EXTLINK_ENABLED",
		"GOPRIVATE", "GONOPROXY", "GONOSUMDB", "GOINSECURE", "GOAUTH", "GOPROXY", "GOSUMDB", "GOVCS",
		"CC", "CXX", "PKG_CONFIG", "Save-LicenseBuildEnvironment", "Set-SealedLicenseBuildEnvironment", "Restore-LicenseBuildEnvironment",
		"Assert-LicenseGoRoot", "env GOROOT", "Assert-PinnedLicenseGoToolchain",
	} {
		if !strings.Contains(buildScript, required) {
			t.Errorf("license bundle environment contract does not contain %q", required)
		}
	}

	root := testLocalTempDir(t)
	fakeGoRoot := filepath.Join(root, "pinned-go")
	fakeGo := filepath.Join(fakeGoRoot, "bin", "go.cmd")
	if err := os.MkdirAll(filepath.Dir(fakeGo), 0o700); err != nil {
		t.Fatalf("create fake pinned Go bin directory: %v", err)
	}
	if err := os.WriteFile(fakeGo, []byte("@echo off\r\nif \"%~1 %~2\"==\"env GOROOT\" (\r\n  echo %GOROOT%\r\n  exit /b 0\r\n)\r\nexit /b 23\r\n"), 0o600); err != nil {
		t.Fatalf("write fake pinned Go executable: %v", err)
	}
	harnessPath := filepath.Join(root, "environment-contract.ps1")
	harness := `param([string] $BuildScript, [string] $FakeGo)
$ErrorActionPreference = 'Continue'
$names = @('GOENV','GOFLAGS','GOWORK','GOEXPERIMENT','GOAMD64','GOTOOLCHAIN','GOROOT','GOFIPS140','GODEBUG','GO111MODULE','GOCACHEPROG','GO_EXTLINK_ENABLED','GOPRIVATE','GONOPROXY','GONOSUMDB','GOINSECURE','GOAUTH','GOPROXY','GOSUMDB','GOVCS','CC','CXX','PKG_CONFIG')
$before = @{}
foreach ($name in $names) {
  $value = 'sentinel-' + $name
  if ($name -eq 'GOFLAGS') { $value = '-overlay=attacker.json -toolexec=attacker.exe' }
  if ($name -eq 'GOWORK') { $value = 'C:\attacker\go.work' }
  if ($name -eq 'GOENV') { $value = 'C:\attacker\go.env' }
  [Environment]::SetEnvironmentVariable($name, $value, 'Process')
  $before[$name] = $value
}
. $BuildScript -FunctionsOnly
$GoExe = [IO.Path]::GetFullPath($FakeGo)
if ($ErrorActionPreference -ne 'Continue') { throw 'FunctionsOnly changed ErrorActionPreference' }
foreach ($name in $names) {
  if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne $before[$name]) { throw "FunctionsOnly polluted $name" }
}
$saved = Save-LicenseBuildEnvironment
try {
  Set-SealedLicenseBuildEnvironment
  $expectedGoRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $GoExe))).TrimEnd([IO.Path]::DirectorySeparatorChar)
  $expected = @{
    GOENV='off'; GOFLAGS=''; GOWORK='off'; GOEXPERIMENT=''; GOAMD64='v1'; GOTOOLCHAIN='local';
    GOROOT=$expectedGoRoot; GOFIPS140='off'; GODEBUG=''; GO111MODULE='on'; GOCACHEPROG=''; GO_EXTLINK_ENABLED='0';
    GOPRIVATE=''; GONOPROXY=''; GONOSUMDB=''; GOINSECURE=''; GOAUTH='off';
    GOPROXY='https://proxy.golang.org,direct'; GOSUMDB='sum.golang.org'; GOVCS='public:git|hg,private:off';
    CC=''; CXX=''; PKG_CONFIG=''
  }
  foreach ($name in $names) {
    $actual = [string][Environment]::GetEnvironmentVariable($name, 'Process')
    if ($actual -cne $expected[$name]) { throw "$name was not sealed: '$actual'" }
  }
  Assert-LicenseGoRoot -GoExecutable $GoExe -ExpectedGoRoot $expectedGoRoot
  [Environment]::SetEnvironmentVariable('GOROOT', 'C:\attacker\compatible-go', 'Process')
  try { Assert-LicenseGoRoot -GoExecutable $GoExe -ExpectedGoRoot $expectedGoRoot; throw 'malicious GOROOT unexpectedly accepted' }
  catch { if ($_.Exception.Message -eq 'malicious GOROOT unexpectedly accepted') { throw } }
  [Environment]::SetEnvironmentVariable('GOROOT', $expectedGoRoot, 'Process')
}
finally {
  Restore-LicenseBuildEnvironment -SavedEnvironment $saved
}
foreach ($name in $names) {
  if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne $before[$name]) { throw "$name was not restored" }
}
`
	if err := os.WriteFile(harnessPath, []byte(harness), 0o600); err != nil {
		t.Fatalf("write environment harness: %v", err)
	}
	buildScriptPath := filepath.Join(testModuleRoot(t), "scripts", "build_linux_license_bundle.ps1")
	output, runErr := exec.Command(powerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath, buildScriptPath, fakeGo).CombinedOutput()
	if runErr != nil {
		t.Fatalf("license environment harness failed: %v; output:\n%s", runErr, output)
	}
}

func TestLicenseBundlePublishVerificationIsTransactional(t *testing.T) {
	powerShell, err := exec.LookPath("powershell")
	if err != nil {
		t.Skip("PowerShell is unavailable on this compatibility-test host")
	}

	buildScript := strings.ReplaceAll(readRepositoryFile(t, "scripts", "build_linux_license_bundle.ps1"), "\r\n", "\n")
	for _, required := range []string{
		"function Publish-VerifiedLicenseBundle",
		"Assert-LicenseBundleArtifacts -Directory $PublishedPath -ExpectedState $ExpectedState",
		"Move-Item -LiteralPath $PreviousPath -Destination $PublishedPath",
		"Remove-SafeArtifactDirectory -Path $PublishedPath",
		"Remove-SafeArtifactDirectory -Path $PreviousPath",
	} {
		if !strings.Contains(buildScript, required) {
			t.Errorf("verified license publication contract does not contain %q", required)
		}
	}
	start := strings.Index(buildScript, "function Publish-VerifiedLicenseBundle")
	if start < 0 {
		t.Fatal("license bundle builder does not define transactional verified publication")
	}
	end := strings.Index(buildScript[start:], "\n}\n\nif ($FunctionsOnly)")
	if end < 0 {
		t.Fatal("could not isolate transactional verified publication function")
	}
	publishFunction := buildScript[start : start+end]
	if strings.Contains(publishFunction, "Publish-StagedArtifacts") {
		t.Fatal("verified publication delegates to the base helper that removes backup before verification")
	}
	requireSubstringsInOrder(t, publishFunction,
		"Move-Item -LiteralPath $StagedPath -Destination $PublishedPath",
		"Assert-LicenseBundleArtifacts -Directory $PublishedPath -ExpectedState $ExpectedState",
		"Remove-SafeArtifactDirectory -Path $PreviousPath",
	)

	root := testLocalTempDir(t)
	harnessPath := filepath.Join(root, "verified-publish-contract.ps1")
	artifactRoot := filepath.Join(root, "artifacts")
	harness := `param([string] $BuildScript, [string] $TestArtifactRoot)
$ErrorActionPreference = 'Stop'
. $BuildScript -FunctionsOnly
$ArtifactsRoot = [IO.Path]::GetFullPath($TestArtifactRoot)
New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null

function New-TestPublishCase([string] $Name) {
  $script:StagingDirectory = Join-Path ([IO.Path]::GetTempPath()) ('spota-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
  $script:StagedPublishDirectory = Join-Path $script:StagingDirectory 'p'
  New-SafeStagingDirectory -Path $script:StagingDirectory
  New-Item -ItemType Directory -Path $script:StagedPublishDirectory | Out-Null
  [IO.File]::WriteAllText((Join-Path $script:StagedPublishDirectory 'ota-admin'), ("ota-$Name" + [char]10), [Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText((Join-Path $script:StagedPublishDirectory 'superpan-license'), ("license-$Name" + [char]10), [Text.UTF8Encoding]::new($false))
  $otaHash = Get-LicenseFileSha256 -Path (Join-Path $script:StagedPublishDirectory 'ota-admin')
  $licenseHash = Get-LicenseFileSha256 -Path (Join-Path $script:StagedPublishDirectory 'superpan-license')
  $manifestText = (('0' * 64) + '  go.mod' + [char]10)
  [IO.File]::WriteAllText((Join-Path $script:StagedPublishDirectory 'source-manifest.sha256'), $manifestText, [Text.UTF8Encoding]::new($false))
  $manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes($manifestText)
  $manifestHash = Get-LicenseBytesSha256 -Bytes $manifestBytes
  $commit = 'a' * 40
  Write-LicenseSha256CheckFile -PayloadPath (Join-Path $script:StagedPublishDirectory 'ota-admin') -CheckFilePath (Join-Path $script:StagedPublishDirectory 'ota-admin.sha256') -ExpectedSha256 $otaHash | Out-Null
  Write-LicenseSha256CheckFile -PayloadPath (Join-Path $script:StagedPublishDirectory 'superpan-license') -CheckFilePath (Join-Path $script:StagedPublishDirectory 'superpan-license.sha256') -ExpectedSha256 $licenseHash | Out-Null
  Write-LicenseSha256CheckFile -PayloadPath (Join-Path $script:StagedPublishDirectory 'source-manifest.sha256') -CheckFilePath (Join-Path $script:StagedPublishDirectory 'source-manifest.sha256.sha256') -ExpectedSha256 $manifestHash | Out-Null
  [IO.File]::WriteAllText((Join-Path $script:StagedPublishDirectory 'source-commit.txt'), ($commit + [char]10), [Text.UTF8Encoding]::new($false))
  $expectedState = New-LicenseBundleExpectedState -OtaAdminSha256 $otaHash -LicenseServiceSha256 $licenseHash -SourceManifestBase64 ([Convert]::ToBase64String($manifestBytes)) -SourceManifestSha256 $manifestHash -SourceCommit $commit
  Assert-LicenseBundleArtifacts -Directory $script:StagedPublishDirectory -ExpectedState $expectedState
  return [pscustomobject]@{
    Staging = $script:StagingDirectory
    Staged = $script:StagedPublishDirectory
    Published = Join-Path $ArtifactsRoot ("published-$Name")
    Backup = Join-Path $ArtifactsRoot ("backup-$Name")
    Expected = $expectedState
  }
}

$success = New-TestPublishCase -Name 'success'
New-Item -ItemType Directory -Path $success.Published | Out-Null
[IO.File]::WriteAllText((Join-Path $success.Published 'old.marker'), 'old-success', [Text.UTF8Encoding]::new($false))
try {
  Publish-VerifiedLicenseBundle -StagedPath $success.Staged -PublishedPath $success.Published -PreviousPath $success.Backup -ExpectedState $success.Expected
  Assert-LicenseBundleArtifacts -Directory $success.Published -ExpectedState $success.Expected
  if (Test-Path -LiteralPath $success.Backup) { throw 'successful publication retained old backup' }
}
finally { if (Test-Path -LiteralPath $success.Staging) { Remove-SafeStagingDirectory -Path $success.Staging } }

$originalAssert = ${function:Assert-LicenseBundleArtifacts}
$script:originalLicenseAssert = $originalAssert
$script:tamperPublished = $false
$script:tamperDirectory = $null
function Assert-LicenseBundleArtifacts {
  param([Parameter(Mandatory)][string] $Directory, [Parameter(Mandatory)][psobject] $ExpectedState)
  if ($script:tamperPublished -and [IO.Path]::GetFullPath($Directory).Equals([IO.Path]::GetFullPath($script:tamperDirectory), [StringComparison]::OrdinalIgnoreCase)) {
    $script:tamperPublished = $false
    [IO.File]::WriteAllBytes((Join-Path $Directory 'superpan-license'), [byte[]]@(1,2,3))
  }
  & $script:originalLicenseAssert -Directory $Directory -ExpectedState $ExpectedState
}

$withOld = New-TestPublishCase -Name 'with-old'
New-Item -ItemType Directory -Path $withOld.Published | Out-Null
[IO.File]::WriteAllText((Join-Path $withOld.Published 'old.marker'), 'exact-old-bundle', [Text.UTF8Encoding]::new($false))
$script:tamperDirectory = $withOld.Published
$script:tamperPublished = $true
try {
  try { Publish-VerifiedLicenseBundle -StagedPath $withOld.Staged -PublishedPath $withOld.Published -PreviousPath $withOld.Backup -ExpectedState $withOld.Expected; throw 'tampered publication with old bundle unexpectedly succeeded' }
  catch { if ($_.Exception.Message -eq 'tampered publication with old bundle unexpectedly succeeded') { throw } }
  $oldNames = @(Get-ChildItem -LiteralPath $withOld.Published -Force | ForEach-Object Name)
  if ($oldNames.Count -ne 1 -or $oldNames[0] -cne 'old.marker') { throw 'old bundle file set was not restored exactly' }
  if ([IO.File]::ReadAllText((Join-Path $withOld.Published 'old.marker')) -cne 'exact-old-bundle') { throw 'old bundle bytes were not restored exactly' }
  if (Test-Path -LiteralPath $withOld.Backup) { throw 'restored old bundle was also left in backup path' }
}
finally { if (Test-Path -LiteralPath $withOld.Staging) { Remove-SafeStagingDirectory -Path $withOld.Staging } }

$withoutOld = New-TestPublishCase -Name 'without-old'
$script:tamperDirectory = $withoutOld.Published
$script:tamperPublished = $true
try {
  try { Publish-VerifiedLicenseBundle -StagedPath $withoutOld.Staged -PublishedPath $withoutOld.Published -PreviousPath $withoutOld.Backup -ExpectedState $withoutOld.Expected; throw 'tampered publication without old bundle unexpectedly succeeded' }
  catch { if ($_.Exception.Message -eq 'tampered publication without old bundle unexpectedly succeeded') { throw } }
  if (Test-Path -LiteralPath $withoutOld.Published) { throw 'invalid published bundle was left at live path' }
  if (Test-Path -LiteralPath $withoutOld.Backup) { throw 'invalid published bundle was unexpectedly retained as backup' }
}
finally { if (Test-Path -LiteralPath $withoutOld.Staging) { Remove-SafeStagingDirectory -Path $withoutOld.Staging } }
`
	if err := os.WriteFile(harnessPath, []byte(harness), 0o600); err != nil {
		t.Fatalf("write verified publication harness: %v", err)
	}
	buildScriptPath := filepath.Join(testModuleRoot(t), "scripts", "build_linux_license_bundle.ps1")
	output, runErr := exec.Command(powerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath, buildScriptPath, artifactRoot).CombinedOutput()
	if runErr != nil {
		t.Fatalf("verified license publication harness failed: %v; output:\n%s", runErr, output)
	}
}

func TestLicenseBundleFunctionsEnforceSourceAndArtifactContracts(t *testing.T) {
	powerShell, err := exec.LookPath("powershell")
	if err != nil {
		t.Skip("PowerShell is unavailable on this compatibility-test host")
	}

	root := testLocalTempDir(t)
	repositoryRoot := filepath.Join(root, "repository")
	moduleRoot := filepath.Join(repositoryRoot, "services", "ota_admin")
	tracked := map[string]string{
		"go.mod": "module example.test/license\n",
		"go.sum": "",
		filepath.Join("cmd", "ota-admin", "main.go"):               "package main\n",
		filepath.Join("cmd", "license-service", "main.go"):         "package main\n",
		filepath.Join("internal", "shared", "shared.go"):           "package shared\n",
		filepath.Join("internal", "webui", "static", "index.html"): "<main>committed</main>\n",
		filepath.Join("cmd", "ota-admin-acceptance", "main.go"):    "package main\n",
		filepath.Join("cmd", "license-load-generator", "main.go"):  "package main\n",
		"production.json": "{}\n",
		"private.key":     "not-a-real-key\n",
		"state.db":        "not-a-real-database\n",
	}
	for relativePath, contents := range tracked {
		path := filepath.Join(moduleRoot, relativePath)
		if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
			t.Fatalf("create fixture parent: %v", err)
		}
		if err := os.WriteFile(path, []byte(contents), 0o600); err != nil {
			t.Fatalf("write fixture %q: %v", relativePath, err)
		}
	}
	for _, args := range [][]string{
		{"init"},
		{"config", "user.email", "compat@example.invalid"},
		{"config", "user.name", "Compatibility Test"},
		{"add", "services/ota_admin"},
		{"commit", "-m", "fixture"},
	} {
		command := exec.Command("git", append([]string{"-C", repositoryRoot}, args...)...)
		if output, commandErr := command.CombinedOutput(); commandErr != nil {
			t.Fatalf("git %v failed: %v; output:\n%s", args, commandErr, output)
		}
	}
	commitOutput, err := exec.Command("git", "-C", repositoryRoot, "rev-parse", "HEAD").Output()
	if err != nil {
		t.Fatalf("resolve fixture commit: %v", err)
	}
	commit := strings.TrimSpace(string(commitOutput))
	untrackedPath := filepath.Join(moduleRoot, "untracked.go")
	if err := os.WriteFile(untrackedPath, []byte("package excluded\n"), 0o600); err != nil {
		t.Fatalf("write untracked fixture: %v", err)
	}

	buildScript := filepath.Join(testModuleRoot(t), "scripts", "build_linux_license_bundle.ps1")
	harnessPath := filepath.Join(root, "license-bundle-functions.ps1")
	snapshotRoot := filepath.Join(root, "snapshot")
	artifactRoot := filepath.Join(root, "artifacts")
	harness := `param([string] $BuildScript, [string] $FixtureRepository, [string] $Commit, [string] $SnapshotRoot, [string] $TestArtifactRoot)
$ErrorActionPreference = 'Stop'
. $BuildScript -FunctionsOnly
$RepositoryRoot = [System.IO.Path]::GetFullPath($FixtureRepository)

try { Assert-LicenseBundleSourceState -SourceCommit ('0' * 40); throw 'wrong commit unexpectedly accepted' }
catch { if ($_.Exception.Message -eq 'wrong commit unexpectedly accepted') { throw } }
try { Assert-LicenseBundleSourceState -SourceCommit $Commit; throw 'dirty tree unexpectedly accepted' }
catch { if ($_.Exception.Message -eq 'dirty tree unexpectedly accepted') { throw } }
Remove-Item -LiteralPath (Join-Path $FixtureRepository 'services\ota_admin\untracked.go') -Force
Assert-LicenseBundleSourceState -SourceCommit $Commit

[string[]] $paths = @(Get-LicenseBundleSourcePaths -SourceCommit $Commit)
[string[]] $expected = @(
  'services/ota_admin/cmd/license-service/main.go',
  'services/ota_admin/cmd/ota-admin/main.go',
  'services/ota_admin/go.mod',
  'services/ota_admin/go.sum',
  'services/ota_admin/internal/shared/shared.go',
  'services/ota_admin/internal/webui/static/index.html'
)
if ([string]::Join([char]10, $paths) -cne [string]::Join([char]10, $expected)) { throw ('unexpected source allowlist:' + [char]10 + ($paths -join [char]10)) }
New-LicenseBundleSourceSnapshot -SourcePaths $paths -DestinationRoot $SnapshotRoot -SourceCommit $Commit
if ((Get-Content -Raw -LiteralPath (Join-Path $SnapshotRoot 'internal\webui\static\index.html')) -notlike '*committed*') { throw 'snapshot did not contain committed bytes' }

$ArtifactsRoot = [System.IO.Path]::GetFullPath($TestArtifactRoot)
New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null
$StagingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('spota-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$StagedPublishDirectory = Join-Path $StagingDirectory 'p'
$staged = $StagedPublishDirectory
$published = Join-Path $ArtifactsRoot 'license-production'
$backup = Join-Path $ArtifactsRoot 'backup'
New-SafeStagingDirectory -Path $StagingDirectory
try {
  New-Item -ItemType Directory -Path $staged | Out-Null
  [System.IO.File]::WriteAllText((Join-Path $staged 'ota-admin'), ('ota' + [char]10), [System.Text.UTF8Encoding]::new($false))
  [System.IO.File]::WriteAllText((Join-Path $staged 'superpan-license'), ('license' + [char]10), [System.Text.UTF8Encoding]::new($false))
  $otaHash = Write-LicenseSha256CheckFile -PayloadPath (Join-Path $staged 'ota-admin') -CheckFilePath (Join-Path $staged 'ota-admin.sha256')
  $licenseHash = Write-LicenseSha256CheckFile -PayloadPath (Join-Path $staged 'superpan-license') -CheckFilePath (Join-Path $staged 'superpan-license.sha256')
  Write-DeterministicSourceManifest -SnapshotRoot $SnapshotRoot -ManifestPath (Join-Path $staged 'source-manifest.sha256')
  $manifestBytes = [IO.File]::ReadAllBytes((Join-Path $staged 'source-manifest.sha256'))
  $manifestHash = Write-LicenseSha256CheckFile -PayloadPath (Join-Path $staged 'source-manifest.sha256') -CheckFilePath (Join-Path $staged 'source-manifest.sha256.sha256')
  [System.IO.File]::WriteAllText((Join-Path $staged 'source-commit.txt'), ($Commit + [char]10), [System.Text.UTF8Encoding]::new($false))
  $expectedState = New-LicenseBundleExpectedState -OtaAdminSha256 $otaHash -LicenseServiceSha256 $licenseHash -SourceManifestBase64 ([Convert]::ToBase64String($manifestBytes)) -SourceManifestSha256 $manifestHash -SourceCommit $Commit
  Assert-LicenseBundleArtifacts -Directory $staged -ExpectedState $expectedState

  [System.IO.File]::WriteAllText((Join-Path $staged 'ota-admin'), ('coordinated-tamper' + [char]10), [System.Text.UTF8Encoding]::new($false))
  Write-LicenseSha256CheckFile -PayloadPath (Join-Path $staged 'ota-admin') -CheckFilePath (Join-Path $staged 'ota-admin.sha256') | Out-Null
  try { Assert-LicenseBundleArtifacts -Directory $staged -ExpectedState $expectedState; throw 'coordinated payload tamper unexpectedly accepted' }
  catch { if ($_.Exception.Message -eq 'coordinated payload tamper unexpectedly accepted') { throw } }
  [System.IO.File]::WriteAllText((Join-Path $staged 'ota-admin'), ('ota' + [char]10), [System.Text.UTF8Encoding]::new($false))
  Write-LicenseSha256CheckFile -PayloadPath (Join-Path $staged 'ota-admin') -CheckFilePath (Join-Path $staged 'ota-admin.sha256') | Out-Null

  [System.IO.File]::WriteAllText((Join-Path $staged 'source-manifest.sha256'), ('0' * 64 + '  injected.go' + [char]10), [System.Text.UTF8Encoding]::new($false))
  Write-LicenseSha256CheckFile -PayloadPath (Join-Path $staged 'source-manifest.sha256') -CheckFilePath (Join-Path $staged 'source-manifest.sha256.sha256') | Out-Null
  try { Assert-LicenseBundleArtifacts -Directory $staged -ExpectedState $expectedState; throw 'coordinated manifest tamper unexpectedly accepted' }
  catch { if ($_.Exception.Message -eq 'coordinated manifest tamper unexpectedly accepted') { throw } }
  [IO.File]::WriteAllBytes((Join-Path $staged 'source-manifest.sha256'), $manifestBytes)
  Write-LicenseSha256CheckFile -PayloadPath (Join-Path $staged 'source-manifest.sha256') -CheckFilePath (Join-Path $staged 'source-manifest.sha256.sha256') | Out-Null

  Assert-LicenseBundleArtifacts -Directory $staged -ExpectedState $expectedState
  Publish-VerifiedLicenseBundle -StagedPath $staged -PublishedPath $published -PreviousPath $backup -ExpectedState $expectedState
  if (-not (Test-Path -LiteralPath (Join-Path $published 'superpan-license') -PathType Leaf)) { throw 'atomic publication omitted license binary' }
  Assert-LicenseBundleArtifacts -Directory $published -ExpectedState $expectedState
  [IO.File]::WriteAllBytes((Join-Path $published 'superpan-license'), [byte[]]@(1,2,3))
  try { Assert-LicenseBundleArtifacts -Directory $published -ExpectedState $expectedState; throw 'published truncation unexpectedly accepted' }
  catch { if ($_.Exception.Message -eq 'published truncation unexpectedly accepted') { throw } }
}
finally {
  if (Test-Path -LiteralPath $StagingDirectory) { Remove-SafeStagingDirectory -Path $StagingDirectory }
}
`
	if err := os.WriteFile(harnessPath, []byte(harness), 0o600); err != nil {
		t.Fatalf("write license bundle harness: %v", err)
	}
	output, runErr := exec.Command(powerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath, buildScript, repositoryRoot, commit, snapshotRoot, artifactRoot).CombinedOutput()
	if runErr != nil {
		t.Fatalf("license bundle function harness failed: %v; output:\n%s", runErr, output)
	}

	manifest, err := os.ReadFile(filepath.Join(artifactRoot, "license-production", "source-manifest.sha256"))
	if err != nil {
		t.Fatalf("read published source manifest: %v", err)
	}
	manifestText := string(manifest)
	if strings.Contains(manifestText, "\\") || !strings.HasSuffix(manifestText, "\n") || strings.Contains(manifestText, "\r") {
		t.Fatalf("source manifest is not canonical LF with slash paths: %q", manifestText)
	}
	lines := strings.Split(strings.TrimSuffix(manifestText, "\n"), "\n")
	if !slices.IsSorted(lines) {
		t.Fatalf("source manifest lines are not ordinally sorted: %q", lines)
	}
}

func TestLinuxStateVerificationDoesNotOpenSQLiteTargetsAfterConnection(t *testing.T) {
	t.Parallel()

	source := strings.ReplaceAll(readRepositoryFile(t, "internal", "store", "state_path_linux.go"), "\r\n", "\n")
	start := strings.Index(source, "func verifyPinnedStateFiles")
	if start < 0 {
		t.Fatal("Linux state path implementation does not define verifyPinnedStateFiles")
	}
	end := strings.Index(source[start:], "\n}\n")
	if end < 0 {
		t.Fatal("could not isolate verifyPinnedStateFiles")
	}
	verification := source[start : start+end]
	for _, prohibited := range []string{"unix.Openat", "unix.Close", "unix.Fchmod"} {
		if strings.Contains(verification, prohibited) {
			t.Errorf("post-open SQLite state verification still uses %s", prohibited)
		}
	}
	for _, required := range []string{"unix.Fstatat", "unix.AT_SYMLINK_NOFOLLOW"} {
		if !strings.Contains(verification, required) {
			t.Errorf("post-open SQLite state verification does not use %s", required)
		}
	}
}

func TestBuildOnlyRemovesValidatedUniqueSystemTempStagingRoot(t *testing.T) {
	powerShell, err := exec.LookPath("powershell")
	if err != nil {
		t.Skip("PowerShell is unavailable on this compatibility-test host")
	}

	buildScript := readRepositoryFile(t, "scripts", "build_linux_amd64.ps1")
	for _, required := range []string{
		"function Assert-SafeStagingDirectory",
		"function New-SafeStagingDirectory",
		"function Remove-SafeStagingDirectory",
		`'^spota-[0-9a-f]{12}$'`,
		`[System.IO.FileAttributes]::ReparsePoint`,
		`Remove-SafeStagingDirectory -Path $StagingDirectory`,
	} {
		if !strings.Contains(buildScript, required) {
			t.Errorf("build script does not contain safe staging contract %q", required)
		}
	}
	if strings.Contains(buildScript, `Remove-Item -LiteralPath $StagingDirectory -Recurse`) {
		t.Fatal("build cleanup bypasses the staging-specific validator")
	}

	protectedRoot := filepath.Join(testLocalTempDir(t), "protected")
	if err := os.Mkdir(protectedRoot, 0o700); err != nil {
		t.Fatalf("create protected root: %v", err)
	}
	protectedMarker := filepath.Join(protectedRoot, "marker.txt")
	if err := os.WriteFile(protectedMarker, []byte("preserve"), 0o600); err != nil {
		t.Fatalf("write protected marker: %v", err)
	}
	harnessPath := filepath.Join(testLocalTempDir(t), "staging-cleanup-harness.ps1")
	harness := `param([string] $BuildScript, [string] $ProtectedRoot)
$ErrorActionPreference = 'Stop'
. $BuildScript -FunctionsOnly

try { Remove-SafeStagingDirectory -Path $ProtectedRoot; throw 'outside removal unexpectedly succeeded' }
catch { if ($_.Exception.Message -eq 'outside removal unexpectedly succeeded') { throw } }
if (-not (Test-Path -LiteralPath (Join-Path $ProtectedRoot 'marker.txt') -PathType Leaf)) { throw 'outside marker was removed' }

$nested = Join-Path $ProtectedRoot 'spota-0123456789ab'
try { Remove-SafeStagingDirectory -Path $nested; throw 'nested removal unexpectedly succeeded' }
catch { if ($_.Exception.Message -eq 'nested removal unexpectedly succeeded') { throw } }

$invalidLeaf = Join-Path ([System.IO.Path]::GetTempPath()) 'spota-0123456789ab-extra'
try { Remove-SafeStagingDirectory -Path $invalidLeaf; throw 'invalid leaf removal unexpectedly succeeded' }
catch { if ($_.Exception.Message -eq 'invalid leaf removal unexpectedly succeeded') { throw } }

$valid = Join-Path ([System.IO.Path]::GetTempPath()) ('spota-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
try {
  New-SafeStagingDirectory -Path $valid
  Set-Content -LiteralPath (Join-Path $valid 'marker.txt') -Value staging
  try { New-SafeStagingDirectory -Path $valid; throw 'preexisting staging root unexpectedly accepted' }
  catch { if ($_.Exception.Message -eq 'preexisting staging root unexpectedly accepted') { throw } }
}
finally {
  if (Test-Path -LiteralPath $valid) { Remove-SafeStagingDirectory -Path $valid }
}
if (Test-Path -LiteralPath $valid) { throw 'validated staging root was not removed' }
`
	if err := os.WriteFile(harnessPath, []byte(harness), 0o600); err != nil {
		t.Fatalf("write staging cleanup harness: %v", err)
	}
	output, runErr := exec.Command(powerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath, filepath.Join(testModuleRoot(t), "scripts", "build_linux_amd64.ps1"), protectedRoot).CombinedOutput()
	if runErr != nil {
		t.Fatalf("staging cleanup harness failed: %v; output:\n%s", runErr, output)
	}
	if _, err := os.Stat(protectedMarker); err != nil {
		t.Fatalf("staging cleanup touched protected marker: %v", err)
	}
}

func TestBuildPublishesOnlyFromValidatedStagingOutput(t *testing.T) {
	t.Parallel()

	buildScript := strings.ReplaceAll(readRepositoryFile(t, "scripts", "build_linux_amd64.ps1"), "\r\n", "\n")
	for _, required := range []string{
		"function Assert-SafeStagedPublishDirectory",
		"function Assert-SameVolumeArtifactPublish",
		"Assert-SafeStagingDirectory -Path $StagingDirectory",
		"$fullPath.Equals($expectedPath, [System.StringComparison]::OrdinalIgnoreCase)",
		"Assert-SafeStagedPublishDirectory -Path $StagedPath",
		"Assert-SameVolumeArtifactPublish -StagedPath $StagedPath -PublishedPath $PublishedPath",
	} {
		if !strings.Contains(buildScript, required) {
			t.Errorf("build script does not contain staged-publication safety contract %q", required)
		}
	}
	publishOffset := strings.Index(buildScript, "function Publish-StagedArtifacts")
	if publishOffset < 0 {
		t.Fatal("build script does not contain artifact publication function")
	}
	publishEnd := strings.Index(buildScript[publishOffset:], "\n}\n\nif ($FunctionsOnly)")
	if publishEnd < 0 {
		t.Fatal("could not isolate artifact publication function")
	}
	publishFunction := buildScript[publishOffset : publishOffset+publishEnd]
	if strings.Contains(publishFunction, "Assert-SafeArtifactDirectory -Path $StagedPath") {
		t.Fatal("temporary staged output is still incorrectly required to live under the artifact root")
	}
	requireSubstringsInOrder(t, publishFunction,
		"Assert-SafeStagedPublishDirectory -Path $StagedPath",
		"Assert-SameVolumeArtifactPublish -StagedPath $StagedPath -PublishedPath $PublishedPath",
		"Move-Item -LiteralPath $PublishedPath -Destination $PreviousPath",
	)
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
$backup = Join-Path $ArtifactsRoot 'backup'
$StagingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('spota-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$StagedPublishDirectory = Join-Path $StagingDirectory 'p'
$staged = $StagedPublishDirectory
New-Item -ItemType Directory -Path $published, $StagingDirectory, $staged -Force | Out-Null
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
	path, err := os.MkdirTemp("", "ota-compat-")
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
	return strings.ReplaceAll(string(contents), "\r\n", "\n")
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
