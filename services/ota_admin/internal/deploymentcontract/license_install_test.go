package deploymentcontract

import (
	"crypto/sha256"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
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
		"declare -A SEEN_ARGS", "mark_seen", "require_seen",
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
		`[ "$BUNDLE_MODE" = "700" ]`, "-type l", "-links +1", "-perm /022",
		"--preflight-only", "LICENSE_INSTALL_PREFLIGHT_COMPLETE",
		"bash \"$OTA_INSTALLER\" --loopback-license-transaction \\\n    --preflight-only",
		`OTA_INSTALLER="$(cd "$(dirname "${BASH_SOURCE[0]}")/../ota-admin" && pwd)/install.sh"`,
		`[ "$(readlink -f -- "$OTA_INSTALLER")" = "$OTA_INSTALLER" ]`,
		`[ "$(stat -c '%U:%G' "$OTA_INSTALLER")" = "root:root" ]`,
		`[ "$(stat -c '%h' "$OTA_INSTALLER")" = "1" ]`,
		"OTA_INSTALLER_MODE=", "OTA installer must not be group/world writable",
		"validate_trusted_path", "TRUSTED_PATH_COMPONENTS", "component_path",
		`[ ! -L "$component_path" ]`, `stat -c '%u' "$component_path"`,
		"trusted path component must be owned by root",
		"trusted path component must not be group/world writable",
		`[ "$(stat -c '%u' /)" = "0" ]`, "trusted root must not be group/world writable",
		"Root ownership plus a non-writable ancestor chain prevents unprivileged rename",
		`validate_trusted_path "$BUNDLE"`, `validate_trusted_path "$LICENSE_CONFIG_SOURCE"`,
		`validate_trusted_path "$LICENSE_UNIT_SOURCE"`, `validate_trusted_path "$OTA_CONFIG_SOURCE"`,
		`validate_trusted_path "$OTA_UNIT_SOURCE"`, `validate_trusted_path "$OTA_INSTALLER"`,
	)
	if strings.Count(install, "# LICENSE_INSTALL_PREFLIGHT_COMPLETE") != 1 {
		t.Fatal("license installer must have exactly one preflight-complete mutation boundary")
	}
	boundaryAt := strings.Index(install, "# LICENSE_INSTALL_PREFLIGHT_COMPLETE")
	otaPreflightAt := strings.Index(install, `bash "$OTA_INSTALLER"`)
	for _, command := range []string{
		"mkdir", "groupadd", "useradd", "usermod", "install", "cp", "mv",
		"rm", "chown", "chmod", "touch", "tee", "systemctl",
	} {
		matcher := regexp.MustCompile(`(?m)(^|[;[:space:]])` + regexp.QuoteMeta(command) + `[[:space:]]`)
		for _, location := range matcher.FindAllStringIndex(install, -1) {
			if location[0] < boundaryAt {
				t.Errorf("mutation command %q appears before preflight completion", command)
			}
			if location[0] < otaPreflightAt {
				t.Errorf("mutation command %q appears before OTA preflight", command)
			}
		}
	}
	requireBefore(t, install, `validate_trusted_path "$OTA_INSTALLER"`, `bash "$OTA_INSTALLER"`)
	requireBefore(t, install, `bash "$OTA_INSTALLER"`, "# LICENSE_INSTALL_PREFLIGHT_COMPLETE")
	requireBefore(t, install, "# LICENSE_INSTALL_PREFLIGHT_COMPLETE", `if [ "$PREFLIGHT_ONLY" = true ]; then`)
	requireBefore(t, install, `bash "$OTA_INSTALLER"`, "groupadd --system superpan-license")
	childScripts := regexp.MustCompile(`(?m)^[[:space:]]*(bash|sh)[[:space:]]+`)
	for _, location := range childScripts.FindAllStringIndex(install, -1) {
		if location[0] < boundaryAt && location[0] != otaPreflightAt {
			t.Error("an untrusted external child script invocation appears before preflight completion")
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
		"--preflight-only", "PREFLIGHT_ONLY", "declare -A SEEN_ARGS", "mark_seen", "require_seen",
		"--config", "--config-sha256", "--unit", "--unit-sha256", "--source-commit",
		"preflight_loopback_license_transaction", "loopback license transaction preflight complete",
		"public-install arguments are forbidden in loopback license transaction mode",
		"loopback transaction arguments require --loopback-license-transaction",
		"loopback transaction inputs must be owned by root:root",
		"validate_trusted_path", `validate_trusted_path "$BINARY_SOURCE"`,
		`validate_trusted_path "$LOOPBACK_CONFIG_SOURCE"`, `validate_trusted_path "$LOOPBACK_UNIT_SOURCE"`,
		"trusted path component must not be a symlink", "trusted path component must be owned by root",
		"trusted path component must not be group/world writable",
		`[ "$(stat -c '%u' /)" = "0" ]`, "trusted root must not be group/world writable",
	)
	requireBefore(t, ota, `if [ "$LOOPBACK_LICENSE_TRANSACTION" = true ]; then`, `[ -f "$ACTIVE_VHOST" ]`)
	requireBefore(t, ota, "preflight_loopback_license_transaction", "loopback license transaction preflight complete")
	requireBefore(t, ota, "loopback license transaction preflight complete", "INCLUDE_LINE=")
}

