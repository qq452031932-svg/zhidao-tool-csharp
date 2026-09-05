package audit

import (
	"errors"
	"regexp"
	"strings"
	"time"
)

type EventType string
type Outcome string

const (
	LoginResult       EventType = "login_result"
	UploadStarted     EventType = "upload_started"
	UploadCompleted   EventType = "upload_completed"
	GateResult        EventType = "gate_result"
	PublishConfirmed  EventType = "publish_confirmed"
	PublishFailed     EventType = "publish_failed"
	RollbackCompleted EventType = "rollback_completed"
	CleanupResult     EventType = "cleanup_result"

	OutcomeSuccess Outcome = "success"
	OutcomeFailure Outcome = "failure"
)

type Event struct {
	Type        EventType
	Outcome     Outcome
	OperationID string
	CandidateID string
	ReleaseID   string
	Code        string
	SourceHash  string
	At          time.Time
}

var (
	idPattern         = regexp.MustCompile(`^[A-Za-z0-9_.:-]{0,128}$`)
	codePattern       = regexp.MustCompile(`^[a-z0-9_:-]{0,64}$`)
	sourceHashPattern = regexp.MustCompile(`^sha256:[0-9a-f]{16,64}$`)
)

func (event Event) Validate() error {
	if !knownType(event.Type) {
		return errors.New("unknown audit event type")
	}
	if event.Outcome != OutcomeSuccess && event.Outcome != OutcomeFailure {
		return errors.New("unknown audit outcome")
	}
	for _, value := range []string{event.OperationID, event.CandidateID, event.ReleaseID} {
		if !idPattern.MatchString(value) {
			return errors.New("audit identifier is not safe metadata")
		}
	}
	if !codePattern.MatchString(event.Code) {
		return errors.New("audit code is not safe metadata")
	}
	if event.SourceHash != "" && !sourceHashPattern.MatchString(event.SourceHash) {
		return errors.New("audit source must be a one-way hash")
	}
	for _, value := range []string{event.OperationID, event.CandidateID, event.ReleaseID, event.Code, event.SourceHash} {
		lower := strings.ToLower(value)
		for _, forbidden := range []string{"password", "cookie", "session=", "package contents", "private key"} {
			if strings.Contains(lower, forbidden) {
				return errors.New("audit metadata contains a forbidden value")
			}
		}
	}
	return nil
}

func knownType(eventType EventType) bool {
	switch eventType {
	case LoginResult, UploadStarted, UploadCompleted, GateResult, PublishConfirmed, PublishFailed, RollbackCompleted, CleanupResult:
		return true
	default:
		return false
	}
}
