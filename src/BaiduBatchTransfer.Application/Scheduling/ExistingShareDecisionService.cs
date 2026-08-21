using System.Security.Cryptography;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed class ExistingShareDecisionService
{
    private const int MaximumDiscoveryPages = 10_000;
    private readonly IBaiduShareAdapter adapter;
    private readonly IBaiduSessionStore sessionStore;
    private readonly IExistingShareDecisionStore store;
    private readonly TimeProvider timeProvider;
    private readonly ShareReconciliationPolicy policy = new();

    public ExistingShareDecisionService(
        IBaiduShareAdapter adapter,
        IBaiduSessionStore sessionStore,
        IExistingShareDecisionStore store,
        TimeProvider? timeProvider = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExistingShareBatchSummary> DiscoverAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        var accounts = await store.GetDecisionAccountsAsync(batchJobId, cancellationToken).ConfigureAwait(false);
        if (accounts.Count == 0)
            throw new InvalidOperationException("The batch has no selected share-enabled accounts.");

        var runId = Guid.NewGuid();
        var snapshots = new List<BatchShareDiscoverySnapshotRecord>(accounts.Count);
        try
        {
            foreach (var account in accounts)
            {
                ValidateAccount(account, batchJobId);
                var expectedWorkCount=await store.BeginDiscoveryAsync(runId,account,cancellationToken).ConfigureAwait(false);
                var session = await sessionStore.LoadAsync(account.AccountId.ToString("D"), cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A selected account session is unavailable.");
                await StageAllAsync(runId, account, session, cancellationToken).ConfigureAwait(false);
                var previous = await store.GetDiscoverySnapshotAsync(
                    batchJobId, account.AccountId, account.Settings.SettingsFingerprint, cancellationToken).ConfigureAwait(false);
                var version = previous is null ? 1 : checked(previous.Version + 1);
                snapshots.Add(await ReadStagedSnapshotAsync(
                    runId, account, expectedWorkCount, version, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false));
            }

            if (!await store.TrySaveDiscoveryAndPauseAsync(runId, snapshots, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The share discovery snapshot changed before it could be persisted.");
        }
        catch
        {
            await store.AbortDiscoveryAsync(runId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return Summary(batchJobId, snapshots);
    }

    public async Task<ExistingShareDecisionConfirmation> ConfirmAsync(
        ConfirmExistingShareDecisionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.BatchJobId == Guid.Empty || string.IsNullOrWhiteSpace(command.ConfirmationToken))
            throw new ArgumentException("A confirmed discovery token is required.", nameof(command));
        if (!Enum.IsDefined(command.BatchDefault))
            throw new ArgumentOutOfRangeException(nameof(command));

        var accounts = await store.GetDecisionAccountsAsync(command.BatchJobId, cancellationToken).ConfigureAwait(false);
        var selected = accounts.Select(value => value.AccountId).ToHashSet();
        if (command.PerAccountOverrides.Keys.Any(accountId => !selected.Contains(accountId)) ||
            command.PerAccountOverrides.Values.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("A decision override does not belong to a selected share-enabled account.", nameof(command));

        var snapshots = new List<BatchShareDiscoverySnapshotRecord>(accounts.Count);
        var verificationRun = Guid.NewGuid();
        try
        {
            foreach (var account in accounts)
            {
                var snapshot = await store.GetDiscoverySnapshotAsync(
                    command.BatchJobId, account.AccountId, account.Settings.SettingsFingerprint, cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot is null) return ExistingShareDecisionConfirmation.Stale;
                var expectedWorkCount=await store.BeginDiscoveryAsync(verificationRun,account,cancellationToken).ConfigureAwait(false);
                var session = await sessionStore.LoadAsync(account.AccountId.ToString("D"), cancellationToken)
                    .ConfigureAwait(false);
                if (session is null) return ExistingShareDecisionConfirmation.Stale;
                await StageAllAsync(verificationRun, account, session, cancellationToken).ConfigureAwait(false);
                var currentEvidence = await ReadStagedSnapshotAsync(
                    verificationRun, account, expectedWorkCount, snapshot.Version, snapshot.CompletedAt, cancellationToken).ConfigureAwait(false);
                if (!SameEvidence(snapshot, currentEvidence)) return ExistingShareDecisionConfirmation.Stale;
                snapshots.Add(snapshot);
            }
        }
        finally
        {
            await store.AbortDiscoveryAsync(verificationRun, CancellationToken.None).ConfigureAwait(false);
        }

        var current = Summary(command.BatchJobId, snapshots);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(current.ConfirmationToken),
                ParseToken(command.ConfirmationToken)))
            return ExistingShareDecisionConfirmation.Stale;

        var confirmedAt = timeProvider.GetUtcNow();
        var decisions = new List<BatchShareDecisionSnapshotRecord>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var decision = command.PerAccountOverrides.GetValueOrDefault(snapshot.AccountId, command.BatchDefault);
            if (decision == ExistingShareDecision.Reuse &&
                snapshot.DiscoveredCount > 0 && snapshot.CompatibleCount == 0)
                throw new InvalidOperationException("Incompatible existing shares cannot be reused.");
            decisions.Add(new(
                snapshot.BatchJobId, snapshot.AccountId, snapshot.SettingsFingerprint,
                decision.ToString(), snapshot.DiscoveryFingerprint, snapshot.DiscoveredCount,
                snapshot.CompatibleCount, snapshot.IncompatibleCount, snapshot.Generation,
                confirmedAt, snapshot.Version));
        }

        return await store.ApplyDecisionSnapshotsAsync(decisions, cancellationToken).ConfigureAwait(false)
            ? ExistingShareDecisionConfirmation.Success
            : ExistingShareDecisionConfirmation.Stale;
    }

    private async Task StageAllAsync(
        Guid runId,
        ShareDecisionAccountContext account,
        AccountSession session,
        CancellationToken cancellationToken)
    {
        var cursor = (string?)null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaximumDiscoveryPages; page++)
        {
            BaiduAdapterResult<BaiduSharePage> result;
            try
            {
                result = await adapter.ListSharesAsync(session, cursor, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                throw new InvalidOperationException("The existing-share discovery request failed.");
            }
            if (!result.IsSuccess)
                throw new InvalidOperationException(result.SafeMessage ?? "The existing-share discovery request failed.");
            await store.StageDiscoveryPageAsync(runId, account, result.Value!.Items, cancellationToken)
                .ConfigureAwait(false);
            var next = result.Value.NextCursor;
            if (next is null) return;
            if (string.IsNullOrWhiteSpace(next) || !cursors.Add(next))
                throw new InvalidOperationException("The existing-share discovery cursor is invalid.");
            cursor = next;
        }
        throw new InvalidOperationException("The existing-share discovery exceeded its bounded page limit.");
    }

    private async Task<BatchShareDiscoverySnapshotRecord> ReadStagedSnapshotAsync(
        Guid runId,
        ShareDecisionAccountContext account,
        long expectedWorkCount,
        long version,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ShareReconciliationPolicy.Append(hash, "account-share-discovery-v1");
        ShareReconciliationPolicy.Append(hash, account.AccountId.ToString("D"));
        ShareReconciliationPolicy.Append(hash, account.Settings.SettingsFingerprint);
        string? cursor = null;
        var discovered = 0;
        var compatible = 0;
        do
        {
            var page = await store.GetStagedDiscoveryPageAsync(
                runId, account.AccountId, cursor, 500, cancellationToken).ConfigureAwait(false);
            foreach (var member in page.Items)
            {
                ShareReconciliationPolicy.Append(hash, member.ShareId);
                ShareReconciliationPolicy.Append(hash, member.ExactItemSetFingerprint);
                ShareReconciliationPolicy.Append(hash, member.IsPermanent ? "1" : "0");
                ShareReconciliationPolicy.Append(hash, member.IsAccessUnlimited ? "1" : "0");
                ShareReconciliationPolicy.Append(hash, member.ExtractionCode);
                discovered++;
                if (member.IsCompatible) compatible++;
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        return new(account.BatchJobId, account.AccountId, account.Settings.SettingsFingerprint,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), discovered, compatible,
            discovered-compatible, await ComputeOperationGenerationAsync(runId, account, expectedWorkCount, cancellationToken).ConfigureAwait(false),
            completedAt, version);
    }

    private static bool SameEvidence(BatchShareDiscoverySnapshotRecord left, BatchShareDiscoverySnapshotRecord right) =>
        left.BatchJobId == right.BatchJobId && left.AccountId == right.AccountId &&
        string.Equals(left.SettingsFingerprint,right.SettingsFingerprint,StringComparison.Ordinal) &&
        string.Equals(left.DiscoveryFingerprint,right.DiscoveryFingerprint,StringComparison.Ordinal) &&
        left.DiscoveredCount == right.DiscoveredCount && left.CompatibleCount == right.CompatibleCount &&
        left.IncompatibleCount == right.IncompatibleCount &&
        string.Equals(left.Generation,right.Generation,StringComparison.Ordinal) && left.Version == right.Version;

    private static ExistingShareBatchSummary Summary(
        Guid batchJobId,
        IReadOnlyList<BatchShareDiscoverySnapshotRecord> snapshots)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ShareReconciliationPolicy.Append(hash, "batch-share-decision-v1");
        ShareReconciliationPolicy.Append(hash, batchJobId.ToString("D"));
        foreach (var snapshot in snapshots)
        {
            ShareReconciliationPolicy.Append(hash, snapshot.AccountId.ToString("D"));
            ShareReconciliationPolicy.Append(hash, snapshot.SettingsFingerprint);
            ShareReconciliationPolicy.Append(hash, snapshot.DiscoveryFingerprint);
            ShareReconciliationPolicy.Append(hash, snapshot.DiscoveredCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ShareReconciliationPolicy.Append(hash, snapshot.CompatibleCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ShareReconciliationPolicy.Append(hash, snapshot.IncompatibleCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ShareReconciliationPolicy.Append(hash, snapshot.Generation);
            ShareReconciliationPolicy.Append(hash, snapshot.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return new(
            batchJobId,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            snapshots.Select(value => new ExistingShareAccountSummary(
                value.AccountId, value.DiscoveredCount, value.CompatibleCount, value.IncompatibleCount)).ToArray());
    }

    private static PermanentShareRequest Request(BatchShareSettingsRecord settings, RemoteItemIdentitySet items) =>
        string.Equals(settings.ExtractionCodeMode, "Random", StringComparison.Ordinal)
            ? new(items)
            : new(items, settings.FixedExtractionCode);

    private static void ValidateAccount(ShareDecisionAccountContext account, Guid batchJobId)
    {
        if (account.BatchJobId != batchJobId || account.AccountId == Guid.Empty ||
            account.Settings.BatchJobId != batchJobId ||
            !account.Settings.IsPermanent || !account.Settings.IsAccessUnlimited)
            throw new InvalidOperationException("The selected share account snapshot is invalid.");
    }

    private async Task<string> ComputeOperationGenerationAsync(
        Guid runId,
        ShareDecisionAccountContext account,
        long expectedWorkCount,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ShareReconciliationPolicy.Append(hash, "account-exact-operation-sets-v1");
        string? cursor = null;
        var count = 0;
        do
        {
            var page = await store.GetDiscoveryRunExactSetFingerprintsKeysetPageAsync(
                runId, account.AccountId, cursor, 500, cancellationToken).ConfigureAwait(false);
            foreach (var fingerprint in page.Items)
            {
                ShareReconciliationPolicy.Append(hash, fingerprint);
                count++;
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        if (count == 0) throw new InvalidOperationException("The selected account has no exact share work.");
        return $"{expectedWorkCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static byte[] ParseToken(string value)
    {
        try
        {
            var bytes = Convert.FromHexString(value);
            return bytes.Length == 32 ? bytes : new byte[32];
        }
        catch (FormatException)
        {
            return new byte[32];
        }
    }
}
