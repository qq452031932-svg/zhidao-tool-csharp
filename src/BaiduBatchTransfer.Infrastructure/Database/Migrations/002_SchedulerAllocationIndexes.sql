ALTER TABLE BatchJobs ADD COLUMN NextAllocationOrdinal INTEGER NOT NULL DEFAULT 1
    CHECK(typeof(NextAllocationOrdinal)='integer' AND NextAllocationOrdinal > 0);

CREATE INDEX IF NOT EXISTS IX_TransferOperations_Batch_AllocationOrdinal
    ON TransferOperations(BatchJobId, AllocationOrdinal);

CREATE INDEX IF NOT EXISTS IX_TransferOperations_Batch_Status_AllocationOrdinal
    ON TransferOperations(BatchJobId, Status, AllocationOrdinal, SequenceNumber, Id);

CREATE INDEX IF NOT EXISTS IX_TransferOperations_Status_Lease
    ON TransferOperations(Status, LeaseExpiresAt);

UPDATE BatchJobs
SET NextAllocationOrdinal = COALESCE(
    (SELECT MAX(operation.AllocationOrdinal) + 1
     FROM TransferOperations operation
     WHERE operation.BatchJobId = BatchJobs.Id),
    1);

DROP INDEX IF EXISTS IX_ResourceItems_Candidate_Status;

CREATE INDEX IF NOT EXISTS IX_ResourceItems_Batch_Status_Constraint_Sequence
    ON ResourceItems(BatchJobId, CandidateStatus, CandidateConstraintAccountId, CandidateSequence);
