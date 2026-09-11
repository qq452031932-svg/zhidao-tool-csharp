using BaiduBatchTransfer.Core.Domain;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed class ManagedBrowserTransferSubmissionContext
{
    public ManagedBrowserTransferSubmissionContext(
        Guid browserId, string targetId, Uri devToolsEndpoint, int timeoutMilliseconds,
        string sourceUri, string baiduUserId, string sourceGenerationToken, string sourceObservationFingerprint,
        string sourceFingerprint, string sourceExactId, ManagedBrowserSourceItemKind sourceItemKind,
        string selectedTargetExactId, string selectedTargetCanonicalPath,
        string targetBaselineFingerprint, DuplicatePolicy duplicatePolicy)
    {
        var source = new ManagedBrowserOpenTransferSourceRequest(
            browserId, targetId, devToolsEndpoint, timeoutMilliseconds, sourceUri, baiduUserId);
        ManagedBrowserProtocol.ValidateBoundedText(sourceGenerationToken, nameof(sourceGenerationToken), 256);
        ValidateFingerprint(sourceObservationFingerprint, nameof(sourceObservationFingerprint));
        ValidateFingerprint(sourceFingerprint, nameof(sourceFingerprint));
        ManagedBrowserTargetInventoryValidation.ValidateExactId(sourceExactId, nameof(sourceExactId));
        if (!Enum.IsDefined(sourceItemKind)) throw new ArgumentOutOfRangeException(nameof(sourceItemKind));
        ManagedBrowserTargetInventoryValidation.ValidateExactId(selectedTargetExactId, nameof(selectedTargetExactId));
        ManagedBrowserTargetInventoryValidation.ValidateCanonicalPath(selectedTargetCanonicalPath);
        ValidateFingerprint(targetBaselineFingerprint, nameof(targetBaselineFingerprint));
        if (!Enum.IsDefined(duplicatePolicy)) throw new ArgumentOutOfRangeException(nameof(duplicatePolicy));

        BrowserId = source.BrowserId;
        TargetId = source.TargetId;
        DevToolsEndpoint = source.DevToolsEndpoint;
        TimeoutMilliseconds = source.TimeoutMilliseconds;
        SourceUri = source.SourceUri;
        BaiduUserId = baiduUserId;
        SourceGenerationToken = sourceGenerationToken;
        SourceObservationFingerprint = sourceObservationFingerprint;
        SourceFingerprint = sourceFingerprint;
        SourceExactId = sourceExactId;
        SourceItemKind = sourceItemKind;
        SelectedTargetExactId = selectedTargetExactId;
        SelectedTargetCanonicalPath = selectedTargetCanonicalPath;
        TargetBaselineFingerprint = targetBaselineFingerprint;
        DuplicatePolicy = duplicatePolicy;
    }

    public Guid BrowserId { get; }
    public string TargetId { get; }
    public Uri DevToolsEndpoint { get; }
    public int TimeoutMilliseconds { get; }
    public string SourceUri { get; }
    public string BaiduUserId { get; }
    public string SourceGenerationToken { get; }
    public string SourceObservationFingerprint { get; }
    public string SourceFingerprint { get; }
    public string SourceExactId { get; }
    public ManagedBrowserSourceItemKind SourceItemKind { get; }
    public string SelectedTargetExactId { get; }
    public string SelectedTargetCanonicalPath { get; }
    public string TargetBaselineFingerprint { get; }
    public DuplicatePolicy DuplicatePolicy { get; }

    internal bool EqualsEvidence(ManagedBrowserTransferSubmissionContext other) =>
        BrowserId == other.BrowserId &&
        string.Equals(TargetId, other.TargetId, StringComparison.Ordinal) &&
        string.Equals(DevToolsEndpoint.AbsoluteUri, other.DevToolsEndpoint.AbsoluteUri, StringComparison.Ordinal) &&
        TimeoutMilliseconds == other.TimeoutMilliseconds &&
        string.Equals(SourceUri, other.SourceUri, StringComparison.Ordinal) &&
        string.Equals(BaiduUserId, other.BaiduUserId, StringComparison.Ordinal) &&
        string.Equals(SourceGenerationToken, other.SourceGenerationToken, StringComparison.Ordinal) &&
        string.Equals(SourceObservationFingerprint, other.SourceObservationFingerprint, StringComparison.Ordinal) &&
        string.Equals(SourceFingerprint, other.SourceFingerprint, StringComparison.Ordinal) &&
        string.Equals(SourceExactId, other.SourceExactId, StringComparison.Ordinal) &&
        SourceItemKind == other.SourceItemKind &&
        string.Equals(SelectedTargetExactId, other.SelectedTargetExactId, StringComparison.Ordinal) &&
        string.Equals(SelectedTargetCanonicalPath, other.SelectedTargetCanonicalPath, StringComparison.Ordinal) &&
        string.Equals(TargetBaselineFingerprint, other.TargetBaselineFingerprint, StringComparison.Ordinal) &&
        DuplicatePolicy == other.DuplicatePolicy;

    internal static void ValidateFingerprint(string value, string parameterName)
    {
        if (!ManagedBrowserProtocol.IsNonce(value))
            throw new ArgumentException("A lowercase SHA256 fingerprint is required.", parameterName);
    }

    public override string ToString() =>
        $"ManagedBrowserTransferSubmissionContext {{ TimeoutMilliseconds = {TimeoutMilliseconds}, DuplicatePolicy = {DuplicatePolicy}, Evidence = [REDACTED] }}";
}

