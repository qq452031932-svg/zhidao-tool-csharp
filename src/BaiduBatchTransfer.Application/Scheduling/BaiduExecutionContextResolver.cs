using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public interface IBaiduExecutionContextResolver
{
    Task<BaiduExecutionResolution> ResolveAsync(Guid accountId, ExecutionProfileSnapshot profile, CancellationToken cancellationToken);
}

public enum BaiduExecutionResolutionStatus
{
    Ready,
    AccountUnavailable,
    LoginRequired,
    BrowserUnavailable,
    AccountIdentityMismatch
}

public sealed class BaiduExecutionResolution
{
    private BaiduExecutionResolution(BaiduExecutionResolutionStatus status, BaiduExecutionContext? context)
    {
        Status = status;
        Context = context;
    }

    public BaiduExecutionResolutionStatus Status { get; }
    public BaiduExecutionContext? Context { get; }
    public string SafeMessage => Status switch
    {
        BaiduExecutionResolutionStatus.Ready => "Execution context is ready.",
        BaiduExecutionResolutionStatus.AccountUnavailable => "The execution account is unavailable.",
        BaiduExecutionResolutionStatus.LoginRequired => "The execution account requires login.",
        BaiduExecutionResolutionStatus.BrowserUnavailable => "The recorded browser execution session is unavailable.",
        _ => "The recorded execution identity does not match the account."
    };

    public static BaiduExecutionResolution Ready(BaiduExecutionContext context) =>
        new(BaiduExecutionResolutionStatus.Ready, context ?? throw new ArgumentNullException(nameof(context)));

    public static BaiduExecutionResolution Unavailable(BaiduExecutionResolutionStatus status)
    {
        if (status is not (BaiduExecutionResolutionStatus.AccountUnavailable or BaiduExecutionResolutionStatus.LoginRequired or
            BaiduExecutionResolutionStatus.BrowserUnavailable or BaiduExecutionResolutionStatus.AccountIdentityMismatch))
            throw new ArgumentOutOfRangeException(nameof(status), status, "An unavailable resolution status is required.");
        return new(status, null);
    }

    public override string ToString() => $"BaiduExecutionResolution {{ Status = {Status}, Context = [REDACTED] }}";
}

/// <summary>Validates persisted bindings only; live UID, profile ownership, and write authorization remain separate checks.</summary>
public sealed class BaiduExecutionContextResolver : IBaiduExecutionContextResolver
{
    private readonly IBaiduExecutionBindingStore bindingStore;
    private readonly IBaiduSessionStore sessionStore;

    public BaiduExecutionContextResolver(IBaiduExecutionBindingStore bindingStore, IBaiduSessionStore sessionStore)
    {
        this.bindingStore = bindingStore ?? throw new ArgumentNullException(nameof(bindingStore));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
    }

    public async Task<BaiduExecutionResolution> ResolveAsync(Guid accountId, ExecutionProfileSnapshot profile, CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account ID is required.", nameof(accountId));
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var binding = await bindingStore.LoadAsync(accountId, profile, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var account = binding.Account;
        if (account is not { Status: "Active", IsEnabled: true, IsDeleted: false })
            return BaiduExecutionResolution.Unavailable(BaiduExecutionResolutionStatus.AccountUnavailable);
        if (account.Id != accountId || string.IsNullOrWhiteSpace(account.BaiduUserId))
            return BaiduExecutionResolution.Unavailable(BaiduExecutionResolutionStatus.AccountIdentityMismatch);

        if (profile.Engine == BaiduExecutionEngine.WebView2V1)
        {
            if (!profile.Equals(ExecutionProfileSnapshot.LegacyForAccount(accountId)))
                return BaiduExecutionResolution.Unavailable(BaiduExecutionResolutionStatus.AccountIdentityMismatch);

            var legacy = await sessionStore.LoadAsync(accountId.ToString("D"), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (legacy is null)
                return BaiduExecutionResolution.Unavailable(BaiduExecutionResolutionStatus.LoginRequired);
            if (!legacy.IsBoundTo(accountId, account.BaiduUserId))
                return BaiduExecutionResolution.Unavailable(BaiduExecutionResolutionStatus.AccountIdentityMismatch);
            return BaiduExecutionResolution.Ready(BaiduExecutionContext.Legacy(legacy));
        }

        if (binding.Installation is not { State: BrowserInstallationLifecycleState.Active } installation ||
            binding.Session is not { State: ManagedBrowserSessionLifecycleState.Active } session)
            return BaiduExecutionResolution.Unavailable(BaiduExecutionResolutionStatus.BrowserUnavailable);

        var identity = session.Identity;
        if (installation.Identity.InstallationId != profile.InstallationId ||
            installation.Identity.BrowserFamily != profile.BrowserFamily ||
            identity.SessionId != profile.SessionId ||
            identity.InstallationId != profile.InstallationId ||
            identity.AccountId != accountId ||
            identity.BrowserFamily != profile.BrowserFamily ||
            !string.Equals(identity.ProfileId, profile.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(identity.StableBaiduUserId, account.BaiduUserId, StringComparison.Ordinal))
            return BaiduExecutionResolution.Unavailable(BaiduExecutionResolutionStatus.AccountIdentityMismatch);

        return BaiduExecutionResolution.Ready(BaiduExecutionContext.Managed(identity));
    }
}
