CREATE TABLE BrowserDirectorySelection (
    Singleton INTEGER PRIMARY KEY CHECK(Singleton=1),
    Directory TEXT NULL,
    Revision INTEGER NOT NULL CHECK(typeof(Revision)='integer' AND Revision>=0),
    IntentKind INTEGER NULL CHECK(IntentKind IN (1,2)),
    OwnerEpoch TEXT NULL,
    IntentId TEXT NULL,
    CHECK((IntentKind IS NULL AND OwnerEpoch IS NULL AND IntentId IS NULL)
       OR (IntentKind IS NOT NULL AND OwnerEpoch IS NOT NULL AND IntentId IS NOT NULL
           AND length(OwnerEpoch)=36 AND length(IntentId)=36))
);
INSERT INTO BrowserDirectorySelection(Singleton,Revision) VALUES(1,0);
