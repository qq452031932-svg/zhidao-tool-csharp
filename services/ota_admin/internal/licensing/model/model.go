package model

import (
	"errors"
	"fmt"
	"strings"
	"time"
	"unicode/utf8"
)

const (
	ProtocolVersion         = 1
	MaxIdentifierLength     = 128
	MaxPackageNameUTF8Bytes = MaxIdentifierLength
	MaxAdminNameUTF8Bytes   = MaxIdentifierLength
)

func ValidateAdminName(value string) error {
	if value == "" || strings.TrimSpace(value) != value || !utf8.ValidString(value) || len(value) > MaxAdminNameUTF8Bytes {
		return fmt.Errorf("name must be valid UTF-8 within %d bytes", MaxAdminNameUTF8Bytes)
	}
	return nil
}

func ValidatePackageName(value string) error {
	if value == "" || strings.TrimSpace(value) != value || !utf8.ValidString(value) || len(value) > MaxPackageNameUTF8Bytes {
		return fmt.Errorf("package name must be valid UTF-8 within %d bytes", MaxPackageNameUTF8Bytes)
	}
	return nil
}

type Decision string

const (
	DecisionAllow Decision = "allow"
	DecisionDeny  Decision = "deny"
)

type Code string

const (
	CodeOK                  Code = "ok"
	CodeInvalidRequest      Code = "invalid_request"
	CodeUnsupportedProtocol Code = "unsupported_protocol"
	CodeLicenseNotFound     Code = "license_not_found"
	CodeLicenseExpired      Code = "license_expired"
	CodeAppNotAllowed       Code = "app_not_allowed"
	CodeAppDisabled         Code = "app_disabled"
	CodeMachineMismatch     Code = "machine_mismatch"
	CodeRateLimited         Code = "rate_limited"
	CodeServiceUnavailable  Code = "service_unavailable"
)

type SignedEnvelope struct {
	KeyID     string `json:"key_id"`
	Payload   string `json:"payload"`
	Signature string `json:"signature"`
}

func (e SignedEnvelope) Validate() error {
	if err := validateIdentifier("key_id", e.KeyID); err != nil {
		return err
	}
	if e.Payload == "" {
		return errors.New("payload is required")
	}
	if e.Signature == "" {
		return errors.New("signature is required")
	}
	return nil
}

type Claims struct {
	ProtocolVersion int        `json:"protocol_version"`
	RequestID       string     `json:"request_id"`
	ServerTime      time.Time  `json:"server_time"`
	Decision        Decision   `json:"decision"`
	Code            Code       `json:"code"`
	CardID          *string    `json:"card_id"`
	SoftwareID      *string    `json:"software_id"`
	MachineHash     *string    `json:"machine_hash"`
	PackageName     *string    `json:"package_name"`
	ExpiresAt       *time.Time `json:"expires_at"`
	LeaseUntil      *time.Time `json:"lease_until"`
}

// Software is an independently licensed product. Its ID never changes; Enabled
// is used instead of deleting a product that may be referenced by licenses.
type Software struct {
	ID           string    `json:"id"`
	Name         string    `json:"name"`
	Enabled      bool      `json:"enabled"`
	PackageCount int       `json:"package_count"`
	CreatedAt    time.Time `json:"created_at"`
}

// Package groups the products authorized by a card. Membership is deliberately
// represented as IDs so the service can evaluate the current package contents.
type Package struct {
	ID          string    `json:"id"`
	Name        string    `json:"name"`
	SoftwareIDs []string  `json:"software_ids"`
	CreatedAt   time.Time `json:"created_at"`
}

type AdminIdempotencyRecord struct {
	Fingerprint        string
	Status             int
	Body               []byte
	ReplayKeyID        string
	CardIDs            []string
	ContentType        string
	ContentDisposition string
	ExpiresAt          time.Time
}

type AdminIdempotencyOutcome string

const (
	AdminIdempotencyNew      AdminIdempotencyOutcome = "new"
	AdminIdempotencyReplay   AdminIdempotencyOutcome = "replay"
	AdminIdempotencyConflict AdminIdempotencyOutcome = "conflict"
	AdminIdempotencyPending  AdminIdempotencyOutcome = "pending"
)

type GenerateCardsRequest struct {
	PackageID string    `json:"package_id"`
	Count     int       `json:"count"`
	BaseDays  int       `json:"base_days"`
	Note      *string   `json:"note"`
	CreatedAt time.Time `json:"created_at"`
}

