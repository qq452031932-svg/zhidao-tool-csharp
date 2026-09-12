package main

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"regexp"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/authwire"
	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	"fanfh.cn/superpan/ota-admin/internal/licensing/signing"
)

const approvedSeed int64 = 20260910
const loadSecretHeader = "X-SuperPan-Load-Test"

var cardFixturePattern = regexp.MustCompile(`^[A-Z0-9]{8}(?:-[A-Z0-9]{8}){3}$`)
var softwareFixturePattern = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)
var machineFixturePattern = regexp.MustCompile(`^[0-9a-f]{64}$`)

type Workload struct{ Rate, DurationSeconds int }

func (w Workload) ValidateProduction() error {
	if w.DurationSeconds != 600 || (w.Rate != 50 && w.Rate != 100) {
		return errors.New("only approved phases are accepted")
	}
	return nil
}

type Manifest struct {
	Seed             int64  `json:"seed"`
	Card             string `json:"card"`
	SoftwareID       string `json:"software_id"`
	MachineHash      string `json:"machine_hash"`
	RejectionCard    string `json:"rejection_card"`
	RejectionCode    string `json:"rejection_code"`
	SigningSPKIPath  string `json:"signing_spki_path"`
	SigningKeyIDPath string `json:"signing_key_id_path"`
}

func (m Manifest) Validate() error {
	if m.Seed != approvedSeed || !cardFixturePattern.MatchString(m.Card) || !cardFixturePattern.MatchString(m.RejectionCard) || !softwareFixturePattern.MatchString(m.SoftwareID) || !machineFixturePattern.MatchString(m.MachineHash) || m.Card == m.RejectionCard || m.RejectionCode != "license_not_found" {
		return errors.New("manifest does not match fixed fixtures")
	}
	return nil
}

type requestKind uint8

const (
	kindValidate requestKind = iota
	kindActivate
	kindReject
	nonceSlotsPerPhase = 2048
)

func classify(index int64) requestKind {
	switch index % 10 {
	case 0:
		return kindActivate
	case 1, 2:
		return kindReject
	default:
		return kindValidate
	}
}

type Report struct {
	Requests, Successes, Rejections, Unexpected int64
	ExpectedRequests                            int64
	P50Millis, P95Millis                        float64
	HealthChecks, HealthFailures                int64
	BusyDelta, LockedDelta                      int64
}

func (r Report) String() string {
	return fmt.Sprintf("requests=%d success=%d rejection=%d unexpected=%d p50_ms=%.2f p95_ms=%.2f health_checks=%d health_failures=%d busy_delta=%d locked_delta=%d", r.Requests, r.Successes, r.Rejections, r.Unexpected, r.P50Millis, r.P95Millis, r.HealthChecks, r.HealthFailures, r.BusyDelta, r.LockedDelta)
}
func (r Report) Check() error {
	if r.Requests < 1 {
		return errors.New("no requests completed")
	}
	if r.ExpectedRequests > 0 && r.Requests != r.ExpectedRequests {
		return errors.New("fixed request count was not completed")
	}
	if float64(r.Unexpected)/float64(r.Requests) >= .001 {
		return errors.New("unexpected threshold failed")
	}
	if r.P95Millis > 250 {
		return errors.New("latency threshold failed")
	}
	if r.HealthChecks < 1 || r.HealthFailures != 0 {
		return errors.New("health continuity failed")
	}
	if r.BusyDelta != 0 || r.LockedDelta != 0 {
		return errors.New("database contention threshold failed")
	}
	return nil
}

type storeMetrics struct {
	Busy   int64 `json:"busy"`
	Locked int64 `json:"locked"`
}
type phaseOptions struct {
	Requests                 int64
	Interval, HealthInterval time.Duration
}
type responseTrust struct {
	keyID     string
	publicKey *ecdsa.PublicKey
}
type bindingResult struct {
	cardID, softwareID, machineHash, packageName string
	expiresAt, leaseUntil                        time.Time
}

