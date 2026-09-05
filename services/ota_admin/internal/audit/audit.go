package audit

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"regexp"
	"time"
)

type EventType string
type Outcome string
type Code string

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

	CodeAccepted          Code = "accepted"
	CodeCompleted         Code = "completed"
	CodeLoginAccepted     Code = "login_accepted"
	CodeLoginRejected     Code = "login_rejected"
	CodeLoginLocked       Code = "login_locked"
	CodeHashMismatch      Code = "hash_mismatch"
	CodeSignatureMismatch Code = "signature_mismatch"
	CodeVersionFloor      Code = "version_floor"
	CodeExpired           Code = "expired"
	CodeInternalFailure   Code = "internal_failure"
)

type SourcePseudonym struct{ value string }

func (source SourcePseudonym) String() string { return source.value }

func PseudonymizeSource(key []byte, rawSource string) (SourcePseudonym, error) {
	if len(key) < 32 {
		return SourcePseudonym{}, errors.New("source HMAC key must contain at least 32 bytes")
	}
	if rawSource == "" {
		return SourcePseudonym{}, errors.New("raw source is required")
	}
	mac := hmac.New(sha256.New, key)
	_, _ = mac.Write([]byte(rawSource))
	return SourcePseudonym{value: "hmac-sha256:" + hex.EncodeToString(mac.Sum(nil))}, nil
}

type Event struct {
	eventType       EventType
	outcome         Outcome
	operationID     string
	candidateID     string
	releaseID       string
	code            Code
	sourcePseudonym SourcePseudonym
	at              time.Time
}

func (event Event) Type() EventType                  { return event.eventType }
func (event Event) Outcome() Outcome                 { return event.outcome }
func (event Event) OperationID() string              { return event.operationID }
func (event Event) CandidateID() string              { return event.candidateID }
func (event Event) ReleaseID() string                { return event.releaseID }
func (event Event) Code() Code                       { return event.code }
func (event Event) SourcePseudonym() SourcePseudonym { return event.sourcePseudonym }
func (event Event) At() time.Time                    { return event.at }

func NewLoginResult(outcome Outcome, source SourcePseudonym, code Code, at time.Time) (Event, error) {
	if source.value == "" || !sourcePattern.MatchString(source.value) {
		return Event{}, errors.New("login audit requires a keyed source pseudonym")
	}
	if (code == CodeLoginAccepted) != (outcome == OutcomeSuccess) {
		return Event{}, errors.New("login audit outcome does not match result code")
	}
	return newEvent(LoginResult, outcome, "", "", "", code, source, at, CodeLoginAccepted, CodeLoginRejected, CodeLoginLocked)
}

func NewUploadStarted(operationID, candidateID string, at time.Time) (Event, error) {
	return newEvent(UploadStarted, OutcomeSuccess, operationID, candidateID, "", CodeAccepted, SourcePseudonym{}, at, CodeAccepted)
}

func NewUploadCompleted(outcome Outcome, operationID, candidateID string, code Code, at time.Time) (Event, error) {
	if (code == CodeCompleted) != (outcome == OutcomeSuccess) {
		return Event{}, errors.New("upload audit outcome does not match result code")
	}
	return newEvent(UploadCompleted, outcome, operationID, candidateID, "", code, SourcePseudonym{}, at, CodeCompleted, CodeHashMismatch, CodeExpired, CodeInternalFailure)
}

func NewGateResult(outcome Outcome, operationID, candidateID string, code Code, at time.Time) (Event, error) {
	if (code == CodeAccepted) != (outcome == OutcomeSuccess) {
		return Event{}, errors.New("gate audit outcome does not match result code")
	}
	return newEvent(GateResult, outcome, operationID, candidateID, "", code, SourcePseudonym{}, at, CodeAccepted, CodeHashMismatch, CodeSignatureMismatch, CodeVersionFloor, CodeInternalFailure)
}

func NewPublishConfirmed(operationID, candidateID, releaseID string, at time.Time) (Event, error) {
	return newEvent(PublishConfirmed, OutcomeSuccess, operationID, candidateID, releaseID, CodeCompleted, SourcePseudonym{}, at, CodeCompleted)
}

func NewPublishFailed(operationID, candidateID string, code Code, at time.Time) (Event, error) {
	return newEvent(PublishFailed, OutcomeFailure, operationID, candidateID, "", code, SourcePseudonym{}, at, CodeHashMismatch, CodeSignatureMismatch, CodeVersionFloor, CodeInternalFailure)
}

func NewRollbackCompleted(operationID, releaseID string, at time.Time) (Event, error) {
	return newEvent(RollbackCompleted, OutcomeSuccess, operationID, "", releaseID, CodeCompleted, SourcePseudonym{}, at, CodeCompleted)
}

func NewCleanupResult(outcome Outcome, operationID, releaseID string, code Code, at time.Time) (Event, error) {
	if (code == CodeCompleted) != (outcome == OutcomeSuccess) {
		return Event{}, errors.New("cleanup audit outcome does not match result code")
	}
	return newEvent(CleanupResult, outcome, operationID, "", releaseID, code, SourcePseudonym{}, at, CodeCompleted, CodeInternalFailure)
}

var (
	uuidPattern   = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)
	sourcePattern = regexp.MustCompile(`^hmac-sha256:[0-9a-f]{64}$`)
)

func newEvent(eventType EventType, outcome Outcome, operationID, candidateID, releaseID string, code Code, source SourcePseudonym, at time.Time, allowedCodes ...Code) (Event, error) {
	if outcome != OutcomeSuccess && outcome != OutcomeFailure {
		return Event{}, errors.New("invalid audit outcome")
	}
	for _, value := range []string{operationID, candidateID, releaseID} {
		if value != "" && !uuidPattern.MatchString(value) {
			return Event{}, errors.New("audit identifiers must be canonical UUIDs")
		}
	}
	allowed := false
	for _, candidate := range allowedCodes {
		allowed = allowed || candidate == code
	}
	if !allowed {
		return Event{}, errors.New("audit code is not valid for this event")
	}
	if at.IsZero() {
		at = time.Now().UTC()
	}
	return Event{eventType: eventType, outcome: outcome, operationID: operationID, candidateID: candidateID, releaseID: releaseID, code: code, sourcePseudonym: source, at: at.UTC()}, nil
}
