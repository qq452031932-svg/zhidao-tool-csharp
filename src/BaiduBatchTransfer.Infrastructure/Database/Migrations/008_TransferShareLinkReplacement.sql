ALTER TABLE ImportSessions ADD COLUMN ColumnMappingFingerprint TEXT NULL;

ALTER TABLE BatchJobs ADD COLUMN ShareOperationCount INTEGER NOT NULL DEFAULT 0
    CHECK(typeof(ShareOperationCount)='integer' AND ShareOperationCount >= 0);

ALTER TABLE ResourceItems ADD COLUMN ResolvedOutputTitle TEXT NULL;

CREATE TABLE ImportColumnDefinitions (
    ImportSessionId TEXT NOT NULL,
    ColumnOrdinal INTEGER NOT NULL CHECK(typeof(ColumnOrdinal)='integer' AND ColumnOrdinal >= 0),
    SourceHeader TEXT NULL,
    MappedRole TEXT NOT NULL CHECK(MappedRole IN ('ShareUrl','Title','ExtractionCode','Preserve')),
    IsRequired INTEGER NOT NULL CHECK(IsRequired IN (0,1)),
    PRIMARY KEY(ImportSessionId, ColumnOrdinal),
    FOREIGN KEY(ImportSessionId) REFERENCES ImportSessions(Id) ON DELETE CASCADE,
    CHECK((MappedRole='ShareUrl' AND IsRequired=1) OR (MappedRole<>'ShareUrl' AND IsRequired=0))
);

CREATE UNIQUE INDEX UX_ImportColumnDefinitions_Session_MappedRole
ON ImportColumnDefinitions(ImportSessionId, MappedRole)
WHERE MappedRole <> 'Preserve';

CREATE INDEX IX_ImportColumnDefinitions_Session_Ordinal
ON ImportColumnDefinitions(ImportSessionId, ColumnOrdinal);

CREATE TABLE StagedImportRowPayloads (
    StagedImportRowId TEXT PRIMARY KEY,
    SourceTitle TEXT NULL,
    ResolvedOutputTitle TEXT NULL,
    OriginalShareUrl TEXT NOT NULL,
    RawValuesJson TEXT NOT NULL,
    FOREIGN KEY(StagedImportRowId) REFERENCES StagedImportRows(Id) ON DELETE CASCADE
);

CREATE TABLE BatchShareSettings (
    BatchJobId TEXT PRIMARY KEY,
    IsPermanent INTEGER NOT NULL CHECK(IsPermanent IN (0,1)),
    IsAccessUnlimited INTEGER NOT NULL CHECK(IsAccessUnlimited IN (0,1)),
    ExtractionCodeMode TEXT NOT NULL CHECK(ExtractionCodeMode IN ('Random','Fixed')),
    FixedExtractionCode TEXT NULL,
    ExistingSharePolicy TEXT NOT NULL CHECK(ExistingSharePolicy IN ('Ask','Reuse','Regenerate','Cancel')),
    SettingsFingerprint TEXT NOT NULL CHECK(length(trim(SettingsFingerprint))>0),
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE,
    CHECK((ExtractionCodeMode='Random' AND FixedExtractionCode IS NULL) OR
          (ExtractionCodeMode='Fixed' AND FixedExtractionCode IS NOT NULL AND length(trim(FixedExtractionCode))>0))
);

CREATE UNIQUE INDEX UX_BatchShareSettings_Batch_Fingerprint
ON BatchShareSettings(BatchJobId, SettingsFingerprint);

CREATE TABLE BatchOutputAccounts (
    BatchJobId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    OutputOrdinal INTEGER NOT NULL CHECK(typeof(OutputOrdinal)='integer' AND OutputOrdinal > 0),
    OutputLabel TEXT NOT NULL CHECK(length(trim(OutputLabel))>0),
    PlannedTransferCount INTEGER NOT NULL CHECK(typeof(PlannedTransferCount)='integer' AND PlannedTransferCount >= 0),
    PlannedShareCount INTEGER NOT NULL CHECK(typeof(PlannedShareCount)='integer' AND PlannedShareCount >= 0),
    ShareEnabled INTEGER NOT NULL CHECK(ShareEnabled IN (0,1)),
    CreatedShareOperationCount INTEGER NOT NULL DEFAULT 0
      CHECK(typeof(CreatedShareOperationCount)='integer' AND CreatedShareOperationCount >= 0
        AND CreatedShareOperationCount <= PlannedShareCount),
    PRIMARY KEY(BatchJobId, AccountId),
    UNIQUE(BatchJobId, OutputOrdinal),
    UNIQUE(BatchJobId, OutputLabel),
    UNIQUE(BatchJobId, AccountId, OutputOrdinal),
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE,
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
    ,CHECK(PlannedShareCount <= PlannedTransferCount)
    ,CHECK((ShareEnabled=0 AND PlannedShareCount=0) OR (ShareEnabled=1 AND PlannedShareCount>0))
);