public sealed class ManagedBrowserTransferSubmissionReadiness
{
    private const string FingerprintDomain = "ManagedBrowserTransferSubmissionReadiness/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ManagedBrowserTransferSubmissionReadiness(
        ManagedBrowserTransferSubmissionContext context, int eligibleControlCount)
        : this(context, eligibleControlCount, null) { }

    internal ManagedBrowserTransferSubmissionReadiness(
        ManagedBrowserTransferSubmissionContext context, int eligibleControlCount,
        string? expectedReadinessFingerprint)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        if (eligibleControlCount != 1) throw new ArgumentOutOfRangeException(nameof(eligibleControlCount));
        EligibleControlCount = eligibleControlCount;
        ReadinessFingerprint = ComputeFingerprint();
        if (expectedReadinessFingerprint is not null)
        {
            ManagedBrowserTransferSubmissionContext.ValidateFingerprint(
                expectedReadinessFingerprint, nameof(expectedReadinessFingerprint));
            if (!CryptographicOperations.FixedTimeEquals(
                    StrictUtf8.GetBytes(ReadinessFingerprint),
                    StrictUtf8.GetBytes(expectedReadinessFingerprint)))
                throw new ArgumentException("The readiness fingerprint does not match.", nameof(expectedReadinessFingerprint));
        }
    }

    public ManagedBrowserTransferSubmissionContext Context { get; }
    public int EligibleControlCount { get; }
    public string ReadinessFingerprint { get; }

    internal static void ValidateFingerprintForContext(
        ManagedBrowserTransferSubmissionContext context, string readinessFingerprint,
        string parameterName)
    {
        ManagedBrowserTransferSubmissionContext.ValidateFingerprint(readinessFingerprint, parameterName);
        var expected = new ManagedBrowserTransferSubmissionReadiness(context, 1).ReadinessFingerprint;
        if (!CryptographicOperations.FixedTimeEquals(
                StrictUtf8.GetBytes(expected), StrictUtf8.GetBytes(readinessFingerprint)))
            throw new ArgumentException("The readiness fingerprint does not match the immutable evidence.", parameterName);
    }

    private string ComputeFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Field(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        Field(FingerprintDomain);
        Field(Context.BrowserId.ToString("D"));
        Field(Context.TargetId);
        Field(Context.DevToolsEndpoint.AbsoluteUri);
        Field(Context.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
        Field(Context.SourceUri);
        Field(Context.BaiduUserId);
        Field(Context.SourceGenerationToken);
        Field(Context.SourceObservationFingerprint);
        Field(Context.SourceFingerprint);
        Field(Context.SourceExactId);
        Field(Context.SourceItemKind.ToString());
        Field(Context.SelectedTargetExactId);
        Field(Context.SelectedTargetCanonicalPath);
        Field(Context.TargetBaselineFingerprint);
        Field(Context.DuplicatePolicy.ToString());
        Field(EligibleControlCount.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public override string ToString() =>
        $"ManagedBrowserTransferSubmissionReadiness {{ EligibleControlCount = {EligibleControlCount}, Evidence = [REDACTED] }}";
}

public sealed class ManagedBrowserObserveTransferSubmissionReadyRequest(
    ManagedBrowserTransferSubmissionContext context) : ManagedBrowserRequestPayload
{
    public ManagedBrowserTransferSubmissionContext Context { get; } =
        context ?? throw new ArgumentNullException(nameof(context));
    public override string ToString() =>
        "ManagedBrowserObserveTransferSubmissionReadyRequest { Evidence = [REDACTED] }";
}

public sealed class ManagedBrowserSubmitTransferOnceRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserSubmitTransferOnceRequest(
        ManagedBrowserTransferSubmissionContext context, string readinessFingerprint)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ManagedBrowserTransferSubmissionReadiness.ValidateFingerprintForContext(
            Context, readinessFingerprint, nameof(readinessFingerprint));
        ReadinessFingerprint = readinessFingerprint;
    }
    public ManagedBrowserTransferSubmissionContext Context { get; }
    public string ReadinessFingerprint { get; }
    public override string ToString() =>
        "ManagedBrowserSubmitTransferOnceRequest { Evidence = [REDACTED] }";
}

