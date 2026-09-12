package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestProductionWorkloadAllowsOnlyApprovedPhases(t *testing.T) {
	for _, w := range []Workload{{50, 600}, {100, 600}} {
		if err := w.ValidateProduction(); err != nil {
			t.Fatalf("%+v: %v", w, err)
		}
	}
	for _, w := range []Workload{{10, 1}, {50, 599}, {99, 600}, {101, 600}} {
		if err := w.ValidateProduction(); err == nil {
			t.Fatalf("accepted %+v", w)
		}
	}
}

func TestOriginIsExplicitLiteralIPv4Loopback(t *testing.T) {
	if err := validateOrigin("http://127.0.0.1:18093"); err != nil {
		t.Fatal(err)
	}
	for _, value := range []string{"", "http://localhost:18093", "http://127.0.0.2:18093", "https://127.0.0.1:18093", "http://127.0.0.1:18093/x", "http://user@127.0.0.1:18093"} {
		if err := validateOrigin(value); err == nil {
			t.Fatalf("accepted %q", value)
		}
	}
}

func TestManifestRequiresFixedSeedAndFixtures(t *testing.T) {
	good := Manifest{Seed: approvedSeed, Card: "AAAAAAAA-AAAAAAAA-AAAAAAAA-AAAAAAAA", SoftwareID: "8270e8d7-1791-487d-ad76-0da7a0cbed12", MachineHash: strings.Repeat("a", 64), RejectionCard: "BBBBBBBB-BBBBBBBB-BBBBBBBB-BBBBBBBB"}
	if err := good.Validate(); err != nil {
		t.Fatal(err)
	}
	bad := good
	bad.Seed++
	if err := bad.Validate(); err == nil {
		t.Fatal("variable seed accepted")
	}
	bad = good
	bad.MachineHash = "machine"
	if err := bad.Validate(); err == nil {
		t.Fatal("non-hash machine fixture accepted")
	}
}

func TestScheduleIsExactDeterministicMix(t *testing.T) {
	counts := map[requestKind]int{}
	for i := int64(0); i < 100; i++ {
		counts[classify(i)]++
	}
	if counts[kindValidate] != 70 || counts[kindActivate] != 10 || counts[kindReject] != 20 {
		t.Fatalf("mix=%v", counts)
	}
}

func TestApprovedPhasesUseDisjointNonceSequences(t *testing.T) {
	if phaseNonce(approvedSeed, 50, 7) == phaseNonce(approvedSeed, 100, 7) {
		t.Fatal("phase nonce sequences overlap")
	}
	if phaseNonce(approvedSeed, 50, 7) != phaseNonce(approvedSeed, 50, 7) {
		t.Fatal("phase nonce is nondeterministic")
	}
}

func TestRunPhaseUsesPublicRoutesMetricsAndConcurrentHealth(t *testing.T) {
	manifest := Manifest{Seed: approvedSeed, Card: "AAAAAAAA-AAAAAAAA-AAAAAAAA-AAAAAAAA", SoftwareID: "8270e8d7-1791-487d-ad76-0da7a0cbed12", MachineHash: strings.Repeat("a", 64), RejectionCard: "BBBBBBBB-BBBBBBBB-BBBBBBBB-BBBBBBBB"}
	var mu sync.Mutex
	paths := map[string]int{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		paths[r.URL.Path]++
		mu.Unlock()
		switch r.URL.Path {
		case "/internal/load-metrics":
			if r.Header.Get(loadSecretHeader) != "test-token" {
				w.WriteHeader(401)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(`{"busy":0,"locked":0}`))
		case "/internal/health":
			w.WriteHeader(200)
		case "/license-api/v1/activate":
			w.WriteHeader(200)
		case "/license-api/v1/validate":
			var body map[string]any
			_ = json.NewDecoder(r.Body).Decode(&body)
			if body["card"] == manifest.RejectionCard {
				w.WriteHeader(403)
			} else {
				w.WriteHeader(200)
			}
		default:
			w.WriteHeader(404)
		}
	}))
	defer server.Close()
	report, err := runPhase(context.Background(), server.URL, Workload{Rate: 100, DurationSeconds: 600}, manifest, "test-token", phaseOptions{Requests: 100, Interval: time.Millisecond, HealthInterval: time.Millisecond})
	if err != nil {
		t.Fatal(err)
	}
	if report.Requests != 100 || report.Successes != 80 || report.Rejections != 20 || report.Unexpected != 0 {
		t.Fatalf("report=%+v", report)
	}
	if report.HealthChecks == 0 || report.HealthFailures != 0 || report.BusyDelta != 0 || report.LockedDelta != 0 {
		t.Fatalf("health/metrics=%+v", report)
	}
	mu.Lock()
	defer mu.Unlock()
	if paths["/license-api/v1/activate"] != 10 || paths["/license-api/v1/validate"] != 90 || paths["/internal/load-metrics"] != 2 || paths["/internal/health"] == 0 {
		t.Fatalf("paths=%v", paths)
	}
}

func TestReportIsAggregateOnlyAndEnforcesThresholds(t *testing.T) {
	report := Report{Requests: 1000, Successes: 800, Rejections: 200, P50Millis: 2, P95Millis: 250, HealthChecks: 1}
	output := report.String()
	for _, forbidden := range []string{"card", "machine", "nonce", "payload", "signature", "token", "secret", "key"} {
		if strings.Contains(strings.ToLower(output), forbidden) {
			t.Fatalf("unsafe report: %s", output)
		}
	}
	if err := report.Check(); err != nil {
		t.Fatal(err)
	}
	report.Unexpected = 1
	if err := report.Check(); err == nil {
		t.Fatal("0.1 percent unexpected accepted")
	}
}
