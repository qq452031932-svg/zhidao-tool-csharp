package deploymentcontract

import (
	"strings"
	"testing"
)

func TestLicenseInstallersAreFailClosed(t *testing.T) {
	install := read(t, "deploy", "license-service", "install.sh")
	rollback := read(t, "deploy", "license-service", "rollback.sh")
	for name, body := range map[string]string{"install.sh": install, "rollback.sh": rollback} {
		if !strings.Contains(body, "set -euo pipefail") { t.Errorf("%s must enable strict shell mode", name) }
		if !strings.Contains(body, "id -u") { t.Errorf("%s must require root", name) }
		if strings.Contains(body, "nginx -s reload") { t.Errorf("%s must not alter public nginx", name) }
	}
	if !strings.Contains(install, "127.0.0.1:18092") { t.Error("installer must pin loopback listener") }
}