CREATE INDEX IX_BatchOutputAccounts_Batch_Ordinal
ON BatchOutputAccounts(BatchJobId, OutputOrdinal, AccountId);

-- TransferOperations is rebuilt rather than patched so the status constraint is
-- fingerprinted and admits Paused while preserving the full v1-v7 row shape.
CREATE TEMP TABLE V8TransferValidation(Value INTEGER NOT NULL CHECK(Value=1));
INSERT INTO V8TransferValidation(Value) SELECT CASE WHEN COUNT(*) >= 0 THEN 1 ELSE 0 END FROM TransferOperations;

CREATE TABLE TransferOperations_v8 (
    Id TEXT PRIMARY KEY, ResourceItemId TEXT NOT NULL, BatchJobId TEXT NOT NULL,
    SequenceNumber INTEGER NOT NULL CHECK(SequenceNumber > 0), AllocationOrdinal INTEGER NOT NULL CHECK(AllocationOrdinal > 0),
    TargetPath TEXT NOT NULL, NormalizedTargetPath TEXT NOT NULL, CreateFolderName TEXT NOT NULL DEFAULT '',
    AssignedAccountId TEXT NOT NULL, AllocationSource TEXT NOT NULL,
    Status TEXT NOT NULL CHECK(Status IN ('Waiting','Reserved','Preparing','Submitting','ResultPending','Succeeded','Failed','Paused','Cancelled')),
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

INSERT INTO TransferOperations_v8(
    Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,
    AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,
    AdapterRequestId,RemoteItemIdsJson,SubmitState,TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,
    LastErrorCode,LastErrorMessage,LeaseExpiresAt,CreatedAt,UpdatedAt)
SELECT
    Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,
    AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,
    AdapterRequestId,RemoteItemIdsJson,SubmitState,TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,
    LastErrorCode,LastErrorMessage,LeaseExpiresAt,CreatedAt,UpdatedAt
FROM TransferOperations;

INSERT INTO V8TransferValidation(Value)
SELECT CASE WHEN (SELECT COUNT(*) FROM TransferOperations)=(SELECT COUNT(*) FROM TransferOperations_v8) THEN 1 ELSE 0 END;

CREATE TABLE ExecutionLogs_v8 (
    Id TEXT PRIMARY KEY, BatchJobId TEXT NULL, ResourceItemId TEXT NULL, TransferOperationId TEXT NULL, AccountId TEXT NULL,
    Stage TEXT NOT NULL, Level TEXT NOT NULL, EventCode TEXT NOT NULL, SanitizedMessage TEXT NOT NULL, CreatedAt TEXT NOT NULL,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE,
    FOREIGN KEY(ResourceItemId) REFERENCES ResourceItems(Id),
    FOREIGN KEY(TransferOperationId) REFERENCES TransferOperations_v8(Id),
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
);

INSERT INTO ExecutionLogs_v8(Id,BatchJobId,ResourceItemId,TransferOperationId,AccountId,Stage,Level,EventCode,SanitizedMessage,CreatedAt)
SELECT Id,BatchJobId,ResourceItemId,TransferOperationId,AccountId,Stage,Level,EventCode,SanitizedMessage,CreatedAt FROM ExecutionLogs;

INSERT INTO V8TransferValidation(Value)
SELECT CASE WHEN (SELECT COUNT(*) FROM ExecutionLogs)=(SELECT COUNT(*) FROM ExecutionLogs_v8) THEN 1 ELSE 0 END;

DROP TABLE ExecutionLogs;
DROP TABLE TransferOperations;
ALTER TABLE TransferOperations_v8 RENAME TO TransferOperations;
ALTER TABLE ExecutionLogs_v8 RENAME TO ExecutionLogs;
DROP TABLE V8TransferValidation;

CREATE INDEX IX_TransferOperations_Account_Status_Lease_Ordinal
ON TransferOperations(AssignedAccountId, Status, LeaseExpiresAt, AllocationOrdinal);
CREATE UNIQUE INDEX UX_TransferOperations_ActiveKey
ON TransferOperations(ResourceItemId, AssignedAccountId, NormalizedTargetPath, CreateFolderName)
WHERE Status <> 'Cancelled';
CREATE INDEX IX_TransferOperations_Status_Lease
ON TransferOperations(Status, LeaseExpiresAt);
CREATE INDEX IX_TransferOperations_Batch_AllocationOrdinal
ON TransferOperations(BatchJobId, AllocationOrdinal, SequenceNumber, Id);
CREATE INDEX IX_TransferOperations_Batch_Status_AllocationOrdinal
ON TransferOperations(BatchJobId, Status, AllocationOrdinal, SequenceNumber, Id);
CREATE INDEX IX_TransferOperations_Result_Account_Keyset
ON TransferOperations(BatchJobId, AssignedAccountId, AllocationOrdinal, SequenceNumber, Id, Status);
CREATE INDEX IX_TransferOperations_Result_UpdatedInstant_Keyset
ON TransferOperations(BatchJobId, julianday(UpdatedAt), AllocationOrdinal, SequenceNumber, Id);
CREATE INDEX IX_TransferOperations_Resource_Batch
ON TransferOperations(ResourceItemId, BatchJobId);
CREATE INDEX IX_TransferOperations_Batch_Account_Status_State
ON TransferOperations(BatchJobId, AssignedAccountId, Status, SubmitState, TargetSlotState);
CREATE UNIQUE INDEX UX_TransferOperations_ShareLineage
ON TransferOperations(Id, ResourceItemId, BatchJobId, AssignedAccountId);

CREATE INDEX IX_ExecutionLogs_Time ON ExecutionLogs(CreatedAt);
CREATE INDEX IX_ExecutionLogs_Batch_Time ON ExecutionLogs(BatchJobId, CreatedAt);
CREATE INDEX IX_ExecutionLogs_Account_Id ON ExecutionLogs(AccountId, Id) WHERE AccountId IS NOT NULL;
CREATE INDEX IX_ExecutionLogs_Resource_Id ON ExecutionLogs(ResourceItemId, Id) WHERE ResourceItemId IS NOT NULL;
CREATE INDEX IX_ExecutionLogs_Operation_Id ON ExecutionLogs(TransferOperationId, Id) WHERE TransferOperationId IS NOT NULL;

CREATE TABLE ShareOperations (
    Id TEXT PRIMARY KEY,
    TransferOperationId TEXT NOT NULL UNIQUE,
    ResourceItemId TEXT NOT NULL,
    BatchJobId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    OutputOrdinal INTEGER NOT NULL CHECK(typeof(OutputOrdinal)='integer' AND OutputOrdinal > 0),
    Status TEXT NOT NULL CHECK(Status IN ('WaitingTransfer','WaitingShare','Reserved','Preparing','Submitting','ResultPending','Succeeded','Failed','Paused','ReusedExisting','Cancelled')),
    ExactItemSetFingerprint TEXT NOT NULL,
    SettingsFingerprint TEXT NOT NULL CHECK(length(trim(SettingsFingerprint))>0),
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation > 0),
    RequestDescriptorJson TEXT NULL,
    AdapterRequestId TEXT NULL,
    SubmissionAcceptance TEXT NOT NULL CHECK(SubmissionAcceptance IN ('Unknown','DefinitelyNotAccepted')),
    AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK(typeof(AttemptCount)='integer' AND AttemptCount >= 0),
    LeaseGeneration INTEGER NOT NULL DEFAULT 0 CHECK(typeof(LeaseGeneration)='integer' AND LeaseGeneration >= 0),
    LeaseExpiresAt TEXT NULL,
    SubmitStartedAt TEXT NULL,
    NextReconcileAt TEXT NULL,
    BaselineGeneration INTEGER NULL CHECK(BaselineGeneration IS NULL OR (typeof(BaselineGeneration)='integer' AND BaselineGeneration > 0)),
    BaselineFingerprint TEXT NULL,
    BaselineShareCount INTEGER NOT NULL DEFAULT 0 CHECK(typeof(BaselineShareCount)='integer' AND BaselineShareCount >= 0),
    BaselineCompletedAt TEXT NULL,
    LastErrorCode TEXT NULL,
    LastErrorMessage TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(TransferOperationId) REFERENCES TransferOperations(Id) ON DELETE CASCADE,
    FOREIGN KEY(ResourceItemId) REFERENCES ResourceItems(Id) ON DELETE CASCADE,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE,
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id),
    FOREIGN KEY(BatchJobId,SettingsFingerprint) REFERENCES BatchShareSettings(BatchJobId,SettingsFingerprint),
    FOREIGN KEY(BatchJobId,AccountId,OutputOrdinal) REFERENCES BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal) ON DELETE CASCADE,
    FOREIGN KEY(TransferOperationId,ResourceItemId,BatchJobId,AccountId)
      REFERENCES TransferOperations(Id,ResourceItemId,BatchJobId,AssignedAccountId) ON DELETE CASCADE,
    CHECK((BaselineCompletedAt IS NULL AND BaselineGeneration IS NULL AND BaselineFingerprint IS NULL AND BaselineShareCount=0)
       OR (BaselineCompletedAt IS NOT NULL AND BaselineGeneration IS NOT NULL AND BaselineFingerprint IS NOT NULL))
);

