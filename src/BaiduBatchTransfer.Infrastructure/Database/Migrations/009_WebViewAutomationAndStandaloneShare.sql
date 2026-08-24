CREATE TABLE StandaloneShareBatches (
    Id TEXT PRIMARY KEY,
    DraftFingerprint TEXT NOT NULL,
    ResultDirectoryReference TEXT NOT NULL,
    Status TEXT NOT NULL CHECK(Status IN ('Queued','Active','Paused','Completed','CompletedWithFailures','Cancelled')),
    OperationCount INTEGER NOT NULL CHECK(OperationCount > 0),
    Revision INTEGER NOT NULL DEFAULT 0 CHECK(Revision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE INDEX IX_StandaloneShareBatches_History
ON StandaloneShareBatches(CreatedAt DESC, Id DESC);

CREATE INDEX IX_StandaloneShareBatches_Status_Updated
ON StandaloneShareBatches(Status, UpdatedAt, Id);

CREATE INDEX IX_StandaloneShareBatches_Draft
ON StandaloneShareBatches(DraftFingerprint, Id);

CREATE TABLE StandaloneShareOperations (
    Id TEXT PRIMARY KEY,
    BatchId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    SequenceNumber INTEGER NOT NULL CHECK(SequenceNumber > 0),
    DisplayName TEXT NOT NULL,
    ResourceKind TEXT NOT NULL CHECK(ResourceKind IN ('File','Folder')),
    ParentPath TEXT NOT NULL,
    ExactItemSetFingerprint TEXT NOT NULL,
    Generation INTEGER NOT NULL DEFAULT 1 CHECK(Generation > 0),
    Status TEXT NOT NULL CHECK(Status IN ('Queued','Reserved','Preparing','Submitting','ResultPending','Succeeded','Failed','Paused','Cancelled')),
    SubmissionState TEXT NOT NULL CHECK(SubmissionState IN ('NotStarted','Prepared','DefinitelyNotAccepted','Accepted','ResultPending','Completed')),
    SubmissionAcceptance TEXT NOT NULL DEFAULT 'Unknown' CHECK(SubmissionAcceptance IN ('Unknown','DefinitelyNotAccepted')),
    BaselineFingerprint TEXT NULL,
    BaselineShareCount INTEGER NULL CHECK(BaselineShareCount IS NULL OR BaselineShareCount >= 0),
    BaselineCompletedAt TEXT NULL,
    LeaseGeneration INTEGER NOT NULL DEFAULT 0 CHECK(LeaseGeneration >= 0),
    LeaseExpiresAt TEXT NULL,
    LastErrorCode TEXT NULL,
    Revision INTEGER NOT NULL DEFAULT 0 CHECK(Revision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(BatchId) REFERENCES StandaloneShareBatches(Id) ON DELETE CASCADE,
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id),
    UNIQUE(BatchId, SequenceNumber),
    UNIQUE(Id, BatchId, AccountId, Generation),
    CHECK((BaselineCompletedAt IS NULL AND BaselineFingerprint IS NULL AND BaselineShareCount IS NULL)
       OR (BaselineCompletedAt IS NOT NULL AND BaselineFingerprint IS NOT NULL AND BaselineShareCount IS NOT NULL))
);

CREATE INDEX IX_StandaloneShareOperations_Lease
ON StandaloneShareOperations(Status, LeaseExpiresAt, BatchId, SequenceNumber, Id);

CREATE INDEX IX_StandaloneShareOperations_Batch_Status
ON StandaloneShareOperations(BatchId, Status, SequenceNumber, Id);

CREATE INDEX IX_StandaloneShareOperations_Account_Status
ON StandaloneShareOperations(AccountId, Status, LeaseExpiresAt, Id);

CREATE TABLE StandaloneShareRemoteItems (
    OperationId TEXT NOT NULL,
    ItemOrdinal INTEGER NOT NULL CHECK(ItemOrdinal = 0),
    RemoteItemId TEXT NOT NULL,
    CanonicalSortKey BLOB NOT NULL,
    DisplayName TEXT NOT NULL,
    PRIMARY KEY(OperationId, ItemOrdinal),
    UNIQUE(OperationId, RemoteItemId),
    FOREIGN KEY(OperationId) REFERENCES StandaloneShareOperations(Id) ON DELETE CASCADE
);

CREATE INDEX IX_StandaloneShareRemoteItems_Canonical
ON StandaloneShareRemoteItems(OperationId, CanonicalSortKey);

CREATE TABLE StandaloneShareDiscoveryBaselines (
    OperationId TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK(Generation > 0),
    ShareId TEXT NOT NULL,
    PRIMARY KEY(OperationId, Generation, ShareId),
    FOREIGN KEY(OperationId) REFERENCES StandaloneShareOperations(Id) ON DELETE CASCADE
);

CREATE INDEX IX_StandaloneShareDiscoveryBaselines_Page
ON StandaloneShareDiscoveryBaselines(OperationId, Generation, ShareId);

CREATE TABLE StandaloneShareResults (
    Id TEXT PRIMARY KEY,
    OperationId TEXT NOT NULL,
    BatchId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK(Generation > 0),
    ExactItemSetFingerprint TEXT NOT NULL,
    SettingsFingerprint TEXT NOT NULL,
    ShareId TEXT NOT NULL,
    ShareUrl TEXT NOT NULL CHECK(ShareUrl LIKE 'https://%'),
    ExtractionCode TEXT NOT NULL,
    IsPermanent INTEGER NOT NULL CHECK(IsPermanent = 1),
    IsAccessUnlimited INTEGER NOT NULL CHECK(IsAccessUnlimited = 1),
    AcceptedAt TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UNIQUE(OperationId, Generation),
    UNIQUE(AccountId, ShareId),
    FOREIGN KEY(OperationId, BatchId, AccountId, Generation)
      REFERENCES StandaloneShareOperations(Id, BatchId, AccountId, Generation) ON DELETE CASCADE
);

CREATE INDEX IX_StandaloneShareResults_Batch_Operation
ON StandaloneShareResults(BatchId, OperationId, Id);

CREATE TABLE BatchResultDestinations (
    BatchJobId TEXT PRIMARY KEY,
    DirectoryReference TEXT NOT NULL,
    Revision INTEGER NOT NULL DEFAULT 0 CHECK(Revision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE
);

CREATE TABLE LocalResultExports (
    Id TEXT PRIMARY KEY,
    OwnerKind TEXT NOT NULL CHECK(OwnerKind IN ('TransferBatch','StandaloneShareBatch')),
    OwnerId TEXT NOT NULL,
    ExportKind TEXT NOT NULL CHECK(ExportKind IN ('ReplacementWorkbook','StandaloneShareWorkbook','OfficialShareDetail')),
    Generation INTEGER NOT NULL CHECK(Generation > 0),
    Status TEXT NOT NULL CHECK(Status IN ('Pending','InProgress','Succeeded','Failed')),
    TargetFileName TEXT NOT NULL,
    FinalFileName TEXT NULL,
    SanitizedErrorCode TEXT NULL,
    LeaseGeneration INTEGER NOT NULL DEFAULT 0 CHECK(LeaseGeneration >= 0),
    LeaseExpiresAt TEXT NULL,
    Revision INTEGER NOT NULL DEFAULT 0 CHECK(Revision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    UNIQUE(OwnerKind, OwnerId, ExportKind, Generation),
    CHECK(Status <> 'Succeeded' OR (FinalFileName IS NOT NULL AND LeaseExpiresAt IS NULL)),
    CHECK(Status <> 'InProgress' OR LeaseExpiresAt IS NOT NULL)
);

CREATE INDEX IX_LocalResultExports_Recoverable
ON LocalResultExports(Status, LeaseExpiresAt, UpdatedAt, Id);

CREATE TABLE WebAutomationAttempts (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK(Generation > 0),
    OwnerKind TEXT NOT NULL CHECK(OwnerKind IN ('Transfer','TransferShare','StandaloneShare')),
    State TEXT NOT NULL CHECK(State IN ('Preparing','BaselinePersisted','Submitting','ResultPending','UserHandoff','Succeeded','DefinitelyNotSubmitted','NeedsDecision')),
    ObservationFingerprint TEXT NULL,
    BaselineFingerprint TEXT NULL,
    BaselineItemCount INTEGER NOT NULL DEFAULT 0 CHECK(BaselineItemCount >= 0),
    BaselinePersistedAt TEXT NULL,
    IsWaitingForUser INTEGER NOT NULL DEFAULT 0 CHECK(IsWaitingForUser IN (0,1)),
    Revision INTEGER NOT NULL DEFAULT 0 CHECK(Revision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY(OperationId, AccountId, Generation),
    CHECK(State NOT IN ('BaselinePersisted','Submitting','ResultPending','Succeeded','NeedsDecision')
       OR (BaselineFingerprint IS NOT NULL AND BaselinePersistedAt IS NOT NULL))
);

CREATE INDEX IX_WebAutomationAttempts_Recovery
ON WebAutomationAttempts(State, IsWaitingForUser, UpdatedAt, OperationId);

CREATE TABLE WebAutomationAttemptRemoteItems (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL,
    ItemOrdinal INTEGER NOT NULL CHECK(ItemOrdinal >= 0),
    RemoteItemId TEXT NOT NULL,
    CanonicalSortKey BLOB NOT NULL,
    DisplayName TEXT NOT NULL,
    PRIMARY KEY(OperationId, AccountId, Generation, ItemOrdinal),
    UNIQUE(OperationId, AccountId, Generation, RemoteItemId),
    FOREIGN KEY(OperationId, AccountId, Generation)
      REFERENCES WebAutomationAttempts(OperationId, AccountId, Generation) ON DELETE CASCADE
);

CREATE INDEX IX_WebAutomationAttemptRemoteItems_Canonical
ON WebAutomationAttemptRemoteItems(OperationId, AccountId, Generation, CanonicalSortKey);
