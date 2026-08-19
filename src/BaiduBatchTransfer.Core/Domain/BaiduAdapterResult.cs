namespace BaiduBatchTransfer.Core.Domain;

public static class BaiduAdapterResult
{
    public const string SensitiveDetailsRemovedMessage = "Sensitive details were removed.";
    public const string UnavailableSafeMessage = "The operation could not be completed.";

    private static readonly string[] UnsafeMessageMarkers =
    [
        "authorization",
        "proxy-authorization",
        "cookie",
        "set-cookie",
        "bduss",
        "stoken",
        "ptoken",
        "token",
        "bearer",
        "sessionid",
        "secret"
    ];

    public static BaiduAdapterResult<object> Failure(BaiduErrorKind errorKind, string? safeMessage) =>
        BaiduAdapterResult<object>.Failure(errorKind, safeMessage);

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

    internal static string SanitizeMessage(string? safeMessage)
    {
        if (string.IsNullOrWhiteSpace(safeMessage))
        {
            return UnavailableSafeMessage;
        }

        var trimmedMessage = safeMessage.Trim();
        foreach (var marker in UnsafeMessageMarkers)
        {
            if (trimmedMessage.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return SensitiveDetailsRemovedMessage;
            }
        }

        return trimmedMessage;
    }
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

    public static BaiduAdapterResult<T> Failure(BaiduErrorKind errorKind, string? safeMessage)
    {
        var scope = BaiduAdapterResult.GetScope(errorKind);
        return new BaiduAdapterResult<T>(
            false,
            default,
            errorKind,
            scope,
            BaiduAdapterResult.SanitizeMessage(safeMessage));
    }
}
