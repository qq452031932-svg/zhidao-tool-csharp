using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

/// <summary>Dispatches a transfer-linked share only to its immutable execution snapshot.</summary>
public sealed class ExecutionBoundShareAdapter
{
    private const string BindingMismatch = "The execution session does not match the operation snapshot.";
    private const string ItemMismatch = "The share request does not match the operation's exact remote items.";
    private readonly IOperationBoundBaiduShareAdapter legacy;
    private readonly IManagedChromiumShareAdapter managed;
    private readonly ShareSubmissionEngine submissionEngine;

    public ExecutionBoundShareAdapter(
        IOperationBoundBaiduShareAdapter legacy,
        IManagedChromiumShareAdapter managed,
        ShareSubmissionEngine? submissionEngine = null)
    {
        this.legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        this.managed = managed ?? throw new ArgumentNullException(nameof(managed));
        this.submissionEngine = submissionEngine ?? new ShareSubmissionEngine();
    }

    public Task<BaiduAdapterResult<ShareDiscoverySnapshot>> DiscoverPermanentSharesForOperationAsync(
        ShareOperationAutomationContext operation,
        BaiduExecutionContext execution,
        PermanentShareRequest request,
        RetryPolicy retryPolicy,
        IAsyncDelay delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(delay);
        Validate(operation, execution, request, cancellationToken);
        return execution.Engine == BaiduExecutionEngine.WebView2V1
            ? submissionEngine.DiscoverAsync(
                legacy, execution.LegacySession!, request, retryPolicy, delay, cancellationToken)
            : managed.DiscoverPermanentSharesForOperationAsync(
                operation, execution.ManagedSession!, request, cancellationToken);
    }

    public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
        ShareOperationAutomationContext operation,
        BaiduExecutionContext execution,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        Validate(operation, execution, request, cancellationToken);
        return execution.Engine == BaiduExecutionEngine.WebView2V1
            ? legacy.PreparePermanentShareForOperationAsync(
                operation, execution.LegacySession!, request, cancellationToken)
            : managed.PreparePermanentShareForOperationAsync(
                operation, execution.ManagedSession!, request, cancellationToken);
    }

    public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
        ShareOperationAutomationContext operation,
        BaiduExecutionContext execution,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        Validate(operation, execution, request, cancellationToken);
        return execution.Engine == BaiduExecutionEngine.WebView2V1
            ? legacy.SubmitPermanentShareForOperationAsync(
                operation, execution.LegacySession!, request, cancellationToken)
            : managed.SubmitPermanentShareForOperationAsync(
                operation, execution.ManagedSession!, request, cancellationToken);
    }

    public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
        ShareOperationAutomationContext operation,
        BaiduExecutionContext execution,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        Validate(operation, execution, request, cancellationToken);
        if (!operation.IsRecovery)
            throw new InvalidOperationException("Exact share confirmation requires a recovery-only context.");
        return execution.Engine == BaiduExecutionEngine.WebView2V1
            ? legacy.ConfirmExactShareForOperationAsync(
                operation, execution.LegacySession!, request, cancellationToken)
            : managed.ConfirmExactShareForOperationAsync(
                operation, execution.ManagedSession!, request, cancellationToken);
    }

    private static void Validate(
        ShareOperationAutomationContext operation,
        BaiduExecutionContext execution,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        operation.Validate();
        if (operation.OwnerKind != WebAutomationOwnerKind.TransferShare)
            throw new InvalidOperationException("Managed execution accepts only transfer-linked share operations.");
        if (request.RemoteItems != operation.ExactRemoteItems)
            throw new InvalidOperationException(ItemMismatch);

        var profile = operation.ExecutionProfile;
        if (execution.Engine != profile.Engine)
            throw new InvalidOperationException(BindingMismatch);
        if (execution.Engine == BaiduExecutionEngine.WebView2V1)
        {
            var session = execution.LegacySession;
            if (session is null || session.LocalAccountId != operation.AccountId ||
                string.IsNullOrWhiteSpace(session.BaiduUserId) ||
                !profile.Equals(ExecutionProfileSnapshot.LegacyForAccount(operation.AccountId)))
                throw new InvalidOperationException(BindingMismatch);
            return;
        }

        if (execution.Engine == BaiduExecutionEngine.ManagedChromiumV1)
        {
            var session = execution.ManagedSession;
            if (session is null || session.AccountId != operation.AccountId ||
                session.SessionId != profile.SessionId || session.InstallationId != profile.InstallationId ||
                session.BrowserFamily != profile.BrowserFamily ||
                !string.Equals(session.ProfileId, profile.ProfileId, StringComparison.Ordinal))
                throw new InvalidOperationException(BindingMismatch);
            return;
        }
        throw new InvalidOperationException(BindingMismatch);
    }
}
