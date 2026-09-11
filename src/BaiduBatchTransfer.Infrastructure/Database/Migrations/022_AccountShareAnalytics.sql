CREATE TABLE AccountShareRecords (
    AccountId TEXT NOT NULL,
    ShareId TEXT NOT NULL,
    Title TEXT NOT NULL,
    ShareUrl TEXT NOT NULL,
    SharedAtUtc TEXT NOT NULL,
    Status TEXT NOT NULL CHECK(Status IN ('Valid','Invalid')),
    IsCurrent INTEGER NOT NULL CHECK(typeof(IsCurrent)='integer' AND IsCurrent IN (0,1)),
    LastSeenAtUtc TEXT NOT NULL,
    PRIMARY KEY(AccountId,ShareId),
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id) ON DELETE CASCADE
);

CREATE TABLE AccountShareMetricSnapshots (
    AccountId TEXT NOT NULL,
    ShareId TEXT NOT NULL,
    Metric TEXT NOT NULL CHECK(Metric IN ('Transfer','View','Download')),
    DateKey TEXT NOT NULL,
    CapturedAtUtc TEXT NOT NULL,
    HistoricalTotal INTEGER NULL CHECK(HistoricalTotal IS NULL OR (typeof(HistoricalTotal)='integer' AND HistoricalTotal>=0)),
    DailyValue INTEGER NULL CHECK(DailyValue IS NULL OR (typeof(DailyValue)='integer' AND DailyValue>=0)),
    DailySource TEXT NULL CHECK(DailySource IS NULL OR DailySource IN ('Direct','Derived')),
    PRIMARY KEY(AccountId,ShareId,Metric,DateKey),
    FOREIGN KEY(AccountId,ShareId) REFERENCES AccountShareRecords(AccountId,ShareId) ON DELETE CASCADE
);

CREATE TABLE AccountShareSyncState (
    AccountId TEXT PRIMARY KEY,
    IssuedGeneration INTEGER NOT NULL DEFAULT 0 CHECK(typeof(IssuedGeneration)='integer' AND IssuedGeneration>=0),
    CommittedGeneration INTEGER NOT NULL DEFAULT 0 CHECK(typeof(CommittedGeneration)='integer' AND CommittedGeneration>=0 AND CommittedGeneration<=IssuedGeneration),
    LastSuccessfulSyncAtUtc TEXT NULL,
    FOREIGN KEY(AccountId) REFERENCES Accounts(Id) ON DELETE CASCADE
);

CREATE INDEX IX_AccountShareRecords_Account_Current_SharedAt
ON AccountShareRecords(AccountId,IsCurrent,SharedAtUtc);

CREATE INDEX IX_AccountShareMetricSnapshots_Account_Share_Metric_Date
ON AccountShareMetricSnapshots(AccountId,ShareId,Metric,DateKey);