CREATE UNIQUE INDEX UX_ShareOperations_ActiveCreateKey
ON ShareOperations(AccountId, ExactItemSetFingerprint, SettingsFingerprint, Generation)
WHERE Status IN ('WaitingShare','Reserved','Preparing','Submitting','ResultPending','Paused');
CREATE INDEX IX_ShareOperations_Status_Lease_Id
ON ShareOperations(Status, LeaseExpiresAt, Id);
CREATE INDEX IX_ShareOperations_Account_Status_Lease_Id
ON ShareOperations(AccountId, Status, LeaseExpiresAt, Id);
CREATE INDEX IX_ShareOperations_Batch_Status_Resource_Id
ON ShareOperations(BatchJobId, Status, ResourceItemId, Id);
CREATE INDEX IX_ShareOperations_Resource_Account_Status_Id
ON ShareOperations(ResourceItemId, AccountId, Status, Id);
CREATE INDEX IX_ShareOperations_Batch_Status_Lease_Output_Id
ON ShareOperations(BatchJobId, Status, LeaseExpiresAt, OutputOrdinal, Id);
CREATE INDEX IX_ShareOperations_Batch_Status_Output_Id_Lease
ON ShareOperations(BatchJobId, Status, OutputOrdinal, Id, LeaseExpiresAt);
CREATE INDEX IX_BatchJobs_Status_Queue_Created_Id
ON BatchJobs(Status, QueueOrder, CreatedAt, Id);

