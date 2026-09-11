namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal enum ManagedTransferQuarantineStage
{
    DirectoryPreparation = 1,
    Picker = 2,
    TargetBaseline = 3,
    FinalSubmission = 4,
}

internal sealed record ManagedTransferQuarantineKey
{
    internal ManagedTransferQuarantineKey(
        ManagedTransferQuarantineStage stage,
        Guid operationId,
        Guid accountId,
        int generation,
        Guid sessionId,
        long attemptRevision,
        Guid? submissionId = null)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (sessionId == Guid.Empty) throw new ArgumentException("A session is required.", nameof(sessionId));
        if (attemptRevision < 0) throw new ArgumentOutOfRangeException(nameof(attemptRevision));
        if (submissionId == Guid.Empty) throw new ArgumentException("An empty submission is not valid.", nameof(submissionId));
        Stage = stage;
        OperationId = operationId;
        AccountId = accountId;
        Generation = generation;
        SessionId = sessionId;
        AttemptRevision = attemptRevision;
        SubmissionId = submissionId;
    }

    internal ManagedTransferQuarantineStage Stage { get; }
    internal Guid OperationId { get; }
    internal Guid AccountId { get; }
    internal int Generation { get; }
    internal Guid SessionId { get; }
    internal long AttemptRevision { get; }
    internal Guid? SubmissionId { get; }

    public override string ToString() =>
        $"ManagedTransferQuarantineKey {{ Stage = {Stage}, Identity = [REDACTED], Submission = {(SubmissionId is null ? "None" : "Bound")} }}";
}

internal enum ManagedTransferQuarantineReconciliation
{
    Released = 1,
    Retained = 2,
    NotFound = 3,
}

internal interface IManagedTransferQuarantine
{
    ManagedTransferQuarantineKey Key { get; }
    Task<ManagedTransferQuarantineReconciliation> ReconcileAsync(
        int timeoutMilliseconds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Strong process-local ownership for every managed-transfer quarantine stage. One account may own only one
/// entry; reconciliation is coalesced and an entry is compare-removed only after a release proof.
/// </summary>
internal sealed class ManagedTransferQuarantineRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<ManagedTransferQuarantineKey, Entry> byKey = [];
    private readonly Dictionary<Guid, Entry> byAccount = [];

    internal bool TryRegister(IManagedTransferQuarantine quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        var entry = new Entry(quarantine);
        lock (sync)
        {
            if (byKey.ContainsKey(quarantine.Key) || byAccount.ContainsKey(quarantine.Key.AccountId))
                return false;
            byKey.Add(quarantine.Key, entry);
            byAccount.Add(quarantine.Key.AccountId, entry);
            return true;
        }
    }

    internal bool TryRegister(
        ManagedTransferQuarantineKey key,
        ManagedTransferDirectoryPreparationQuarantine quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        return TryRegister(Wrap(key, ManagedTransferQuarantineStage.DirectoryPreparation,
            async (timeout, token) => IsReleased(
                await quarantine.ReconcileAsync(timeout, token).ConfigureAwait(false))));
    }

    internal bool TryRegister(
        ManagedTransferQuarantineKey key,
        ManagedTransferPickerQuarantine quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        return TryRegister(Wrap(key, ManagedTransferQuarantineStage.Picker,
            async (timeout, token) => IsReleased(
                await quarantine.ReconcileAsync(timeout, token).ConfigureAwait(false))));
    }

    internal bool TryRegister(
        ManagedTransferQuarantineKey key,
        ManagedTransferTargetBaselineQuarantine quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        return TryRegister(Wrap(key, ManagedTransferQuarantineStage.TargetBaseline,
            async (timeout, token) => IsReleased(
                await quarantine.ReconcileAsync(timeout, token).ConfigureAwait(false))));
    }

    internal bool TryRegister(
        ManagedTransferQuarantineKey key,
        ManagedTransferFinalSubmissionQuarantine quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        return TryRegister(Wrap(key, ManagedTransferQuarantineStage.FinalSubmission,
            async (timeout, token) => IsReleased(
                await quarantine.ReconcileAsync(timeout, token).ConfigureAwait(false))));
    }

    internal bool IsAccountQuarantined(Guid accountId)
    {
        if (accountId == Guid.Empty) return false;
        lock (sync) return byAccount.ContainsKey(accountId);
    }

    internal IManagedTransferQuarantine? Find(ManagedTransferQuarantineKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (sync) return byKey.TryGetValue(key, out var entry) ? entry.Quarantine : null;
    }

    internal async Task<ManagedTransferQuarantineReconciliation> ReconcileAsync(
        ManagedTransferQuarantineKey key,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
            return ManagedTransferQuarantineReconciliation.Retained;
        Task<ManagedTransferQuarantineReconciliation> reconciliation;
        lock (sync)
        {
            if (!byKey.TryGetValue(key, out var entry))
                return ManagedTransferQuarantineReconciliation.NotFound;
            entry.Active ??= ReconcileOwnedAsync(entry, timeoutMilliseconds);
            reconciliation = entry.Active;
        }
        return await reconciliation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ManagedTransferQuarantineReconciliation> ReconcileOwnedAsync(
        Entry entry,
        int timeoutMilliseconds)
    {
        await Task.Yield();
        ManagedTransferQuarantineReconciliation result;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));
        try
        {
            result = await entry.Quarantine.ReconcileAsync(timeoutMilliseconds, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = ManagedTransferQuarantineReconciliation.Retained;
        }

        lock (sync)
        {
            if (result == ManagedTransferQuarantineReconciliation.Released &&
                byKey.TryGetValue(entry.Quarantine.Key, out var current) && ReferenceEquals(current, entry))
            {
                byKey.Remove(entry.Quarantine.Key);
                if (byAccount.TryGetValue(entry.Quarantine.Key.AccountId, out var accountEntry) &&
                    ReferenceEquals(accountEntry, entry))
                    byAccount.Remove(entry.Quarantine.Key.AccountId);
            }
            entry.Active = null;
        }
        return result;
    }

    private static IManagedTransferQuarantine Wrap(
        ManagedTransferQuarantineKey key,
        ManagedTransferQuarantineStage requiredStage,
        Func<int, CancellationToken, Task<bool>> reconcile)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(reconcile);
        if (key.Stage != requiredStage)
            throw new ArgumentException("The quarantine key does not match the stage.", nameof(key));
        return new WrappedQuarantine(key, async (timeout, token) =>
            await reconcile(timeout, token).ConfigureAwait(false)
                ? ManagedTransferQuarantineReconciliation.Released
                : ManagedTransferQuarantineReconciliation.Retained);
    }

