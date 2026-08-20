ALTER TABLE Accounts
ADD COLUMN RemovalState TEXT NOT NULL DEFAULT 'None'
CHECK(RemovalState IN ('None','CleanupPending'));

ALTER TABLE Accounts
ADD COLUMN RemovalGeneration TEXT NULL;

-- v6 account removal first disabled the account and cleared credentials, then
-- attempted external profile cleanup. A crash or cleanup failure therefore
-- left this exact durable fingerprint without an explicit pending marker.
-- Ordinary disabled accounts do not retain a profile after having both
-- credentials and validation state cleared.
UPDATE Accounts
SET RemovalState='CleanupPending', RemovalGeneration=hex(randomblob(32))
WHERE Status='Disabled'
  AND IsEnabled=0
  AND EncryptedSession IS NULL
  AND LastValidatedAt IS NULL
  AND IsDeleted=0
  AND DeletedAt IS NULL
  AND WebView2ProfileDirectory IS NOT NULL
  AND length(trim(WebView2ProfileDirectory))>0;

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