CREATE TRIGGER TR_ShareOperations_RequireEnabledPlannedOutput
BEFORE INSERT ON ShareOperations
BEGIN
    SELECT CASE WHEN NOT EXISTS(
        SELECT 1 FROM BatchOutputAccounts output
        WHERE output.BatchJobId=NEW.BatchJobId
          AND output.AccountId=NEW.AccountId
          AND output.OutputOrdinal=NEW.OutputOrdinal
          AND output.ShareEnabled=1
          AND output.CreatedShareOperationCount < output.PlannedShareCount)
      THEN RAISE(ABORT, 'confirmed output cannot accept share work') END;
END;

CREATE TRIGGER TR_ShareOperations_IncrementPlannedOutput
AFTER INSERT ON ShareOperations
BEGIN
    UPDATE BatchOutputAccounts SET CreatedShareOperationCount=CreatedShareOperationCount+1
    WHERE BatchJobId=NEW.BatchJobId AND AccountId=NEW.AccountId;
END;

CREATE TRIGGER TR_ShareOperations_DecrementPlannedOutput
AFTER DELETE ON ShareOperations
BEGIN
    UPDATE BatchOutputAccounts SET CreatedShareOperationCount=CreatedShareOperationCount-1
    WHERE BatchJobId=OLD.BatchJobId AND AccountId=OLD.AccountId;
END;

CREATE TABLE ShareOperationGenerations (
    ShareOperationId TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation > 0),
    BatchJobId TEXT NOT NULL,
    ResourceItemId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    ExactItemSetFingerprint TEXT NOT NULL,
    SettingsFingerprint TEXT NOT NULL,
    PRIMARY KEY(ShareOperationId,Generation),
    UNIQUE(ShareOperationId,Generation,BatchJobId,ResourceItemId,AccountId,ExactItemSetFingerprint,SettingsFingerprint),
    FOREIGN KEY(ShareOperationId) REFERENCES ShareOperations(Id) ON DELETE CASCADE
);

