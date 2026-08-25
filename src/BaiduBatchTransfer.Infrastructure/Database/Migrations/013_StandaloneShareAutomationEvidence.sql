ALTER TABLE StandaloneShareOperations
ADD COLUMN ExtractionCodeMode TEXT NOT NULL DEFAULT 'Random'
CHECK(ExtractionCodeMode IN ('Random','Fixed'));

ALTER TABLE StandaloneShareOperations
ADD COLUMN FixedExtractionCode TEXT NULL;

ALTER TABLE StandaloneShareOperations
ADD COLUMN ExistingShareDecision TEXT NOT NULL DEFAULT 'Reuse'
CHECK(ExistingShareDecision IN ('Reuse','Regenerate','Cancel'));

ALTER TABLE StandaloneShareOperations
ADD COLUMN SettingsFingerprint TEXT NOT NULL
DEFAULT '1b46ee3ae6069c21a5d785c56769c60e765b13c33eaec6d3aadae142c19f586a'
CHECK(length(trim(SettingsFingerprint)) > 0);

ALTER TABLE StandaloneShareOperations
ADD COLUMN RequestDescriptorJson TEXT NULL;

ALTER TABLE StandaloneShareOperations
ADD COLUMN SubmitStartedAt TEXT NULL;

ALTER TABLE StandaloneShareOperations
ADD COLUMN AdapterRequestId TEXT NULL;

ALTER TABLE StandaloneShareOperations
ADD COLUMN AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK(AttemptCount >= 0);

ALTER TABLE StandaloneShareOperations
ADD COLUMN NextReconcileAt TEXT NULL;

ALTER TABLE StandaloneShareOperations
ADD COLUMN ReconciliationIssue TEXT NULL;

CREATE INDEX IX_StandaloneShareOperations_Reconciliation
ON StandaloneShareOperations(Status,NextReconcileAt,BatchId,SequenceNumber,Id);

CREATE TABLE WebAutomationShareAcceptances (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK(Generation > 0),
    OwnerKind TEXT NOT NULL CHECK(OwnerKind IN ('TransferShare','StandaloneShare')),
    AdapterRequestId TEXT NOT NULL,
    ShareId TEXT NOT NULL,
    ShareUrl TEXT NOT NULL CHECK(ShareUrl LIKE 'https://%'),
    ExtractionCode TEXT NOT NULL,
    ExtractionCodeMode TEXT NOT NULL CHECK(ExtractionCodeMode IN ('Random','Fixed')),
    ExactItemSetFingerprint TEXT NOT NULL,
    ObservationFingerprint TEXT NOT NULL,
    AcceptedAt TEXT NOT NULL,
    PRIMARY KEY(OperationId,AccountId,Generation),
    FOREIGN KEY(OperationId,AccountId,Generation)
      REFERENCES WebAutomationAttempts(OperationId,AccountId,Generation) ON DELETE CASCADE
);
