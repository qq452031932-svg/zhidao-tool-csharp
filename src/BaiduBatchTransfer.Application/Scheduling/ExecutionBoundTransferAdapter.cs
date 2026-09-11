using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

/// <summary>
/// Dispatches only to the operation's persisted execution engine after matching the supplied snapshot.
/// This is not a live UID/ownership check or write authorization. Concrete adapters must still verify
/// page identity, profile ownership, the write permit and the persisted submission boundary.
/// </summary>
public sealed class ExecutionBoundTransferAdapter
{
    private const string BindingMismatch = "The execution session does not match the operation snapshot.";
    private const string SourceMismatch = "The transfer source does not match the operation.";
    private readonly IOperationBoundBaiduNetdiskAdapter _legacy;
    private readonly IManagedChromiumTransferAdapter _managed;

    public ExecutionBoundTransferAdapter(IOperationBoundBaiduNetdiskAdapter legacy, IManagedChromiumTransferAdapter managed)
    {
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        _managed = managed ?? throw new ArgumentNullException(nameof(managed));
    }

    public Task<BaiduAdapterResult<TransferDirectoryPreparationOutcome>> EnsureDirectoryForOperationAsync(
        TransferOperationAutomationContext operation, BaiduExecutionContext execution, RemotePath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ValidateBinding(operation, execution, cancellationToken);
        return execution.Engine == BaiduExecutionEngine.WebView2V1
            ? _legacy.EnsureDirectoryForOperationAsync(operation, execution.LegacySession!, path, cancellationToken)
            : _managed.EnsureDirectoryForOperationAsync(operation, execution.ManagedSession!, path, cancellationToken);
    }

    public Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(
        TransferOperationAutomationContext operation, BaiduExecutionContext execution, ShareReference share,
        RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken)
    {
        ValidateShareArguments(operation, execution, share, destination, duplicatePolicy, cancellationToken);
        return execution.Engine == BaiduExecutionEngine.WebView2V1
            ? _legacy.PrepareWholeShareForOperationAsync(
                operation, execution.LegacySession!, share, destination, duplicatePolicy, cancellationToken)
            : _managed.PrepareWholeShareForOperationAsync(
                operation, execution.ManagedSession!, share, destination, duplicatePolicy, cancellationToken);
    }

    public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
        TransferOperationAutomationContext operation, BaiduExecutionContext execution, ShareReference share,
        RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken)
    {
        ValidateShareArguments(operation, execution, share, destination, duplicatePolicy, cancellationToken);
        return execution.Engine == BaiduExecutionEngine.WebView2V1
            ? _legacy.SaveWholeShareForOperationAsync(
                operation, execution.LegacySession!, share, destination, duplicatePolicy, cancellationToken)
            : _managed.SaveWholeShareForOperationAsync(
                operation, execution.ManagedSession!, share, destination, duplicatePolicy, cancellationToken);
    }

    public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
        TransferOperationAutomationContext operation, BaiduExecutionContext execution, TransferConfirmationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateBinding(operation, execution, cancellationToken);
        if (!string.Equals(query.SourceShareId, operation.SourceShare.StableShareId, StringComparison.Ordinal))
            throw new InvalidOperationException(SourceMismatch);
        return execution.Engine == BaiduExecutionEngine.WebView2V1
            ? _legacy.ConfirmExactTransferForOperationAsync(operation, execution.LegacySession!, query, cancellationToken)
            : _managed.ConfirmExactTransferForOperationAsync(operation, execution.ManagedSession!, query, cancellationToken);
    }

    private static void ValidateShareArguments(
        TransferOperationAutomationContext operation,
        BaiduExecutionContext execution,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(destination);
        if (!Enum.IsDefined(duplicatePolicy))
            throw new ArgumentOutOfRangeException(nameof(duplicatePolicy));
        ValidateBinding(operation, execution, cancellationToken);
        if (!share.Equals(operation.SourceShare))
            throw new InvalidOperationException(SourceMismatch);
    }

    private static void ValidateBinding(
        TransferOperationAutomationContext operation,
        BaiduExecutionContext execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        operation.Validate();
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
