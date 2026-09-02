CREATE TABLE ShareAnalyticsPreferences (
    SingletonId INTEGER PRIMARY KEY CHECK(SingletonId=1),
    LastViewedAccountId TEXT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    FOREIGN KEY(LastViewedAccountId) REFERENCES Accounts(Id) ON DELETE SET NULL
);

INSERT INTO ShareAnalyticsPreferences(SingletonId,LastViewedAccountId,UpdatedAtUtc)
VALUES(1,NULL,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
