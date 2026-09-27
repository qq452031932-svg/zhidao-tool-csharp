namespace BaiduBatchTransfer.Core.Domain;

public enum BaiduErrorKind
{
    InvalidLink,
    InvalidExtractionCode,
    ExpiredLogin,
    VerificationRequired,
    InsufficientCapacity,
    InvalidTargetPath,
    DuplicateSkipped,
    FileCountLimitExceeded,
    TransientService,
    UnknownResponse,
    NotValidated,
}
