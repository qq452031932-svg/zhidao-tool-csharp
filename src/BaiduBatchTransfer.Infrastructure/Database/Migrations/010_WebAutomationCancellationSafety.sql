CREATE TABLE WebAutomationAttempts_v10 (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK(Generation > 0),
    OwnerKind TEXT NOT NULL CHECK(OwnerKind IN ('Transfer','TransferShare','StandaloneShare')),
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
    PRIMARY KEY(OperationId, AccountId, Generation),
    CHECK(State NOT IN ('BaselinePersisted','Submitting','ResultPending','Succeeded')
       OR (BaselineFingerprint IS NOT NULL AND BaselinePersistedAt IS NOT NULL)),
    CHECK((State='UserHandoff' AND ResumeStage IS NOT NULL AND IsWaitingForUser=1)
       OR (State<>'UserHandoff' AND ResumeStage IS NULL)),
    CHECK(State<>'UserHandoff' OR ResumeStage='Preparing'
       OR (BaselineFingerprint IS NOT NULL AND BaselinePersistedAt IS NOT NULL))
);

INSERT INTO WebAutomationAttempts_v10(
    OperationId,AccountId,Generation,OwnerKind,State,ResumeStage,ObservationFingerprint,
    BaselineFingerprint,BaselineItemCount,BaselinePersistedAt,IsWaitingForUser,Revision,CreatedAt,UpdatedAt)
SELECT OperationId,AccountId,Generation,OwnerKind,State,ResumeStage,ObservationFingerprint,
    BaselineFingerprint,BaselineItemCount,BaselinePersistedAt,IsWaitingForUser,Revision,CreatedAt,UpdatedAt
FROM WebAutomationAttempts;

CREATE TABLE WebAutomationAttemptRemoteItems_v10 (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL,
    ItemOrdinal INTEGER NOT NULL CHECK(ItemOrdinal >= 0),
    RemoteItemId TEXT NOT NULL,
    CanonicalSortKey BLOB NOT NULL,
    PRIMARY KEY(OperationId, AccountId, Generation, ItemOrdinal),
    UNIQUE(OperationId, AccountId, Generation, RemoteItemId),
    FOREIGN KEY(OperationId, AccountId, Generation)
      REFERENCES WebAutomationAttempts_v10(OperationId, AccountId, Generation) ON DELETE CASCADE
);

INSERT INTO WebAutomationAttemptRemoteItems_v10(
    OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey)
SELECT OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey
FROM WebAutomationAttemptRemoteItems;

DROP TABLE WebAutomationAttemptRemoteItems;
DROP TABLE WebAutomationAttempts;
ALTER TABLE WebAutomationAttempts_v10 RENAME TO WebAutomationAttempts;
ALTER TABLE WebAutomationAttemptRemoteItems_v10 RENAME TO WebAutomationAttemptRemoteItems;

CREATE INDEX IX_WebAutomationAttempts_Recovery
ON WebAutomationAttempts(State, IsWaitingForUser, UpdatedAt, OperationId);

CREATE INDEX IX_WebAutomationAttemptRemoteItems_Canonical
ON WebAutomationAttemptRemoteItems(OperationId, Generation, RemoteItemId);
