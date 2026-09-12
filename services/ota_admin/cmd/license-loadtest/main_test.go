package main

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/authwire"
	licensehttp "fanfh.cn/superpan/ota-admin/internal/licensing/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	"fanfh.cn/superpan/ota-admin/internal/licensing/service"
	"fanfh.cn/superpan/ota-admin/internal/licensing/signing"
	licensestore "fanfh.cn/superpan/ota-admin/internal/licensing/store"
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
	good := Manifest{Seed: approvedSeed, Card: "AAAAAAAA-AAAAAAAA-AAAAAAAA-AAAAAAAA", SoftwareID: "8270e8d7-1791-487d-ad76-0da7a0cbed12", MachineHash: strings.Repeat("a", 64), RejectionCard: "BBBBBBBB-BBBBBBBB-BBBBBBBB-BBBBBBBB", RejectionCode: "license_not_found"}
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

func TestRunPhaseUsesRealSignedRouterSemantics(t *testing.T) {
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	signer, _ := signing.New("capacity-key", key, rand.Reader)
	spki, _ := x509.MarshalPKIXPublicKey(&key.PublicKey)
	dir := t.TempDir()
	spkiPath := dir + "/signing.der"
	keyIDPath := dir + "/key-id"
	_ = os.WriteFile(spkiPath, spki, 0600)
	_ = os.WriteFile(keyIDPath, []byte("capacity-key\n"), 0600)
	manifest := Manifest{Seed: approvedSeed, Card: "AAAAAAAA-AAAAAAAA-AAAAAAAA-AAAAAAAA", SoftwareID: "8270e8d7-1791-487d-ad76-0da7a0cbed12", MachineHash: strings.Repeat("a", 64), RejectionCard: "BBBBBBBB-BBBBBBBB-BBBBBBBB-BBBBBBBB", SigningSPKIPath: spkiPath, SigningKeyIDPath: keyIDPath, RejectionCode: "license_not_found"}
	secret := bytes.Repeat([]byte{0x5a}, 32)
	svc := &semanticService{manifest: manifest}
	handler, err := licensehttp.NewRouter(licensehttp.Options{Service: svc, ReplayStore: acceptReplay{}, Signer: signer, Clock: liveClock{}, MaxBodyBytes: 4096, MaxHeaderBytes: 4096, TimestampWindow: time.Minute, NonceLifetime: time.Minute, MaxReplayNonces: 1000, ActivationRate: licensehttp.RateLimit{Requests: 1, Window: time.Minute}, ValidationRate: licensehttp.RateLimit{Requests: 1, Window: time.Minute}, MaxLimiterEntries: 10, LoadTestSecret: secret, StoreMetrics: zeroMetrics{}})
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	report, err := runPhase(context.Background(), server.URL, Workload{Rate: 100, DurationSeconds: 600}, manifest, authwire.EncodeInternalToken(secret), phaseOptions{Requests: 100, Interval: time.Millisecond, HealthInterval: time.Millisecond})
	if err != nil {
		t.Fatal(err)
	}
	if report.Requests != 100 || report.Successes != 80 || report.Rejections != 20 || report.Unexpected != 0 {
		t.Fatalf("report=%+v", report)
	}
	if report.HealthChecks == 0 || report.HealthFailures != 0 || report.BusyDelta != 0 || report.LockedDelta != 0 {
		t.Fatalf("health/metrics=%+v", report)
	}
}

type liveClock struct{}

func (liveClock) Now() time.Time { return time.Now().UTC() }

type acceptReplay struct{}

func (acceptReplay) ClaimReplayNonce(context.Context, string, time.Time, time.Time, int) (bool, error) {
	return true, nil
}

type zeroMetrics struct{}

func (zeroMetrics) Metrics() licensestore.Metrics { return licensestore.Metrics{} }

type semanticService struct{ manifest Manifest }

func (s *semanticService) Activate(_ context.Context, id, card, software, machine string) (model.ActivationState, error) {
	return stableState(id, s.manifest), nil
}
func (s *semanticService) Validate(_ context.Context, card, software, machine string) (model.ActivationState, error) {
	if card == s.manifest.RejectionCard {
		return model.ActivationState{}, service.ErrNotFound
	}
	return stableState("validation", s.manifest), nil
}
func stableState(id string, m Manifest) model.ActivationState {
	now := time.Date(2026, 9, 10, 0, 0, 0, 0, time.UTC)
	return model.ActivationState{RequestID: id, CardID: "card-id", SoftwareID: m.SoftwareID, MachineHash: m.MachineHash, PackageName: "package", ActivatedAt: now, ExpiresAt: now.Add(time.Hour), LeaseUntil: now.Add(time.Minute)}
}

func TestBusinessResponseVerifierRejectsFalsePositiveMutations(t *testing.T) {
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	signer, _ := signing.New("capacity-key", key, rand.Reader)
	now := time.Now().UTC()
	card, software, machine, pkg := "card-id", "8270e8d7-1791-487d-ad76-0da7a0cbed12", strings.Repeat("a", 64), "package"
	expires, lease := now.Add(time.Hour), now.Add(time.Minute)
	envelope, _ := signer.Sign(model.Claims{ProtocolVersion: 1, RequestID: "capacity-3", ServerTime: now, Decision: model.DecisionAllow, Code: model.CodeOK, CardID: &card, SoftwareID: &software, MachineHash: &machine, PackageName: &pkg, ExpiresAt: &expires, LeaseUntil: &lease})
	body, _ := json.Marshal(envelope)
	manifest := Manifest{SoftwareID: software, MachineHash: machine, RejectionCode: "license_not_found"}
	trust := responseTrust{"capacity-key", &key.PublicKey}
	for _, tc := range []struct {
		name                          string
		status                        int
		contentType, cache, requestID string
		body                          []byte
	}{{"valid", 200, "application/json", "no-store", "capacity-3", body}, {"conflict", 409, "application/json", "no-store", "capacity-3", body}, {"wrong request", 200, "application/json", "no-store", "other", body}, {"cacheable", 200, "application/json", "public", "capacity-3", body}, {"unsigned", 200, "application/json", "no-store", "capacity-3", []byte(`{"code":"ok"}`)}} {
		resp := &http.Response{StatusCode: tc.status, Header: http.Header{"Content-Type": []string{tc.contentType}, "Cache-Control": []string{tc.cache}}, Body: io.NopCloser(bytes.NewReader(tc.body))}
		_, err := verifyBusinessResponse(resp, tc.requestID, kindValidate, manifest, trust)
		if tc.name == "valid" && err != nil {
			t.Fatal(err)
		}
		if tc.name != "valid" && err == nil {
			t.Fatalf("mutation %s accepted", tc.name)
		}
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
