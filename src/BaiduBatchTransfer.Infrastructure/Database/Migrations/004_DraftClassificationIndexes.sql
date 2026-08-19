CREATE INDEX IF NOT EXISTS IX_StagedImportRows_Session_Normalized_Source
ON StagedImportRows(ImportSessionId, NormalizedShareId, SourceRowNumber, Id);

CREATE INDEX IF NOT EXISTS IX_StagedImportRows_Session_RequestedAccount
ON StagedImportRows(ImportSessionId, RequestedAccountName)
WHERE RequestedAccountName IS NOT NULL;