    private static bool IsReleased(ManagedTransferDirectoryPreparationReconciliationStatus status) =>
        status is ManagedTransferDirectoryPreparationReconciliationStatus.Released or
            ManagedTransferDirectoryPreparationReconciliationStatus.AlreadyReleased;

    private static bool IsReleased(ManagedTransferPickerReconciliationStatus status) =>
        status is ManagedTransferPickerReconciliationStatus.Released or
            ManagedTransferPickerReconciliationStatus.AlreadyReleased;

    private static bool IsReleased(ManagedTransferTargetBaselineReconciliationStatus status) =>
        status is ManagedTransferTargetBaselineReconciliationStatus.Released or
            ManagedTransferTargetBaselineReconciliationStatus.AlreadyReleased;

    private static bool IsReleased(ManagedTransferFinalSubmissionReconciliationStatus status) =>
        status is ManagedTransferFinalSubmissionReconciliationStatus.Released or
            ManagedTransferFinalSubmissionReconciliationStatus.AlreadyReleased;

    private sealed class Entry(IManagedTransferQuarantine quarantine)
    {
        internal IManagedTransferQuarantine Quarantine { get; } = quarantine;
        internal Task<ManagedTransferQuarantineReconciliation>? Active { get; set; }
    }

    private sealed class WrappedQuarantine(
        ManagedTransferQuarantineKey key,
        Func<int, CancellationToken, Task<ManagedTransferQuarantineReconciliation>> reconcile)
        : IManagedTransferQuarantine
    {
        public ManagedTransferQuarantineKey Key { get; } = key;

        public Task<ManagedTransferQuarantineReconciliation> ReconcileAsync(
            int timeoutMilliseconds,
            CancellationToken cancellationToken) =>
            reconcile(timeoutMilliseconds, cancellationToken);
    }
}
