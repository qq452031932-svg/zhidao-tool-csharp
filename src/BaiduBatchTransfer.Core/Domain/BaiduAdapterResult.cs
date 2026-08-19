namespace BaiduBatchTransfer.Core.Domain;

public static class BaiduAdapterResult
{
    public static BaiduAdapterResult<object> Failure(BaiduErrorKind errorKind, string? adapterDiagnostic) =>
        BaiduAdapterResult<object>.Failure(errorKind, adapterDiagnostic);

    internal static BaiduErrorScope GetScope(BaiduErrorKind errorKind) => errorKind switch
    {
        BaiduErrorKind.InvalidLink or
        BaiduErrorKind.InvalidExtractionCode => BaiduErrorScope.Resource,

        BaiduErrorKind.ExpiredLogin or
        BaiduErrorKind.VerificationRequired => BaiduErrorScope.Account,

        BaiduErrorKind.InsufficientCapacity or
        BaiduErrorKind.InvalidTargetPath or
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
        string? safeMessage)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorKind = errorKind;
        Scope = scope;
        SafeMessage = safeMessage;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public BaiduErrorKind? ErrorKind { get; }

    public BaiduErrorScope? Scope { get; }

    public string? SafeMessage { get; }

    public static BaiduAdapterResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new BaiduAdapterResult<T>(true, value, null, null, null);
    }

    public static BaiduAdapterResult<T> Failure(BaiduErrorKind errorKind, string? adapterDiagnostic)
    {
        // Diagnostics may contain credentials and are deliberately excluded from the public result.
        _ = adapterDiagnostic;
        var scope = BaiduAdapterResult.GetScope(errorKind);
        return new BaiduAdapterResult<T>(
            false,
            default,
            errorKind,
            scope,
            BaiduAdapterResult.GetSafeMessage(errorKind));
    }
}
