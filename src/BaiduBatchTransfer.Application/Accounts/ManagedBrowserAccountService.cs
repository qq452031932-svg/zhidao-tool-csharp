using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Accounts;

public enum ManagedBrowserLoginRequestKind { AddNew, ReloginExisting }

/// <summary>The caller reserves an add ID once. Retries and existing-account login never allocate another ID.</summary>
public sealed class ManagedBrowserLoginRequest
{
    private ManagedBrowserLoginRequest(ManagedBrowserLoginRequestKind kind, Guid accountId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("A local account ID is required.", nameof(accountId));
        Kind = kind;
        AccountId = accountId;
    }

    public ManagedBrowserLoginRequestKind Kind { get; }
    public Guid AccountId { get; }
    public static ManagedBrowserLoginRequest AddNew(Guid reservedAccountId) => new(ManagedBrowserLoginRequestKind.AddNew, reservedAccountId);
    public static ManagedBrowserLoginRequest ReloginExisting(Guid accountId) => new(ManagedBrowserLoginRequestKind.ReloginExisting, accountId);
    public override string ToString() => $"ManagedBrowserLoginRequest {{ Kind = {Kind}, AccountId = [REDACTED] }}";
}

/// <summary>
/// Accepts only the typed, observed stable identity tuple. This path never consumes a cookie session,
/// calls a legacy adapter, protects a session, or changes the account ID selected by the caller.
/// The store performs account/UID validation again while holding the transaction's writer lock.
/// </summary>
public sealed class ManagedBrowserAccountService(IManagedBrowserLoginStore store)
{
    private readonly IManagedBrowserLoginStore store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<ManagedBrowserBindingStatus> BindAsync(ManagedBrowserLoginRequest request,
        BrowserInstallationIdentity installation, ManagedBrowserSessionReference observedSession,
        StoredManagedBrowserSession? expectedCurrent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(observedSession);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.AccountId != observedSession.AccountId ||
            (expectedCurrent is not null && (expectedCurrent.Identity.AccountId != request.AccountId ||
                request.Kind == ManagedBrowserLoginRequestKind.AddNew)))
            return Task.FromResult(ManagedBrowserBindingStatus.Conflict);
        return store.BindLoginAsync(request.Kind == ManagedBrowserLoginRequestKind.AddNew,
            installation, observedSession, expectedCurrent, cancellationToken);
    }
}
