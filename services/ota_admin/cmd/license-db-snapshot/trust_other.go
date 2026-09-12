//go:build !linux

package main

import (
	"context"
	"errors"
)

type platformTrustProbe struct{}
type platformSourceBinder struct{}

func (platformSourceBinder) Bind(string, FileIdentity) (SourceBinding, error) {
	return nil, errors.New("verified source binding is supported only on Linux")
}

type platformQuiescenceProbe struct{}

func (platformQuiescenceProbe) Prove(context.Context) error {
	return errors.New("service UID quiescence is supported only on Linux")
}
func platformPathIdentity(string) (FileIdentity, error) {
	return FileIdentity{}, errors.New("path identity is supported only on Linux")
}

func (platformTrustProbe) ValidateSource(string, SourceKind) (FileIdentity, error) {
	return FileIdentity{}, errors.New("strict filesystem trust validation is supported only on Linux")
}
func (platformTrustProbe) ValidateBackupDirectory(string) (FileIdentity, error) {
	return FileIdentity{}, errors.New("strict filesystem trust validation is supported only on Linux")
}
func (platformTrustProbe) ValidateSidecar(string, SourceKind) (FileIdentity, bool, error) {
	return FileIdentity{}, false, errors.New("strict filesystem trust validation is supported only on Linux")
}
func (platformTrustProbe) SecureOutput(string) error {
	return errors.New("strict filesystem trust validation is supported only on Linux")
}
