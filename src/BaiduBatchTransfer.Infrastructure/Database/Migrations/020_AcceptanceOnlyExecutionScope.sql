ALTER TABLE BatchJobs
ADD COLUMN AcceptanceOnly INTEGER NOT NULL DEFAULT 0
CHECK(typeof(AcceptanceOnly)='integer' AND AcceptanceOnly IN (0,1));

ALTER TABLE TransferOperations
ADD COLUMN AcceptanceOnly INTEGER NOT NULL DEFAULT 0
CHECK(typeof(AcceptanceOnly)='integer' AND AcceptanceOnly IN (0,1));

CREATE UNIQUE INDEX UX_TransferOperations_AcceptanceOnlyBatch
ON TransferOperations(BatchJobId)
WHERE AcceptanceOnly=1;

CREATE INDEX IX_BatchJobs_AcceptanceOnly_Status_Queue
ON BatchJobs(AcceptanceOnly,Status,QueueOrder,CreatedAt,Id);

CREATE TRIGGER TR_BatchJobs_AcceptanceOnly_InsertShape
BEFORE INSERT ON BatchJobs
WHEN NEW.AcceptanceOnly=1 AND
  (NEW.ResourceCount<>1 OR NEW.OperationCount<>1 OR NEW.ShareOperationCount<>1)
BEGIN
  SELECT RAISE(ABORT,'AcceptanceOnly batch shape mismatch');
END;

CREATE TRIGGER TR_BatchJobs_AcceptanceOnly_Immutable
BEFORE UPDATE OF AcceptanceOnly ON BatchJobs
WHEN OLD.AcceptanceOnly<>NEW.AcceptanceOnly
BEGIN
  SELECT RAISE(ABORT,'AcceptanceOnly batch scope is immutable');
END;

CREATE TRIGGER TR_TransferOperations_AcceptanceOnly_InsertScope
BEFORE INSERT ON TransferOperations
WHEN NOT EXISTS(
  SELECT 1 FROM BatchJobs batch
  WHERE batch.Id=NEW.BatchJobId
    AND batch.AcceptanceOnly=NEW.AcceptanceOnly)
BEGIN
  SELECT RAISE(ABORT,'TransferOperation acceptance scope mismatch');
END;

CREATE TRIGGER TR_TransferOperations_AcceptanceOnly_Managed
BEFORE INSERT ON TransferOperations
WHEN NEW.AcceptanceOnly=1 AND NEW.ExecutionEngineVersion<>'ManagedChromium-v1'
BEGIN
  SELECT RAISE(ABORT,'AcceptanceOnly transfer requires Managed Chromium');
END;

CREATE TRIGGER TR_TransferOperations_AcceptanceOnly_Immutable
BEFORE UPDATE OF AcceptanceOnly ON TransferOperations
WHEN OLD.AcceptanceOnly<>NEW.AcceptanceOnly
BEGIN
  SELECT RAISE(ABORT,'AcceptanceOnly transfer scope is immutable');
END;

CREATE TRIGGER TR_ShareOperations_AcceptanceOnly_InsertBinding
BEFORE INSERT ON ShareOperations
WHEN EXISTS(
  SELECT 1 FROM TransferOperations transfer
  WHERE transfer.Id=NEW.TransferOperationId AND transfer.AcceptanceOnly=1)
AND NOT EXISTS(
  SELECT 1
  FROM TransferOperations transfer
  JOIN BatchJobs batch ON batch.Id=transfer.BatchJobId
  WHERE transfer.Id=NEW.TransferOperationId
    AND transfer.AcceptanceOnly=1
    AND batch.AcceptanceOnly=1
    AND NEW.BatchJobId=transfer.BatchJobId
    AND NEW.ResourceItemId=transfer.ResourceItemId
    AND NEW.AccountId=transfer.AssignedAccountId
    AND NEW.Status='WaitingTransfer'
    AND NEW.ExecutionEngineVersion=transfer.ExecutionEngineVersion
    AND NEW.BrowserFamily=transfer.BrowserFamily
    AND NEW.ProfileId=transfer.ProfileId
    AND NEW.InstallationId=transfer.InstallationId
    AND NEW.SessionId=transfer.SessionId)
BEGIN
  SELECT RAISE(ABORT,'AcceptanceOnly share binding mismatch');
END;

CREATE TRIGGER TR_ShareOperations_AcceptanceOnly_UpdateBinding
BEFORE UPDATE OF TransferOperationId,ResourceItemId,BatchJobId,AccountId,
  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId ON ShareOperations
WHEN EXISTS(
  SELECT 1 FROM TransferOperations transfer
  WHERE transfer.Id=OLD.TransferOperationId AND transfer.AcceptanceOnly=1)
AND NOT EXISTS(
  SELECT 1
  FROM TransferOperations transfer
  JOIN BatchJobs batch ON batch.Id=transfer.BatchJobId
  WHERE transfer.Id=NEW.TransferOperationId
    AND transfer.AcceptanceOnly=1
    AND batch.AcceptanceOnly=1
    AND NEW.BatchJobId=transfer.BatchJobId
    AND NEW.ResourceItemId=transfer.ResourceItemId
    AND NEW.AccountId=transfer.AssignedAccountId
    AND NEW.ExecutionEngineVersion=transfer.ExecutionEngineVersion
    AND NEW.BrowserFamily=transfer.BrowserFamily
    AND NEW.ProfileId=transfer.ProfileId
    AND NEW.InstallationId=transfer.InstallationId
    AND NEW.SessionId=transfer.SessionId)
BEGIN
  SELECT RAISE(ABORT,'AcceptanceOnly share binding is immutable');
END;