public sealed class ManagedBrowserObserveTransferSubmissionOutcomeRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserObserveTransferSubmissionOutcomeRequest(
        ManagedBrowserTransferSubmissionContext context, string readinessFingerprint)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ManagedBrowserTransferSubmissionReadiness.ValidateFingerprintForContext(
            Context, readinessFingerprint, nameof(readinessFingerprint));
        ReadinessFingerprint = readinessFingerprint;
    }
    public ManagedBrowserTransferSubmissionContext Context { get; }
    public string ReadinessFingerprint { get; }
    public override string ToString() =>
        "ManagedBrowserObserveTransferSubmissionOutcomeRequest { Evidence = [REDACTED] }";
}

public enum ManagedBrowserTransferSubmissionReadinessResultKind
{
    Ready = 1,
    PageNotValidated,
    AccountIdentityMismatch,
    LoginRequired,
    VerificationRequired,
    BrowserUnavailable,
    TimedOut,
    Cancelled,
}

public sealed class ManagedBrowserTransferSubmissionReadinessResult : ManagedBrowserResult
{
    public ManagedBrowserTransferSubmissionReadinessResult(
        ManagedBrowserTransferSubmissionReadinessResultKind kind,
        ManagedBrowserTransferSubmissionReadiness? readiness = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if ((kind == ManagedBrowserTransferSubmissionReadinessResultKind.Ready) != (readiness is not null))
            throw new ArgumentException("Only Ready may contain readiness evidence.", nameof(readiness));
        Kind = kind;
        Readiness = readiness;
    }
    public ManagedBrowserTransferSubmissionReadinessResultKind Kind { get; }
    public ManagedBrowserTransferSubmissionReadiness? Readiness { get; }
    public override string ToString() =>
        $"ManagedBrowserTransferSubmissionReadinessResult {{ Kind = {Kind}, Evidence = [REDACTED] }}";
}

public enum ManagedBrowserTransferSubmitDispatchState
{
    KnownNotDispatched = 1,
    ClickDispatched = 2,
    DispatchOutcomeUnknown = 3,
}

public enum ManagedBrowserTransferSubmitResultKind
{
    ClickAttempted = 1,
    ReadinessMismatch,
    PageNotValidated,
    AccountIdentityMismatch,
    LoginRequired,
    VerificationRequired,
    BrowserUnavailable,
    TimedOut,
    Cancelled,
}

public sealed class ManagedBrowserSubmitTransferOnceResult : ManagedBrowserResult
{
    public ManagedBrowserSubmitTransferOnceResult(
        ManagedBrowserTransferSubmitResultKind kind,
        ManagedBrowserTransferSubmitDispatchState dispatchState)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(dispatchState)) throw new ArgumentOutOfRangeException(nameof(dispatchState));
        var valid = (kind, dispatchState) switch
        {
            (ManagedBrowserTransferSubmitResultKind.ClickAttempted,
                ManagedBrowserTransferSubmitDispatchState.ClickDispatched or
                ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown) => true,
            (ManagedBrowserTransferSubmitResultKind.ReadinessMismatch or
                ManagedBrowserTransferSubmitResultKind.PageNotValidated or
                ManagedBrowserTransferSubmitResultKind.AccountIdentityMismatch or
                ManagedBrowserTransferSubmitResultKind.LoginRequired or
                ManagedBrowserTransferSubmitResultKind.VerificationRequired,
                ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched) => true,
            (ManagedBrowserTransferSubmitResultKind.BrowserUnavailable or
                ManagedBrowserTransferSubmitResultKind.TimedOut or
                ManagedBrowserTransferSubmitResultKind.Cancelled,
                ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched or
                ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown) => true,
            _ => false,
        };
        if (!valid)
            throw new ArgumentException("The submit result and dispatch state are inconsistent.", nameof(dispatchState));
        Kind = kind;
        DispatchState = dispatchState;
    }
    public ManagedBrowserTransferSubmitResultKind Kind { get; }
    public ManagedBrowserTransferSubmitDispatchState DispatchState { get; }
    public override string ToString() =>
        $"ManagedBrowserSubmitTransferOnceResult {{ Kind = {Kind}, DispatchState = {DispatchState} }}";
}

