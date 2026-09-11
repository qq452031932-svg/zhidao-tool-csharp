using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public enum LocalResultArtifactState
{
    Missing,
    Valid,
    Invalid,
}

public enum LocalResultExportDisposition
{
    Exported,
    AlreadySucceeded,
    NotReady,
    Busy,
    Failed,
    Conflict,
}

public sealed record LocalResultExportOwnerSnapshot
{
    public LocalResultExportOwnerSnapshot(
        LocalResultExportKey key,
        string destinationDirectory,
        bool isTerminal,
        long expectedRowCount)
    {
        key.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Key = key;
        DestinationDirectory = destinationDirectory;
        IsTerminal = isTerminal;
        if (expectedRowCount < 0) throw new ArgumentOutOfRangeException(nameof(expectedRowCount));
        ExpectedRowCount = expectedRowCount;
    }

    public LocalResultExportKey Key { get; }
    public string DestinationDirectory { get; }
    public bool IsTerminal { get; }
    public long ExpectedRowCount { get; }
}

public sealed record LocalResultArtifactPublishReceipt
{
    public LocalResultArtifactPublishReceipt(string finalFileName, long exportedRows)
    {
        FinalFileName = LocalResultExportValidation.SafeFileName(finalFileName, nameof(finalFileName));
        if (exportedRows < 0) throw new ArgumentOutOfRangeException(nameof(exportedRows));
        ExportedRows = exportedRows;
    }

    public string FinalFileName { get; }
    public long ExportedRows { get; }
}

public sealed record LocalResultExportOutcome(
    LocalResultExportDisposition Disposition,
    string? FinalFileName = null)
{
    public override string ToString() =>
        $"LocalResultExportOutcome {{ Disposition = {Disposition}, FinalFileName = [REDACTED] }}";
}

public interface ILocalResultExportOwnerRepository
{
    Task<LocalResultExportOwnerSnapshot?> GetAsync(
        LocalResultExportKey key,
        CancellationToken cancellationToken = default);

