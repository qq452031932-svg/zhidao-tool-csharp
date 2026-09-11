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

func containsAny(s string, words ...string) bool { for _, w := range words { if hasSubstring(s, w) { return true } }; return false }
func hasSubstring(s, sub string) bool { for i := 0; i+len(sub) <= len(s); i++ { if s[i:i+len(sub)] == sub { return true } }; return false }
