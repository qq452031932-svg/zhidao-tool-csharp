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
	"path/filepath"
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

func TestSQLiteProbeCanRunAgainAfterServiceRestart(t *testing.T) {
	t.Parallel()

	databasePath := filepath.Join(t.TempDir(), "probe.db")
	if err := exerciseSQLite(databasePath); err != nil {
		t.Fatalf("first exerciseSQLite() error = %v", err)
	}
	if err := exerciseSQLite(databasePath); err != nil {
		t.Fatalf("second exerciseSQLite() error = %v", err)
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

func readRepositoryFile(t *testing.T, pathParts ...string) string {
	t.Helper()
	path := filepath.Join(append([]string{"..", ".."}, pathParts...)...)
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
