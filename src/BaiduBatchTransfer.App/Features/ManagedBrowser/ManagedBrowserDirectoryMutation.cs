namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

/// <summary>Immutable fixed-action directory mutation evidence. It is never a selector or script carrier.</summary>
internal sealed class ManagedBrowserDirectoryMutationRequest : ManagedBrowserRequestPayload
{
    private ManagedBrowserDirectoryMutationRequest(ManagedBrowserAction action, ManagedBrowserOpenTransferSourceRequest binding,
        ManagedBrowserDirectorySnapshot expectedDirectory, string? rootBreadcrumbExactId, string? childExactId, string? childCanonicalPath, string? newSegment)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(expectedDirectory);
        if (action is not (ManagedBrowserAction.OpenTransferDirectoryRoot or ManagedBrowserAction.EnterTransferDirectory or ManagedBrowserAction.BeginCreateTransferDirectory or ManagedBrowserAction.ConfirmCreateTransferDirectory or ManagedBrowserAction.CancelTransferPicker))
            throw new ArgumentOutOfRangeException(nameof(action));
        if (expectedDirectory.BaiduUserId != binding.ExpectedBaiduUserId ||
            expectedDirectory.ComputeFingerprint(binding.SourceUri) != expectedDirectory.ObservationFingerprint ||
            (action == ManagedBrowserAction.OpenTransferDirectoryRoot &&
             (rootBreadcrumbExactId is null || expectedDirectory.AncestorExactIds[0] != rootBreadcrumbExactId)) ||
            (action == ManagedBrowserAction.EnterTransferDirectory &&
             (childExactId is null || childCanonicalPath is null ||
              !expectedDirectory.Items.Any(item => item.ExactId == childExactId &&
                  !item.ServerName.Contains('/') && !item.ServerName.Contains('\\') &&
                  childCanonicalPath == (expectedDirectory.CanonicalPath == "/"
                      ? "/" + item.ServerName : expectedDirectory.CanonicalPath + "/" + item.ServerName)))))
        {
            throw new ArgumentException("The fixed root action requires matching immutable directory evidence.");
        }
        if (rootBreadcrumbExactId is not null) ManagedBrowserDirectorySnapshot.ValidateExactId(rootBreadcrumbExactId);
        if (childExactId is not null) ManagedBrowserDirectorySnapshot.ValidateExactId(childExactId);
        if (childCanonicalPath is not null) ManagedBrowserDirectorySnapshot.ValidateCanonicalPath(childCanonicalPath);
        if (newSegment is not null) ValidateNewSegment(newSegment);
        if ((action is ManagedBrowserAction.BeginCreateTransferDirectory or ManagedBrowserAction.ConfirmCreateTransferDirectory) && newSegment is null)
            throw new ArgumentException("A fixed create action requires one exact segment.");
        Action = action; Binding = binding;
        ExpectedDirectory = expectedDirectory;
        RootBreadcrumbExactId = rootBreadcrumbExactId;
        ChildExactId = childExactId;
        ChildCanonicalPath = childCanonicalPath;
        NewSegment = newSegment;
    }

    internal ManagedBrowserAction Action { get; }
    internal ManagedBrowserOpenTransferSourceRequest Binding { get; }
    internal ManagedBrowserDirectorySnapshot ExpectedDirectory { get; }
    internal string? RootBreadcrumbExactId { get; }
    internal string? ChildExactId { get; }
    internal string? ChildCanonicalPath { get; }
    internal string? NewSegment { get; }

    internal static ManagedBrowserDirectoryMutationRequest ForOpenRoot(
        ManagedBrowserOpenTransferSourceRequest binding, ManagedBrowserDirectorySnapshot expectedDirectory,
        string rootBreadcrumbExactId) => new(ManagedBrowserAction.OpenTransferDirectoryRoot, binding, expectedDirectory,
            rootBreadcrumbExactId, null, null, null);

    internal static ManagedBrowserDirectoryMutationRequest ForEnterDirectory(
        ManagedBrowserOpenTransferSourceRequest binding, ManagedBrowserDirectorySnapshot expectedDirectory,
        string childExactId, string childCanonicalPath) => new(ManagedBrowserAction.EnterTransferDirectory, binding,
            expectedDirectory, null, childExactId, childCanonicalPath, null);

    internal static ManagedBrowserDirectoryMutationRequest ForBeginCreate(
        ManagedBrowserOpenTransferSourceRequest binding, ManagedBrowserDirectorySnapshot expectedDirectory,
        string newSegment) => new(ManagedBrowserAction.BeginCreateTransferDirectory, binding,
            expectedDirectory, null, null, null, newSegment);

    internal static ManagedBrowserDirectoryMutationRequest ForConfirmCreate(
        ManagedBrowserOpenTransferSourceRequest binding, ManagedBrowserDirectorySnapshot expectedDirectory,
        string newSegment) => new(ManagedBrowserAction.ConfirmCreateTransferDirectory, binding,
            expectedDirectory, null, null, null, newSegment);

    internal static ManagedBrowserDirectoryMutationRequest ForCancelPicker(
        ManagedBrowserOpenTransferSourceRequest binding, ManagedBrowserDirectorySnapshot expectedDirectory) =>
        new(ManagedBrowserAction.CancelTransferPicker, binding, expectedDirectory, null, null, null, null);

    private static void ValidateNewSegment(string value)
    {
        ManagedBrowserProtocol.ValidateBoundedText(value, nameof(value), 256);
        if (value is "." or ".." || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("A single safe directory segment is required.", nameof(value));
    }

    public override string ToString() => "ManagedBrowserDirectoryMutationRequest { Evidence = [REDACTED] }";
}

