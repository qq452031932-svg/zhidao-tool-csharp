package deploymentcontract

import (
	"os/exec"
	"reflect"
	"strings"
	"testing"
)

func TestCapacityUnitParsesAndMatchesProductionIsolation(t *testing.T) {
	prod, err := unitDirectives(read(t, "deploy", "license-service", "superpan-license.service"))
	if err != nil {
		t.Fatal(err)
	}
	load, err := unitDirectives(read(t, "deploy", "license-service", "superpan-license-loadtest.service"))
	if err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"User", "Group", "SupplementaryGroups", "UMask", "NoNewPrivileges", "PrivateTmp", "PrivateDevices", "ProtectSystem", "ProtectHome", "InaccessibleDirectories", "CapabilityBoundingSet", "RestrictAddressFamilies", "LimitNOFILE", "TimeoutStopSec"} {
		if !reflect.DeepEqual(load["Service"][key], prod["Service"][key]) {
			t.Errorf("%s differs: load=%v prod=%v", key, load["Service"][key], prod["Service"][key])
		}
	}
	if got := load["Service"]["ExecStart"]; len(got) != 1 || !strings.Contains(got[0], "superpan-license serve") || !strings.Contains(got[0], "license-capacity-18093/config.json") {
		t.Fatalf("wrong ExecStart: %v", got)
	}
	if got := load["Service"]["WorkingDirectory"]; !reflect.DeepEqual(got, []string{"/var/lib/license-capacity-18093"}) {
		t.Fatalf("wrong working directory: %v", got)
	}
}

func TestCapacityOrchestratorHasExactIdentitySamplingAndCleanupContract(t *testing.T) {
	body := read(t, "deploy", "license-service", "run-capacity-test.sh")
	requireFragments(t, body, "--bundle", "--tools", "source-commit.txt", "superpan-license.sha256", "license-loadtest", "license-dbcheck", "MainPID", "/proc/$MAIN_PID/exe", "/proc/$MAIN_PID/stat", "/proc/meminfo", "/proc/$MAIN_PID/status", "50 600", "100 600", "runuser -u superpan-license -- \"$TOOLS/license-loadtest\"", "--load-secret-fd 3", "/internal/health", "CPU_AVG", "RSS_GROWTH", "MEMORY_FLOOR", "systemctl stop superpan-license-loadtest.service", "instance.stopped", "PUBLIC_INCLUDE")
	if strings.Index(body, "assert_public_absent\ncanonical_dir") >= strings.LastIndex(body, "systemctl start \"$UNIT\"") {
		t.Fatal("public-route gate is not before start")
	}
	if strings.LastIndex(body, "systemctl stop superpan-license-loadtest.service") >= strings.LastIndex(body, "runuser -u superpan-license") {
		t.Fatal("dbcheck is not after exact stop")
	}
	if strings.Contains(body, "eval ") || strings.Contains(body, "rm -rf") || strings.Contains(body, "LOAD_SECRET=") {
		t.Fatal("unsafe orchestration construct")
	}
}

func TestCapacityBuilderUsesCleanSameCommitImmutableSnapshot(t *testing.T) {
	body := read(t, "services", "ota_admin", "scripts", "build_linux_license_test_tools.ps1")
	requireFragments(t, body, "[Parameter(Mandatory)][string] $SourceCommit", "SourceCommit must exactly equal HEAD", "status --porcelain=v1 --untracked-files=all", "git -C $RepositoryRoot", "ls-tree", "New-LicenseBundleSourceSnapshot", "Set-SealedLicenseBuildEnvironment", "Assert-PinnedLicenseGoToolchain", "license-loadtest", "license-dbcheck", "'.sha256'", "source-commit.txt", "[System.IO.Directory]::Move")
	requireBefore(t, body, "Assert-LicenseTestToolSourceState", "New-LicenseTestToolSourceSnapshot")
	if strings.LastIndex(body, "New-LicenseTestToolSourceSnapshot -SourcePaths") >= strings.LastIndex(body, "foreach ($name") {
		t.Fatal("snapshot is not created before builds")
	}
}

func TestCapacityShellSyntax(t *testing.T) {
	bash, err := exec.LookPath("bash")
	if err != nil {
		t.Skip("bash unavailable")
	}
	for _, name := range []string{"deploy/license-service/run-capacity-test.sh"} {
		cmd := exec.Command(bash, "-n", name)
		cmd.Dir = repositoryRoot(t)
		if output, err := cmd.CombinedOutput(); err != nil {
			t.Fatalf("%s: %v: %s", name, err, output)
		}
	}
}

func TestCapacityDocumentIsUnfilledEvidenceTemplate(t *testing.T) {
	doc := read(t, "docs", "testing", "2026-09-10-license-capacity-acceptance.md")
	for _, want := range []string{"50 requests/second", "100 requests/second", "600 seconds", "70%", "10%", "20%", "250 ms", "0.1%", "70%", "1 GiB", "10%", "BUSY", "LOCKED", "PENDING AUTHORIZED LINUX STAGING"} {
		if !strings.Contains(doc, want) {
			t.Errorf("missing %q", want)
		}
	}
	for _, forbidden := range []string{"PASS (staging)", "staging evidence: complete"} {
		if strings.Contains(doc, forbidden) {
			t.Errorf("fabricated evidence marker %q", forbidden)
		}
	}
}
