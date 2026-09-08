package model

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
	"time"
)

func TestProtocolConstantsAreStable(t *testing.T) {
	if ProtocolVersion != 1 {
		t.Fatalf("ProtocolVersion = %d, want 1", ProtocolVersion)
	}

	wantCodes := []Code{
		CodeOK,
		CodeInvalidRequest,
		CodeUnsupportedProtocol,
		CodeLicenseNotFound,
		CodeLicenseExpired,
		CodeAppNotAllowed,
		CodeAppDisabled,
		CodeMachineMismatch,
		CodeRateLimited,
		CodeServiceUnavailable,
	}
	wantValues := []string{
		"ok",
		"invalid_request",
		"unsupported_protocol",
		"license_not_found",
		"license_expired",
		"app_not_allowed",
		"app_disabled",
		"machine_mismatch",
		"rate_limited",
		"service_unavailable",
	}

	for i, code := range wantCodes {
		if string(code) != wantValues[i] {
			t.Errorf("code %d = %q, want %q", i, code, wantValues[i])
		}
	}
}

func TestSignedEnvelopeUsesStableJSONFieldNames(t *testing.T) {
	encoded, err := json.Marshal(SignedEnvelope{
		KeyID:     "license-signing-local-1",
		Payload:   "cGF5bG9hZA==",
		Signature: "c2lnbmF0dXJl",
	})
	if err != nil {
		t.Fatal(err)
	}

	if got, want := string(encoded), `{"key_id":"license-signing-local-1","payload":"cGF5bG9hZA==","signature":"c2lnbmF0dXJl"}`; got != want {
		t.Fatalf("encoded envelope = %s, want %s", got, want)
	}
}

func TestClaimsUseStableJSONFieldNames(t *testing.T) {
	now := time.Date(2026, 9, 8, 12, 0, 0, 0, time.UTC)
	claims := Claims{
		ProtocolVersion: ProtocolVersion,
		RequestID:       "request-1",
		ServerTime:      now,
		Decision:        DecisionAllow,
		Code:            CodeOK,
		CardID:          stringPointer("card-1"),
		SoftwareID:      stringPointer("software-1"),
		MachineHash:     stringPointer("machine-1"),
		PackageName:     stringPointer("package-1"),
		ExpiresAt:       timePointer(now.Add(24 * time.Hour)),
		LeaseUntil:      timePointer(now.Add(30 * time.Minute)),
	}

	encoded, err := json.Marshal(claims)
	if err != nil {
		t.Fatal(err)
	}

	var fields map[string]json.RawMessage
	if err := json.Unmarshal(encoded, &fields); err != nil {
		t.Fatal(err)
	}
	want := []string{
		"protocol_version", "request_id", "server_time", "decision", "code",
		"card_id", "software_id", "machine_hash", "package_name", "expires_at", "lease_until",
	}
	if len(fields) != len(want) {
		t.Fatalf("field count = %d, want %d: %s", len(fields), len(want), encoded)
	}
	for _, name := range want {
		if _, ok := fields[name]; !ok {
			t.Errorf("missing JSON field %q in %s", name, encoded)
		}
	}
}

func TestClaimsRejectUnknownDecisionCodePairs(t *testing.T) {
	tests := []struct {
		name     string
		decision Decision
		code     Code
	}{
		{name: "unknown decision", decision: "maybe", code: CodeOK},
		{name: "unknown code", decision: DecisionDeny, code: "other"},
		{name: "allow with error", decision: DecisionAllow, code: CodeLicenseExpired},
		{name: "deny with ok", decision: DecisionDeny, code: CodeOK},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			claims := validClaims()
			claims.Decision = tt.decision
			claims.Code = tt.code
			if err := claims.Validate(); err == nil {
				t.Fatal("Validate() error = nil, want rejection")
			}
		})
	}
}

func TestDenialClaimsDoNotRequireSuccessOnlyIdentifiers(t *testing.T) {
	claims := validClaims()
	claims.Decision = DecisionDeny
	claims.Code = CodeLicenseNotFound
	claims.CardID = nil
	claims.SoftwareID = nil
	claims.MachineHash = nil
	claims.PackageName = nil

	if err := claims.Validate(); err != nil {
		t.Fatalf("Validate() error = %v, want valid denial claims", err)
	}
}

