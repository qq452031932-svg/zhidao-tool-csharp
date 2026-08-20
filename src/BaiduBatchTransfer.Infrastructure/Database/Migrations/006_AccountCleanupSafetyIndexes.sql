CREATE INDEX IF NOT EXISTS IX_ExecutionLogs_Account_Id
ON ExecutionLogs(AccountId, Id)
WHERE AccountId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_ExecutionLogs_Resource_Id
ON ExecutionLogs(ResourceItemId, Id)
WHERE ResourceItemId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_ExecutionLogs_Operation_Id
ON ExecutionLogs(TransferOperationId, Id)
WHERE TransferOperationId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_TransferOperations_Batch_Account_Status_State
ON TransferOperations(BatchJobId, AssignedAccountId, Status, SubmitState, TargetSlotState);

CREATE INDEX IF NOT EXISTS IX_StagedImportRows_Duplicate_Id
ON StagedImportRows(DuplicateOfStagedRowId, Id)
WHERE DuplicateOfStagedRowId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_ResourceItems_StagedImportRow_Id
ON ResourceItems(StagedImportRowId, Id)
WHERE StagedImportRowId IS NOT NULL;
