CREATE TABLE IF NOT EXISTS ImportSessions (
    Id TEXT PRIMARY KEY, SourceType TEXT NOT NULL, SourceName TEXT NOT NULL,
    Status TEXT NOT NULL CHECK(Status IN ('Parsing','Draft','Prechecking','ReadyToConfirm','BatchCreated','Cancelled')),
    TotalRows INTEGER NOT NULL DEFAULT 0 CHECK(TotalRows >= 0), ReadyRows INTEGER NOT NULL DEFAULT 0 CHECK(ReadyRows >= 0),
    InvalidRows INTEGER NOT NULL DEFAULT 0 CHECK(InvalidRows >= 0), DuplicateRows INTEGER NOT NULL DEFAULT 0 CHECK(DuplicateRows >= 0),
    ExcludedRows INTEGER NOT NULL DEFAULT 0 CHECK(ExcludedRows >= 0), PrecheckAccountId TEXT NULL,
    CreatedBatchJobId TEXT NULL, AppendToBatchJobId TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(PrecheckAccountId) REFERENCES Accounts(Id), FOREIGN KEY(CreatedBatchJobId) REFERENCES BatchJobs(Id),
    FOREIGN KEY(AppendToBatchJobId) REFERENCES BatchJobs(Id),
    CHECK(ReadyRows + InvalidRows + DuplicateRows + ExcludedRows <= TotalRows)
);

CREATE TABLE IF NOT EXISTS Accounts (
    Id TEXT PRIMARY KEY, BaiduUserId TEXT NOT NULL UNIQUE, DisplayName TEXT NOT NULL, EncryptedSession TEXT NULL,
    WebView2ProfileDirectory TEXT NULL, LoginMethod TEXT NOT NULL,
    Status TEXT NOT NULL CHECK(Status IN ('Active','Invalid','Disabled','Deleted')),
    CapacityTotal INTEGER NULL CHECK(CapacityTotal IS NULL OR CapacityTotal >= 0),
    CapacityUsed INTEGER NULL CHECK(CapacityUsed IS NULL OR CapacityUsed >= 0), DailyLimit INTEGER NOT NULL DEFAULT 0 CHECK(DailyLimit >= 0),
    DailyExecutedCount INTEGER NOT NULL DEFAULT 0 CHECK(DailyExecutedCount >= 0), SortOrder INTEGER NOT NULL DEFAULT 0,
    IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK(IsEnabled IN (0,1)), LastValidatedAt TEXT NULL,
    DailyCountDateKey TEXT NOT NULL, IsDeleted INTEGER NOT NULL DEFAULT 0 CHECK(IsDeleted IN (0,1)), DeletedAt TEXT NULL
);

CREATE TABLE IF NOT EXISTS StagedImportRows (
    Id TEXT PRIMARY KEY, ImportSessionId TEXT NOT NULL, SourceRowNumber INTEGER NOT NULL CHECK(SourceRowNumber > 0),
    ShareUrl TEXT NOT NULL, NormalizedShareId TEXT NULL, ExtractionCode TEXT NULL, TargetPath TEXT NULL,
    CreateFolderName TEXT NULL, RequestedAccountName TEXT NULL, MappedAccountId TEXT NULL, Note TEXT NULL,
    Disposition TEXT NOT NULL CHECK(Disposition IN ('Ready','NeedsResolution','Invalid','Duplicate','Conflict','Excluded')),
    DuplicateOfStagedRowId TEXT NULL, ValidationMessage TEXT NULL, PrecheckStatus TEXT NULL,
    PrecheckErrorCode TEXT NULL, PrecheckedAt TEXT NULL, UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(ImportSessionId) REFERENCES ImportSessions(Id) ON DELETE CASCADE,
    FOREIGN KEY(MappedAccountId) REFERENCES Accounts(Id), FOREIGN KEY(DuplicateOfStagedRowId) REFERENCES StagedImportRows(Id),
    UNIQUE(ImportSessionId, SourceRowNumber), CHECK(PrecheckStatus IS NULL OR PrecheckStatus IN ('Succeeded','Failed'))
);