func TestAllowedClaimsRequireSuccessOnlyFields(t *testing.T) {
	claims := validClaims()
	claims.CardID = nil

	if err := claims.Validate(); err == nil {
		t.Fatal("Validate() error = nil, want missing success field rejection")
	}
}

func TestAllowedClaimsRequireRequestID(t *testing.T) {
	claims := validClaims()
	claims.RequestID = ""

	if err := claims.Validate(); err == nil {
		t.Fatal("Validate() error = nil, want missing request_id rejection")
	}
}

func TestDenialClaimsRequireRequestID(t *testing.T) {
	claims := validClaims()
	claims.Decision = DecisionDeny
	claims.Code = CodeLicenseNotFound
	claims.RequestID = ""

	if err := claims.Validate(); err == nil {
		t.Fatal("Validate() error = nil, want missing request_id rejection")
	}
}

func TestDeniedClaimsMarshalCanonicalNullAbsence(t *testing.T) {
	claims := Claims{
		ProtocolVersion: ProtocolVersion,
		RequestID:       "request-denied-1",
		ServerTime:      time.Date(2026, 9, 8, 12, 0, 0, 0, time.UTC),
		Decision:        DecisionDeny,
		Code:            CodeLicenseNotFound,
	}

	encoded, err := json.Marshal(claims)
	if err != nil {
		t.Fatal(err)
	}
	want, err := os.ReadFile("testdata/denial_claims_v1.json")
	if err != nil {
		t.Fatal(err)
	}
	if got := string(encoded); got != strings.TrimSpace(string(want)) {
		t.Fatalf("encoded denial claims = %s, want %s", got, want)
	}
}

func TestDenialClaimsRejectEmptySuccessOnlyIdentifiers(t *testing.T) {
	data := []byte(`{"protocol_version":1,"request_id":"request-denied-1","server_time":"2026-09-08T12:00:00Z","decision":"deny","code":"license_not_found","card_id":"","software_id":null,"machine_hash":null,"package_name":null,"expires_at":null,"lease_until":null}`)
	var claims Claims
	if err := json.Unmarshal(data, &claims); err != nil {
		t.Fatal(err)
	}

	if err := claims.Validate(); err == nil {
		t.Fatal("Validate() error = nil, want empty success-only identifier rejection")
	}
}

func TestClaimsMeasureIdentifierLimitInUTF8Bytes(t *testing.T) {
	claims := validClaims()
	claims.RequestID = strings.Repeat("é", MaxIdentifierLength/2)
	if err := claims.Validate(); err != nil {
		t.Fatalf("Validate() error = %v for identifier at byte limit", err)
	}

	claims.RequestID += "a"
	if err := claims.Validate(); err == nil {
		t.Fatal("Validate() error = nil, want identifier over UTF-8 byte limit rejection")
	}
}

func TestClaimsRejectOverlongIdentifiers(t *testing.T) {
	claims := validClaims()
	claims.RequestID = strings.Repeat("r", MaxIdentifierLength+1)

	if err := claims.Validate(); err == nil {
		t.Fatal("Validate() error = nil, want overlong identifier rejection")
	}
}

func TestSignedEnvelopeRejectsOverlongKeyID(t *testing.T) {
	envelope := SignedEnvelope{
		KeyID:     strings.Repeat("k", MaxIdentifierLength+1),
		Payload:   "cGF5bG9hZA==",
		Signature: "c2lnbmF0dXJl",
	}

	if err := envelope.Validate(); err == nil {
		t.Fatal("Validate() error = nil, want overlong key identifier rejection")
	}
}

func validClaims() Claims {
	now := time.Date(2026, 9, 8, 12, 0, 0, 0, time.UTC)
	return Claims{
		ProtocolVersion: ProtocolVersion,
		RequestID:       "request-1",
		ServerTime:      now,
		Decision:        DecisionAllow,
		Code:            CodeOK,
		CardID:          stringPointer("card-1"),
		SoftwareID:      stringPointer("software-1"),
		MachineHash:     stringPointer("machine-1"),
		PackageName:     stringPointer("package-1"),
		ExpiresAt:       timePointer(now.Add(24 * time.Hour)),
		LeaseUntil:      timePointer(now.Add(30 * time.Minute)),
	}
}

func timePointer(value time.Time) *time.Time {
	return &value
}

func stringPointer(value string) *string {
	return &value
}