func installerBash(t *testing.T) string {
	t.Helper()
	if path, err := exec.LookPath("bash"); err == nil {
		return path
	}
	if runtime.GOOS == "windows" {
		for _, path := range []string{
			filepath.Join(os.Getenv("ProgramFiles"), "Git", "bin", "bash.exe"),
			filepath.Join(os.Getenv("ProgramFiles"), "Git", "usr", "bin", "bash.exe"),
		} {
			if _, err := os.Stat(path); err == nil {
				return path
			}
		}
	}
	t.Skip("bash is unavailable for executable installer contract tests")
	return ""
}

func runInstaller(t *testing.T, relative string, args ...string) (string, error) {
	t.Helper()
	path := filepath.Join(repositoryRoot(t), filepath.FromSlash(relative))
	command := exec.Command(installerBash(t), append([]string{path}, args...)...)
	output, err := command.CombinedOutput()
	return strings.ReplaceAll(string(output), "\r\n", "\n"), err
}

func optionArguments(options [][2]string) []string {
	var args []string
	for _, option := range options {
		args = append(args, option[0])
		if option[1] != "" {
			args = append(args, option[1])
		}
	}
	return args
}

func TestLicenseInstallPreflightArgumentsExecutable(t *testing.T) {
	licenseOptions := [][2]string{
		{"--bundle", "/fixture/bundle"}, {"--source-commit", strings.Repeat("a", 40)},
		{"--ota-binary-sha256", strings.Repeat("a", 64)}, {"--license-binary-sha256", strings.Repeat("b", 64)},
		{"--source-manifest-sha256", strings.Repeat("c", 64)}, {"--license-config", "/fixture/license-config"},
		{"--license-config-sha256", strings.Repeat("d", 64)}, {"--license-unit", "/fixture/license-unit"},
		{"--license-unit-sha256", strings.Repeat("e", 64)}, {"--ota-config", "/fixture/ota-config"},
		{"--ota-config-sha256", strings.Repeat("f", 64)}, {"--ota-unit", "/fixture/ota-unit"},
		{"--ota-unit-sha256", strings.Repeat("1", 64)}, {"--preflight-only", ""},
	}
	for missing := 0; missing < len(licenseOptions)-1; missing++ {
		option := licenseOptions[missing][0]
		t.Run("license missing "+option, func(t *testing.T) {
			without := append([][2]string{}, licenseOptions[:missing]...)
			without = append(without, licenseOptions[missing+1:]...)
			output, err := runInstaller(t, "deploy/license-service/install.sh", optionArguments(without)...)
			if err == nil || !strings.Contains(output, "missing required option: "+option) {
				t.Fatalf("expected missing-option usage error, got err=%v output=%q", err, output)
			}
		})
	}
	for duplicate := range licenseOptions {
		option := licenseOptions[duplicate]
		t.Run("license duplicate "+option[0], func(t *testing.T) {
			args := append(optionArguments(licenseOptions), option[0])
			if option[1] != "" {
				args = append(args, option[1])
			}
			output, err := runInstaller(t, "deploy/license-service/install.sh", args...)
			if err == nil || !strings.Contains(output, "option must be provided exactly once: "+option[0]) {
				t.Fatalf("expected duplicate-option usage error, got err=%v output=%q", err, output)
			}
		})
	}

	otaOptions := [][2]string{
		{"--loopback-license-transaction", ""}, {"--preflight-only", ""},
		{"--binary", "/fixture/ota"}, {"--binary-sha256", strings.Repeat("a", 64)},
		{"--config", "/fixture/config"}, {"--config-sha256", strings.Repeat("b", 64)},
		{"--unit", "/fixture/unit"}, {"--unit-sha256", strings.Repeat("c", 64)},
		{"--source-commit", strings.Repeat("d", 40)},
	}
	for missing := 1; missing < len(otaOptions); missing++ {
		option := otaOptions[missing][0]
		t.Run("OTA missing "+option, func(t *testing.T) {
			without := append([][2]string{}, otaOptions[:missing]...)
			without = append(without, otaOptions[missing+1:]...)
			output, err := runInstaller(t, "deploy/ota-admin/install.sh", optionArguments(without)...)
			if err == nil || !strings.Contains(output, "missing required option: "+option) {
				t.Fatalf("expected missing-option usage error, got err=%v output=%q", err, output)
			}
		})
	}
	for duplicate := range otaOptions {
		option := otaOptions[duplicate]
		t.Run("OTA duplicate "+option[0], func(t *testing.T) {
			args := append(optionArguments(otaOptions), option[0])
			if option[1] != "" {
				args = append(args, option[1])
			}
			output, err := runInstaller(t, "deploy/ota-admin/install.sh", args...)
			if err == nil || !strings.Contains(output, "option must be provided exactly once: "+option[0]) {
				t.Fatalf("expected duplicate-option usage error, got err=%v output=%q", err, output)
			}
		})
	}

	t.Run("OTA missing mode", func(t *testing.T) {
		output, err := runInstaller(t, "deploy/ota-admin/install.sh", optionArguments(otaOptions[1:])...)
		if err == nil || !strings.Contains(output, "requires --loopback-license-transaction") {
			t.Fatalf("expected missing loopback mode error, got err=%v output=%q", err, output)
		}
	})
}

