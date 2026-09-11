ALTER TABLE BatchJobs
ADD COLUMN DirectBackground INTEGER NOT NULL DEFAULT 0
CHECK(typeof(DirectBackground)='integer' AND DirectBackground IN (0,1));

ALTER TABLE BatchJobs
ADD COLUMN MaxConcurrency INTEGER NOT NULL DEFAULT 1
CHECK(typeof(MaxConcurrency)='integer' AND MaxConcurrency BETWEEN 1 AND 3);

ALTER TABLE BatchJobs
ADD COLUMN DirectActivationState TEXT NOT NULL DEFAULT 'Inert'
CHECK(DirectActivationState IN ('Inert','Active'));

ALTER TABLE BatchJobs
ADD COLUMN DirectPauseReason TEXT NULL;

ALTER TABLE TransferOperations
ADD COLUMN UnknownResubmissionCount INTEGER NOT NULL DEFAULT 0
CHECK(typeof(UnknownResubmissionCount)='integer' AND UnknownResubmissionCount BETWEEN 0 AND 1);

ALTER TABLE ShareOperations
ADD COLUMN DirectExtractionCode TEXT NULL
CHECK(DirectExtractionCode IS NULL OR
  (length(DirectExtractionCode)=4 AND DirectExtractionCode NOT GLOB '*[^a-z0-9]*'));

ALTER TABLE ShareOperationRemoteItems
ADD COLUMN ResponseOrdinal INTEGER NULL
CHECK(ResponseOrdinal IS NULL OR
  (typeof(ResponseOrdinal)='integer' AND ResponseOrdinal >= 0));

CREATE TABLE DirectBackgroundWorkspacePreferences (
    SingletonId INTEGER PRIMARY KEY CHECK(SingletonId=1),
    DefaultResultDirectory TEXT NOT NULL CHECK(length(trim(DefaultResultDirectory))>0),
    UpdatedAt TEXT NOT NULL
);

CREATE TABLE DirectBackgroundAuthorizationState (
    SingletonId INTEGER PRIMARY KEY CHECK(SingletonId=1),
    NormalEnabled INTEGER NOT NULL DEFAULT 0
      CHECK(typeof(NormalEnabled)='integer' AND NormalEnabled IN (0,1)),
    UpdatedAt TEXT NOT NULL
);

INSERT INTO DirectBackgroundAuthorizationState(SingletonId,NormalEnabled,UpdatedAt)
VALUES(1,0,strftime('%Y-%m-%dT%H:%M:%fZ','now'));

CREATE TABLE DirectBackgroundAcceptanceAuthorizations (
    OperationId TEXT NOT NULL CHECK(length(trim(OperationId))>0),
    BatchId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation > 0),
    AllowedAction TEXT NOT NULL CHECK(length(trim(AllowedAction))>0),
    MutationOrdinal INTEGER NOT NULL DEFAULT 0
      CHECK(typeof(MutationOrdinal)='integer' AND MutationOrdinal >= 0),
    AttemptOrdinal INTEGER NOT NULL
      CHECK(typeof(AttemptOrdinal)='integer' AND AttemptOrdinal BETWEEN 1 AND 3),
    ExpiresAt TEXT NOT NULL,
    ConsumedAt TEXT NULL,
    PRIMARY KEY(OperationId,Generation,AllowedAction,MutationOrdinal,AttemptOrdinal),
    FOREIGN KEY(BatchId) REFERENCES BatchJobs(Id) ON DELETE CASCADE,
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
);

CREATE INDEX IX_BatchJobs_DirectBackground_Activation_Status_Queue
ON BatchJobs(DirectBackground,DirectActivationState,Status,QueueOrder,CreatedAt,Id);

CREATE INDEX IX_DirectBackgroundAcceptanceAuthorizations_Expiry
ON DirectBackgroundAcceptanceAuthorizations(ConsumedAt,ExpiresAt,BatchId,AccountId);

CREATE TRIGGER TR_BatchJobs_DirectBackground_Immutable
BEFORE UPDATE OF DirectBackground ON BatchJobs
WHEN OLD.DirectBackground<>NEW.DirectBackground
BEGIN
    SELECT RAISE(ABORT,'DirectBackground batch scope is immutable');
END;

CREATE TRIGGER TR_BatchJobs_DirectMaxConcurrency_Immutable
BEFORE UPDATE OF MaxConcurrency ON BatchJobs
WHEN OLD.MaxConcurrency<>NEW.MaxConcurrency
BEGIN
    SELECT RAISE(ABORT,'DirectBackground concurrency is immutable');
END;

CREATE TRIGGER TR_BatchJobs_DirectActivation_Transition
BEFORE UPDATE OF DirectActivationState ON BatchJobs
WHEN OLD.DirectActivationState<>NEW.DirectActivationState
AND NOT(
    OLD.DirectBackground=1 AND
    OLD.DirectActivationState='Inert' AND
    NEW.DirectActivationState='Active')
BEGIN
    SELECT RAISE(ABORT,'DirectBackground activation transition is invalid');
END;

CREATE TRIGGER TR_BatchJobs_DirectPause_InsertScope
BEFORE INSERT ON BatchJobs
WHEN NEW.DirectBackground=0 AND NEW.DirectPauseReason IS NOT NULL
BEGIN
    SELECT RAISE(ABORT,'DirectBackground pause state requires a direct batch');
END;

CREATE TRIGGER TR_BatchJobs_DirectPause_UpdateScope
BEFORE UPDATE OF DirectPauseReason ON BatchJobs
WHEN NEW.DirectBackground=0 AND NEW.DirectPauseReason IS NOT NULL
BEGIN
    SELECT RAISE(ABORT,'DirectBackground pause state requires a direct batch');
END;

CREATE TRIGGER TR_ShareOperations_DirectExtractionCode_Immutable
BEFORE UPDATE OF DirectExtractionCode ON ShareOperations
WHEN OLD.DirectExtractionCode IS NOT NEW.DirectExtractionCode
BEGIN
    SELECT RAISE(ABORT,'DirectBackground extraction code is immutable');
END;

CREATE TRIGGER TR_ShareOperationRemoteItems_ResponseOrdinal_SealOnce
BEFORE UPDATE OF ResponseOrdinal ON ShareOperationRemoteItems
WHEN OLD.ResponseOrdinal IS NOT NULL OR NEW.ResponseOrdinal IS NULL
BEGIN
    SELECT RAISE(ABORT,'DirectBackground response ordinal can be sealed once');
END;
