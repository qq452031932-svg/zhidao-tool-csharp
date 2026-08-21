namespace BaiduBatchTransfer.Core.Domain;

public static class BaiduAdapterResult
{
    public static BaiduAdapterResult<object> Failure(
        BaiduErrorKind errorKind,
        string? adapterDiagnostic,
        TimeSpan? retryAfter = null,
        SubmissionAcceptance submissionAcceptance = SubmissionAcceptance.Unknown) =>
        BaiduAdapterResult<object>.Failure(errorKind, adapterDiagnostic, retryAfter, submissionAcceptance);

    internal static BaiduErrorScope GetScope(BaiduErrorKind errorKind) => errorKind switch
    {
        BaiduErrorKind.InvalidLink or
        BaiduErrorKind.InvalidExtractionCode => BaiduErrorScope.Resource,

        BaiduErrorKind.ExpiredLogin or
        BaiduErrorKind.VerificationRequired => BaiduErrorScope.Account,

        BaiduErrorKind.InsufficientCapacity or
        BaiduErrorKind.InvalidTargetPath or
        BaiduErrorKind.NotValidated or
        BaiduErrorKind.TransientService => BaiduErrorScope.Operation,

        BaiduErrorKind.UnknownResponse => BaiduErrorScope.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(errorKind), errorKind, "Unknown adapter error kind.")
    };

    internal static string GetSafeMessage(BaiduErrorKind errorKind) => errorKind switch
    {
        BaiduErrorKind.InvalidLink => "The share link is invalid.",
        BaiduErrorKind.InvalidExtractionCode => "The extraction code is invalid.",
        BaiduErrorKind.ExpiredLogin => "The account session has expired.",
        BaiduErrorKind.VerificationRequired => "Account verification is required.",
        BaiduErrorKind.InsufficientCapacity => "The account has insufficient capacity.",
        BaiduErrorKind.InvalidTargetPath => "The target path is invalid.",
        BaiduErrorKind.TransientService => "The service is temporarily unavailable.",
        BaiduErrorKind.UnknownResponse => "The service returned an unrecognized response.",
        BaiduErrorKind.NotValidated => "百度转存或分享能力尚未通过现场验收。",
        _ => throw new ArgumentOutOfRangeException(nameof(errorKind), errorKind, "Unknown adapter error kind.")
    };
}

public sealed class BaiduAdapterResult<T>
{
    private BaiduAdapterResult(
        bool isSuccess,
        T? value,
        BaiduErrorKind? errorKind,
        BaiduErrorScope? scope,
        string? safeMessage,
        TimeSpan? retryAfter,
        SubmissionAcceptance submissionAcceptance)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorKind = errorKind;
        Scope = scope;
        SafeMessage = safeMessage;
        RetryAfter = retryAfter;
        SubmissionAcceptance = submissionAcceptance;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public BaiduErrorKind? ErrorKind { get; }

    public BaiduErrorScope? Scope { get; }

    public string? SafeMessage { get; }

    public TimeSpan? RetryAfter { get; }

    public SubmissionAcceptance SubmissionAcceptance { get; }

    public static BaiduAdapterResult<T> Success(T value)
        => Success(value, SubmissionAcceptance.Unknown);

    public static BaiduAdapterResult<T> Success(
        T value,
        SubmissionAcceptance submissionAcceptance)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new BaiduAdapterResult<T>(true, value, null, null, null, null, submissionAcceptance);
    }

    public static BaiduAdapterResult<T> Failure(
        BaiduErrorKind errorKind,
        string? adapterDiagnostic,
        TimeSpan? retryAfter = null,
        SubmissionAcceptance submissionAcceptance = SubmissionAcceptance.Unknown)
    {
        if (retryAfter < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryAfter), "The server retry delay cannot be negative.");
        // Diagnostics may contain credentials and are deliberately excluded from the public result.
        _ = adapterDiagnostic;
        var scope = BaiduAdapterResult.GetScope(errorKind);
        return new BaiduAdapterResult<T>(
            false,
            default,
            errorKind,
            scope,
            BaiduAdapterResult.GetSafeMessage(errorKind),
            retryAfter,
            submissionAcceptance);
    }
}