    Task<Page<LocalResultExportOwnerSnapshot>> GetReadyPageAsync(
        PageQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalResultOperationEvidence>> GetProvenOperationEvidenceAsync(
        LocalResultExportKey key,
        IReadOnlyList<LocalResultOperationIdentity> candidates,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocalResultOperationEvidence>>([]);
}

public interface ILocalResultExportArtifactService
{
    Task<string> ChooseTargetFileNameAsync(
        LocalResultExportOwnerSnapshot owner,
        CancellationToken cancellationToken = default);

    Task<LocalResultArtifactState> InspectAsync(
        LocalResultExportOwnerSnapshot owner,
        string targetFileName,
        CancellationToken cancellationToken = default);

    Task<LocalResultArtifactPublishReceipt> PublishAsync(
        LocalResultExportOwnerSnapshot owner,
        string targetFileName,
        CancellationToken cancellationToken = default,
        IReadOnlyList<LocalResultOperationEvidence>? provenOperations = null);

    Task<LocalResultExportCompletedEvidence?> GetVerifiedEvidenceAsync(
        LocalResultExportOwnerSnapshot owner,
        string targetFileName,
        IReadOnlyList<LocalResultOperationIdentity> candidates,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<LocalResultExportCompletedEvidence?>(null);
}

public interface ILocalResultExportCoordinator
{
    Task<LocalResultExportOutcome> ExportStandaloneAsync(
        Guid batchId,
        CancellationToken cancellationToken = default);

    Task<LocalResultExportOutcome> ExportTransferAsync(
        Guid batchId,
        CancellationToken cancellationToken = default);

    Task<int> RecoverAsync(CancellationToken cancellationToken = default);
}

public interface ILocalResultExportObserver
{
    IReadOnlyList<LocalResultOperationIdentity> GetTrackedOperations(LocalResultExportKey exportKey) => [];

    Task OnLocalResultExportCompletedAsync(
        LocalResultExportCompletedEvidence evidence,
        CancellationToken cancellationToken = default);
}
public sealed record LocalResultOperationIdentity(
    string AdapterKind,
    Guid AccountId,
    Guid OperationId,
    int Generation);

public sealed record LocalResultOperationEvidence(
    string AdapterKind,
    Guid AccountId,
    Guid OperationId,
    int Generation,
    string ContentFingerprint);

public sealed record LocalResultExportCompletedEvidence(
    LocalResultExportKey ExportKey,
    IReadOnlyList<LocalResultOperationEvidence> Operations,
    string LayoutFingerprint,
    string ArtifactFingerprint);

public sealed class LocalResultExportCoordinator(
    ILocalResultExportStore store,
    ILocalResultExportOwnerRepository owners,
    ILocalResultExportArtifactService artifacts,
    TimeProvider? timeProvider = null,
    TimeSpan? leaseDuration = null,
    ILocalResultExportObserver? observer = null) : ILocalResultExportCoordinator
{
    private readonly ILocalResultExportStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILocalResultExportOwnerRepository owners = owners ?? throw new ArgumentNullException(nameof(owners));
    private readonly ILocalResultExportArtifactService artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan leaseDuration = ValidateLeaseDuration(leaseDuration ?? TimeSpan.FromMinutes(5));
    private readonly ILocalResultExportObserver? observer = observer;
    private readonly SemaphoreSlim processGate = new(1, 1);

    public async Task<LocalResultExportOutcome> ExportStandaloneAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        await processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureAndExportAsync(new(
                LocalResultOwnerKind.StandaloneShareBatch,
                Required(batchId),
                LocalResultExportKind.StandaloneShareWorkbook,
                1), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            processGate.Release();
        }
    }

    public async Task<LocalResultExportOutcome> ExportTransferAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        await processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureAndExportAsync(new(
                LocalResultOwnerKind.TransferBatch,
                Required(batchId),
                LocalResultExportKind.ReplacementWorkbook,
                1), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            processGate.Release();
        }
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverExpiredLeasesAsync(cancellationToken).ConfigureAwait(false);

            var completed = 0;
            for (var pageNumber = 1; ; pageNumber++)
            {
                var page = await owners.GetReadyPageAsync(new PageQuery(pageNumber, 100), cancellationToken)
                    .ConfigureAwait(false);
                foreach (var owner in page.Items)
                {
                    try
                    {
                        var result = await EnsureAndExportAsync(owner.Key, cancellationToken).ConfigureAwait(false);
                        if (result.Disposition is LocalResultExportDisposition.Exported or
                            LocalResultExportDisposition.AlreadySucceeded)
                            completed++;
                    }
                    catch (InvalidDataException)
                    {
                        // Historical pre-snapshot batches cannot be exported with exact columns.
                        // They must not prevent newer direct-background batches from starting.
                    }
                }
                if ((long)pageNumber * page.PageSize >= page.TotalCount) break;
            }
            return completed;
        }
        finally
        {
            processGate.Release();
        }
    }

    private async Task<LocalResultExportOutcome> EnsureAndExportAsync(
        LocalResultExportKey key,
        CancellationToken cancellationToken)
    {
        key.Validate();
        await RecoverExpiredLeasesAsync(cancellationToken).ConfigureAwait(false);
        var existing = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing?.Status == LocalResultExportStatus.Succeeded)
        {
            var owner = await owners.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (owner is not null && existing.FinalFileName is not null)
                await NotifyObserverSafelyAsync(owner, existing.FinalFileName).ConfigureAwait(false);
            return new(LocalResultExportDisposition.AlreadySucceeded, existing.FinalFileName);
        }