CREATE TABLE IF NOT EXISTS AccountRules (
    Id TEXT PRIMARY KEY, AccountId TEXT NOT NULL, RuleType TEXT NOT NULL, RuleValue TEXT NOT NULL,
    Priority INTEGER NOT NULL, IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK(IsEnabled IN (0,1)),
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS BatchJobs (
    Id TEXT PRIMARY KEY, Name TEXT NOT NULL, SourceType TEXT NOT NULL,
    TransferMode TEXT NOT NULL CHECK(TransferMode IN ('Replication','Distribution')),
    QuotaBasis TEXT NOT NULL CHECK(QuotaBasis IN ('TaskCount','SuccessTarget')),
    ShortagePolicy TEXT NOT NULL CHECK(ShortagePolicy IN ('ProportionalReduction','AccountPriority','WaitForCandidates')),
    ResourceCount INTEGER NOT NULL DEFAULT 0 CHECK(ResourceCount >= 0),
    OperationCount INTEGER NOT NULL DEFAULT 0 CHECK(OperationCount >= 0), DefaultTargetPath TEXT NOT NULL,
    FolderCreationMode TEXT NOT NULL, DuplicatePolicy TEXT NOT NULL CHECK(DuplicatePolicy IN ('Skip','Overwrite','Rename')), ValidationMode TEXT NOT NULL,
    Status TEXT NOT NULL CHECK(Status IN ('Queued','Active','Paused','AwaitingCandidates','PausedReady','Completed','CompletedWithShortfall','Cancelled')),
    CreatedAt TEXT NOT NULL, StartedAt TEXT NULL, CompletedAt TEXT NULL, ImportSessionId TEXT NULL, QueueOrder INTEGER NOT NULL,
    FOREIGN KEY(ImportSessionId) REFERENCES ImportSessions(Id)
);

CREATE TABLE IF NOT EXISTS ResourceItems (
    Id TEXT PRIMARY KEY, BatchJobId TEXT NOT NULL, StagedImportRowId TEXT NULL, CandidateSequence INTEGER NOT NULL CHECK(CandidateSequence > 0),
    ShareUrl TEXT NOT NULL, NormalizedShareId TEXT NOT NULL, ExtractionCode TEXT NULL, Note TEXT NULL,
    TargetPathOverride TEXT NULL, NormalizedTargetPathOverride TEXT NULL, CreateFolderName TEXT NULL,
    FolderCreationModeOverride TEXT NOT NULL, GlobalValidityStatus TEXT NOT NULL, CandidateConstraintAccountId TEXT NULL,
    CandidateStatus TEXT NOT NULL CHECK(CandidateStatus IN ('Unallocated','Reserved','Expanded','GloballyInvalid','Unused')),
    ReservedAccountId TEXT NULL, ReservationExpiresAt TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE,
    FOREIGN KEY(StagedImportRowId) REFERENCES StagedImportRows(Id),
    FOREIGN KEY(CandidateConstraintAccountId) REFERENCES Accounts(Id), FOREIGN KEY(ReservedAccountId) REFERENCES Accounts(Id),
    UNIQUE(BatchJobId, CandidateSequence)
);

CREATE TABLE IF NOT EXISTS TransferOperations (
    Id TEXT PRIMARY KEY, ResourceItemId TEXT NOT NULL, BatchJobId TEXT NOT NULL,
    SequenceNumber INTEGER NOT NULL CHECK(SequenceNumber > 0), AllocationOrdinal INTEGER NOT NULL CHECK(AllocationOrdinal > 0),
    TargetPath TEXT NOT NULL, NormalizedTargetPath TEXT NOT NULL, CreateFolderName TEXT NOT NULL DEFAULT '',
    AssignedAccountId TEXT NOT NULL, AllocationSource TEXT NOT NULL,
    Status TEXT NOT NULL CHECK(Status IN ('Waiting','Reserved','Preparing','Submitting','ResultPending','Succeeded','Failed','Cancelled')),
    OutcomeKind TEXT NULL CHECK(OutcomeKind IS NULL OR OutcomeKind IN ('NewTransfer','AlreadySatisfied','Failed','GloballyInvalid','Cancelled')),
    AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK(AttemptCount >= 0), OperationFingerprint TEXT NOT NULL,
    ConfirmationDescriptorJson TEXT NULL, AdapterRequestId TEXT NULL, RemoteItemIdsJson TEXT NULL,
    SubmitState TEXT NOT NULL CHECK(SubmitState IN ('NotPrepared','PreparingToSubmit','Submitting','ResultReceived','PendingConfirmation')),
    TargetSlotState TEXT NOT NULL CHECK(TargetSlotState IN ('Unoccupied','Reserved','Fulfilled','Released')),
    TargetSlotReservedAt TEXT NULL, DailyCountedAt TEXT NULL,
    SubmitStartedAt TEXT NULL, LastErrorCode TEXT NULL, LastErrorMessage TEXT NULL, LeaseExpiresAt TEXT NULL,
    CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(ResourceItemId) REFERENCES ResourceItems(Id) ON DELETE CASCADE,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE, FOREIGN KEY(AssignedAccountId) REFERENCES Accounts(Id),
    CHECK(SubmitState IN ('NotPrepared','PreparingToSubmit') OR ConfirmationDescriptorJson IS NOT NULL)
);

CREATE TABLE IF NOT EXISTS AccountAllocations (
    Id TEXT PRIMARY KEY, BatchJobId TEXT NOT NULL, AccountId TEXT NOT NULL, PriorityOrder INTEGER NOT NULL,
    DefaultTargetPath TEXT NOT NULL, NormalizedDefaultTargetPath TEXT NOT NULL, TargetItemCount INTEGER NOT NULL DEFAULT 0,
    TargetSuccessCount INTEGER NOT NULL DEFAULT 0, EffectiveTargetSuccessCount INTEGER NOT NULL DEFAULT 0,
    AssignedItemCount INTEGER NOT NULL DEFAULT 0, AttemptedItemCount INTEGER NOT NULL DEFAULT 0,
    NewSuccessItemCount INTEGER NOT NULL DEFAULT 0, AlreadySatisfiedItemCount INTEGER NOT NULL DEFAULT 0,
    SuccessItemCount INTEGER NOT NULL DEFAULT 0, FailedItemCount INTEGER NOT NULL DEFAULT 0, LockedItemCount INTEGER NOT NULL DEFAULT 0,
    ExclusiveCandidateCount INTEGER NOT NULL DEFAULT 0, UnusedCandidateCount INTEGER NOT NULL DEFAULT 0,
    ReservedSlotCount INTEGER NOT NULL DEFAULT 0, FulfilledCount INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE, FOREIGN KEY(AccountId) REFERENCES Accounts(Id),
    UNIQUE(BatchJobId, AccountId),
    CHECK(TargetItemCount >= 0 AND TargetSuccessCount >= 0 AND EffectiveTargetSuccessCount >= 0 AND
          AssignedItemCount >= 0 AND AttemptedItemCount >= 0 AND NewSuccessItemCount >= 0 AND
          AlreadySatisfiedItemCount >= 0 AND SuccessItemCount >= 0 AND FailedItemCount >= 0 AND
          LockedItemCount >= 0 AND ExclusiveCandidateCount >= 0 AND UnusedCandidateCount >= 0 AND
          ReservedSlotCount >= 0 AND FulfilledCount >= 0)
);

CREATE TABLE IF NOT EXISTS ExecutionLogs (
    Id TEXT PRIMARY KEY, BatchJobId TEXT NULL, ResourceItemId TEXT NULL, TransferOperationId TEXT NULL, AccountId TEXT NULL,
    Stage TEXT NOT NULL, Level TEXT NOT NULL, EventCode TEXT NOT NULL, SanitizedMessage TEXT NOT NULL, CreatedAt TEXT NOT NULL,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE, FOREIGN KEY(ResourceItemId) REFERENCES ResourceItems(Id),
    FOREIGN KEY(TransferOperationId) REFERENCES TransferOperations(Id), FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
);

CREATE TABLE IF NOT EXISTS SchemaVersions (Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL, AppVersion TEXT NOT NULL);

CREATE INDEX IF NOT EXISTS IX_StagedImportRows_Session_Disposition ON StagedImportRows(ImportSessionId, Disposition, SourceRowNumber);
CREATE INDEX IF NOT EXISTS IX_BatchJobs_Status_Queue ON BatchJobs(Status, QueueOrder);
CREATE INDEX IF NOT EXISTS IX_Accounts_Status ON Accounts(Status, IsEnabled, IsDeleted, SortOrder);
CREATE INDEX IF NOT EXISTS IX_ResourceItems_Candidate_Status ON ResourceItems(BatchJobId, CandidateSequence, CandidateStatus);
CREATE INDEX IF NOT EXISTS IX_TransferOperations_Account_Status_Lease_Ordinal ON TransferOperations(AssignedAccountId, Status, LeaseExpiresAt, AllocationOrdinal);
CREATE INDEX IF NOT EXISTS IX_TransferOperations_Batch_Status_Sequence ON TransferOperations(BatchJobId, Status, SequenceNumber);
CREATE INDEX IF NOT EXISTS IX_ExecutionLogs_Time ON ExecutionLogs(CreatedAt);
CREATE INDEX IF NOT EXISTS IX_ExecutionLogs_Batch_Time ON ExecutionLogs(BatchJobId, CreatedAt);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TransferOperations_ActiveKey
ON TransferOperations(ResourceItemId, AssignedAccountId, NormalizedTargetPath, CreateFolderName)
WHERE Status <> 'Cancelled';
