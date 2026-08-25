-- v8 acceptance rows did not bind the selector catalog. They cannot safely be
-- promoted to the four-part key, so this migration deliberately invalidates them.
DROP TABLE AdapterCapabilityAcceptances;

CREATE TABLE AdapterCapabilityAcceptances (
    AdapterKind TEXT NOT NULL,
    ProtocolVersion TEXT NOT NULL,
    SelectorVersion TEXT NOT NULL,
    AdapterBuildVersion TEXT NOT NULL,
    AcceptedAt TEXT NOT NULL,
    EvidenceHash TEXT NOT NULL CHECK(length(EvidenceHash)=64 AND EvidenceHash NOT GLOB '*[^0-9A-F]*'),
    PRIMARY KEY(AdapterKind, ProtocolVersion, SelectorVersion, AdapterBuildVersion)
);
