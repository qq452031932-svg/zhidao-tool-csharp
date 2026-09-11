ALTER TABLE WebAutomationAttempts ADD COLUMN SubmissionId TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN SubmissionReadinessFingerprint TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN OfficialObservationFingerprint TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN TargetConfirmationFingerprint TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN TargetConfirmedAt TEXT NULL;

UPDATE WebAutomationAttempts
SET OfficialObservationFingerprint=ObservationFingerprint
WHERE OfficialRequestId IS NOT NULL;
