package deploymentcontract

import (
	"strings"
	"testing"
)

func requireFragments(t *testing.T, body string, fragments ...string) {
	t.Helper()
	for _, fragment := range fragments {
		if !strings.Contains(body, fragment) {
			t.Errorf("missing deployment contract fragment %q", fragment)
		}
	}
}

func requireBefore(t *testing.T, body, earlier, later string) {
	t.Helper()
	earlierAt, laterAt := strings.Index(body, earlier), strings.Index(body, later)
	if earlierAt < 0 || laterAt < 0 || earlierAt >= laterAt {
		t.Errorf("expected %q before %q", earlier, later)
	}
}

func TestLicenseInstallBundleAuthentication(t *testing.T) {
	install := read(t, "deploy", "license-service", "install.sh")
	requireFragments(t, install,
		"--bundle", "--source-commit", "--ota-binary-sha256", "--license-binary-sha256",
		"--source-manifest-sha256", "--license-config", "--license-config-sha256",
		"--license-unit", "--license-unit-sha256", "--ota-config", "--ota-config-sha256",
		"--ota-unit", "--ota-unit-sha256",
		"readlink -f -- \"$BUNDLE\"", "bundle path must be absolute and canonical",
		"ota-admin", "ota-admin.sha256", "superpan-license", "superpan-license.sha256",
		"source-manifest.sha256", "source-manifest.sha256.sha256", "source-commit.txt",
		"bundle is incomplete or contains unexpected files",
		"verify_sidecar", `printf '%s  %s\n' "$expected" "$name" | cmp -s - "$sidecar"`,
		"verify_sha256 \"$BUNDLE/ota-admin\" \"$OTA_BINARY_SHA256\"",
		"verify_sha256 \"$BUNDLE/superpan-license\" \"$LICENSE_BINARY_SHA256\"",
		"verify_sha256 \"$BUNDLE/source-manifest.sha256\" \"$SOURCE_MANIFEST_SHA256\"",
		"verify_sha256 \"$LICENSE_CONFIG_SOURCE\" \"$LICENSE_CONFIG_SHA256\"",
		"verify_sha256 \"$LICENSE_UNIT_SOURCE\" \"$LICENSE_UNIT_SHA256\"",
		"verify_sha256 \"$OTA_CONFIG_SOURCE\" \"$OTA_CONFIG_SHA256\"",
		"verify_sha256 \"$OTA_UNIT_SOURCE\" \"$OTA_UNIT_SHA256\"",
	)
	if strings.Contains(install, `: "${BUNDLE:`) {
		t.Error("bundle must be an explicit argument, not an implicit environment input")
	}
	for _, name := range []string{"config.json", "superpan-license.service", "superpan-ota-admin.service"} {
		if strings.Contains(install, `BUNDLE/`+name) {
			t.Errorf("trusted template %s must not be required inside the seven-file bundle", name)
		}
	}
}

func TestLicenseInstallPreflight(t *testing.T) {
	install := read(t, "deploy", "license-service", "install.sh")
	requireFragments(t, install,
		"[ -d \"$BUNDLE\" ] && [ ! -L \"$BUNDLE\" ]",
		"stat -c '%U:%G' \"$BUNDLE\"", "root:root", "stat -c '%a' \"$BUNDLE\"",
		"-type l", "-links +1", "-perm /022", "# MUTATION BOUNDARY",
	)
	for _, mutation := range []string{"mkdir -p", "install -d", "groupadd", "useradd", "BACKUP_DIR=", "systemctl daemon-reload"} {
		if strings.Contains(install, mutation) {
			requireBefore(t, install, "# MUTATION BOUNDARY", mutation)
		}
	}
	for _, forbidden := range []string{"ota.fanfh.cn.license-api.nginx.conf", "nginx -t", "nginx -s reload", "ACTIVE_VHOST", "active-vhost"} {
		if strings.Contains(install, forbidden) {
			t.Errorf("license transaction must not contain public-route operation %q", forbidden)
		}
	}
}

func TestLicenseInstallLoopbackOnly(t *testing.T) {
	ota := read(t, "deploy", "ota-admin", "install.sh")
	requireFragments(t, ota,
		"--loopback-license-transaction", "LOOPBACK_LICENSE_TRANSACTION",
		"--config", "--config-sha256", "--unit", "--unit-sha256", "--source-commit",
		"preflight_loopback_license_transaction", "loopback license transaction preflight complete",
		"public-install arguments are forbidden in loopback license transaction mode",
		"loopback transaction arguments require --loopback-license-transaction",
		"loopback transaction inputs must be owned by root:root",
	)
	requireBefore(t, ota, `if [ "$LOOPBACK_LICENSE_TRANSACTION" = true ]; then`, `[ -f "$ACTIVE_VHOST" ]`)
	requireBefore(t, ota, "preflight_loopback_license_transaction", "loopback license transaction preflight complete")
	requireBefore(t, ota, "loopback license transaction preflight complete", "INCLUDE_LINE=")
}

func TestLicenseInstallSameCommit(t *testing.T) {
	install := read(t, "deploy", "license-service", "install.sh")
	requireFragments(t, install,
		`[[ "$SOURCE_COMMIT" =~ ^[0-9a-f]{40}$ ]]`,
		`[ "$BUNDLE_SOURCE_COMMIT" = "$SOURCE_COMMIT" ]`,
		`SOURCE_COMMIT_TARGET="/opt/superpan-license/source-commit.txt"`,
		`install -m 0644 -o root -g root "$BUNDLE/source-commit.txt" "$SOURCE_COMMIT_TARGET"`,
		`--source-commit "$SOURCE_COMMIT"`,
		`--binary "$BUNDLE/ota-admin"`,
	)
	if strings.Contains(install, "git rev-parse") {
		t.Error("installer must use the explicitly authenticated source commit, not ambient Git state")
	}
}

func TestLicenseInstallersAreFailClosed(t *testing.T) {
	install := read(t, "deploy", "license-service", "install.sh")
	rollback := read(t, "deploy", "license-service", "rollback.sh")
	for name, body := range map[string]string{"install.sh": install, "rollback.sh": rollback} {
		if !strings.Contains(body, "set -euo pipefail") {
			t.Errorf("%s must enable strict shell mode", name)
		}
		if !strings.Contains(body, "id -u") {
			t.Errorf("%s must require root", name)
		}
		if strings.Contains(body, "nginx -s reload") {
			t.Errorf("%s must not alter public nginx", name)
		}
	}
	if !strings.Contains(install, "127.0.0.1:18092") {
		t.Error("installer must pin loopback listener")
	}
}
