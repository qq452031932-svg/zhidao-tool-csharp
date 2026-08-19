using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Imports;

public sealed record DraftMutationResult(
    int TotalRows,
    int ReadyRows,
    int InvalidRows,
    int DuplicateRows,
    int ConflictRows,
    int NeedsResolutionRows,
    int ExcludedRows);

public sealed class TransferModeReclassificationService(IUnitOfWorkFactory unitOfWorkFactory)
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task<DraftMutationResult> SwitchAsync(
        Guid sessionId,
        TransferMode transferMode,
        CancellationToken cancellationToken = default)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await DraftReclassificationEngine.RequireMutableSessionAsync(
                unit.Imports, sessionId, cancellationToken).ConfigureAwait(false);
            var result = await DraftReclassificationEngine.ReclassifyAsync(
                unit.Imports, session, transferMode, cancellationToken).ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await TryRollbackAsync(unit).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task TryRollbackAsync(IUnitOfWork unit)
    {
        try { await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }
}

internal static class DraftReclassificationEngine
{
    private const int PageSize = 1_000;
    private static readonly ImportConflictService Conflicts = new();

    internal static async Task<ImportSessionRecord> RequireMutableSessionAsync(
        IImportRepository repository,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("An import session is required.", nameof(sessionId));
        var session = await repository.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new DraftWorkflowException("DRAFT_SESSION_NOT_FOUND", "导入草稿不存在。");
        if (session.Status is not ("Draft" or "ReadyToConfirm"))
            throw new DraftWorkflowException("DRAFT_NOT_MUTABLE", "当前导入草稿不能修改。");
        return session;
    }

    internal static async Task<DraftMutationResult> ReclassifyAsync(
        IImportRepository repository,
        ImportSessionRecord session,
        TransferMode transferMode,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(transferMode)) throw new ArgumentOutOfRangeException(nameof(transferMode));
        string? identityCursor = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identities = await repository.GetDuplicateNormalizedShareIdsPageAsync(
                session.Id, identityCursor, PageSize, cancellationToken).ConfigureAwait(false);
            if (identities.Count == 0) break;
            foreach (var identity in identities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReclassifyIdentityAsync(repository, session.Id, identity, transferMode, cancellationToken)
                    .ConfigureAwait(false);
            }
            identityCursor = identities[^1];
        }

        await ReclassifyUniqueRowsAsync(repository, session.Id, transferMode, cancellationToken).ConfigureAwait(false);

        return await RefreshSessionAsync(repository, session, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReclassifyUniqueRowsAsync(
        IImportRepository repository,
        Guid sessionId,
        TransferMode transferMode,
        CancellationToken cancellationToken)
    {
        long cursor = 0;
        while (true)
        {
            var page = await repository.GetNonDuplicateClassifiableRowsKeysetPageAsync(
                sessionId, cursor, PageSize, cancellationToken).ConfigureAwait(false);
            var changed = new List<StagedImportRowRecord>(page.Items.Count);
            foreach (var row in page.Items)
            {
                if (!IsClassifiable(row) || row.NormalizedShareId is null) continue;
                var desired = ClassifyPrimary(row, transferMode);
                if (!SameClassification(row, desired)) changed.Add(desired);
            }
            if (changed.Count != 0) await repository.UpdateRowsAsync(changed, cancellationToken).ConfigureAwait(false);
            if (page.NextCursor is null) break;
            cursor = page.NextCursor.Value;
        }
    }

    private static async Task<DraftMutationResult> RefreshSessionAsync(
        IImportRepository repository,
        ImportSessionRecord session,
        CancellationToken cancellationToken)
    {
        var counts = await repository.GetDispositionCountsAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await repository.UpdateSessionAsync(session with
        {
            Status = "Draft",
            TotalRows = counts.TotalRows,
            ReadyRows = counts.ReadyRows,
            InvalidRows = checked(counts.InvalidRows + counts.ConflictRows + counts.NeedsResolutionRows),
            DuplicateRows = counts.DuplicateRows,
            ExcludedRows = counts.ExcludedRows,
            UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);
        return new(counts.TotalRows, counts.ReadyRows, counts.InvalidRows, counts.DuplicateRows,
            counts.ConflictRows, counts.NeedsResolutionRows, counts.ExcludedRows);
    }

    private static async Task ReclassifyIdentityAsync(
        IImportRepository repository,
        Guid sessionId,
        string identity,
        TransferMode mode,
        CancellationToken cancellationToken)
    {
        StagedImportRowRecord? primary = null;
        var after = 0;
        while (true)
        {
            var page = await repository.GetRowsByNormalizedShareIdAfterSourceRowAsync(
                sessionId, identity, after, PageSize, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            after = page[^1].SourceRowNumber;
            var changed = new List<StagedImportRowRecord>(page.Count);
            foreach (var row in page)
            {
                if (!IsClassifiable(row)) continue;
                var desired = primary is null
                    ? ClassifyPrimary(row, mode)
                    : ClassifyLater(primary, row, mode);
                primary ??= desired;
                if (!SameClassification(row, desired)) changed.Add(desired);
            }
            if (changed.Count != 0)
                await repository.UpdateRowsAsync(changed, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsClassifiable(StagedImportRowRecord row) =>
        row.Disposition is "Ready" or "Duplicate" or "Conflict";

    private static StagedImportRowRecord ClassifyPrimary(StagedImportRowRecord row, TransferMode mode)
    {
        if (mode == TransferMode.Replication && HasPerRowAccount(row))
            return WithClassification(row, "Conflict", null, "DRAFT_REPLICATION_ACCOUNT_COLUMN");
        return WithClassification(row, "Ready", null, null);
    }

    private static StagedImportRowRecord ClassifyLater(
        StagedImportRowRecord primary,
        StagedImportRowRecord row,
        TransferMode mode)
    {
        var result = Conflicts.Classify(mode, primary, row);
        return result.Kind switch
        {
            DuplicateKind.Duplicate => WithClassification(row, "Duplicate", primary.Id, null),
            DuplicateKind.Conflict when result.ConflictReason == DuplicateConflictReason.ReplicationAccountSubsetUnsupported =>
                WithClassification(row, "Conflict", null, "DRAFT_REPLICATION_ACCOUNT_COLUMN"),
            DuplicateKind.Conflict => WithClassification(row, "Conflict", null, "DRAFT_SOURCE_SETTINGS_CONFLICT"),
            _ => WithClassification(row, "Ready", null, null),
        };
    }

    private static bool HasPerRowAccount(StagedImportRowRecord row) =>
        row.RequestedAccountName is not null || row.MappedAccountId is not null;

    private static StagedImportRowRecord WithClassification(
        StagedImportRowRecord row,
        string disposition,
        Guid? duplicateOf,
        string? validationMessage) => row with
    {
        Disposition = disposition,
        DuplicateOfStagedRowId = duplicateOf,
        ValidationMessage = validationMessage,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static bool SameClassification(StagedImportRowRecord left, StagedImportRowRecord right) =>
        left.Disposition == right.Disposition &&
        left.DuplicateOfStagedRowId == right.DuplicateOfStagedRowId &&
        string.Equals(left.ValidationMessage, right.ValidationMessage, StringComparison.Ordinal);
}

public sealed class DraftWorkflowException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code)
        ? throw new ArgumentException("An error code is required.", nameof(code))
        : code;

    public override string ToString() =>
        $"DraftWorkflowException {{ Code = {Code}, Message = [REDACTED] }}";
}