func validateOrigin(origin string) error {
	u, err := url.Parse(origin)
	if err != nil || u.Scheme != "http" || u.Hostname() != "127.0.0.1" || u.Port() == "" || u.Path != "" || u.RawQuery != "" || u.Fragment != "" || u.User != nil {
		return errors.New("origin must be explicit literal IPv4 loopback HTTP")
	}
	return nil
}

func runPhase(ctx context.Context, origin string, workload Workload, manifest Manifest, token string, options phaseOptions) (Report, error) {
	if err := validateOrigin(origin); err != nil {
		return Report{}, err
	}
	if err := manifest.Validate(); err != nil {
		return Report{}, err
	}
	if options.Requests == 0 {
		if err := workload.ValidateProduction(); err != nil {
			return Report{}, err
		}
		options.Requests = int64(workload.Rate * workload.DurationSeconds)
	}
	if options.Interval == 0 {
		options.Interval = time.Second / time.Duration(workload.Rate)
	}
	if options.HealthInterval == 0 {
		options.HealthInterval = time.Second
	}
	client := &http.Client{Timeout: 5 * time.Second}
	trust, err := loadResponseTrust(manifest)
	if err != nil {
		return Report{}, err
	}
	before, err := readMetrics(ctx, client, origin, token)
	if err != nil {
		return Report{}, err
	}
	phaseCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	var report Report
	report.ExpectedRequests = options.Requests
	var mu sync.Mutex
	var healthWG sync.WaitGroup
	var activationBaseline *bindingResult
	healthWG.Add(1)
	go func() {
		defer healthWG.Done()
		ticker := time.NewTicker(options.HealthInterval)
		defer ticker.Stop()
		for {
			select {
			case <-phaseCtx.Done():
				return
			case <-ticker.C:
				req, _ := http.NewRequestWithContext(phaseCtx, http.MethodGet, origin+"/internal/health", nil)
				resp, e := client.Do(req)
				if errors.Is(e, context.Canceled) && phaseCtx.Err() != nil {
					return
				}
				mu.Lock()
				report.HealthChecks++
				if e != nil || resp.StatusCode != 200 {
					report.HealthFailures++
				}
				mu.Unlock()
				if resp != nil {
					_, _ = io.Copy(io.Discard, resp.Body)
					_ = resp.Body.Close()
				}
			}
		}
	}()
	latencies := make([]float64, 0, options.Requests)
	ticker := time.NewTicker(options.Interval)
	defer ticker.Stop()
	var wg sync.WaitGroup
	for index := int64(0); index < options.Requests; index++ {
		select {
		case <-ctx.Done():
			cancel()
			healthWG.Wait()
			return Report{}, ctx.Err()
		case <-ticker.C:
		}
		wg.Add(1)
		go func(index int64) {
			defer wg.Done()
			kind := classify(index)
			id := requestIdentity(index, kind)
			body, path := requestBody(manifest, workload.Rate, index, kind, time.Now().UTC())
			req, e := http.NewRequestWithContext(ctx, http.MethodPost, origin+path, bytes.NewReader(body))
			if e == nil {
				req.Header.Set("Content-Type", "application/json")
				req.Header.Set(loadSecretHeader, token)
			}
			start := time.Now()
			var claims model.Claims
			if e == nil {
				var resp *http.Response
				resp, e = client.Do(req)
				if resp != nil {
					claims, e = verifyBusinessResponse(resp, id, kind, manifest, trust)
					_ = resp.Body.Close()
				}
			}
			elapsed := float64(time.Since(start).Microseconds()) / 1000
			mu.Lock()
			defer mu.Unlock()
			report.Requests++
			latencies = append(latencies, elapsed)
			if e == nil && kind == kindActivate {
				current := bindingFromClaims(claims)
				if activationBaseline == nil {
					activationBaseline = &current
				} else if *activationBaseline != current {
					e = errors.New("idempotent activation result changed")
				}
			}
			if e != nil {
				report.Unexpected++
			} else if kind == kindReject {
				report.Rejections++
			} else {
				report.Successes++
			}
		}(index)
	}
	wg.Wait()
	cancel()
	healthWG.Wait()
	after, err := readMetrics(ctx, client, origin, token)
	if err != nil {
		return Report{}, err
	}
	report.BusyDelta = after.Busy - before.Busy
	report.LockedDelta = after.Locked - before.Locked
	sort.Float64s(latencies)
	report.P50Millis = percentile(latencies, .50)
	report.P95Millis = percentile(latencies, .95)
	return report, nil
}

