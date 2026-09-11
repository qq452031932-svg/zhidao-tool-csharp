package deploymentcontract

import (
 "strings"
 "testing"
)

func TestCapacityArtifactsAreLoopbackAndRedacted(t *testing.T) {
 load := read(t, "deploy", "license-service", "run-capacity-test.sh")
 unit := read(t, "deploy", "license-service", "superpan-license-loadtest.service")
 for _, want := range []string{"127.0.0.1:18093", "license-loadtest", "license-dbcheck"} { if !strings.Contains(load+unit,want) { t.Errorf("missing %q",want) } }
 if strings.Contains(load, "card=\") || strings.Contains(load, "secret") { t.Error("capacity script may leak sensitive values") }
}
