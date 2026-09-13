package deploymentcontract

import (
	"strings"
	"testing"
)

func TestCapacitySeparatesWritableStateFromRootControlledConfiguration(t *testing.T) {
	unit := read(t, "deploy", "license-service", "superpan-license-loadtest.service")
	if !strings.Contains(unit, "--config /etc/superpan-license-capacity-18093/config.json") {
		t.Fatal("capacity service configuration is still below writable state")
	}

	body := read(t, "deploy", "license-service", "run-capacity-test.sh")
	requireFragments(t, body,
		"ROOT=/var/lib/license-capacity-18093",
		"CONFIG_ROOT=/etc/superpan-license-capacity-18093",
		"CONFIG=$CONFIG_ROOT/config.json",
		"SECRET_FILE=$CONFIG_ROOT/load-test.secret",
		"$CONFIG_ROOT/response-signing.spki.der",
		"$CONFIG_ROOT/response-signing.key-id",
		"root:superpan-license:640",
		"root:superpan-license-admin:640",
	)
	if strings.Contains(body, "$ROOT/config.json") || strings.Contains(body, "$ROOT/encryption.key") {
		t.Fatal("root-controlled configuration is still placed below writable capacity state")
	}
}