func loadResponseTrust(m Manifest) (responseTrust, error) {
	spki, err := readTrustedPublicFile(m.SigningSPKIPath, 4096)
	if err != nil {
		return responseTrust{}, errors.New("signing trust unavailable")
	}
	parsed, err := x509.ParsePKIXPublicKey(spki)
	key, ok := parsed.(*ecdsa.PublicKey)
	if err != nil || !ok {
		return responseTrust{}, errors.New("invalid signing trust")
	}
	keyIDBytes, err := readTrustedPublicFile(m.SigningKeyIDPath, 129)
	keyID := strings.TrimSuffix(string(keyIDBytes), "\n")
	if err != nil || keyID == "" || strings.Contains(keyID, "\n") {
		return responseTrust{}, errors.New("invalid signing identity")
	}
	return responseTrust{keyID, key}, nil
}
func readTrustedPublicFile(path string, limit int64) ([]byte, error) {
	info, err := os.Lstat(path)
	if err != nil || !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 || (runtime.GOOS != "windows" && info.Mode().Perm()&0022 != 0) {
		return nil, errors.New("untrusted public file")
	}
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	data, err := io.ReadAll(io.LimitReader(f, limit+1))
	if err != nil || int64(len(data)) > limit {
		return nil, errors.New("public file too large")
	}
	return data, nil
}
func verifyBusinessResponse(resp *http.Response, requestID string, kind requestKind, m Manifest, trust responseTrust) (model.Claims, error) {
	if len(resp.Header.Values("Content-Type")) != 1 || resp.Header.Get("Content-Type") != "application/json" || len(resp.Header.Values("Cache-Control")) != 1 || resp.Header.Get("Cache-Control") != "no-store" {
		return model.Claims{}, errors.New("response headers invalid")
	}
	body, readErr := io.ReadAll(io.LimitReader(resp.Body, 65537))
	if readErr != nil || len(body) > 65536 {
		return model.Claims{}, errors.New("response body invalid")
	}
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.DisallowUnknownFields()
	var envelope model.SignedEnvelope
	if decoder.Decode(&envelope) != nil {
		return model.Claims{}, errors.New("invalid response envelope")
	}
	var trailing any
	if decoder.Decode(&trailing) != io.EOF {
		return model.Claims{}, errors.New("response has trailing data")
	}
	claims, err := signing.Verify(envelope, trust.keyID, trust.publicKey)
	if err != nil || claims.RequestID != requestID {
		return model.Claims{}, errors.New("response authentication failed")
	}
	if kind == kindReject {
		if resp.StatusCode != http.StatusForbidden || claims.Decision != model.DecisionDeny || string(claims.Code) != m.RejectionCode || claims.CardID != nil || claims.SoftwareID != nil || claims.MachineHash != nil || claims.PackageName != nil || claims.ExpiresAt != nil || claims.LeaseUntil != nil {
			return model.Claims{}, errors.New("unexpected rejection semantics")
		}
		return claims, nil
	}
	if resp.StatusCode != http.StatusOK || claims.Decision != model.DecisionAllow || claims.Code != model.CodeOK || claims.SoftwareID == nil || *claims.SoftwareID != m.SoftwareID || claims.MachineHash == nil || *claims.MachineHash != m.MachineHash || claims.CardID == nil || claims.PackageName == nil || claims.ExpiresAt == nil || claims.LeaseUntil == nil {
		return model.Claims{}, errors.New("unexpected authorization semantics")
	}
	return claims, nil
}
func bindingFromClaims(c model.Claims) bindingResult {
	return bindingResult{*c.CardID, *c.SoftwareID, *c.MachineHash, *c.PackageName, *c.ExpiresAt, *c.LeaseUntil}
}

