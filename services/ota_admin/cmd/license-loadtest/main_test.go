package main

import "testing"

func TestValidateOriginAcceptsOnlyLoopbackHTTP(t *testing.T) {
	for _, origin := range []string{"http://127.0.0.1:18092", "http://localhost:18092"} {
		if err := validateOrigin(origin); err != nil { t.Errorf("%s rejected: %v", origin, err) }
	}
	for _, origin := range []string{"https://ota.fanfh.cn", "http://0.0.0.0:18092", "http://127.0.0.1:1/path"} {
		if err := validateOrigin(origin); err == nil { t.Errorf("%s accepted unexpectedly", origin) }
	}
}

func TestFormatReportContainsOnlyAggregateMetrics(t *testing.T) {
	r := Report{Requests: 10, Successes: 8, Rejections: 2, Errors: 0, P95Millis: 12.5}
	s := r.String()
	if s == "" || containsAny(s, "card", "nonce", "machine", "signature", "secret") { t.Fatalf("unsafe report: %s", s) }
}

func TestWorkloadRejectsUnsafeRateAndDuration(t *testing.T) {
	for _, w := range []Workload{{Rate: 0, DurationSeconds: 60}, {Rate: 50, DurationSeconds: 0}, {Rate: 101, DurationSeconds: 600}} {
		if err := w.Validate(); err == nil { t.Errorf("workload %#v accepted", w) }
	}
	if err := (Workload{Rate: 50, DurationSeconds: 600}).Validate(); err != nil { t.Fatal(err) }
}

func TestReportThresholdsDetectFailure(t *testing.T) {
	if err := (Report{Requests: 1000, Errors: 0, P95Millis: 250}).Check(); err != nil { t.Fatal(err) }
	if err := (Report{Requests: 1000, Errors: 2, P95Millis: 10}).Check(); err == nil { t.Fatal("error threshold not enforced") }
}

func containsAny(s string, words ...string) bool { for _, w := range words { if hasSubstring(s, w) { return true } }; return false }
func hasSubstring(s, sub string) bool { for i := 0; i+len(sub) <= len(s); i++ { if s[i:i+len(sub)] == sub { return true } }; return false }
