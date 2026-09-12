//go:build !linux

package main

import "errors"

type platformTrustProbe struct{}

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