func requestBody(m Manifest, rate int, index int64, kind requestKind, now time.Time) ([]byte, string) {
	card := m.Card
	path := "/license-api/v1/validate"
	id := requestIdentity(index, kind)
	field := "request_id"
	if kind == kindActivate {
		path = "/license-api/v1/activate"
		field = "activation_request_id"
	}
	if kind == kindReject {
		card = m.RejectionCard
	}
	payload := map[string]any{"protocol_version": 1, field: id, "card": card, "software_id": m.SoftwareID, "machine_hash": m.MachineHash, "timestamp": now.Format(time.RFC3339Nano), "nonce": phaseNonce(m.Seed, rate, index)}
	body, _ := json.Marshal(payload)
	return body, path
}
func requestIdentity(index int64, kind requestKind) string {
	if kind == kindActivate {
		return "capacity-idempotent"
	}
	return fmt.Sprintf("capacity-%d", index)
}
func deterministicNonce(seed, index int64) string {
	sum := sha256.Sum256([]byte(fmt.Sprintf("%d:%d", seed, index)))
	return base64.RawURLEncoding.EncodeToString(sum[:])[:32]
}

func phaseNonce(seed int64, rate int, index int64) string {
	return deterministicNonce(seed+int64(rate)*1_000_000_000, index%nonceSlotsPerPhase)
}
func percentile(values []float64, p float64) float64 {
	if len(values) == 0 {
		return 0
	}
	return values[int(float64(len(values)-1)*p)]
}
func readMetrics(ctx context.Context, client *http.Client, origin, token string) (storeMetrics, error) {
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, origin+"/internal/load-metrics", nil)
	req.Header.Set(loadSecretHeader, token)
	resp, err := client.Do(req)
	if err != nil {
		return storeMetrics{}, errors.New("load metrics unavailable")
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return storeMetrics{}, errors.New("load metrics unavailable")
	}
	var m storeMetrics
	if json.NewDecoder(io.LimitReader(resp.Body, 1024)).Decode(&m) != nil {
		return storeMetrics{}, errors.New("invalid load metrics")
	}
	return m, nil
}
func readProtectedFile(path string) ([]byte, error) {
	info, err := os.Lstat(path)
	if err != nil {
		return nil, err
	}
	if info.Mode()&os.ModeSymlink != 0 || !info.Mode().IsRegular() || info.Mode().Perm()&0077 != 0 {
		return nil, errors.New("protected input must be private regular file")
	}
	return os.ReadFile(path)
}
func readSecretFD(fd int) (string, error) {
	if fd < 3 {
		return "", errors.New("secret fd must be non-standard")
	}
	f := os.NewFile(uintptr(fd), "credential")
	if f == nil {
		return "", errors.New("invalid secret fd")
	}
	defer f.Close()
	b, err := io.ReadAll(io.LimitReader(f, 33))
	if err != nil || len(b) != 32 {
		return "", errors.New("invalid credential")
	}
	return authwire.EncodeInternalToken(b), nil
}
func runCLI(args []string) error {
	fs := flag.NewFlagSet("license-loadtest", flag.ContinueOnError)
	origin := fs.String("origin", "", "loopback origin")
	rate := fs.Int("rate", 0, "approved rate")
	duration := fs.Int("duration-seconds", 0, "approved duration")
	manifestPath := fs.String("manifest", "", "protected manifest")
	secretFD := fs.Int("load-secret-fd", -1, "protected descriptor")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if fs.NArg() != 0 {
		return errors.New("positional arguments forbidden")
	}
	w := Workload{*rate, *duration}
	if err := w.ValidateProduction(); err != nil {
		return err
	}
	data, err := readProtectedFile(*manifestPath)
	if err != nil {
		return err
	}
	var manifest Manifest
	if json.Unmarshal(data, &manifest) != nil {
		return errors.New("invalid manifest")
	}
	token, err := readSecretFD(*secretFD)
	if err != nil {
		return err
	}
	report, err := runPhase(context.Background(), *origin, w, manifest, token, phaseOptions{})
	if err != nil {
		return err
	}
	fmt.Println(report.String())
	return report.Check()
}
func main() {
	if err := runCLI(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "capacity phase failed")
		os.Exit(1)
	}
}