public enum ManagedBrowserTransferOutcomeKind
{
    AcceptedBound = 1,
    UnboundAcknowledgement,
    ExplicitlyRejected,
    Conflict,
    PageNotValidated,
    AccountIdentityMismatch,
    LoginRequired,
    VerificationRequired,
    BrowserUnavailable,
    TimedOut,
    Cancelled,
}

public sealed class ManagedBrowserTransferSubmissionOutcomeResult : ManagedBrowserResult
{
    private const string FingerprintDomain = "ManagedBrowserTransferSubmissionOutcome/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ManagedBrowserTransferSubmissionOutcomeResult(
        ManagedBrowserTransferOutcomeKind kind, string? officialRequestId = null,
        string? exactTargetId = null, string? sourceExactId = null,
        string? destinationExactId = null, string? officialObservationFingerprint = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var hasBinding = officialRequestId is not null || exactTargetId is not null || sourceExactId is not null ||
                         destinationExactId is not null || officialObservationFingerprint is not null;
        if (kind != ManagedBrowserTransferOutcomeKind.AcceptedBound)
        {
            if (hasBinding) throw new ArgumentException("Only AcceptedBound may contain official binding evidence.");
        }
        else
        {
            ManagedBrowserProtocol.ValidateBoundedText(officialRequestId, nameof(officialRequestId), 256);
            ManagedBrowserTargetInventoryValidation.ValidateExactId(exactTargetId!, nameof(exactTargetId));
            ManagedBrowserTargetInventoryValidation.ValidateExactId(sourceExactId!, nameof(sourceExactId));
            ManagedBrowserTargetInventoryValidation.ValidateExactId(destinationExactId!, nameof(destinationExactId));
            ManagedBrowserTransferSubmissionContext.ValidateFingerprint(
                officialObservationFingerprint!, nameof(officialObservationFingerprint));
            var computed = ComputeOfficialFingerprint(
                officialRequestId!, exactTargetId!, sourceExactId!, destinationExactId!);
            if (!CryptographicOperations.FixedTimeEquals(
                    StrictUtf8.GetBytes(computed), StrictUtf8.GetBytes(officialObservationFingerprint!)))
                throw new ArgumentException("The official observation fingerprint does not match.",
                    nameof(officialObservationFingerprint));
        }

        Kind = kind;
        OfficialRequestId = officialRequestId;
        ExactTargetId = exactTargetId;
        SourceExactId = sourceExactId;
        DestinationExactId = destinationExactId;
        OfficialObservationFingerprint = officialObservationFingerprint;
    }

    public ManagedBrowserTransferOutcomeKind Kind { get; }
    public string? OfficialRequestId { get; }
    public string? ExactTargetId { get; }
    public string? SourceExactId { get; }
    public string? DestinationExactId { get; }
    public string? OfficialObservationFingerprint { get; }

    public static ManagedBrowserTransferSubmissionOutcomeResult CreateAcceptedBound(
        string officialRequestId, string exactTargetId, string sourceExactId, string destinationExactId) =>
        new(ManagedBrowserTransferOutcomeKind.AcceptedBound, officialRequestId, exactTargetId,
            sourceExactId, destinationExactId,
            ComputeOfficialFingerprint(officialRequestId, exactTargetId, sourceExactId, destinationExactId));

    internal static string ComputeOfficialFingerprint(
        string officialRequestId, string exactTargetId, string sourceExactId, string destinationExactId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Field(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        Field(FingerprintDomain); Field(officialRequestId); Field(exactTargetId);
        Field(sourceExactId); Field(destinationExactId);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public override string ToString() =>
        $"ManagedBrowserTransferSubmissionOutcomeResult {{ Kind = {Kind}, Evidence = [REDACTED] }}";
}