CREATE TRIGGER TR_ShareOperations_RecordInitialGeneration
AFTER INSERT ON ShareOperations
BEGIN
    INSERT INTO ShareOperationGenerations(ShareOperationId,Generation,BatchJobId,ResourceItemId,AccountId,
      ExactItemSetFingerprint,SettingsFingerprint)
    VALUES(NEW.Id,NEW.Generation,NEW.BatchJobId,NEW.ResourceItemId,NEW.AccountId,
      NEW.ExactItemSetFingerprint,NEW.SettingsFingerprint);
END;

CREATE TRIGGER TR_ShareOperations_RecordNextGeneration
AFTER UPDATE OF Generation ON ShareOperations
WHEN NEW.Generation <> OLD.Generation
BEGIN
    INSERT INTO ShareOperationGenerations(ShareOperationId,Generation,BatchJobId,ResourceItemId,AccountId,
      ExactItemSetFingerprint,SettingsFingerprint)
    VALUES(NEW.Id,NEW.Generation,NEW.BatchJobId,NEW.ResourceItemId,NEW.AccountId,
      NEW.ExactItemSetFingerprint,NEW.SettingsFingerprint);
END;

CREATE TRIGGER TR_ShareOperationGenerations_Immutable
BEFORE UPDATE ON ShareOperationGenerations
BEGIN
    SELECT RAISE(ABORT, 'share generation lineage is immutable');
END;

CREATE TABLE ShareOperationRemoteItems (
    ShareOperationId TEXT NOT NULL,
    ItemOrdinal INTEGER NOT NULL CHECK(typeof(ItemOrdinal)='integer' AND ItemOrdinal >= 0),
    RemoteItemId TEXT NOT NULL CHECK(length(trim(RemoteItemId))>0),
    DisplayName TEXT NOT NULL,
    PRIMARY KEY(ShareOperationId, RemoteItemId),
    UNIQUE(ShareOperationId, ItemOrdinal),
    FOREIGN KEY(ShareOperationId) REFERENCES ShareOperations(Id) ON DELETE CASCADE
);

CREATE INDEX IX_ShareOperationRemoteItems_Operation_Ordinal_Id
ON ShareOperationRemoteItems(ShareOperationId, ItemOrdinal, RemoteItemId);

CREATE TABLE ShareDiscoveryBaselines (
    ShareOperationId TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation > 0),
    ShareId TEXT NOT NULL CHECK(length(trim(ShareId))>0),
    PRIMARY KEY(ShareOperationId, Generation, ShareId),
    FOREIGN KEY(ShareOperationId) REFERENCES ShareOperations(Id) ON DELETE CASCADE
);

CREATE INDEX IX_ShareDiscoveryBaselines_Operation_ShareId
ON ShareDiscoveryBaselines(ShareOperationId, Generation, ShareId);

CREATE TABLE BaiduShareResults (
    Id TEXT PRIMARY KEY,
    ShareOperationId TEXT NOT NULL,
    BatchJobId TEXT NOT NULL,
    ResourceItemId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    ExactItemSetFingerprint TEXT NOT NULL,
    SettingsFingerprint TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation > 0),
    ShareId TEXT NOT NULL,
    ShareUrl TEXT NOT NULL,
    ExtractionCode TEXT NOT NULL,
    IsAccepted INTEGER NOT NULL CHECK(IsAccepted IN (0,1)),
    IsCurrent INTEGER NOT NULL CHECK(IsCurrent IN (0,1)),
    SharedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(ShareOperationId) REFERENCES ShareOperations(Id) ON DELETE CASCADE,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE,
    FOREIGN KEY(ResourceItemId) REFERENCES ResourceItems(Id) ON DELETE CASCADE,
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
    ,FOREIGN KEY(ShareOperationId,Generation,BatchJobId,ResourceItemId,AccountId,
      ExactItemSetFingerprint,SettingsFingerprint)
      REFERENCES ShareOperationGenerations(ShareOperationId,Generation,BatchJobId,ResourceItemId,AccountId,
        ExactItemSetFingerprint,SettingsFingerprint)
);

CREATE UNIQUE INDEX UX_BaiduShareResults_AcceptedCurrentSet
ON BaiduShareResults(AccountId, ExactItemSetFingerprint, SettingsFingerprint)
WHERE IsAccepted=1 AND IsCurrent=1;
CREATE UNIQUE INDEX UX_BaiduShareResults_Account_ShareId_Current
ON BaiduShareResults(AccountId, ShareId)
WHERE IsCurrent=1;
CREATE INDEX IX_BaiduShareResults_Operation_Generation
ON BaiduShareResults(ShareOperationId, Generation, Id);
CREATE INDEX IX_BaiduShareResults_Batch_Resource_Account_Id
ON BaiduShareResults(BatchJobId, ResourceItemId, AccountId, Id);

