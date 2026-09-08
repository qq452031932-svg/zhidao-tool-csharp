package model

import (
	"errors"
	"fmt"
	"time"
)

const (
	ProtocolVersion     = 1
	MaxIdentifierLength = 128
)

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
	CardID          string     `json:"card_id"`
	SoftwareID      string     `json:"software_id"`
	MachineHash     string     `json:"machine_hash"`
	PackageName     string     `json:"package_name"`
	ExpiresAt       *time.Time `json:"expires_at"`
	LeaseUntil      *time.Time `json:"lease_until"`
}

func (c Claims) Validate() error {
	if c.ProtocolVersion != ProtocolVersion {
		return fmt.Errorf("protocol_version must be %d", ProtocolVersion)
	}
	identifiers := []struct {
		name  string
		value string
	}{
		{name: "request_id", value: c.RequestID},
		{name: "card_id", value: c.CardID},
		{name: "software_id", value: c.SoftwareID},
		{name: "machine_hash", value: c.MachineHash},
		{name: "package_name", value: c.PackageName},
	}
	for _, identifier := range identifiers {
		if err := validateOptionalIdentifier(identifier.name, identifier.value); err != nil {
			return err
		}
	}

	if c.Decision == DecisionAllow && c.Code == CodeOK {
		for _, identifier := range identifiers[1:] {
			if err := validateIdentifier(identifier.name, identifier.value); err != nil {
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

func validateOptionalIdentifier(name, value string) error {
	if value == "" {
		return nil
	}
	return validateIdentifier(name, value)
}