// SealedCard contains the at-rest representation prepared by the service. The
// store never receives or indexes the plaintext full card value.
type SealedCard struct {
	ID              string `json:"id"`
	Ciphertext      []byte `json:"ciphertext"`
	EncryptionKeyID string `json:"-"`
	FullDigest      string `json:"full_digest"`
	Prefix8         string `json:"prefix8"`
	Last4           string `json:"last4"`
	// Note is an optional per-card override. Nil preserves the batch note.
	Note *string `json:"-"`
}

type Card struct {
	ID                   string     `json:"id"`
	PackageID            string     `json:"package_id"`
	FullDigest           string     `json:"-"`
	Prefix8              string     `json:"prefix8"`
	Last4                string     `json:"last4"`
	BaseDays             int        `json:"base_days"`
	UnactivatedTotalDays int        `json:"unactivated_total_days"`
	Note                 *string    `json:"note"`
	ActivatedAt          *time.Time `json:"activated_at"`
	ExpiresAt            *time.Time `json:"expires_at"`
	MachineHash          *string    `json:"machine_hash"`
	CreatedAt            time.Time  `json:"created_at"`
}

type CardFilter struct {
	FullDigest   string `json:"full_digest"`
	Prefix8      string `json:"prefix8"`
	Last4        string `json:"last4"`
	NoteContains string `json:"note_contains"`
	PackageID    string `json:"package_id"`
}

type CardState string

const (
	CardStateUnactivated CardState = "unactivated"
	CardStateEffective   CardState = "effective"
	CardStateExpired     CardState = "expired"
)

type AdminCardFilter struct {
	FullDigest   string
	Prefix8      string
	Last4        string
	NoteContains string
	PackageID    string
	CardType     string
	State        CardState
}

const (
	CardTypeMonth    = "month"
	CardTypeQuarter  = "quarter"
	CardTypeHalfYear = "half_year"
	CardTypeYear     = "year"
)

func DurationDaysForCardType(cardType string) (int, bool) {
	switch cardType {
	case CardTypeMonth:
		return 30, true
	case CardTypeQuarter:
		return 90, true
	case CardTypeHalfYear:
		return 180, true
	case CardTypeYear:
		return 365, true
	default:
		return 0, false
	}
}

func CardTypeForDurationDays(days int) (string, bool) {
	switch days {
	case 30:
		return CardTypeMonth, true
	case 90:
		return CardTypeQuarter, true
	case 180:
		return CardTypeHalfYear, true
	case 365:
		return CardTypeYear, true
	default:
		return "", false
	}
}

type AdminCardRecord struct {
	ID                   string     `json:"id"`
	PackageID            string     `json:"package_id"`
	PackageName          string     `json:"package_name"`
	Ciphertext           []byte     `json:"-"`
	EncryptionKeyID      string     `json:"-"`
	FullDigest           string     `json:"-"`
	Prefix8              string     `json:"prefix8"`
	Last4                string     `json:"last4"`
	BaseDays             int        `json:"base_days"`
	UnactivatedTotalDays int        `json:"unactivated_total_days"`
	Note                 *string    `json:"note"`
	ActivatedAt          *time.Time `json:"activated_at"`
	ExpiresAt            *time.Time `json:"expires_at"`
	MachineHash          *string    `json:"-"`
	CreatedAt            time.Time  `json:"created_at"`
}

type AdminCardSummary struct {
	ID             string     `json:"id"`
	PackageID      string     `json:"package_id"`
	PackageName    string     `json:"package_name"`
	Prefix8        string     `json:"prefix8"`
	Last4          string     `json:"last4"`
	CardType       string     `json:"card_type"`
	DurationDays   int        `json:"duration_days"`
	State          CardState  `json:"state"`
	ActivatedAt    *time.Time `json:"activated_at"`
	ExpiresAt      *time.Time `json:"expires_at"`
	BindingShortID *string    `json:"binding_short_id"`
	Note           *string    `json:"note"`
	CreatedAt      time.Time  `json:"created_at"`
}

type AdminExportCard struct {
	Summary   AdminCardSummary `json:"card"`
	Plaintext string           `json:"plaintext"`
}

type DeviceRecord struct {
	CardID      string    `json:"card_id"`
	MachineHash string    `json:"-"`
	BoundAt     time.Time `json:"bound_at"`
	LastSeenAt  time.Time `json:"last_seen_at"`
	ExpiresAt   time.Time `json:"expires_at"`
}

type DeviceSummary struct {
	ID                       string    `json:"id"`
	CardShortID              string    `json:"card_short_id"`
	MachineShortID           string    `json:"machine_short_id"`
	BoundAt                  time.Time `json:"bound_at"`
	LastSuccessfulValidation time.Time `json:"last_successful_validation"`
	ExpiresAt                time.Time `json:"expires_at"`
}