CREATE TABLE BatchShareDiscoverySnapshots (
    BatchJobId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    SettingsFingerprint TEXT NOT NULL,
    DiscoveryFingerprint TEXT NOT NULL,
    DiscoveredCount INTEGER NOT NULL CHECK(typeof(DiscoveredCount)='integer' AND DiscoveredCount >= 0),
    CompatibleCount INTEGER NOT NULL CHECK(typeof(CompatibleCount)='integer' AND CompatibleCount >= 0),
    IncompatibleCount INTEGER NOT NULL CHECK(typeof(IncompatibleCount)='integer' AND IncompatibleCount >= 0),
    Generation TEXT NOT NULL CHECK(length(trim(Generation))>0),
    CompletedAt TEXT NOT NULL,
    Version INTEGER NOT NULL CHECK(typeof(Version)='integer' AND Version > 0),
    PRIMARY KEY(BatchJobId,AccountId,SettingsFingerprint),
    UNIQUE(BatchJobId,AccountId,SettingsFingerprint,DiscoveryFingerprint,DiscoveredCount,
      CompatibleCount,IncompatibleCount,Generation,Version),
    FOREIGN KEY(BatchJobId,SettingsFingerprint) REFERENCES BatchShareSettings(BatchJobId,SettingsFingerprint),
    FOREIGN KEY(BatchJobId,AccountId) REFERENCES BatchOutputAccounts(BatchJobId,AccountId) ON DELETE CASCADE,
    CHECK(CompatibleCount + IncompatibleCount <= DiscoveredCount)
);

CREATE TABLE BatchShareDecisionSnapshots (
    BatchJobId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    SettingsFingerprint TEXT NOT NULL CHECK(length(trim(SettingsFingerprint))>0),
    Decision TEXT NOT NULL CHECK(Decision IN ('Reuse','Regenerate','Cancel')),
    DiscoveryFingerprint TEXT NOT NULL,
    DiscoveredCount INTEGER NOT NULL CHECK(typeof(DiscoveredCount)='integer' AND DiscoveredCount >= 0),
    CompatibleCount INTEGER NOT NULL CHECK(typeof(CompatibleCount)='integer' AND CompatibleCount >= 0),
    IncompatibleCount INTEGER NOT NULL CHECK(typeof(IncompatibleCount)='integer' AND IncompatibleCount >= 0),
    Generation TEXT NOT NULL,
    ConfirmedAt TEXT NOT NULL,
    DiscoveryVersion INTEGER NOT NULL CHECK(typeof(DiscoveryVersion)='integer' AND DiscoveryVersion > 0),
    PRIMARY KEY(BatchJobId, AccountId),
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE,
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id),
    FOREIGN KEY(BatchJobId,SettingsFingerprint) REFERENCES BatchShareSettings(BatchJobId,SettingsFingerprint),
    FOREIGN KEY(BatchJobId,AccountId) REFERENCES BatchOutputAccounts(BatchJobId,AccountId) ON DELETE CASCADE,
    FOREIGN KEY(BatchJobId,AccountId,SettingsFingerprint,DiscoveryFingerprint,DiscoveredCount,
      CompatibleCount,IncompatibleCount,Generation,DiscoveryVersion)
      REFERENCES BatchShareDiscoverySnapshots(BatchJobId,AccountId,SettingsFingerprint,DiscoveryFingerprint,
        DiscoveredCount,CompatibleCount,IncompatibleCount,Generation,Version),
    CHECK(CompatibleCount + IncompatibleCount <= DiscoveredCount)
);

CREATE INDEX IX_BatchShareDecisionSnapshots_Batch_Account
ON BatchShareDecisionSnapshots(BatchJobId, AccountId);

CREATE TABLE AdapterCapabilityAcceptances (
    AdapterKind TEXT NOT NULL,
    ProtocolVersion TEXT NOT NULL,
    AdapterBuildVersion TEXT NOT NULL,
    AcceptedAt TEXT NOT NULL,
    EvidenceHash TEXT NOT NULL CHECK(length(EvidenceHash)=64 AND EvidenceHash NOT GLOB '*[^0-9A-F]*'),
    PRIMARY KEY(AdapterKind, ProtocolVersion, AdapterBuildVersion)
);

CREATE INDEX IX_ResourceItems_Batch_Candidate_Export
ON ResourceItems(BatchJobId, CandidateSequence, Id);
