ALTER TABLE Accounts
ADD COLUMN RemovalState TEXT NOT NULL DEFAULT 'None'
CHECK(RemovalState IN ('None','CleanupPending'));

ALTER TABLE Accounts
ADD COLUMN RemovalGeneration TEXT NULL;

CREATE INDEX IF NOT EXISTS IX_Accounts_RemovalState_Sort
ON Accounts(RemovalState, SortOrder, DisplayName, Id);

CREATE TRIGGER IF NOT EXISTS TR_Accounts_RemovalState_Insert
BEFORE INSERT ON Accounts
WHEN NOT (
    (NEW.RemovalState='None' AND NEW.RemovalGeneration IS NULL)
    OR
    (NEW.RemovalState='CleanupPending'
     AND NEW.RemovalGeneration IS NOT NULL
     AND length(NEW.RemovalGeneration)=64
     AND NEW.RemovalGeneration NOT GLOB '*[^0-9A-F]*'
     AND NEW.Status='Disabled'
     AND NEW.IsEnabled=0
     AND NEW.EncryptedSession IS NULL
     AND NEW.LastValidatedAt IS NULL
     AND NEW.IsDeleted=0
     AND NEW.DeletedAt IS NULL)
)
BEGIN
    SELECT RAISE(ABORT, 'invalid account removal state');
END;

CREATE TRIGGER IF NOT EXISTS TR_Accounts_RemovalState_Update
BEFORE UPDATE ON Accounts
WHEN NOT (
    (NEW.RemovalState='None' AND NEW.RemovalGeneration IS NULL)
    OR
    (NEW.RemovalState='CleanupPending'
     AND NEW.RemovalGeneration IS NOT NULL
     AND length(NEW.RemovalGeneration)=64
     AND NEW.RemovalGeneration NOT GLOB '*[^0-9A-F]*'
     AND NEW.Status='Disabled'
     AND NEW.IsEnabled=0
     AND NEW.EncryptedSession IS NULL
     AND NEW.LastValidatedAt IS NULL
     AND NEW.IsDeleted=0
     AND NEW.DeletedAt IS NULL)
)
BEGIN
    SELECT RAISE(ABORT, 'invalid account removal state');
END;