internal enum ManagedBrowserDirectoryMutationResultKind
{
    RootOpened = 1,
    DirectoryEntered,
    CreateDialogOpened,
    DirectoryCreated,
    PickerCancelled,
    PageNotValidated,
    AccountIdentityMismatch,
    LoginRequired,
    VerificationRequired,
    BrowserUnavailable,
    TimedOut,
    Cancelled,
    OutcomeUnknown,
}

internal sealed class ManagedBrowserDirectoryMutationResult : ManagedBrowserResult
{
    internal ManagedBrowserDirectoryMutationResult(ManagedBrowserDirectoryMutationResultKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
    }

    internal ManagedBrowserDirectoryMutationResultKind Kind { get; }
    public override string ToString() => $"ManagedBrowserDirectoryMutationResult {{ Kind = {Kind} }}";
}

/// <summary>Fixed read-only proof that the original external-share page has no picker or competing modal.</summary>
internal sealed class ManagedBrowserObserveTransferPickerClosedRequest : ManagedBrowserRequestPayload
{
    internal ManagedBrowserObserveTransferPickerClosedRequest(ManagedBrowserOpenTransferSourceRequest binding,
        ManagedBrowserSourceSnapshot expectedSource)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(expectedSource);
        if (expectedSource.BaiduUserId != binding.ExpectedBaiduUserId ||
            expectedSource.ComputeFingerprint(binding.SourceUri) != expectedSource.ObservationFingerprint)
            throw new ArgumentException("The original immutable external-share source evidence is required.");
        Binding = binding; ExpectedSource = expectedSource;
    }
    internal ManagedBrowserOpenTransferSourceRequest Binding { get; }
    internal ManagedBrowserSourceSnapshot ExpectedSource { get; }
    public override string ToString() => "ManagedBrowserObserveTransferPickerClosedRequest { Evidence = [REDACTED] }";
}

internal enum ManagedBrowserTransferPickerClosedResultKind
{
    PickerClosed = 1, PageNotValidated, AccountIdentityMismatch, LoginRequired,
    VerificationRequired, BrowserUnavailable, TimedOut, Cancelled,
}

internal sealed class ManagedBrowserTransferPickerClosedResult : ManagedBrowserResult
{
    internal ManagedBrowserTransferPickerClosedResult(ManagedBrowserTransferPickerClosedResultKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
    }
    internal ManagedBrowserTransferPickerClosedResultKind Kind { get; }
    public override string ToString() => $"ManagedBrowserTransferPickerClosedResult {{ Kind = {Kind} }}";
}
