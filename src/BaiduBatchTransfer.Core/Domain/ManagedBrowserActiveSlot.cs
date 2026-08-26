namespace BaiduBatchTransfer.Core.Domain;

public sealed record ManagedBrowserInstallationActivationRequest
{
    public ManagedBrowserInstallationActivationRequest(
        StoredBrowserInstallation? expectedCurrent,
        StoredBrowserInstallation target)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (expectedCurrent is not null && expectedCurrent.Identity.InstallationId == target.Identity.InstallationId)
            throw new ArgumentException("The expected current and target identities must differ.", nameof(target));

        ExpectedCurrent = expectedCurrent;
    }

    public StoredBrowserInstallation? ExpectedCurrent { get; }

    public StoredBrowserInstallation Target { get; }
}

public sealed record ManagedBrowserSessionActivationRequest
{
    public ManagedBrowserSessionActivationRequest(
        StoredManagedBrowserSession? expectedCurrent,
        StoredManagedBrowserSession target)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (expectedCurrent is not null)
        {
            if (expectedCurrent.Identity.SessionId == target.Identity.SessionId)
                throw new ArgumentException("The expected current and target identities must differ.", nameof(target));
            if (expectedCurrent.Identity.AccountId != target.Identity.AccountId ||
                expectedCurrent.Identity.BrowserFamily != target.Identity.BrowserFamily)
                throw new ArgumentException("The expected current and target must share an account and browser family.", nameof(target));
        }

        ExpectedCurrent = expectedCurrent;
    }

    public StoredManagedBrowserSession? ExpectedCurrent { get; }

    public StoredManagedBrowserSession Target { get; }
}

public sealed record ManagedBrowserActiveSlotTransitionResult<T> where T : class
{
    public ManagedBrowserActiveSlotTransitionResult(
        ManagedBrowserTransitionStatus status,
        T? current)
    {
        if (status is not (ManagedBrowserTransitionStatus.Applied or
                           ManagedBrowserTransitionStatus.Conflict or
                           ManagedBrowserTransitionStatus.NotFound))
            throw new ArgumentOutOfRangeException(nameof(status), status, "A transition status is required.");
        if (status is ManagedBrowserTransitionStatus.Applied or ManagedBrowserTransitionStatus.Conflict)
        {
            if (current is null)
                throw new ArgumentException("An applied or conflicting slot transition must report its current row.", nameof(current));
        }
        else if (current is not null)
        {
            throw new ArgumentException("A missing slot cannot report a current row.", nameof(current));
        }

        Status = status;
        Current = current;
    }

    public ManagedBrowserTransitionStatus Status { get; }

    public T? Current { get; }

    public static ManagedBrowserActiveSlotTransitionResult<T> Applied(T current) =>
        new(ManagedBrowserTransitionStatus.Applied, current);

    public static ManagedBrowserActiveSlotTransitionResult<T> Conflict(T current) =>
        new(ManagedBrowserTransitionStatus.Conflict, current);

    public static ManagedBrowserActiveSlotTransitionResult<T> NotFound() =>
        new(ManagedBrowserTransitionStatus.NotFound, null);

    public override string ToString() =>
        $"ManagedBrowserActiveSlotTransitionResult {{ Status = {Status}, Current = " +
        (Current is null ? "null" : "[REDACTED]") + " }}";
}
