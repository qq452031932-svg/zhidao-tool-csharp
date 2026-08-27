CREATE TABLE BrowserInstallations (
    InstallationId TEXT PRIMARY KEY,
    BrowserFamily TEXT NOT NULL CHECK(BrowserFamily IN ('Chrome','Edge')),
    InstallationDirectory TEXT NOT NULL CHECK(length(trim(InstallationDirectory)) > 0),
    ExecutablePath TEXT NOT NULL CHECK(length(trim(ExecutablePath)) > 0),
    Publisher TEXT NOT NULL CHECK(length(trim(Publisher)) > 0),
    ProductName TEXT NOT NULL CHECK(length(trim(ProductName)) > 0),
    FileIdentity TEXT NOT NULL UNIQUE CHECK(length(trim(FileIdentity)) > 0),
    State TEXT NOT NULL CHECK(State IN ('Active','Retired')),
    Revision INTEGER NOT NULL DEFAULT 0 CHECK(typeof(Revision)='integer' AND Revision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    UNIQUE(InstallationId,BrowserFamily)
);

CREATE UNIQUE INDEX UX_BrowserInstallations_OneActive
ON BrowserInstallations(State) WHERE State='Active';

CREATE TABLE ManagedBrowserSessions (
    SessionId TEXT PRIMARY KEY,
    AccountId TEXT NOT NULL CHECK(
      length(AccountId)=36
      AND substr(AccountId,9,1)='-'
      AND substr(AccountId,14,1)='-'
      AND substr(AccountId,19,1)='-'
      AND substr(AccountId,24,1)='-'
      AND length(replace(AccountId,'-',''))=32
      AND lower(replace(AccountId,'-','')) NOT GLOB '*[^0-9a-f]*'),
    BaiduUserId TEXT NOT NULL CHECK(length(trim(BaiduUserId)) > 0),
    InstallationId TEXT NOT NULL,
    BrowserFamily TEXT NOT NULL CHECK(BrowserFamily IN ('Chrome','Edge')),
    ProfileId TEXT NOT NULL CHECK(length(trim(ProfileId)) > 0),
    ProfileOwnershipId TEXT NOT NULL CHECK(length(trim(ProfileOwnershipId)) > 0),
    State TEXT NOT NULL CHECK(State IN (
      'Active','Retired','QuarantinePending','Quarantined','CleanupPending','Deleted')),
    Revision INTEGER NOT NULL DEFAULT 0 CHECK(typeof(Revision)='integer' AND Revision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    UNIQUE(BrowserFamily,ProfileId),
    UNIQUE(SessionId,InstallationId),
    UNIQUE(SessionId,InstallationId,BrowserFamily),
    UNIQUE(SessionId,InstallationId,BrowserFamily,ProfileId,AccountId),
    UNIQUE(SessionId,AccountId,ProfileOwnershipId),
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id),
    FOREIGN KEY(InstallationId,BrowserFamily)
      REFERENCES BrowserInstallations(InstallationId,BrowserFamily)
);

CREATE UNIQUE INDEX UX_ManagedBrowserSessions_ActiveAccountFamily
ON ManagedBrowserSessions(AccountId,BrowserFamily) WHERE State='Active';

CREATE INDEX IX_ManagedBrowserSessions_Installation_State
ON ManagedBrowserSessions(InstallationId,State,SessionId);

CREATE TABLE ManagedProfileCleanupManifests (
    ManifestId TEXT PRIMARY KEY,
    AccountId TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    State TEXT NOT NULL CHECK(State IN (
      'Prepared','QuarantineMoveIntent','Quarantined','DeleteIntent','CleanupPending','Deleted')),
    SourceProfilePath TEXT NOT NULL CHECK(length(trim(SourceProfilePath)) > 0),
    QuarantinePath TEXT NOT NULL CHECK(length(trim(QuarantinePath)) > 0),
    ExpectedOwnershipId TEXT NOT NULL CHECK(length(trim(ExpectedOwnershipId)) > 0),
    ConfirmationTokenHash TEXT NULL,
    Revision INTEGER NOT NULL DEFAULT 0 CHECK(typeof(Revision)='integer' AND Revision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id),
    FOREIGN KEY(SessionId,AccountId,ExpectedOwnershipId)
      REFERENCES ManagedBrowserSessions(SessionId,AccountId,ProfileOwnershipId)
);

CREATE INDEX IX_ManagedProfileCleanupManifests_Session_State
ON ManagedProfileCleanupManifests(SessionId,State,UpdatedAt,ManifestId);

CREATE TABLE ManagedBrowserRetentions (
    OwnerKind TEXT NOT NULL CHECK(OwnerKind IN ('Operation','Attempt','CleanupManifest')),
    OwnerId TEXT NOT NULL,
    InstallationId TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    PRIMARY KEY(OwnerKind,OwnerId,InstallationId,SessionId),
    FOREIGN KEY(InstallationId) REFERENCES BrowserInstallations(InstallationId),
    FOREIGN KEY(SessionId,InstallationId)
      REFERENCES ManagedBrowserSessions(SessionId,InstallationId)
);

CREATE INDEX IX_ManagedBrowserRetentions_Identity
ON ManagedBrowserRetentions(InstallationId,SessionId,OwnerKind,OwnerId);

CREATE TABLE ManagedBrowserCleanupClaims (
    ManifestId TEXT PRIMARY KEY,
    InstallationId TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    ExpectedInstallationRevision INTEGER NOT NULL CHECK(ExpectedInstallationRevision >= 0),
    ExpectedSessionRevision INTEGER NOT NULL CHECK(ExpectedSessionRevision >= 0),
    ExpectedManifestRevision INTEGER NOT NULL CHECK(ExpectedManifestRevision >= 0),
    CreatedAt TEXT NOT NULL,
    UNIQUE(InstallationId,SessionId),
    FOREIGN KEY(ManifestId) REFERENCES ManagedProfileCleanupManifests(ManifestId),
    FOREIGN KEY(SessionId,InstallationId)
      REFERENCES ManagedBrowserSessions(SessionId,InstallationId),
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
);

CREATE INDEX IX_ManagedBrowserCleanupClaims_Identity
ON ManagedBrowserCleanupClaims(InstallationId,SessionId,ManifestId);

-- v15 intentionally had no polymorphic owner foreign key.  Classify every
-- orphan before deleting anything: only an exact DefinitelyNotSubmitted,
-- zero-evidence closure is safe to discard.  Any other orphan aborts the whole
-- migration transaction so recovery evidence is never silently destroyed.
CREATE TEMP TABLE V16OwnerlessWebAutomationAttempts (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL,
    OwnerKind TEXT NOT NULL,
    PRIMARY KEY(OperationId,AccountId,Generation)
) WITHOUT ROWID;

INSERT INTO V16OwnerlessWebAutomationAttempts(OperationId,AccountId,Generation,OwnerKind)
SELECT attempt.OperationId,attempt.AccountId,attempt.Generation,attempt.OwnerKind
FROM WebAutomationAttempts attempt
WHERE (attempt.OwnerKind='Transfer' AND NOT EXISTS(
         SELECT 1 FROM TransferOperations operation
         WHERE operation.Id=attempt.OperationId
           AND operation.AssignedAccountId=attempt.AccountId
           AND operation.AttemptCount=attempt.Generation))
   OR (attempt.OwnerKind='TransferShare' AND NOT EXISTS(
         SELECT 1 FROM ShareOperations operation
         WHERE operation.Id=attempt.OperationId
           AND operation.AccountId=attempt.AccountId
           AND operation.Generation=attempt.Generation))
   OR (attempt.OwnerKind='StandaloneShare' AND NOT EXISTS(
         SELECT 1 FROM StandaloneShareOperations operation
         WHERE operation.Id=attempt.OperationId
           AND operation.AccountId=attempt.AccountId
           AND operation.Generation=attempt.Generation))
   OR (attempt.OwnerKind='RemoteSelection' AND NOT EXISTS(
         SELECT 1 FROM Accounts account WHERE account.Id=attempt.AccountId));

CREATE TEMP TABLE V16OwnerlessWebAutomationAttemptValidation(
    Value INTEGER NOT NULL CHECK(Value=1)
);

INSERT INTO V16OwnerlessWebAutomationAttemptValidation(Value)
SELECT CASE WHEN NOT EXISTS(
  SELECT 1
  FROM V16OwnerlessWebAutomationAttempts orphan
  JOIN WebAutomationAttempts attempt
    ON attempt.OperationId=orphan.OperationId
   AND attempt.AccountId=orphan.AccountId
   AND attempt.Generation=orphan.Generation
  WHERE attempt.State<>'DefinitelyNotSubmitted'
     OR attempt.ResumeStage IS NOT NULL
     OR attempt.ObservationFingerprint IS NOT NULL
     OR attempt.BaselineFingerprint IS NOT NULL
     OR attempt.BaselineItemCount<>0
     OR attempt.BaselinePersistedAt IS NOT NULL
     OR attempt.IsWaitingForUser<>0
     OR attempt.AcceptanceFingerprint IS NOT NULL
     OR attempt.AcceptedItemCount<>0
     OR attempt.AcceptancePersistedAt IS NOT NULL
     OR attempt.SourceTotalCount IS NOT NULL
     OR attempt.SourceReachedEnd IS NOT NULL
     OR attempt.SourceIsStable IS NOT NULL
     OR attempt.SourceGenerationToken IS NOT NULL
     OR attempt.SourceFingerprint IS NOT NULL
     OR attempt.SourceObservationFingerprint IS NOT NULL
     OR attempt.BaiduUserId IS NOT NULL
     OR attempt.CapabilityAdapterKind IS NOT NULL
     OR attempt.CapabilityProtocolVersion IS NOT NULL
     OR attempt.CapabilitySelectorVersion IS NOT NULL
     OR attempt.CapabilityBuildVersion IS NOT NULL
     OR attempt.TargetExactId IS NOT NULL
     OR attempt.TargetPath IS NOT NULL
     OR attempt.TargetTotalCount IS NOT NULL
     OR attempt.TargetReachedEnd IS NOT NULL
     OR attempt.TargetIsStable IS NOT NULL
     OR attempt.TargetGenerationToken IS NOT NULL
     OR attempt.TargetObservationFingerprint IS NOT NULL
     OR attempt.SubmissionSourceFingerprint IS NOT NULL
     OR attempt.SubmissionTargetExactId IS NOT NULL
     OR attempt.SubmissionTargetBaselineFingerprint IS NOT NULL
     OR attempt.SubmissionMarkedAt IS NOT NULL
     OR attempt.OfficialRequestId IS NOT NULL
     OR attempt.TransferMappingFingerprint IS NOT NULL
     OR attempt.TransferMappingCount<>0
     OR EXISTS(
          SELECT 1 FROM WebAutomationAttemptSourceItems evidence
          WHERE evidence.OperationId=orphan.OperationId
            AND evidence.AccountId=orphan.AccountId
            AND evidence.Generation=orphan.Generation)
     OR EXISTS(
          SELECT 1 FROM WebAutomationDirectoryMutations evidence
          WHERE evidence.OperationId=orphan.OperationId
            AND evidence.AccountId=orphan.AccountId
            AND evidence.OriginGeneration=orphan.Generation)
     OR EXISTS(
          SELECT 1 FROM WebAutomationAttemptRemoteItems evidence
          WHERE evidence.OperationId=orphan.OperationId
            AND evidence.AccountId=orphan.AccountId
            AND evidence.Generation=orphan.Generation)
     OR EXISTS(
          SELECT 1 FROM WebAutomationTransferMappings evidence
          WHERE evidence.OperationId=orphan.OperationId
            AND evidence.AccountId=orphan.AccountId
            AND evidence.Generation=orphan.Generation)
     OR EXISTS(
          SELECT 1 FROM WebAutomationAttemptAcceptedRemoteItems evidence
          WHERE evidence.OperationId=orphan.OperationId
            AND evidence.AccountId=orphan.AccountId
            AND evidence.Generation=orphan.Generation)
     OR EXISTS(
          SELECT 1 FROM WebAutomationShareAcceptances evidence
          WHERE evidence.OperationId=orphan.OperationId
            AND evidence.AccountId=orphan.AccountId
            AND evidence.Generation=orphan.Generation)
) THEN 1 ELSE 0 END;

DROP TABLE V16OwnerlessWebAutomationAttemptValidation;

DELETE FROM WebAutomationDirectoryMutations
WHERE EXISTS(
  SELECT 1 FROM V16OwnerlessWebAutomationAttempts orphan
  WHERE orphan.OperationId=WebAutomationDirectoryMutations.OperationId
    AND orphan.AccountId=WebAutomationDirectoryMutations.AccountId
    AND orphan.Generation=WebAutomationDirectoryMutations.OriginGeneration);

DELETE FROM WebAutomationAttemptSourceItems
WHERE EXISTS(
  SELECT 1 FROM V16OwnerlessWebAutomationAttempts orphan
  WHERE orphan.OperationId=WebAutomationAttemptSourceItems.OperationId
    AND orphan.AccountId=WebAutomationAttemptSourceItems.AccountId
    AND orphan.Generation=WebAutomationAttemptSourceItems.Generation);

DELETE FROM WebAutomationAttemptRemoteItems
WHERE EXISTS(
  SELECT 1 FROM V16OwnerlessWebAutomationAttempts orphan
  WHERE orphan.OperationId=WebAutomationAttemptRemoteItems.OperationId
    AND orphan.AccountId=WebAutomationAttemptRemoteItems.AccountId
    AND orphan.Generation=WebAutomationAttemptRemoteItems.Generation);

DELETE FROM WebAutomationTransferMappings
WHERE EXISTS(
  SELECT 1 FROM V16OwnerlessWebAutomationAttempts orphan
  WHERE orphan.OperationId=WebAutomationTransferMappings.OperationId
    AND orphan.AccountId=WebAutomationTransferMappings.AccountId
    AND orphan.Generation=WebAutomationTransferMappings.Generation);

DELETE FROM WebAutomationAttemptAcceptedRemoteItems
WHERE EXISTS(
  SELECT 1 FROM V16OwnerlessWebAutomationAttempts orphan
  WHERE orphan.OperationId=WebAutomationAttemptAcceptedRemoteItems.OperationId
    AND orphan.AccountId=WebAutomationAttemptAcceptedRemoteItems.AccountId
    AND orphan.Generation=WebAutomationAttemptAcceptedRemoteItems.Generation);

DELETE FROM WebAutomationShareAcceptances
WHERE EXISTS(
  SELECT 1 FROM V16OwnerlessWebAutomationAttempts orphan
  WHERE orphan.OperationId=WebAutomationShareAcceptances.OperationId
    AND orphan.AccountId=WebAutomationShareAcceptances.AccountId
    AND orphan.Generation=WebAutomationShareAcceptances.Generation);

DELETE FROM WebAutomationAttempts
WHERE EXISTS(
  SELECT 1 FROM V16OwnerlessWebAutomationAttempts orphan
  WHERE orphan.OperationId=WebAutomationAttempts.OperationId
    AND orphan.AccountId=WebAutomationAttempts.AccountId
    AND orphan.Generation=WebAutomationAttempts.Generation);

DROP TABLE V16OwnerlessWebAutomationAttempts;

-- Stage and validate every v15 owner before altering any original table.  The
-- staging table deliberately has final non-null/check constraints so a bad
-- parent/account mapping aborts the surrounding migration transaction.
CREATE TEMP TABLE V16ExecutionSnapshotStaging (
    OwnerTable TEXT NOT NULL CHECK(OwnerTable IN (
      'TransferOperations','ShareOperations','StandaloneShareOperations','WebAutomationAttempts')),
    OwnerKey TEXT NOT NULL,
    AccountId TEXT NOT NULL CHECK(
      length(AccountId)=36
      AND substr(AccountId,9,1)='-'
      AND substr(AccountId,14,1)='-'
      AND substr(AccountId,19,1)='-'
      AND substr(AccountId,24,1)='-'
      AND length(replace(AccountId,'-',''))=32
      AND lower(replace(AccountId,'-','')) NOT GLOB '*[^0-9a-f]*'),
    ExecutionEngineVersion TEXT NOT NULL CHECK(ExecutionEngineVersion='WebView2-v1'),
    BrowserFamily TEXT NOT NULL CHECK(BrowserFamily='LegacyWebView2'),
    ProfileId TEXT NOT NULL CHECK(length(trim(ProfileId)) > 0),
    InstallationId TEXT NULL CHECK(InstallationId IS NULL),
    SessionId TEXT NULL CHECK(SessionId IS NULL),
    PRIMARY KEY(OwnerTable,OwnerKey)
);

INSERT INTO V16ExecutionSnapshotStaging(
  OwnerTable,OwnerKey,AccountId,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
SELECT 'TransferOperations',Id,AssignedAccountId,'WebView2-v1','LegacyWebView2',
       'legacy-webview2-account-' || lower(replace(AssignedAccountId,'-','')),NULL,NULL
FROM TransferOperations;

INSERT INTO V16ExecutionSnapshotStaging(
  OwnerTable,OwnerKey,AccountId,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
SELECT 'ShareOperations',Id,AccountId,'WebView2-v1','LegacyWebView2',
       'legacy-webview2-account-' || lower(replace(AccountId,'-','')),NULL,NULL
FROM ShareOperations;

INSERT INTO V16ExecutionSnapshotStaging(
  OwnerTable,OwnerKey,AccountId,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
SELECT 'StandaloneShareOperations',Id,AccountId,'WebView2-v1','LegacyWebView2',
       'legacy-webview2-account-' || lower(replace(AccountId,'-','')),NULL,NULL
FROM StandaloneShareOperations;

INSERT INTO V16ExecutionSnapshotStaging(
  OwnerTable,OwnerKey,AccountId,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
SELECT 'WebAutomationAttempts',
       attempt.OperationId || ':' || attempt.AccountId || ':' || printf('%d',attempt.Generation),
       attempt.AccountId,'WebView2-v1','LegacyWebView2',
       CASE attempt.OwnerKind
         WHEN 'Transfer' THEN (
           SELECT 'legacy-webview2-account-' || lower(replace(operation.AssignedAccountId,'-',''))
           FROM TransferOperations operation
           WHERE operation.Id=attempt.OperationId AND operation.AssignedAccountId=attempt.AccountId)
         WHEN 'TransferShare' THEN (
           SELECT 'legacy-webview2-account-' || lower(replace(operation.AccountId,'-',''))
           FROM ShareOperations operation
           WHERE operation.Id=attempt.OperationId AND operation.AccountId=attempt.AccountId)
         WHEN 'StandaloneShare' THEN (
           SELECT 'legacy-webview2-account-' || lower(replace(operation.AccountId,'-',''))
           FROM StandaloneShareOperations operation
           WHERE operation.Id=attempt.OperationId AND operation.AccountId=attempt.AccountId)
         WHEN 'RemoteSelection' THEN
           'legacy-webview2-account-' || lower(replace(attempt.AccountId,'-',''))
       END,
       NULL,NULL
FROM WebAutomationAttempts attempt;

CREATE TEMP TABLE V16ExecutionSnapshotValidation(Value INTEGER NOT NULL CHECK(Value=1));
INSERT INTO V16ExecutionSnapshotValidation(Value)
SELECT CASE WHEN
  (SELECT COUNT(*) FROM V16ExecutionSnapshotStaging WHERE OwnerTable='TransferOperations') =
    (SELECT COUNT(*) FROM TransferOperations)
  AND (SELECT COUNT(*) FROM V16ExecutionSnapshotStaging WHERE OwnerTable='ShareOperations') =
    (SELECT COUNT(*) FROM ShareOperations)
  AND (SELECT COUNT(*) FROM V16ExecutionSnapshotStaging WHERE OwnerTable='StandaloneShareOperations') =
    (SELECT COUNT(*) FROM StandaloneShareOperations)
  AND (SELECT COUNT(*) FROM V16ExecutionSnapshotStaging WHERE OwnerTable='WebAutomationAttempts') =
    (SELECT COUNT(*) FROM WebAutomationAttempts)
THEN 1 ELSE 0 END;

CREATE TABLE TransferOperations_v16 (
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
    ExecutionEngineVersion TEXT NOT NULL,
    BrowserFamily TEXT NOT NULL CHECK(BrowserFamily IN ('LegacyWebView2','Chrome','Edge')),
    ProfileId TEXT NOT NULL CHECK(length(trim(ProfileId)) > 0),
    InstallationId TEXT NULL, SessionId TEXT NULL,
    FOREIGN KEY(ResourceItemId) REFERENCES ResourceItems(Id) ON DELETE CASCADE,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE, FOREIGN KEY(AssignedAccountId) REFERENCES Accounts(Id),
    FOREIGN KEY(InstallationId) REFERENCES BrowserInstallations(InstallationId),
    FOREIGN KEY(SessionId,InstallationId,BrowserFamily,ProfileId,AssignedAccountId)
      REFERENCES ManagedBrowserSessions(SessionId,InstallationId,BrowserFamily,ProfileId,AccountId),
    CHECK(SubmitState IN ('NotPrepared','PreparingToSubmit') OR ConfirmationDescriptorJson IS NOT NULL),
    CHECK((ExecutionEngineVersion='WebView2-v1' AND BrowserFamily='LegacyWebView2'
             AND ProfileId='legacy-webview2-account-' || lower(replace(AssignedAccountId,'-',''))
             AND InstallationId IS NULL AND SessionId IS NULL)
       OR (ExecutionEngineVersion='ManagedChromium-v1' AND BrowserFamily IN ('Chrome','Edge')
             AND InstallationId IS NOT NULL AND SessionId IS NOT NULL))
);

INSERT INTO TransferOperations_v16(
  Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,
  AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,
  AdapterRequestId,RemoteItemIdsJson,SubmitState,TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,
  LastErrorCode,LastErrorMessage,LeaseExpiresAt,CreatedAt,UpdatedAt,
  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
SELECT operation.Id,operation.ResourceItemId,operation.BatchJobId,operation.SequenceNumber,operation.AllocationOrdinal,
  operation.TargetPath,operation.NormalizedTargetPath,operation.CreateFolderName,operation.AssignedAccountId,
  operation.AllocationSource,operation.Status,operation.OutcomeKind,operation.AttemptCount,operation.OperationFingerprint,
  operation.ConfirmationDescriptorJson,operation.AdapterRequestId,operation.RemoteItemIdsJson,operation.SubmitState,
  operation.TargetSlotState,operation.TargetSlotReservedAt,operation.DailyCountedAt,operation.SubmitStartedAt,
  operation.LastErrorCode,operation.LastErrorMessage,operation.LeaseExpiresAt,operation.CreatedAt,operation.UpdatedAt,
  staged.ExecutionEngineVersion,staged.BrowserFamily,staged.ProfileId,staged.InstallationId,staged.SessionId
FROM TransferOperations operation
JOIN V16ExecutionSnapshotStaging staged
  ON staged.OwnerTable='TransferOperations' AND staged.OwnerKey=operation.Id;

CREATE TABLE ShareOperations_v16 (
    Id TEXT PRIMARY KEY,
    TransferOperationId TEXT NOT NULL UNIQUE,
    ResourceItemId TEXT NOT NULL,
    BatchJobId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    OutputOrdinal INTEGER NOT NULL CHECK(typeof(OutputOrdinal)='integer' AND OutputOrdinal > 0),
    Status TEXT NOT NULL CHECK(Status IN ('WaitingTransfer','WaitingShare','Reserved','Preparing','Submitting','ResultPending','Succeeded','Failed','Paused','ReusedExisting','Cancelled')),
    ExactItemSetFingerprint TEXT NULL,
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
    ExecutionEngineVersion TEXT NOT NULL,
    BrowserFamily TEXT NOT NULL CHECK(BrowserFamily IN ('LegacyWebView2','Chrome','Edge')),
    ProfileId TEXT NOT NULL CHECK(length(trim(ProfileId)) > 0),
    InstallationId TEXT NULL, SessionId TEXT NULL,
    FOREIGN KEY(TransferOperationId) REFERENCES TransferOperations(Id) ON DELETE CASCADE,
    FOREIGN KEY(ResourceItemId) REFERENCES ResourceItems(Id) ON DELETE CASCADE,
    FOREIGN KEY(BatchJobId) REFERENCES BatchJobs(Id) ON DELETE CASCADE,
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id),
    FOREIGN KEY(BatchJobId,SettingsFingerprint) REFERENCES BatchShareSettings(BatchJobId,SettingsFingerprint),
    FOREIGN KEY(BatchJobId,AccountId,OutputOrdinal) REFERENCES BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal) ON DELETE CASCADE,
    FOREIGN KEY(TransferOperationId,ResourceItemId,BatchJobId,AccountId)
      REFERENCES TransferOperations(Id,ResourceItemId,BatchJobId,AssignedAccountId) ON DELETE CASCADE,
    FOREIGN KEY(InstallationId) REFERENCES BrowserInstallations(InstallationId),
    FOREIGN KEY(SessionId,InstallationId,BrowserFamily,ProfileId,AccountId)
      REFERENCES ManagedBrowserSessions(SessionId,InstallationId,BrowserFamily,ProfileId,AccountId),
    CHECK((Status='WaitingTransfer' AND ExactItemSetFingerprint IS NULL AND Generation=1
             AND RequestDescriptorJson IS NULL AND AdapterRequestId IS NULL
             AND SubmissionAcceptance='Unknown' AND AttemptCount=0 AND LeaseGeneration=0
             AND LeaseExpiresAt IS NULL AND SubmitStartedAt IS NULL AND NextReconcileAt IS NULL)
       OR (Status<>'WaitingTransfer' AND ExactItemSetFingerprint IS NOT NULL
             AND length(trim(ExactItemSetFingerprint))>0)),
    CHECK((BaselineCompletedAt IS NULL AND BaselineGeneration IS NULL AND BaselineFingerprint IS NULL AND BaselineShareCount=0)
       OR (BaselineCompletedAt IS NOT NULL AND BaselineGeneration IS NOT NULL AND BaselineFingerprint IS NOT NULL)),
    CHECK((ExecutionEngineVersion='WebView2-v1' AND BrowserFamily='LegacyWebView2'
             AND ProfileId='legacy-webview2-account-' || lower(replace(AccountId,'-',''))
             AND InstallationId IS NULL AND SessionId IS NULL)
       OR (ExecutionEngineVersion='ManagedChromium-v1' AND BrowserFamily IN ('Chrome','Edge')
             AND InstallationId IS NOT NULL AND SessionId IS NOT NULL))
);

INSERT INTO ShareOperations_v16(
  Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,ExactItemSetFingerprint,
  SettingsFingerprint,Generation,RequestDescriptorJson,AdapterRequestId,SubmissionAcceptance,AttemptCount,
  LeaseGeneration,LeaseExpiresAt,SubmitStartedAt,NextReconcileAt,BaselineGeneration,BaselineFingerprint,
  BaselineShareCount,BaselineCompletedAt,LastErrorCode,LastErrorMessage,CreatedAt,UpdatedAt,
  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
SELECT operation.Id,operation.TransferOperationId,operation.ResourceItemId,operation.BatchJobId,operation.AccountId,
  operation.OutputOrdinal,operation.Status,operation.ExactItemSetFingerprint,operation.SettingsFingerprint,
  operation.Generation,operation.RequestDescriptorJson,operation.AdapterRequestId,operation.SubmissionAcceptance,
  operation.AttemptCount,operation.LeaseGeneration,operation.LeaseExpiresAt,operation.SubmitStartedAt,
  operation.NextReconcileAt,operation.BaselineGeneration,operation.BaselineFingerprint,operation.BaselineShareCount,
  operation.BaselineCompletedAt,operation.LastErrorCode,operation.LastErrorMessage,operation.CreatedAt,operation.UpdatedAt,
  staged.ExecutionEngineVersion,staged.BrowserFamily,staged.ProfileId,staged.InstallationId,staged.SessionId
FROM ShareOperations operation
JOIN V16ExecutionSnapshotStaging staged
  ON staged.OwnerTable='ShareOperations' AND staged.OwnerKey=operation.Id;

CREATE TABLE StandaloneShareOperations_v16 (
    Id TEXT PRIMARY KEY,
    BatchId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    SequenceNumber INTEGER NOT NULL CHECK(SequenceNumber > 0),
    DisplayName TEXT NOT NULL,
    ResourceKind TEXT NOT NULL CHECK(ResourceKind IN ('File','Folder')),
    ParentPath TEXT NOT NULL,
    ExactItemSetFingerprint TEXT NOT NULL,
    Generation INTEGER NOT NULL DEFAULT 1 CHECK(Generation > 0),
    Status TEXT NOT NULL CHECK(Status IN ('Queued','Reserved','Preparing','Submitting','ResultPending','NeedsDecision','Succeeded','Failed','Paused','Cancelled')),
    SubmissionState TEXT NOT NULL CHECK(SubmissionState IN ('NotStarted','Prepared','DefinitelyNotAccepted','Accepted','ResultPending','Completed')),
    SubmissionAcceptance TEXT NOT NULL DEFAULT 'Unknown' CHECK(SubmissionAcceptance IN ('Unknown','DefinitelyNotAccepted')),
    BaselineFingerprint TEXT NULL,
    BaselineShareCount INTEGER NULL CHECK(BaselineShareCount IS NULL OR BaselineShareCount >= 0),
    BaselineCompletedAt TEXT NULL,
    LeaseGeneration INTEGER NOT NULL DEFAULT 0 CHECK(LeaseGeneration >= 0),
    LeaseOwnerId TEXT NULL,
    LeaseExpiresAt TEXT NULL,
    LastErrorCode TEXT NULL,
    Revision INTEGER NOT NULL DEFAULT 0 CHECK(Revision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ExtractionCodeMode TEXT NOT NULL DEFAULT 'Random' CHECK(ExtractionCodeMode IN ('Random','Fixed')),
    FixedExtractionCode TEXT NULL,
    ExistingShareDecision TEXT NOT NULL DEFAULT 'Reuse' CHECK(ExistingShareDecision IN ('Reuse','Regenerate','Cancel')),
    SettingsFingerprint TEXT NOT NULL DEFAULT '1b46ee3ae6069c21a5d785c56769c60e765b13c33eaec6d3aadae142c19f586a' CHECK(length(trim(SettingsFingerprint)) > 0),
    RequestDescriptorJson TEXT NULL,
    SubmitStartedAt TEXT NULL,
    AdapterRequestId TEXT NULL,
    AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK(AttemptCount >= 0),
    NextReconcileAt TEXT NULL,
    ReconciliationIssue TEXT NULL,
    ExecutionEngineVersion TEXT NOT NULL,
    BrowserFamily TEXT NOT NULL CHECK(BrowserFamily IN ('LegacyWebView2','Chrome','Edge')),
    ProfileId TEXT NOT NULL CHECK(length(trim(ProfileId)) > 0),
    InstallationId TEXT NULL, SessionId TEXT NULL,
    FOREIGN KEY(BatchId) REFERENCES StandaloneShareBatches(Id) ON DELETE CASCADE,
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id),
    FOREIGN KEY(InstallationId) REFERENCES BrowserInstallations(InstallationId),
    FOREIGN KEY(SessionId,InstallationId,BrowserFamily,ProfileId,AccountId)
      REFERENCES ManagedBrowserSessions(SessionId,InstallationId,BrowserFamily,ProfileId,AccountId),
    UNIQUE(BatchId, SequenceNumber),
    UNIQUE(Id, BatchId, AccountId, Generation),
    CHECK((BaselineCompletedAt IS NULL AND BaselineFingerprint IS NULL AND BaselineShareCount IS NULL)
       OR (BaselineCompletedAt IS NOT NULL AND BaselineFingerprint IS NOT NULL AND BaselineShareCount IS NOT NULL)),
    CHECK((LeaseOwnerId IS NULL AND LeaseExpiresAt IS NULL)
       OR (LeaseOwnerId IS NOT NULL AND LeaseExpiresAt IS NOT NULL)),
    CHECK((ExecutionEngineVersion='WebView2-v1' AND BrowserFamily='LegacyWebView2'
             AND ProfileId='legacy-webview2-account-' || lower(replace(AccountId,'-',''))
             AND InstallationId IS NULL AND SessionId IS NULL)
       OR (ExecutionEngineVersion='ManagedChromium-v1' AND BrowserFamily IN ('Chrome','Edge')
             AND InstallationId IS NOT NULL AND SessionId IS NOT NULL))
);

INSERT INTO StandaloneShareOperations_v16(
  Id,BatchId,AccountId,SequenceNumber,DisplayName,ResourceKind,ParentPath,ExactItemSetFingerprint,
  Generation,Status,SubmissionState,SubmissionAcceptance,BaselineFingerprint,BaselineShareCount,
  BaselineCompletedAt,LeaseGeneration,LeaseOwnerId,LeaseExpiresAt,LastErrorCode,Revision,CreatedAt,UpdatedAt,
  ExtractionCodeMode,FixedExtractionCode,ExistingShareDecision,SettingsFingerprint,RequestDescriptorJson,
  SubmitStartedAt,AdapterRequestId,AttemptCount,NextReconcileAt,ReconciliationIssue,
  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
SELECT operation.Id,operation.BatchId,operation.AccountId,operation.SequenceNumber,operation.DisplayName,
  operation.ResourceKind,operation.ParentPath,operation.ExactItemSetFingerprint,operation.Generation,operation.Status,
  operation.SubmissionState,operation.SubmissionAcceptance,operation.BaselineFingerprint,operation.BaselineShareCount,
  operation.BaselineCompletedAt,operation.LeaseGeneration,operation.LeaseOwnerId,operation.LeaseExpiresAt,
  operation.LastErrorCode,operation.Revision,operation.CreatedAt,operation.UpdatedAt,operation.ExtractionCodeMode,
  operation.FixedExtractionCode,operation.ExistingShareDecision,operation.SettingsFingerprint,
  operation.RequestDescriptorJson,operation.SubmitStartedAt,operation.AdapterRequestId,operation.AttemptCount,
  operation.NextReconcileAt,operation.ReconciliationIssue,
  staged.ExecutionEngineVersion,staged.BrowserFamily,staged.ProfileId,staged.InstallationId,staged.SessionId
FROM StandaloneShareOperations operation
JOIN V16ExecutionSnapshotStaging staged
  ON staged.OwnerTable='StandaloneShareOperations' AND staged.OwnerKey=operation.Id;

CREATE TABLE WebAutomationAttempts_v16 (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK(Generation > 0),
    OwnerKind TEXT NOT NULL CHECK(OwnerKind IN ('Transfer','TransferShare','StandaloneShare','RemoteSelection')),
    State TEXT NOT NULL CHECK(State IN ('Preparing','BaselinePersisted','Submitting','ResultPending','UserHandoff','Succeeded','DefinitelyNotSubmitted','NeedsDecision')),
    ResumeStage TEXT NULL CHECK(ResumeStage IS NULL OR ResumeStage IN ('Preparing','BaselinePersisted','Submitting','ResultPending')),
    ObservationFingerprint TEXT NULL,
    BaselineFingerprint TEXT NULL,
    BaselineItemCount INTEGER NOT NULL DEFAULT 0 CHECK(BaselineItemCount >= 0),
    BaselinePersistedAt TEXT NULL,
    IsWaitingForUser INTEGER NOT NULL DEFAULT 0 CHECK(IsWaitingForUser IN (0,1)),
    Revision INTEGER NOT NULL DEFAULT 0 CHECK(Revision >= 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    AcceptanceFingerprint TEXT NULL,
    AcceptedItemCount INTEGER NOT NULL DEFAULT 0 CHECK(AcceptedItemCount >= 0),
    AcceptancePersistedAt TEXT NULL,
    SourceTotalCount INTEGER NULL CHECK(SourceTotalCount IS NULL OR SourceTotalCount >= 0),
    SourceReachedEnd INTEGER NULL CHECK(SourceReachedEnd IS NULL OR SourceReachedEnd IN (0,1)),
    SourceIsStable INTEGER NULL CHECK(SourceIsStable IS NULL OR SourceIsStable IN (0,1)),
    SourceGenerationToken TEXT NULL,
    SourceFingerprint TEXT NULL,
    SourceObservationFingerprint TEXT NULL,
    BaiduUserId TEXT NULL,
    CapabilityAdapterKind TEXT NULL,
    CapabilityProtocolVersion TEXT NULL,
    CapabilitySelectorVersion TEXT NULL,
    CapabilityBuildVersion TEXT NULL,
    TargetExactId TEXT NULL,
    TargetPath TEXT NULL,
    TargetTotalCount INTEGER NULL CHECK(TargetTotalCount IS NULL OR TargetTotalCount >= 0),
    TargetReachedEnd INTEGER NULL CHECK(TargetReachedEnd IS NULL OR TargetReachedEnd IN (0,1)),
    TargetIsStable INTEGER NULL CHECK(TargetIsStable IS NULL OR TargetIsStable IN (0,1)),
    TargetGenerationToken TEXT NULL,
    TargetObservationFingerprint TEXT NULL,
    SubmissionSourceFingerprint TEXT NULL,
    SubmissionTargetExactId TEXT NULL,
    SubmissionTargetBaselineFingerprint TEXT NULL,
    SubmissionMarkedAt TEXT NULL,
    OfficialRequestId TEXT NULL,
    TransferMappingFingerprint TEXT NULL,
    TransferMappingCount INTEGER NOT NULL DEFAULT 0 CHECK(TransferMappingCount >= 0),
    ExecutionEngineVersion TEXT NOT NULL,
    BrowserFamily TEXT NOT NULL CHECK(BrowserFamily IN ('LegacyWebView2','Chrome','Edge')),
    ProfileId TEXT NOT NULL CHECK(length(trim(ProfileId)) > 0),
    InstallationId TEXT NULL, SessionId TEXT NULL,
    PRIMARY KEY(OperationId, AccountId, Generation),
    FOREIGN KEY(InstallationId) REFERENCES BrowserInstallations(InstallationId),
    FOREIGN KEY(SessionId,InstallationId,BrowserFamily,ProfileId,AccountId)
      REFERENCES ManagedBrowserSessions(SessionId,InstallationId,BrowserFamily,ProfileId,AccountId),
    CHECK(State NOT IN ('BaselinePersisted','Submitting','ResultPending','Succeeded')
       OR (BaselineFingerprint IS NOT NULL AND BaselinePersistedAt IS NOT NULL)),
    CHECK((State='UserHandoff' AND ResumeStage IS NOT NULL AND IsWaitingForUser=1)
       OR (State<>'UserHandoff' AND ResumeStage IS NULL)),
    CHECK(State<>'UserHandoff' OR ResumeStage='Preparing'
       OR (BaselineFingerprint IS NOT NULL AND BaselinePersistedAt IS NOT NULL)),
    CHECK((ExecutionEngineVersion='WebView2-v1' AND BrowserFamily='LegacyWebView2'
             AND ProfileId='legacy-webview2-account-' || lower(replace(AccountId,'-',''))
             AND InstallationId IS NULL AND SessionId IS NULL)
       OR (ExecutionEngineVersion='ManagedChromium-v1' AND BrowserFamily IN ('Chrome','Edge')
             AND InstallationId IS NOT NULL AND SessionId IS NOT NULL))
);

INSERT INTO WebAutomationAttempts_v16(
  OperationId,AccountId,Generation,OwnerKind,State,ResumeStage,ObservationFingerprint,
  BaselineFingerprint,BaselineItemCount,BaselinePersistedAt,IsWaitingForUser,Revision,CreatedAt,UpdatedAt,
  AcceptanceFingerprint,AcceptedItemCount,AcceptancePersistedAt,
  SourceTotalCount,SourceReachedEnd,SourceIsStable,SourceGenerationToken,SourceFingerprint,
  SourceObservationFingerprint,BaiduUserId,CapabilityAdapterKind,CapabilityProtocolVersion,
  CapabilitySelectorVersion,CapabilityBuildVersion,TargetExactId,TargetPath,TargetTotalCount,
  TargetReachedEnd,TargetIsStable,TargetGenerationToken,TargetObservationFingerprint,
  SubmissionSourceFingerprint,SubmissionTargetExactId,SubmissionTargetBaselineFingerprint,SubmissionMarkedAt,
  OfficialRequestId,TransferMappingFingerprint,TransferMappingCount,
  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
SELECT attempt.OperationId,attempt.AccountId,attempt.Generation,attempt.OwnerKind,attempt.State,attempt.ResumeStage,
  attempt.ObservationFingerprint,attempt.BaselineFingerprint,attempt.BaselineItemCount,attempt.BaselinePersistedAt,
  attempt.IsWaitingForUser,attempt.Revision,attempt.CreatedAt,attempt.UpdatedAt,attempt.AcceptanceFingerprint,
  attempt.AcceptedItemCount,attempt.AcceptancePersistedAt,attempt.SourceTotalCount,attempt.SourceReachedEnd,
  attempt.SourceIsStable,attempt.SourceGenerationToken,attempt.SourceFingerprint,attempt.SourceObservationFingerprint,
  attempt.BaiduUserId,attempt.CapabilityAdapterKind,attempt.CapabilityProtocolVersion,attempt.CapabilitySelectorVersion,
  attempt.CapabilityBuildVersion,attempt.TargetExactId,attempt.TargetPath,attempt.TargetTotalCount,attempt.TargetReachedEnd,
  attempt.TargetIsStable,attempt.TargetGenerationToken,attempt.TargetObservationFingerprint,
  attempt.SubmissionSourceFingerprint,attempt.SubmissionTargetExactId,attempt.SubmissionTargetBaselineFingerprint,
  attempt.SubmissionMarkedAt,attempt.OfficialRequestId,attempt.TransferMappingFingerprint,attempt.TransferMappingCount,
  staged.ExecutionEngineVersion,staged.BrowserFamily,staged.ProfileId,staged.InstallationId,staged.SessionId
FROM WebAutomationAttempts attempt
JOIN V16ExecutionSnapshotStaging staged
  ON staged.OwnerTable='WebAutomationAttempts'
 AND staged.OwnerKey=attempt.OperationId || ':' || attempt.AccountId || ':' || printf('%d',attempt.Generation);

INSERT INTO V16ExecutionSnapshotValidation(Value)
SELECT CASE WHEN
  (SELECT COUNT(*) FROM TransferOperations)=(SELECT COUNT(*) FROM TransferOperations_v16)
  AND (SELECT COUNT(*) FROM ShareOperations)=(SELECT COUNT(*) FROM ShareOperations_v16)
  AND (SELECT COUNT(*) FROM StandaloneShareOperations)=(SELECT COUNT(*) FROM StandaloneShareOperations_v16)
  AND (SELECT COUNT(*) FROM WebAutomationAttempts)=(SELECT COUNT(*) FROM WebAutomationAttempts_v16)
THEN 1 ELSE 0 END;

DROP TRIGGER TR_ShareOperationRemoteItems_SealDelete;
DROP TABLE WebAutomationAttempts;
DROP TABLE StandaloneShareOperations;
DROP TABLE ShareOperations;
DROP TABLE TransferOperations;
ALTER TABLE TransferOperations_v16 RENAME TO TransferOperations;
ALTER TABLE ShareOperations_v16 RENAME TO ShareOperations;
ALTER TABLE StandaloneShareOperations_v16 RENAME TO StandaloneShareOperations;
ALTER TABLE WebAutomationAttempts_v16 RENAME TO WebAutomationAttempts;

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

CREATE UNIQUE INDEX UX_ShareOperations_ActiveCreateKey
ON ShareOperations(AccountId, ExactItemSetFingerprint, SettingsFingerprint, Generation)
WHERE Status IN ('WaitingShare','Reserved','Preparing','Submitting','ResultPending','Paused');
CREATE INDEX IX_ShareOperations_Status_Lease_Id
ON ShareOperations(Status, LeaseExpiresAt, Id);
CREATE INDEX IX_ShareOperations_Account_Status_Lease_Id
ON ShareOperations(AccountId, Status, LeaseExpiresAt, Id);
CREATE INDEX IX_ShareOperations_Batch_Status_Lease_Output_Id
ON ShareOperations(BatchJobId, Status, LeaseExpiresAt, OutputOrdinal, Id);

CREATE INDEX IX_StandaloneShareOperations_Lease
ON StandaloneShareOperations(Status, LeaseExpiresAt, BatchId, SequenceNumber, Id);
CREATE INDEX IX_StandaloneShareOperations_Batch_Status
ON StandaloneShareOperations(BatchId, Status, SequenceNumber, Id);
CREATE INDEX IX_StandaloneShareOperations_Account_Status
ON StandaloneShareOperations(AccountId, Status, LeaseExpiresAt, Id);
CREATE INDEX IX_StandaloneShareOperations_Reconciliation
ON StandaloneShareOperations(Status,NextReconcileAt,BatchId,SequenceNumber,Id);

CREATE INDEX IX_WebAutomationAttempts_Recovery
ON WebAutomationAttempts(State,IsWaitingForUser,UpdatedAt,OperationId);

CREATE TRIGGER TR_ManagedBrowserSessions_StableUid_Insert
BEFORE INSERT ON ManagedBrowserSessions
WHEN NOT EXISTS(
  SELECT 1 FROM Accounts account
  WHERE account.Id=NEW.AccountId AND account.BaiduUserId=NEW.BaiduUserId)
BEGIN
    SELECT RAISE(ABORT, 'managed browser session account identity mismatch');
END;

CREATE TRIGGER TR_ManagedBrowserSessions_IdentityImmutable
BEFORE UPDATE OF AccountId,BaiduUserId,InstallationId,BrowserFamily,ProfileId,ProfileOwnershipId
ON ManagedBrowserSessions
WHEN NEW.AccountId IS NOT OLD.AccountId
  OR NEW.BaiduUserId IS NOT OLD.BaiduUserId
  OR NEW.InstallationId IS NOT OLD.InstallationId
  OR NEW.BrowserFamily IS NOT OLD.BrowserFamily
  OR NEW.ProfileId IS NOT OLD.ProfileId
  OR NEW.ProfileOwnershipId IS NOT OLD.ProfileOwnershipId
BEGIN
    SELECT RAISE(ABORT, 'managed browser session identity is immutable');
END;

CREATE TRIGGER TR_ManagedProfileCleanupManifests_Prepared_Insert
BEFORE INSERT ON ManagedProfileCleanupManifests
WHEN NEW.State<>'Prepared' OR NEW.ConfirmationTokenHash IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'cleanup manifest must begin prepared without confirmation');
END;

CREATE TRIGGER TR_ManagedBrowserCleanupClaims_Validate_Insert
BEFORE INSERT ON ManagedBrowserCleanupClaims
WHEN NOT EXISTS(
       SELECT 1 FROM BrowserInstallations installation
       WHERE installation.InstallationId=NEW.InstallationId
         AND installation.Revision=NEW.ExpectedInstallationRevision)
  OR NOT EXISTS(
       SELECT 1 FROM ManagedBrowserSessions session
       WHERE session.SessionId=NEW.SessionId AND session.InstallationId=NEW.InstallationId
         AND session.AccountId=NEW.AccountId AND session.State='Retired'
         AND session.Revision=NEW.ExpectedSessionRevision)
  OR NOT EXISTS(
       SELECT 1 FROM ManagedProfileCleanupManifests manifest
       WHERE manifest.ManifestId=NEW.ManifestId AND manifest.SessionId=NEW.SessionId
         AND manifest.AccountId=NEW.AccountId AND manifest.State='Prepared'
         AND manifest.Revision=NEW.ExpectedManifestRevision
         AND manifest.ConfirmationTokenHash IS NULL)
  OR EXISTS(
       SELECT 1 FROM TransferOperations operation
       WHERE operation.InstallationId=NEW.InstallationId AND operation.SessionId=NEW.SessionId)
  OR EXISTS(
       SELECT 1 FROM ShareOperations operation
       WHERE operation.InstallationId=NEW.InstallationId AND operation.SessionId=NEW.SessionId)
  OR EXISTS(
       SELECT 1 FROM StandaloneShareOperations operation
       WHERE operation.InstallationId=NEW.InstallationId AND operation.SessionId=NEW.SessionId)
  OR EXISTS(
       SELECT 1 FROM WebAutomationAttempts attempt
       WHERE attempt.InstallationId=NEW.InstallationId AND attempt.SessionId=NEW.SessionId)
  OR EXISTS(
       SELECT 1 FROM ManagedProfileCleanupManifests manifest
       WHERE manifest.SessionId=NEW.SessionId AND manifest.State<>'Deleted'
         AND manifest.ManifestId<>NEW.ManifestId)
  OR EXISTS(
       SELECT 1 FROM ManagedBrowserRetentions retention
       WHERE retention.InstallationId=NEW.InstallationId AND retention.SessionId=NEW.SessionId
         AND NOT(retention.OwnerKind='CleanupManifest' AND retention.OwnerId=NEW.ManifestId))
BEGIN
    SELECT RAISE(ABORT, 'managed browser cleanup claim is not eligible');
END;

CREATE TRIGGER TR_TransferOperations_RejectCleanupClaim_Insert
BEFORE INSERT ON TransferOperations
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1' AND (
  NOT EXISTS(
    SELECT 1 FROM ManagedBrowserSessions session
    WHERE session.SessionId=NEW.SessionId AND session.InstallationId=NEW.InstallationId
      AND session.State IN ('Active','Retired'))
  OR EXISTS(
    SELECT 1 FROM ManagedBrowserCleanupClaims claim
    WHERE claim.InstallationId=NEW.InstallationId AND claim.SessionId=NEW.SessionId))
BEGIN
    SELECT RAISE(ABORT, 'managed browser identity is fenced for cleanup');
END;

CREATE TRIGGER TR_ShareOperations_RejectCleanupClaim_Insert
BEFORE INSERT ON ShareOperations
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1' AND (
  NOT EXISTS(
    SELECT 1 FROM ManagedBrowserSessions session
    WHERE session.SessionId=NEW.SessionId AND session.InstallationId=NEW.InstallationId
      AND session.State IN ('Active','Retired'))
  OR EXISTS(
    SELECT 1 FROM ManagedBrowserCleanupClaims claim
    WHERE claim.InstallationId=NEW.InstallationId AND claim.SessionId=NEW.SessionId))
BEGIN
    SELECT RAISE(ABORT, 'managed browser identity is fenced for cleanup');
END;

CREATE TRIGGER TR_StandaloneShareOperations_RejectCleanupClaim_Insert
BEFORE INSERT ON StandaloneShareOperations
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1' AND (
  NOT EXISTS(
    SELECT 1 FROM ManagedBrowserSessions session
    WHERE session.SessionId=NEW.SessionId AND session.InstallationId=NEW.InstallationId
      AND session.State IN ('Active','Retired'))
  OR EXISTS(
    SELECT 1 FROM ManagedBrowserCleanupClaims claim
    WHERE claim.InstallationId=NEW.InstallationId AND claim.SessionId=NEW.SessionId))
BEGIN
    SELECT RAISE(ABORT, 'managed browser identity is fenced for cleanup');
END;

CREATE TRIGGER TR_WebAutomationAttempts_RejectCleanupClaim_Insert
BEFORE INSERT ON WebAutomationAttempts
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1' AND (
  NOT EXISTS(
    SELECT 1 FROM ManagedBrowserSessions session
    WHERE session.SessionId=NEW.SessionId AND session.InstallationId=NEW.InstallationId
      AND session.State IN ('Active','Retired'))
  OR EXISTS(
    SELECT 1 FROM ManagedBrowserCleanupClaims claim
    WHERE claim.InstallationId=NEW.InstallationId AND claim.SessionId=NEW.SessionId))
BEGIN
    SELECT RAISE(ABORT, 'managed browser identity is fenced for cleanup');
END;

CREATE TRIGGER TR_ManagedProfileCleanupManifests_RejectCleanupClaim_Insert
BEFORE INSERT ON ManagedProfileCleanupManifests
WHEN EXISTS(
  SELECT 1 FROM ManagedBrowserSessions session
  JOIN ManagedBrowserCleanupClaims claim
    ON claim.InstallationId=session.InstallationId AND claim.SessionId=session.SessionId
  WHERE session.SessionId=NEW.SessionId)
BEGIN
    SELECT RAISE(ABORT, 'managed browser identity is fenced for cleanup');
END;

CREATE TRIGGER TR_TransferOperations_ExecutionSnapshotImmutable
BEFORE UPDATE OF ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId
ON TransferOperations
WHEN NEW.ExecutionEngineVersion IS NOT OLD.ExecutionEngineVersion
  OR NEW.BrowserFamily IS NOT OLD.BrowserFamily
  OR NEW.ProfileId IS NOT OLD.ProfileId
  OR NEW.InstallationId IS NOT OLD.InstallationId
  OR NEW.SessionId IS NOT OLD.SessionId
BEGIN
    SELECT CASE WHEN NOT (
      OLD.ExecutionEngineVersion='WebView2-v1' AND NEW.ExecutionEngineVersion='WebView2-v1'
      AND OLD.BrowserFamily='LegacyWebView2' AND NEW.BrowserFamily='LegacyWebView2'
      AND OLD.InstallationId IS NULL AND NEW.InstallationId IS NULL
      AND OLD.SessionId IS NULL AND NEW.SessionId IS NULL
      AND OLD.AssignedAccountId<>NEW.AssignedAccountId
      AND OLD.ProfileId='legacy-webview2-account-' || lower(replace(OLD.AssignedAccountId,'-',''))
      AND NEW.ProfileId='legacy-webview2-account-' || lower(replace(NEW.AssignedAccountId,'-',''))
      AND OLD.Status='Waiting' AND NEW.Status='Waiting'
      AND OLD.SubmitState='NotPrepared' AND NEW.SubmitState='NotPrepared'
      AND OLD.AttemptCount=0 AND NEW.AttemptCount=0
      AND OLD.DailyCountedAt IS NULL AND NEW.DailyCountedAt IS NULL
      AND OLD.ConfirmationDescriptorJson IS NULL AND NEW.ConfirmationDescriptorJson IS NULL
      AND OLD.SubmitStartedAt IS NULL AND NEW.SubmitStartedAt IS NULL
      AND OLD.AdapterRequestId IS NULL AND NEW.AdapterRequestId IS NULL
      AND OLD.RemoteItemIdsJson IS NULL AND NEW.RemoteItemIdsJson IS NULL
      AND NOT EXISTS(
        SELECT 1 FROM WebAutomationAttempts attempt WHERE attempt.OperationId=OLD.Id)
      AND NOT EXISTS(
        SELECT 1 FROM WebAutomationDirectoryMutations mutation
        WHERE mutation.OperationId=OLD.Id)
    ) THEN RAISE(ABORT, 'execution snapshot is immutable') END;
END;

CREATE TRIGGER TR_ShareOperations_ExecutionSnapshotImmutable
BEFORE UPDATE OF ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId
ON ShareOperations
WHEN NEW.ExecutionEngineVersion IS NOT OLD.ExecutionEngineVersion
  OR NEW.BrowserFamily IS NOT OLD.BrowserFamily
  OR NEW.ProfileId IS NOT OLD.ProfileId
  OR NEW.InstallationId IS NOT OLD.InstallationId
  OR NEW.SessionId IS NOT OLD.SessionId
BEGIN
    SELECT RAISE(ABORT, 'execution snapshot is immutable');
END;

CREATE TRIGGER TR_StandaloneShareOperations_ExecutionSnapshotImmutable
BEFORE UPDATE OF ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId
ON StandaloneShareOperations
WHEN NEW.ExecutionEngineVersion IS NOT OLD.ExecutionEngineVersion
  OR NEW.BrowserFamily IS NOT OLD.BrowserFamily
  OR NEW.ProfileId IS NOT OLD.ProfileId
  OR NEW.InstallationId IS NOT OLD.InstallationId
  OR NEW.SessionId IS NOT OLD.SessionId
BEGIN
    SELECT RAISE(ABORT, 'execution snapshot is immutable');
END;

CREATE TRIGGER TR_WebAutomationAttempts_ExecutionSnapshotImmutable
BEFORE UPDATE OF ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId
ON WebAutomationAttempts
WHEN NEW.ExecutionEngineVersion IS NOT OLD.ExecutionEngineVersion
  OR NEW.BrowserFamily IS NOT OLD.BrowserFamily
  OR NEW.ProfileId IS NOT OLD.ProfileId
  OR NEW.InstallationId IS NOT OLD.InstallationId
  OR NEW.SessionId IS NOT OLD.SessionId
BEGIN
    SELECT RAISE(ABORT, 'execution snapshot is immutable');
END;

CREATE TRIGGER TR_WebAutomationAttempts_ExactOwnerSnapshot_Insert
BEFORE INSERT ON WebAutomationAttempts
WHEN NEW.OwnerKind IN ('Transfer','TransferShare','StandaloneShare','RemoteSelection')
 AND NOT (
   (NEW.OwnerKind='Transfer' AND EXISTS(
      SELECT 1 FROM TransferOperations operation
      WHERE operation.Id=NEW.OperationId AND operation.AssignedAccountId=NEW.AccountId
        AND operation.ExecutionEngineVersion=NEW.ExecutionEngineVersion
        AND operation.BrowserFamily=NEW.BrowserFamily AND operation.ProfileId=NEW.ProfileId
        AND operation.InstallationId IS NEW.InstallationId AND operation.SessionId IS NEW.SessionId))
   OR (NEW.OwnerKind='TransferShare' AND EXISTS(
      SELECT 1 FROM ShareOperations operation
      WHERE operation.Id=NEW.OperationId AND operation.AccountId=NEW.AccountId
        AND operation.ExecutionEngineVersion=NEW.ExecutionEngineVersion
        AND operation.BrowserFamily=NEW.BrowserFamily AND operation.ProfileId=NEW.ProfileId
        AND operation.InstallationId IS NEW.InstallationId AND operation.SessionId IS NEW.SessionId))
   OR (NEW.OwnerKind='StandaloneShare' AND EXISTS(
      SELECT 1 FROM StandaloneShareOperations operation
      WHERE operation.Id=NEW.OperationId AND operation.AccountId=NEW.AccountId
        AND operation.ExecutionEngineVersion=NEW.ExecutionEngineVersion
        AND operation.BrowserFamily=NEW.BrowserFamily AND operation.ProfileId=NEW.ProfileId
        AND operation.InstallationId IS NEW.InstallationId AND operation.SessionId IS NEW.SessionId))
   OR (NEW.OwnerKind='RemoteSelection' AND EXISTS(
      SELECT 1 FROM Accounts account WHERE account.Id=NEW.AccountId))
 )
BEGIN
    SELECT RAISE(ABORT, 'attempt execution snapshot does not match its owner');
END;

CREATE TRIGGER TR_WebAutomationAttempts_ExactOwnerSnapshot_Update
BEFORE UPDATE ON WebAutomationAttempts
WHEN NEW.OwnerKind IN ('Transfer','TransferShare','StandaloneShare','RemoteSelection')
 AND NOT (
   (NEW.OwnerKind='Transfer' AND EXISTS(
      SELECT 1 FROM TransferOperations operation
      WHERE operation.Id=NEW.OperationId AND operation.AssignedAccountId=NEW.AccountId
        AND operation.ExecutionEngineVersion=NEW.ExecutionEngineVersion
        AND operation.BrowserFamily=NEW.BrowserFamily AND operation.ProfileId=NEW.ProfileId
        AND operation.InstallationId IS NEW.InstallationId AND operation.SessionId IS NEW.SessionId))
   OR (NEW.OwnerKind='TransferShare' AND EXISTS(
      SELECT 1 FROM ShareOperations operation
      WHERE operation.Id=NEW.OperationId AND operation.AccountId=NEW.AccountId
        AND operation.ExecutionEngineVersion=NEW.ExecutionEngineVersion
        AND operation.BrowserFamily=NEW.BrowserFamily AND operation.ProfileId=NEW.ProfileId
        AND operation.InstallationId IS NEW.InstallationId AND operation.SessionId IS NEW.SessionId))
   OR (NEW.OwnerKind='StandaloneShare' AND EXISTS(
      SELECT 1 FROM StandaloneShareOperations operation
      WHERE operation.Id=NEW.OperationId AND operation.AccountId=NEW.AccountId
        AND operation.ExecutionEngineVersion=NEW.ExecutionEngineVersion
        AND operation.BrowserFamily=NEW.BrowserFamily AND operation.ProfileId=NEW.ProfileId
        AND operation.InstallationId IS NEW.InstallationId AND operation.SessionId IS NEW.SessionId))
   OR (NEW.OwnerKind='RemoteSelection' AND EXISTS(
      SELECT 1 FROM Accounts account WHERE account.Id=NEW.AccountId))
 )
BEGIN
    SELECT RAISE(ABORT, 'attempt execution snapshot does not match its owner');
END;

CREATE TRIGGER TR_TransferOperations_RequireAttemptClosure_Delete
BEFORE DELETE ON TransferOperations
WHEN EXISTS(
  SELECT 1 FROM WebAutomationAttempts attempt
  WHERE attempt.OwnerKind='Transfer' AND attempt.OperationId=OLD.Id
    AND attempt.AccountId=OLD.AssignedAccountId)
BEGIN
    SELECT RAISE(ABORT, 'transfer attempt history must be closed before owner deletion');
END;

CREATE TRIGGER TR_ShareOperations_RequireAttemptClosure_Delete
BEFORE DELETE ON ShareOperations
WHEN EXISTS(
  SELECT 1 FROM WebAutomationAttempts attempt
  WHERE attempt.OwnerKind='TransferShare' AND attempt.OperationId=OLD.Id
    AND attempt.AccountId=OLD.AccountId)
BEGIN
    SELECT RAISE(ABORT, 'share attempt history must be closed before owner deletion');
END;

CREATE TRIGGER TR_StandaloneShareOperations_RequireAttemptClosure_Delete
BEFORE DELETE ON StandaloneShareOperations
WHEN EXISTS(
  SELECT 1 FROM WebAutomationAttempts attempt
  WHERE attempt.OwnerKind='StandaloneShare' AND attempt.OperationId=OLD.Id
    AND attempt.AccountId=OLD.AccountId)
BEGIN
    SELECT RAISE(ABORT, 'standalone share attempt history must be closed before owner deletion');
END;

CREATE TRIGGER TR_RemoteSelectionSessions_RequireAttemptClosure_Delete
BEFORE DELETE ON RemoteSelectionSessions
WHEN EXISTS(
  SELECT 1 FROM WebAutomationAttempts attempt
  WHERE attempt.OwnerKind='RemoteSelection' AND attempt.OperationId=OLD.SessionId
    AND attempt.AccountId=OLD.AccountId AND attempt.Generation=OLD.Generation)
BEGIN
    SELECT RAISE(ABORT, 'remote selection attempt history must be closed before owner deletion');
END;

CREATE TRIGGER TR_Accounts_RequireRemoteSelectionAttemptClosure_Delete
BEFORE DELETE ON Accounts
WHEN EXISTS(
  SELECT 1 FROM WebAutomationAttempts attempt
  WHERE attempt.OwnerKind='RemoteSelection' AND attempt.AccountId=OLD.Id)
BEGIN
    SELECT RAISE(ABORT, 'remote selection attempt history must be closed before account deletion');
END;

CREATE TRIGGER TR_ManagedProfileCleanupManifests_IdentityImmutable
BEFORE UPDATE OF AccountId,SessionId,SourceProfilePath,QuarantinePath,ExpectedOwnershipId
ON ManagedProfileCleanupManifests
WHEN NEW.AccountId IS NOT OLD.AccountId
  OR NEW.SessionId IS NOT OLD.SessionId
  OR NEW.SourceProfilePath IS NOT OLD.SourceProfilePath
  OR NEW.QuarantinePath IS NOT OLD.QuarantinePath
  OR NEW.ExpectedOwnershipId IS NOT OLD.ExpectedOwnershipId
BEGIN
    SELECT RAISE(ABORT, 'cleanup manifest identity is immutable');
END;

CREATE TRIGGER TR_ManagedProfileCleanupManifests_StateMachine
BEFORE UPDATE OF State,ConfirmationTokenHash ON ManagedProfileCleanupManifests
WHEN NOT (
  NEW.Revision=OLD.Revision+1
  AND (
    (OLD.State='Prepared' AND NEW.State='QuarantineMoveIntent'
      AND OLD.ConfirmationTokenHash IS NULL AND NEW.ConfirmationTokenHash IS NULL
      AND EXISTS(
        SELECT 1 FROM ManagedBrowserCleanupClaims claim
        JOIN ManagedBrowserSessions session ON session.SessionId=claim.SessionId
        WHERE claim.ManifestId=OLD.ManifestId AND session.State='QuarantinePending'))
    OR (OLD.State='QuarantineMoveIntent' AND NEW.State='Quarantined'
      AND OLD.ConfirmationTokenHash IS NULL AND NEW.ConfirmationTokenHash IS NULL
      AND EXISTS(
        SELECT 1 FROM ManagedBrowserCleanupClaims claim
        JOIN ManagedBrowserSessions session ON session.SessionId=claim.SessionId
        WHERE claim.ManifestId=OLD.ManifestId AND session.State='Quarantined'))
    OR (OLD.State='QuarantineMoveIntent' AND NEW.State='CleanupPending'
      AND OLD.ConfirmationTokenHash IS NULL AND NEW.ConfirmationTokenHash IS NULL
      AND EXISTS(
        SELECT 1 FROM ManagedBrowserCleanupClaims claim
        JOIN ManagedBrowserSessions session ON session.SessionId=claim.SessionId
        WHERE claim.ManifestId=OLD.ManifestId AND session.State='CleanupPending'))
    OR (OLD.State='Quarantined' AND NEW.State='DeleteIntent'
      AND OLD.ConfirmationTokenHash IS NULL
      AND NEW.ConfirmationTokenHash IS NOT NULL
      AND length(trim(NEW.ConfirmationTokenHash))>0
      AND NEW.ConfirmationTokenHash=trim(NEW.ConfirmationTokenHash)
      AND length(NEW.ConfirmationTokenHash)<=512
      AND EXISTS(
        SELECT 1 FROM ManagedBrowserCleanupClaims claim
        JOIN ManagedBrowserSessions session ON session.SessionId=claim.SessionId
        WHERE claim.ManifestId=OLD.ManifestId AND session.State='Quarantined'))
    OR (OLD.State='DeleteIntent' AND NEW.State='Deleted'
      AND NEW.ConfirmationTokenHash IS OLD.ConfirmationTokenHash
      AND OLD.ConfirmationTokenHash IS NOT NULL
      AND EXISTS(
        SELECT 1 FROM ManagedBrowserCleanupClaims claim
        JOIN ManagedBrowserSessions session ON session.SessionId=claim.SessionId
        WHERE claim.ManifestId=OLD.ManifestId AND session.State='Deleted'))
    OR (OLD.State='DeleteIntent' AND NEW.State='CleanupPending'
      AND NEW.ConfirmationTokenHash IS OLD.ConfirmationTokenHash
      AND OLD.ConfirmationTokenHash IS NOT NULL
      AND EXISTS(
        SELECT 1 FROM ManagedBrowserCleanupClaims claim
        JOIN ManagedBrowserSessions session ON session.SessionId=claim.SessionId
        WHERE claim.ManifestId=OLD.ManifestId AND session.State='CleanupPending'))
  )
)
BEGIN
    SELECT RAISE(ABORT, 'cleanup manifest state transition is invalid');
END;

CREATE TRIGGER TR_ManagedProfileCleanupManifests_DeletedTerminal
BEFORE UPDATE OF State ON ManagedProfileCleanupManifests
WHEN OLD.State='Deleted' AND NEW.State<>'Deleted'
BEGIN
    SELECT RAISE(ABORT, 'deleted cleanup manifest is terminal');
END;

CREATE TRIGGER TR_TransferOperations_RetainManagedIdentity_Insert
AFTER INSERT ON TransferOperations
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1'
BEGIN
    INSERT OR IGNORE INTO ManagedBrowserRetentions(
      OwnerKind,OwnerId,InstallationId,SessionId,CreatedAt)
    VALUES('Operation',NEW.Id,NEW.InstallationId,NEW.SessionId,NEW.CreatedAt);
END;

CREATE TRIGGER TR_TransferOperations_ReleaseManagedIdentity_Delete
AFTER DELETE ON TransferOperations
WHEN OLD.ExecutionEngineVersion='ManagedChromium-v1'
BEGIN
    DELETE FROM ManagedBrowserRetentions
    WHERE OwnerKind='Operation' AND OwnerId=OLD.Id
      AND InstallationId=OLD.InstallationId AND SessionId=OLD.SessionId;
END;

CREATE TRIGGER TR_ShareOperations_RetainManagedIdentity_Insert
AFTER INSERT ON ShareOperations
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1'
BEGIN
    INSERT OR IGNORE INTO ManagedBrowserRetentions(
      OwnerKind,OwnerId,InstallationId,SessionId,CreatedAt)
    VALUES('Operation',NEW.Id,NEW.InstallationId,NEW.SessionId,NEW.CreatedAt);
END;

CREATE TRIGGER TR_ShareOperations_ReleaseManagedIdentity_Delete
AFTER DELETE ON ShareOperations
WHEN OLD.ExecutionEngineVersion='ManagedChromium-v1'
BEGIN
    DELETE FROM ManagedBrowserRetentions
    WHERE OwnerKind='Operation' AND OwnerId=OLD.Id
      AND InstallationId=OLD.InstallationId AND SessionId=OLD.SessionId;
END;

CREATE TRIGGER TR_StandaloneShareOperations_RetainManagedIdentity_Insert
AFTER INSERT ON StandaloneShareOperations
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1'
BEGIN
    INSERT OR IGNORE INTO ManagedBrowserRetentions(
      OwnerKind,OwnerId,InstallationId,SessionId,CreatedAt)
    VALUES('Operation',NEW.Id,NEW.InstallationId,NEW.SessionId,NEW.CreatedAt);
END;

CREATE TRIGGER TR_StandaloneShareOperations_ReleaseManagedIdentity_Delete
AFTER DELETE ON StandaloneShareOperations
WHEN OLD.ExecutionEngineVersion='ManagedChromium-v1'
BEGIN
    DELETE FROM ManagedBrowserRetentions
    WHERE OwnerKind='Operation' AND OwnerId=OLD.Id
      AND InstallationId=OLD.InstallationId AND SessionId=OLD.SessionId;
END;

CREATE TRIGGER TR_WebAutomationAttempts_RetainManagedIdentity_Insert
AFTER INSERT ON WebAutomationAttempts
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1'
BEGIN
    INSERT OR IGNORE INTO ManagedBrowserRetentions(
      OwnerKind,OwnerId,InstallationId,SessionId,CreatedAt)
    VALUES('Attempt',NEW.OperationId,NEW.InstallationId,NEW.SessionId,NEW.CreatedAt);
END;

CREATE TRIGGER TR_WebAutomationAttempts_ReleaseManagedIdentity_Delete
AFTER DELETE ON WebAutomationAttempts
WHEN OLD.ExecutionEngineVersion='ManagedChromium-v1'
BEGIN
    DELETE FROM ManagedBrowserRetentions
    WHERE OwnerKind='Attempt' AND OwnerId=OLD.OperationId
      AND InstallationId=OLD.InstallationId AND SessionId=OLD.SessionId
      AND NOT EXISTS(
        SELECT 1 FROM WebAutomationAttempts attempt
        WHERE attempt.OperationId=OLD.OperationId
          AND attempt.InstallationId IS OLD.InstallationId
          AND attempt.SessionId IS OLD.SessionId);
END;

CREATE TRIGGER TR_ManagedProfileCleanupManifests_RetainIdentity_Insert
AFTER INSERT ON ManagedProfileCleanupManifests
BEGIN
    INSERT OR IGNORE INTO ManagedBrowserRetentions(
      OwnerKind,OwnerId,InstallationId,SessionId,CreatedAt)
    SELECT 'CleanupManifest',NEW.ManifestId,session.InstallationId,NEW.SessionId,NEW.CreatedAt
    FROM ManagedBrowserSessions session
    WHERE session.SessionId=NEW.SessionId AND session.AccountId=NEW.AccountId
      AND session.ProfileOwnershipId=NEW.ExpectedOwnershipId;
END;

CREATE TRIGGER TR_ManagedProfileCleanupManifests_ReleaseIdentity_Deleted
AFTER UPDATE OF State ON ManagedProfileCleanupManifests
WHEN OLD.State<>'Deleted' AND NEW.State='Deleted'
BEGIN
    DELETE FROM ManagedBrowserRetentions
    WHERE OwnerKind='CleanupManifest' AND OwnerId=NEW.ManifestId AND SessionId=NEW.SessionId;
END;

CREATE TRIGGER TR_ManagedProfileCleanupManifests_ReleaseIdentity_Delete
AFTER DELETE ON ManagedProfileCleanupManifests
BEGIN
    DELETE FROM ManagedBrowserRetentions
    WHERE OwnerKind='CleanupManifest' AND OwnerId=OLD.ManifestId AND SessionId=OLD.SessionId;
END;

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

CREATE TRIGGER TR_TransferOperations_RemoveTerminalPendingShare
AFTER UPDATE OF Status ON TransferOperations
WHEN NEW.Status IN ('Failed','Cancelled') AND OLD.Status<>NEW.Status
BEGIN
    UPDATE BatchJobs SET ShareOperationCount=ShareOperationCount-1
    WHERE Id=NEW.BatchJobId AND EXISTS(
      SELECT 1 FROM ShareOperations share
      WHERE share.TransferOperationId=NEW.Id AND share.Status='WaitingTransfer');
    DELETE FROM ShareOperations
    WHERE TransferOperationId=NEW.Id AND Status='WaitingTransfer';
END;

CREATE TRIGGER TR_ShareOperations_RecordInitialGeneration
AFTER INSERT ON ShareOperations
WHEN NEW.ExactItemSetFingerprint IS NOT NULL
BEGIN
    INSERT INTO ShareOperationGenerations(ShareOperationId,Generation,BatchJobId,ResourceItemId,AccountId,
      ExactItemSetFingerprint,SettingsFingerprint)
    VALUES(NEW.Id,NEW.Generation,NEW.BatchJobId,NEW.ResourceItemId,NEW.AccountId,
      NEW.ExactItemSetFingerprint,NEW.SettingsFingerprint);
END;

CREATE TRIGGER TR_ShareOperations_RecordNextGeneration
AFTER UPDATE OF Generation ON ShareOperations
WHEN NEW.Generation <> OLD.Generation AND NEW.ExactItemSetFingerprint IS NOT NULL
BEGIN
    INSERT INTO ShareOperationGenerations(ShareOperationId,Generation,BatchJobId,ResourceItemId,AccountId,
      ExactItemSetFingerprint,SettingsFingerprint,ExactItemCount)
    VALUES(NEW.Id,NEW.Generation,NEW.BatchJobId,NEW.ResourceItemId,NEW.AccountId,
      NEW.ExactItemSetFingerprint,NEW.SettingsFingerprint,
      (SELECT ExactItemCount FROM ShareOperationGenerations
       WHERE ShareOperationId=NEW.Id AND Generation=OLD.Generation));
END;

CREATE TRIGGER TR_ShareOperationRemoteItems_SealDelete
BEFORE DELETE ON ShareOperationRemoteItems
WHEN EXISTS(SELECT 1 FROM ShareOperationGenerations lineage
  WHERE lineage.ShareOperationId=OLD.ShareOperationId AND lineage.ExactItemCount IS NOT NULL)
AND EXISTS(SELECT 1 FROM ShareOperations operation WHERE operation.Id=OLD.ShareOperationId)
BEGIN
    SELECT RAISE(ABORT, 'validated exact remote items are immutable');
END;

DROP TABLE V16ExecutionSnapshotValidation;
DROP TABLE V16ExecutionSnapshotStaging;