func fixtureSHA256(body []byte) string {
	return fmt.Sprintf("%x", sha256.Sum256(body))
}

func TestLicenseInstallPreflightExecutableFixture(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Git Bash reports NTFS directories as 0755; exact 0700 and link metadata require Linux")
	}
	uid, err := exec.Command("id", "-u").Output()
	if err != nil || strings.TrimSpace(string(uid)) != "0" {
		t.Skip("trusted-ancestor executable fixture requires Linux root and a /root-owned tree")
	}
	root, err := os.MkdirTemp("/root", "license-install-preflight-")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(root) })
	if err := os.Chmod(root, 0700); err != nil {
		t.Fatal(err)
	}
	fixtureDeploy := filepath.Join(root, "deploy")
	fixtureLicense := filepath.Join(fixtureDeploy, "license-service")
	fixtureOTA := filepath.Join(fixtureDeploy, "ota-admin")
	for _, directory := range []string{fixtureDeploy, fixtureLicense, fixtureOTA} {
		if err := os.Mkdir(directory, 0700); err != nil {
			t.Fatal(err)
		}
	}
	for source, destination := range map[string]string{
		filepath.Join(repositoryRoot(t), "deploy", "license-service", "install.sh"): filepath.Join(fixtureLicense, "install.sh"),
		filepath.Join(repositoryRoot(t), "deploy", "ota-admin", "install.sh"):       filepath.Join(fixtureOTA, "install.sh"),
	} {
		body, err := os.ReadFile(source)
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(destination, body, 0500); err != nil {
			t.Fatal(err)
		}
	}
	licenseInstaller := filepath.Join(fixtureLicense, "install.sh")
	bundle := filepath.Join(root, "bundle")
	if err := os.Mkdir(bundle, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(bundle, 0700); err != nil {
		t.Fatal(err)
	}
	contents := map[string][]byte{
		"ota-admin":              []byte("ota fixture\n"),
		"superpan-license":       []byte("license fixture\n"),
		"source-manifest.sha256": []byte(strings.Repeat("0", 64) + "  go.mod\n"),
	}
	for name, body := range contents {
		if err := os.WriteFile(filepath.Join(bundle, name), body, 0600); err != nil {
			t.Fatal(err)
		}
	}
	commit := strings.Repeat("a", 40)
	if err := os.WriteFile(filepath.Join(bundle, "source-commit.txt"), []byte(commit+"\n"), 0600); err != nil {
		t.Fatal(err)
	}
	for payload, sidecar := range map[string]string{
		"ota-admin": "ota-admin.sha256", "superpan-license": "superpan-license.sha256",
		"source-manifest.sha256": "source-manifest.sha256.sha256",
	} {
		line := fixtureSHA256(contents[payload]) + "  " + payload + "\n"
		if err := os.WriteFile(filepath.Join(bundle, sidecar), []byte(line), 0600); err != nil {
			t.Fatal(err)
		}
	}
	templates := make(map[string]string)
	for _, name := range []string{"license-config", "license-unit", "ota-config", "ota-unit"} {
		path := filepath.Join(root, name)
		if err := os.WriteFile(path, []byte(name+"\n"), 0600); err != nil {
			t.Fatal(err)
		}
		templates[name] = path
	}
	wrappers := filepath.Join(root, "wrappers")
	if err := os.Mkdir(wrappers, 0700); err != nil {
		t.Fatal(err)
	}
	probe := filepath.Join(root, "mutation-probe")
	for _, name := range []string{"mkdir", "groupadd", "useradd", "usermod", "install", "cp", "mv", "rm", "chown", "chmod", "touch", "tee", "systemctl"} {
		body := "#!/bin/sh\necho " + name + " >>\"$MUTATION_PROBE\"\nexit 97\n"
		path := filepath.Join(wrappers, name)
		if err := os.WriteFile(path, []byte(body), 0700); err != nil {
			t.Fatal(err)
		}
		if err := os.Chmod(path, 0700); err != nil {
			t.Fatal(err)
		}
	}
	args := []string{
		"--bundle", bundle, "--source-commit", commit,
		"--ota-binary-sha256", fixtureSHA256(contents["ota-admin"]),
		"--license-binary-sha256", fixtureSHA256(contents["superpan-license"]),
		"--source-manifest-sha256", fixtureSHA256(contents["source-manifest.sha256"]),
		"--license-config", templates["license-config"], "--license-config-sha256", fixtureSHA256([]byte("license-config\n")),
		"--license-unit", templates["license-unit"], "--license-unit-sha256", fixtureSHA256([]byte("license-unit\n")),
		"--ota-config", templates["ota-config"], "--ota-config-sha256", fixtureSHA256([]byte("ota-config\n")),
		"--ota-unit", templates["ota-unit"], "--ota-unit-sha256", fixtureSHA256([]byte("ota-unit\n")),
		"--preflight-only",
	}
	run := func(arguments []string) ([]byte, error) {
		command := exec.Command(installerBash(t), append([]string{licenseInstaller}, arguments...)...)
		command.Env = append(os.Environ(), "PATH="+wrappers+string(os.PathListSeparator)+os.Getenv("PATH"), "MUTATION_PROBE="+probe)
		return command.CombinedOutput()
	}
	assertNoMutation := func() {
		t.Helper()
		if _, err := os.Stat(probe); !os.IsNotExist(err) {
			t.Fatalf("preflight invoked a mutation command; probe status: %v", err)
		}
	}
	output, err := run(args)
	if err != nil {
		t.Fatalf("valid executable preflight failed: %v: %s", err, output)
	}
	if !strings.Contains(string(output), "license installation preflight complete") {
		t.Fatalf("preflight success marker missing: %s", output)
	}
	assertNoMutation()
	t.Run("reject writable ancestor", func(t *testing.T) {
		if err := os.Chmod(root, 0770); err != nil {
			t.Fatal(err)
		}
		defer os.Chmod(root, 0700)
		output, err := run(args)
		if err == nil || !strings.Contains(string(output), "trusted path component must not be group/world writable") {
			t.Fatalf("writable ancestor was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject non-root ancestor", func(t *testing.T) {
		if err := os.Chown(root, 1, 1); err != nil {
			t.Fatal(err)
		}
		defer os.Chown(root, 0, 0)
		output, err := run(args)
		if err == nil || !strings.Contains(string(output), "trusted path component must be owned by root") {
			t.Fatalf("non-root ancestor was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject symlink ancestor", func(t *testing.T) {
		realParent := filepath.Join(root, "real-template-parent")
		linkParent := filepath.Join(root, "linked-template-parent")
		if err := os.Mkdir(realParent, 0700); err != nil {
			t.Fatal(err)
		}
		linkedConfig := filepath.Join(realParent, "license-config")
		if err := os.WriteFile(linkedConfig, []byte("license-config\n"), 0600); err != nil {
			t.Fatal(err)
		}
		if err := os.Symlink(realParent, linkParent); err != nil {
			t.Skipf("directory symlink unavailable: %v", err)
		}
		mutated := append([]string{}, args...)
		for index := range mutated {
			if mutated[index] == "--license-config" {
				mutated[index+1] = filepath.Join(linkParent, "license-config")
			}
		}
		output, err := run(mutated)
		if err == nil || !strings.Contains(string(output), "not be a symlink") {
			t.Fatalf("symlink ancestor was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	for _, mode := range []os.FileMode{0755, 0750, 0711, 0500} {
		t.Run(fmt.Sprintf("reject bundle mode %04o", mode), func(t *testing.T) {
			if err := os.Chmod(bundle, mode); err != nil {
				t.Fatal(err)
			}
			output, err := run(args)
			if err == nil || !strings.Contains(string(output), "mode must be exactly 0700") {
				t.Fatalf("unsafe mode was not rejected: err=%v output=%s", err, output)
			}
			assertNoMutation()
		})
	}
	if err := os.Chmod(bundle, 0700); err != nil {
		t.Fatal(err)
	}
	t.Run("reject relative bundle", func(t *testing.T) {
		mutated := append([]string{}, args...)
		mutated[1] = "relative-bundle"
		output, err := run(mutated)
		if err == nil || !strings.Contains(string(output), "absolute and canonical") {
			t.Fatalf("relative bundle was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject extra bundle file", func(t *testing.T) {
		extra := filepath.Join(bundle, "extra")
		if err := os.WriteFile(extra, []byte("extra"), 0600); err != nil {
			t.Fatal(err)
		}
		defer os.Remove(extra)
		output, err := run(args)
		if err == nil || !strings.Contains(string(output), "unexpected files") {
			t.Fatalf("extra file was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject missing bundle file", func(t *testing.T) {
		path := filepath.Join(bundle, "ota-admin.sha256")
		saved, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		if err := os.Remove(path); err != nil {
			t.Fatal(err)
		}
		defer os.WriteFile(path, saved, 0600)
		output, err := run(args)
		if err == nil || !strings.Contains(string(output), "incomplete") {
			t.Fatalf("missing file was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject malformed commit", func(t *testing.T) {
		mutated := append([]string{}, args...)
		for index := range mutated {
			if mutated[index] == "--source-commit" {
				mutated[index+1] = "ABC"
			}
		}
		output, err := run(mutated)
		if err == nil || !strings.Contains(string(output), "source commit") {
			t.Fatalf("malformed commit was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject sidecar mismatch", func(t *testing.T) {
		path := filepath.Join(bundle, "ota-admin.sha256")
		saved, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(strings.Repeat("f", 64)+"  ota-admin\n"), 0600); err != nil {
			t.Fatal(err)
		}
		defer os.WriteFile(path, saved, 0600)
		output, err := run(args)
		if err == nil || !strings.Contains(string(output), "sidecar does not match") {
			t.Fatalf("sidecar mismatch was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject external expected hash mismatch", func(t *testing.T) {
		mutated := append([]string{}, args...)
		for index := range mutated {
			if mutated[index] == "--ota-binary-sha256" {
				mutated[index+1] = strings.Repeat("f", 64)
			}
		}
		output, err := run(mutated)
		if err == nil || !strings.Contains(string(output), "sidecar does not match") {
			t.Fatalf("external hash mismatch was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject actual payload mismatch", func(t *testing.T) {
		path := filepath.Join(bundle, "ota-admin")
		saved, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, append(saved, 'x'), 0600); err != nil {
			t.Fatal(err)
		}
		defer os.WriteFile(path, saved, 0600)
		output, err := run(args)
		if err == nil || !strings.Contains(string(output), "SHA-256 mismatch") {
			t.Fatalf("actual payload mismatch was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject commit record mismatch", func(t *testing.T) {
		path := filepath.Join(bundle, "source-commit.txt")
		if err := os.WriteFile(path, []byte(strings.Repeat("b", 40)+"\n"), 0600); err != nil {
			t.Fatal(err)
		}
		defer os.WriteFile(path, []byte(commit+"\n"), 0600)
		output, err := run(args)
		if err == nil || !strings.Contains(string(output), "does not match --source-commit") {
			t.Fatalf("commit mismatch was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject writable bundle file", func(t *testing.T) {
		path := filepath.Join(bundle, "ota-admin")
		if err := os.Chmod(path, 0620); err != nil {
			t.Fatal(err)
		}
		defer os.Chmod(path, 0600)
		output, err := run(args)
		if err == nil || !strings.Contains(string(output), "group/world writable") {
			t.Fatalf("writable bundle file was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject bundle symlink", func(t *testing.T) {
		link := filepath.Join(root, "bundle-link")
		if err := os.Symlink(bundle, link); err != nil {
			t.Skipf("directory symlink unavailable: %v", err)
		}
		mutated := append([]string{}, args...)
		mutated[1] = link
		output, err := run(mutated)
		if err == nil || !strings.Contains(string(output), "absolute and canonical") {
			t.Fatalf("bundle symlink was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject payload symlink", func(t *testing.T) {
		path := filepath.Join(bundle, "ota-admin")
		target := filepath.Join(root, "ota-target")
		if err := os.WriteFile(target, contents["ota-admin"], 0600); err != nil {
			t.Fatal(err)
		}
		if err := os.Remove(path); err != nil {
			t.Fatal(err)
		}
		if err := os.Symlink(target, path); err != nil {
			_ = os.WriteFile(path, contents["ota-admin"], 0600)
			t.Skipf("file symlink unavailable: %v", err)
		}
		defer func() { _ = os.Remove(path); _ = os.WriteFile(path, contents["ota-admin"], 0600) }()
		output, err := run(args)
		if err == nil || !strings.Contains(string(output), "symlink") {
			t.Fatalf("payload symlink was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
	t.Run("reject payload hardlink", func(t *testing.T) {
		path := filepath.Join(bundle, "ota-admin")
		target := filepath.Join(root, "ota-hardlink-target")
		if err := os.WriteFile(target, contents["ota-admin"], 0600); err != nil {
			t.Fatal(err)
		}
		if err := os.Remove(path); err != nil {
			t.Fatal(err)
		}
		if err := os.Link(target, path); err != nil {
			_ = os.WriteFile(path, contents["ota-admin"], 0600)
			t.Skipf("hardlink unavailable: %v", err)
		}
		defer func() { _ = os.Remove(path); _ = os.WriteFile(path, contents["ota-admin"], 0600) }()
		output, err := run(args)
		if err == nil || !strings.Contains(string(output), "hard-linked") {
			t.Fatalf("payload hardlink was not rejected: err=%v output=%s", err, output)
		}
		assertNoMutation()
	})
}

func TestLicenseInstallLoopbackOnlyDefaultUsageRegression(t *testing.T) {
	output, err := runInstaller(t, "deploy/ota-admin/install.sh", "--help")
	if err != nil {
		t.Fatalf("default installer help failed: %v: %s", err, output)
	}
	if !strings.Contains(output, "--certificate PATH") || !strings.Contains(output, "--active-vhost-sha256 HEX") {
		t.Fatalf("default OTA install usage regressed: %s", output)
	}
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
