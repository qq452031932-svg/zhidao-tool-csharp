DROP INDEX IF EXISTS IX_TransferOperations_Batch_Status_Sequence;
DROP INDEX IF EXISTS IX_TransferOperations_Batch_AllocationOrdinal;
DROP INDEX IF EXISTS IX_TransferOperations_Batch_Status_AllocationOrdinal;

CREATE INDEX IX_TransferOperations_Batch_AllocationOrdinal
    ON TransferOperations(BatchJobId, AllocationOrdinal, SequenceNumber, Id);

CREATE INDEX IX_TransferOperations_Batch_Status_AllocationOrdinal
    ON TransferOperations(BatchJobId, Status, AllocationOrdinal, SequenceNumber, Id);

CREATE INDEX IF NOT EXISTS IX_StagedImportRows_Precheck_Keyset
    ON StagedImportRows(ImportSessionId, Disposition, PrecheckStatus, SourceRowNumber, Id);

CREATE INDEX IF NOT EXISTS IX_ResourceItems_Result_Filter_Keyset
    ON ResourceItems(BatchJobId, GlobalValidityStatus, CandidateStatus, CandidateSequence, Id);

CREATE INDEX IF NOT EXISTS IX_TransferOperations_Result_Account_Keyset
    ON TransferOperations(BatchJobId, AssignedAccountId, AllocationOrdinal, SequenceNumber, Id, Status);

CREATE INDEX IF NOT EXISTS IX_TransferOperations_Result_UpdatedInstant_Keyset
    ON TransferOperations(BatchJobId, julianday(UpdatedAt), AllocationOrdinal, SequenceNumber, Id);
