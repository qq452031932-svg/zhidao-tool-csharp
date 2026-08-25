ALTER TABLE WebAutomationAttempts ADD COLUMN AcceptanceFingerprint TEXT NULL;
ALTER TABLE WebAutomationAttempts ADD COLUMN AcceptedItemCount INTEGER NOT NULL DEFAULT 0 CHECK(AcceptedItemCount >= 0);
ALTER TABLE WebAutomationAttempts ADD COLUMN AcceptancePersistedAt TEXT NULL;

CREATE TABLE WebAutomationAttemptAcceptedRemoteItems (
    OperationId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL,
    ItemOrdinal INTEGER NOT NULL CHECK(ItemOrdinal >= 0),
    RemoteItemId TEXT NOT NULL,
    CanonicalSortKey BLOB NOT NULL,
    PRIMARY KEY(OperationId, AccountId, Generation, ItemOrdinal),
    UNIQUE(OperationId, AccountId, Generation, RemoteItemId),
    FOREIGN KEY(OperationId, AccountId, Generation)
      REFERENCES WebAutomationAttempts(OperationId, AccountId, Generation) ON DELETE CASCADE
);

CREATE INDEX IX_WebAutomationAttemptAcceptedRemoteItems_Canonical
ON WebAutomationAttemptAcceptedRemoteItems(OperationId, Generation, RemoteItemId);