// CardSummary intentionally omits ciphertext and the full-card digest.
type CardSummary struct {
	ID                   string     `json:"id"`
	PackageID            string     `json:"package_id"`
	Prefix8              string     `json:"prefix8"`
	Last4                string     `json:"last4"`
	BaseDays             int        `json:"base_days"`
	UnactivatedTotalDays int        `json:"unactivated_total_days"`
	Note                 *string    `json:"note"`
	ActivatedAt          *time.Time `json:"activated_at"`
	ExpiresAt            *time.Time `json:"expires_at"`
	MachineHash          *string    `json:"machine_hash"`
	CreatedAt            time.Time  `json:"created_at"`
}

type ActivationMutation struct {
	RequestID   string `json:"request_id"`
	CardID      string `json:"card_id"`
	SoftwareID  string `json:"software_id"`
	MachineHash string `json:"machine_hash"`
	// ObservedAt is the current server time for this request. ActivatedAt and
	// ExpiresAt are retained for wire compatibility; the store derives the
	// authoritative lifetime from persisted card state instead of trusting them.
	ObservedAt  time.Time `json:"observed_at"`
	ActivatedAt time.Time `json:"activated_at"`
	ExpiresAt   time.Time `json:"expires_at"`
	LeaseUntil  time.Time `json:"lease_until"`
}

type ActivationState struct {
	RequestID   string    `json:"request_id"`
	CardID      string    `json:"card_id"`
	SoftwareID  string    `json:"software_id"`
	MachineHash string    `json:"machine_hash"`
	PackageName string    `json:"package_name"`
	ActivatedAt time.Time `json:"activated_at"`
	ExpiresAt   time.Time `json:"expires_at"`
	LeaseUntil  time.Time `json:"lease_until"`
}

type Lease struct {
	CardID      string    `json:"card_id"`
	SoftwareID  string    `json:"software_id"`
	MachineHash string    `json:"machine_hash"`
	LastSeenAt  time.Time `json:"last_seen_at"`
	LeaseUntil  time.Time `json:"lease_until"`
}

func (c Claims) Validate() error {
	if c.ProtocolVersion != ProtocolVersion {
		return fmt.Errorf("protocol_version must be %d", ProtocolVersion)
	}
	if err := validateIdentifier("request_id", c.RequestID); err != nil {
		return err
	}
	optionalIdentifiers := []struct {
		name  string
		value *string
	}{
		{name: "card_id", value: c.CardID},
		{name: "software_id", value: c.SoftwareID},
		{name: "machine_hash", value: c.MachineHash},
		{name: "package_name", value: c.PackageName},
	}
	for _, identifier := range optionalIdentifiers {
		if err := validateOptionalIdentifier(identifier.name, identifier.value); err != nil {
			return err
		}
	}

	if c.Decision == DecisionAllow && c.Code == CodeOK {
		for _, identifier := range optionalIdentifiers {
			if err := validateRequiredIdentifierPointer(identifier.name, identifier.value); err != nil {
				return err
			}
		}
		if c.ExpiresAt == nil {
			return errors.New("expires_at is required for allowed claims")
		}
		if c.LeaseUntil == nil {
			return errors.New("lease_until is required for allowed claims")
		}
		return nil
	}
	if c.Decision != DecisionDeny {
		return fmt.Errorf("unknown decision/code pair %q/%q", c.Decision, c.Code)
	}
	switch c.Code {
	case CodeInvalidRequest,
		CodeUnsupportedProtocol,
		CodeLicenseNotFound,
		CodeLicenseExpired,
		CodeAppNotAllowed,
		CodeAppDisabled,
		CodeMachineMismatch,
		CodeRateLimited,
		CodeServiceUnavailable:
		return nil
	default:
		return fmt.Errorf("unknown decision/code pair %q/%q", c.Decision, c.Code)
	}
}

func validateIdentifier(name, value string) error {
	if value == "" {
		return fmt.Errorf("%s is required", name)
	}
	if len(value) > MaxIdentifierLength {
		return fmt.Errorf("%s exceeds %d bytes", name, MaxIdentifierLength)
	}
	return nil
}

func validateRequiredIdentifierPointer(name string, value *string) error {
	if value == nil {
		return fmt.Errorf("%s is required", name)
	}
	return validateIdentifier(name, *value)
}

func validateOptionalIdentifier(name string, value *string) error {
	if value == nil {
		return nil
	}
	return validateIdentifier(name, *value)
}
