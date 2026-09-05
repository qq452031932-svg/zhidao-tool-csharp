package audit_test

import (
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/audit"
)

const (
	opID   = "22222222-2222-4222-8222-222222222222"
	candID = "11111111-1111-4111-8111-111111111111"
)

func TestEventSpecificConstructorsRejectUnstructuredMetadata(t *testing.T) {
	t.Parallel()
	if _, err := audit.NewPublishFailed("op_1", candID, audit.CodeHashMismatch, time.Now()); err == nil {
		t.Fatal("constructor accepted a non-UUID operation ID")
	}
	if _, err := audit.NewPublishFailed(opID, candID, audit.CodeLoginRejected, time.Now()); err == nil {
		t.Fatal("publish constructor accepted a login-only code")
	}
	if _, err := audit.NewUploadCompleted(audit.OutcomeFailure, opID, candID, audit.Code("password=hunter2"), time.Now()); err == nil {
		t.Fatal("constructor accepted an arbitrary code")
	}
	if _, err := audit.NewUploadStarted("", candID, time.Now()); err == nil {
		t.Fatal("upload event accepted a missing required operation ID")
	}
	if _, err := audit.NewPublishConfirmed(opID, candID, "", time.Now()); err == nil {
		t.Fatal("publish event accepted a missing required release ID")
	}
}

func TestSourcePseudonymIsFullLengthKeyedHMAC(t *testing.T) {
	t.Parallel()
	key := []byte("0123456789abcdef0123456789abcdef")
	first, err := audit.PseudonymizeSource(key, "203.0.113.9")
	if err != nil {
		t.Fatal(err)
	}
	second, err := audit.PseudonymizeSource(key, "203.0.113.9")
	if err != nil {
		t.Fatal(err)
	}
	if first.String() != second.String() || !strings.HasPrefix(first.String(), "hmac-sha256:") || len(first.String()) != len("hmac-sha256:")+64 {
		t.Fatalf("pseudonym = %q", first.String())
	}
	if strings.Contains(first.String(), "203.0.113.9") {
		t.Fatal("pseudonym contains raw source IP")
	}
	if _, err := audit.PseudonymizeSource([]byte("short"), "203.0.113.9"); err == nil {
		t.Fatal("PseudonymizeSource() accepted a short HMAC key")
	}
}

func TestLoginConstructorRequiresPseudonymAndLoginCode(t *testing.T) {
	t.Parallel()
	if _, err := audit.NewLoginResult(audit.OutcomeFailure, audit.SourcePseudonym{}, audit.CodeLoginRejected, time.Now()); err == nil {
		t.Fatal("login audit accepted an absent pseudonym")
	}
	source, err := audit.PseudonymizeSource([]byte("0123456789abcdef0123456789abcdef"), "198.51.100.4")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := audit.NewLoginResult(audit.OutcomeFailure, source, audit.CodeHashMismatch, time.Now()); err == nil {
		t.Fatal("login audit accepted a publish code")
	}
	if _, err := audit.NewLoginResult(audit.OutcomeSuccess, source, audit.CodeLoginRejected, time.Now()); err == nil {
		t.Fatal("login audit accepted a failure code with success outcome")
	}
	if _, err := audit.NewUploadCompleted(audit.OutcomeSuccess, opID, candID, audit.CodeHashMismatch, time.Now()); err == nil {
		t.Fatal("upload audit accepted a failure code with success outcome")
	}
}
