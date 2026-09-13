package deploymentcontract

import (
	"strings"
	"testing"
)

func TestCapacityActualGroupSetDeduplicatesCentOSV219Output(t *testing.T) {
	body := read(t, "deploy", "license-service", "run-capacity-test.sh")
	actualAssignment := ""
	for _, line := range strings.Split(body, "\n") {
		if strings.HasPrefix(line, "ACTUAL_GROUPS=") {
			actualAssignment = strings.SplitN(line, "; EXPECTED_GROUPS=", 2)[0]
			break
		}
	}
	if !strings.Contains(actualAssignment, "sort -n -u") {
		t.Fatalf("actual runtime groups are not deduplicated: %q", actualAssignment)
	}
}
