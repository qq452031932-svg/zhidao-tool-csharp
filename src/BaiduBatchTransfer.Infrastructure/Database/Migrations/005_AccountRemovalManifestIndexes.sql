CREATE INDEX IF NOT EXISTS IX_AccountAllocations_Account_Batch
ON AccountAllocations(AccountId, BatchJobId);

CREATE INDEX IF NOT EXISTS IX_ResourceItems_Constraint_Batch
ON ResourceItems(CandidateConstraintAccountId, BatchJobId)
WHERE CandidateConstraintAccountId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_ResourceItems_Reserved_Batch
ON ResourceItems(ReservedAccountId, BatchJobId)
WHERE ReservedAccountId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_ImportSessions_Precheck_Id
ON ImportSessions(PrecheckAccountId, Id)
WHERE PrecheckAccountId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_ImportSessions_CreatedBatch_Id
ON ImportSessions(CreatedBatchJobId, Id)
WHERE CreatedBatchJobId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_ImportSessions_AppendBatch_Id
ON ImportSessions(AppendToBatchJobId, Id)
WHERE AppendToBatchJobId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_BatchJobs_ImportSession_Id
ON BatchJobs(ImportSessionId, Id)
WHERE ImportSessionId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_TransferOperations_Resource_Batch
ON TransferOperations(ResourceItemId, BatchJobId);
