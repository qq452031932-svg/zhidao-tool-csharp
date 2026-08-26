ALTER TABLE WebAutomationAttempts ADD COLUMN SourceTotalCount INTEGER NULL CHECK(SourceTotalCount IS NULL OR SourceTotalCount >= 0);
ALTER TABLE WebAutomationAttempts ADD COLUMN SourceReachedEnd INTEGER NULL CHECK(SourceReachedEnd IS NULL OR SourceReachedEnd IN (0,1));
ALTER TABLE WebAutomationAttempts ADD COLUMN SourceIsStable INTEGER NULL CHECK(SourceIsStable IS NULL OR SourceIsStable IN (0,1));
ALTER TABLE WebAutomationAttempts ADD COLUMN SourceGenerationToken TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN SourceFingerprint TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN SourceObservationFingerprint TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN BaiduUserId TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN CapabilityAdapterKind TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN CapabilityProtocolVersion TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN CapabilitySelectorVersion TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN CapabilityBuildVersion TEXT NULL;

ALTER TABLE WebAutomationAttempts ADD COLUMN TargetExactId TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN TargetPath TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN TargetTotalCount INTEGER NULL CHECK(TargetTotalCount IS NULL OR TargetTotalCount >= 0);
ALTER TABLE WebAutomationAttempts ADD COLUMN TargetReachedEnd INTEGER NULL CHECK(TargetReachedEnd IS NULL OR TargetReachedEnd IN (0,1));
ALTER TABLE WebAutomationAttempts ADD COLUMN TargetIsStable INTEGER NULL CHECK(TargetIsStable IS NULL OR TargetIsStable IN (0,1));
ALTER TABLE WebAutomationAttempts ADD COLUMN TargetGenerationToken TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN TargetObservationFingerprint TEXT NULL;

ALTER TABLE WebAutomationAttempts ADD COLUMN SubmissionSourceFingerprint TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN SubmissionTargetExactId TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN SubmissionTargetBaselineFingerprint TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN SubmissionMarkedAt TEXT NULL;

ALTER TABLE WebAutomationAttempts ADD COLUMN OfficialRequestId TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN TransferMappingFingerprint TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN TransferMappingCount INTEGER NOT NULL DEFAULT 0 CHECK(TransferMappingCount >= 0);

CREATE TABLE WebAutomationAttemptSourceItems (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL,
    ItemOrdinal INTEGER NOT NULL CHECK(ItemOrdinal >= 0),
    RemoteItemId TEXT NOT NULL,
    CanonicalSortKey BLOB NOT NULL,
    PRIMARY KEY(OperationId, AccountId, Generation, ItemOrdinal),
    UNIQUE(OperationId, AccountId, Generation, RemoteItemId),
    FOREIGN KEY(OperationId, AccountId, Generation)
      REFERENCES WebAutomationAttempts(OperationId, AccountId, Generation) ON DELETE CASCADE
);

CREATE INDEX IX_WebAutomationAttemptSourceItems_Canonical
ON WebAutomationAttemptSourceItems(OperationId, AccountId, Generation, CanonicalSortKey);

CREATE TABLE WebAutomationTransferMappings (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL,
    ItemOrdinal INTEGER NOT NULL CHECK(ItemOrdinal >= 0),
    SourceRemoteItemId TEXT NOT NULL,
    SourceCanonicalSortKey BLOB NOT NULL,
    DestinationRemoteItemId TEXT NOT NULL,
    DestinationCanonicalSortKey BLOB NOT NULL,
    PRIMARY KEY(OperationId, AccountId, Generation, ItemOrdinal),
    UNIQUE(OperationId, AccountId, Generation, SourceRemoteItemId),
    UNIQUE(OperationId, AccountId, Generation, DestinationRemoteItemId),
    FOREIGN KEY(OperationId, AccountId, Generation)
      REFERENCES WebAutomationAttempts(OperationId, AccountId, Generation) ON DELETE CASCADE
);

CREATE INDEX IX_WebAutomationTransferMappings_Source
ON WebAutomationTransferMappings(OperationId, AccountId, Generation, SourceCanonicalSortKey);

CREATE TABLE WebAutomationDirectoryMutations (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    OriginGeneration INTEGER NOT NULL CHECK(OriginGeneration > 0),
    SegmentOrdinal INTEGER NOT NULL CHECK(SegmentOrdinal >= 0),
    ParentExactId TEXT NOT NULL,
    CanonicalSegment TEXT NOT NULL,
    ChildExactId TEXT NULL,
    CanonicalChildPath TEXT NOT NULL,
    ListingGenerationToken TEXT NOT NULL,
    BaiduUserId TEXT NOT NULL,
    CapabilityAdapterKind TEXT NOT NULL,
    CapabilityProtocolVersion TEXT NOT NULL,
    CapabilitySelectorVersion TEXT NOT NULL,
    CapabilityBuildVersion TEXT NOT NULL,
    State TEXT NOT NULL CHECK(State IN ('Intent','Confirmed','OutcomeUnknown')),
    OwningAttemptRevision INTEGER NOT NULL CHECK(OwningAttemptRevision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY(OperationId, AccountId, OriginGeneration, SegmentOrdinal)
);

CREATE UNIQUE INDEX UX_WebAutomationDirectoryMutations_Unresolved
ON WebAutomationDirectoryMutations(OperationId, AccountId, ParentExactId, CanonicalSegment)
WHERE State IN ('Intent','OutcomeUnknown');

CREATE INDEX IX_WebAutomationDirectoryMutations_Recovery
ON WebAutomationDirectoryMutations(OperationId, AccountId, State, UpdatedAt, OriginGeneration, SegmentOrdinal);
