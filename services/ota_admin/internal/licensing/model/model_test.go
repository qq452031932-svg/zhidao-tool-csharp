package model

import (
	"encoding/json"
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
		CardID:          "card-1",
		SoftwareID:      "software-1",
		MachineHash:     "machine-1",
		PackageName:     "package-1",
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
	claims.CardID = ""
	claims.SoftwareID = ""
	claims.MachineHash = ""
	claims.PackageName = ""

	if err := claims.Validate(); err != nil {
		t.Fatalf("Validate() error = %v, want valid denial claims", err)
	}
}

func TestAllowedClaimsRequireSuccessOnlyFields(t *testing.T) {
	claims := validClaims()
	claims.CardID = ""

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
		CardID:          "card-1",
		SoftwareID:      "software-1",
		MachineHash:     "machine-1",
		PackageName:     "package-1",
		ExpiresAt:       timePointer(now.Add(24 * time.Hour)),
		LeaseUntil:      timePointer(now.Add(30 * time.Minute)),
	}
}

func timePointer(value time.Time) *time.Time {
	return &value
}