        if (existing is null)
        {
            var owner = await owners.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (owner is null || !owner.IsTerminal)
                return new(LocalResultExportDisposition.NotReady);
            var target = LocalResultExportValidation.SafeFileName(
                await artifacts.ChooseTargetFileNameAsync(owner, cancellationToken).ConfigureAwait(false),
                "targetFileName");
            existing = await store.CreateOrGetAsync(LocalResultExportRequest.Create(
                key.OwnerKind, key.OwnerId, key.ExportKind, key.Generation,
                target, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }

        return await ProcessAsync(existing, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalResultExportOutcome> ProcessAsync(
        LocalResultExportRecord record,
        CancellationToken cancellationToken)
    {
        if (record.Status == LocalResultExportStatus.Succeeded)
            return new(LocalResultExportDisposition.AlreadySucceeded, record.FinalFileName);
        if (record.Status == LocalResultExportStatus.InProgress)
            return new(LocalResultExportDisposition.Busy);

        var owner = await owners.GetAsync(record.Key, cancellationToken).ConfigureAwait(false);
        if (owner is null || !owner.IsTerminal)
            return new(LocalResultExportDisposition.NotReady);

        var now = timeProvider.GetUtcNow();
        var lease = await store.TryAcquireAsync(
            record.Key, record.Revision, Guid.NewGuid(), now, now.Add(leaseDuration), cancellationToken)
            .ConfigureAwait(false);
        if (lease is null) return new(LocalResultExportDisposition.Busy);

        try
        {
            var state = await artifacts.InspectAsync(owner, record.TargetFileName, cancellationToken)
                .ConfigureAwait(false);
            if (state == LocalResultArtifactState.Invalid)
                return await FailAsync(lease, "INVALID_EXISTING_RESULT", CancellationToken.None).ConfigureAwait(false);
            if (state == LocalResultArtifactState.Valid &&
                !string.Equals(
                    record.SanitizedErrorCode,
                    "RECOVERED_EXPIRED_LEASE",
                    StringComparison.Ordinal))
                return await FailAsync(lease, "UNEXPECTED_EXISTING_RESULT", CancellationToken.None).ConfigureAwait(false);

            var finalFileName = record.TargetFileName;
            if (state == LocalResultArtifactState.Missing)
            {
                var provenOperations = await GetProvenOperationsSafelyAsync(owner).ConfigureAwait(false);
                var receipt = await artifacts.PublishAsync(
                    owner, record.TargetFileName, cancellationToken, provenOperations).ConfigureAwait(false);
                if (!string.Equals(receipt.FinalFileName, record.TargetFileName, StringComparison.Ordinal))
                    return await FailAsync(lease, "RESULT_IDENTITY_MISMATCH", CancellationToken.None)
                        .ConfigureAwait(false);
                finalFileName = receipt.FinalFileName;
            }

            var completed = await store.TryCompleteAsync(
                lease.ExportId, lease.Key.Generation, lease.LeaseGeneration, lease.LeaseOwnerId,
                lease.Revision, lease.LeaseExpiresAt, finalFileName, timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            if (!completed) return new(LocalResultExportDisposition.Conflict);
            await NotifyObserverSafelyAsync(owner, finalFileName).ConfigureAwait(false);
            return new(LocalResultExportDisposition.Exported, finalFileName);
        }
        catch (OperationCanceledException)
        {
            await FailAsync(lease, "EXPORT_CANCELLED", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            return await FailAsync(lease, "EXPORT_FAILED", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task NotifyObserverSafelyAsync(
        LocalResultExportOwnerSnapshot owner,
        string finalFileName)
    {
        if (observer is null) return;
        try
        {
            var candidates = observer.GetTrackedOperations(owner.Key);
            if (candidates.Count == 0) return;
            // Only the artifact service can prove that these exact operation identities
            // are present in this exact published workbook. Database state and a file hash
            // gathered independently are deliberately insufficient (TOCTOU fail-closed).
            var evidence = await artifacts.GetVerifiedEvidenceAsync(
                owner, finalFileName, candidates, CancellationToken.None).ConfigureAwait(false);
            if (evidence is null || evidence.ExportKey != owner.Key) return;
            await observer.OnLocalResultExportCompletedAsync(evidence, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Acceptance telemetry is best-effort after durable export success.
        }
    }

    private async Task<IReadOnlyList<LocalResultOperationEvidence>> GetProvenOperationsSafelyAsync(
        LocalResultExportOwnerSnapshot owner)
    {
        if (observer is null) return [];
        try
        {
            var candidates = observer.GetTrackedOperations(owner.Key);
            if (candidates.Count == 0) return [];
            return await owners.GetProvenOperationEvidenceAsync(
                owner.Key, candidates, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Acceptance membership is optional telemetry; the business export still publishes.
            return [];
        }
    }

    private async Task<LocalResultExportOutcome> FailAsync(
        LocalResultExportLease lease,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var failed = await store.TryFailAsync(
            lease.ExportId, lease.Key.Generation, lease.LeaseGeneration, lease.LeaseOwnerId,
            lease.Revision, lease.LeaseExpiresAt, errorCode, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return new(failed ? LocalResultExportDisposition.Failed : LocalResultExportDisposition.Conflict);
    }

    private async Task RecoverExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        while (await store.RecoverExpiredAsync(now, 1_000, cancellationToken).ConfigureAwait(false) == 1_000)
        {
        }
    }

    private static Guid Required(Guid id) =>
        id != Guid.Empty ? id : throw new ArgumentException("A batch is required.", nameof(id));

    private static TimeSpan ValidateLeaseDuration(TimeSpan value) =>
        value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value));
}
