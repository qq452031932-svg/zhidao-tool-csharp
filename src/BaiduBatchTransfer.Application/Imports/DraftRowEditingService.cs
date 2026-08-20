using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Imports;

public sealed class DraftRowEditRequest
{
    public DraftRowEditRequest(
        Guid importSessionId,
        Guid rowId,
        string? shareUrl = null,
        string? extractionCode = null,
        string? targetPath = null,
        string? createFolderName = null,
        string? requestedAccountName = null,
        Guid? mappedAccountId = null,
        string? note = null,
        bool clearExtractionCode = false,
        bool clearTargetPath = false,
        bool clearCreateFolderName = false,
        bool clearRequestedAccount = false,
        bool clearNote = false)
    {
        ImportSessionId = importSessionId;
        RowId = rowId;
        ShareUrl = shareUrl;
        ExtractionCode = extractionCode;
        TargetPath = targetPath;
        CreateFolderName = createFolderName;
        RequestedAccountName = requestedAccountName;
        MappedAccountId = mappedAccountId;
        Note = note;
        ClearExtractionCode = clearExtractionCode;
        ClearTargetPath = clearTargetPath;
        ClearCreateFolderName = clearCreateFolderName;
        ClearRequestedAccount = clearRequestedAccount;
        ClearNote = clearNote;
    }

    public Guid ImportSessionId { get; }
    public Guid RowId { get; }
    public string? ShareUrl { get; }
    public string? ExtractionCode { get; }
    public string? TargetPath { get; }
    public string? CreateFolderName { get; }
    public string? RequestedAccountName { get; }
    public Guid? MappedAccountId { get; }
    public string? Note { get; }
    public bool ClearExtractionCode { get; }
    public bool ClearTargetPath { get; }
    public bool ClearCreateFolderName { get; }
    public bool ClearRequestedAccount { get; }
    public bool ClearNote { get; }

    public override string ToString() =>
        $"DraftRowEditRequest {{ ImportSessionId = {ImportSessionId}, RowId = {RowId}, UserContent = [REDACTED] }}";
}

public sealed record DraftBulkOverride(
    string? TargetPath = null,
    string? CreateFolderName = null,
    string? RequestedAccountName = null,
    Guid? MappedAccountId = null,
    string? Note = null,
    bool ClearTargetPath = false,
    bool ClearCreateFolderName = false,
    bool ClearRequestedAccount = false,
    bool ClearNote = false)
{
    public override string ToString() => "DraftBulkOverride { UserContent = [REDACTED] }";
}

public sealed class DraftRowEditingService(IUnitOfWorkFactory unitOfWorkFactory)
{
    private const int PageSize = 1_000;
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    private readonly BaiduShareNormalizer shareNormalizer = new();
    private readonly BaiduPathNormalizer pathNormalizer = new();

    public Task<DraftMutationResult> EditAsync(
        DraftRowEditRequest request,
        TransferMode transferMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MutateAsync(request.ImportSessionId, transferMode, async (unit, session) =>
        {
            var row = await unit.Imports.GetRowByIdAsync(request.RowId, cancellationToken).ConfigureAwait(false)
                ?? throw new DraftWorkflowException("DRAFT_ROW_NOT_FOUND", "草稿记录不存在。");
            if (row.ImportSessionId != request.ImportSessionId)
                throw new DraftWorkflowException("DRAFT_ROW_SESSION_MISMATCH", "草稿记录不属于当前导入。");
            if (row.Disposition == "Excluded")
                throw new DraftWorkflowException("DRAFT_ROW_EXCLUDED", "请先恢复已排除记录再编辑。");
            var normalized = row.NormalizedShareId;
            var shareUrl = row.ShareUrl;
            if (request.ShareUrl is not null)
            {
                try
                {
                    var value = shareNormalizer.Normalize(request.ShareUrl);
                    shareUrl = request.ShareUrl;
                    normalized = value.ShareId;
                }
                catch (ArgumentException)
                {
                    throw new DraftWorkflowException("DRAFT_INVALID_LINK", "分享链接格式无效。");
                }
            }
            var target = request.ClearTargetPath ? null : request.TargetPath ?? row.TargetPath;
            if (target is not null)
            {
                try { _ = pathNormalizer.Normalize(target); }
                catch (ArgumentException) { throw new DraftWorkflowException("DRAFT_INVALID_TARGET_PATH", "目标目录格式无效。"); }
            }
            var requested = request.ClearRequestedAccount ? null : request.RequestedAccountName ?? row.RequestedAccountName;
            if (request.MappedAccountId is { } requestedMapping)
                await RequireActiveAccountAsync(unit.Accounts, requestedMapping, cancellationToken).ConfigureAwait(false);
            var mapped = request.ClearRequestedAccount
                ? null
                : request.RequestedAccountName is not null
                    ? request.MappedAccountId
                    : request.MappedAccountId ?? row.MappedAccountId;
            var extractionCode = request.ClearExtractionCode ? null : request.ExtractionCode ?? row.ExtractionCode;
            var invalidatesPrecheck = !string.Equals(shareUrl, row.ShareUrl, StringComparison.Ordinal) ||
                !string.Equals(extractionCode, row.ExtractionCode, StringComparison.Ordinal);
            var desired = row with
            {
                ShareUrl = shareUrl,
                NormalizedShareId = normalized,
                ExtractionCode = extractionCode,
                TargetPath = target,
                CreateFolderName = request.ClearCreateFolderName ? null : request.CreateFolderName ?? row.CreateFolderName,
                RequestedAccountName = requested,
                MappedAccountId = mapped,
                Note = request.ClearNote ? null : request.Note ?? row.Note,
                Disposition = normalized is null ? "Invalid" : "Ready",
                DuplicateOfStagedRowId = null,
                ValidationMessage = normalized is null ? "DRAFT_INVALID_LINK" : null,
                PrecheckStatus = invalidatesPrecheck ? null : row.PrecheckStatus,
                PrecheckErrorCode = invalidatesPrecheck ? null : row.PrecheckErrorCode,
                PrecheckedAt = invalidatesPrecheck ? null : row.PrecheckedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await unit.Imports.UpdateRowsAsync([desired], cancellationToken).ConfigureAwait(false);
            if (invalidatesPrecheck)
            {
                var payload = await unit.Imports.GetRowPayloadAsync(row.Id, cancellationToken).ConfigureAwait(false);
                if (payload is not null)
                    await unit.Imports.UpdateRowPayloadAsync(
                        payload with { ResolvedOutputTitle = null },
                        cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task<DraftMutationResult> ExcludeAsync(
        Guid sessionId,
        Guid rowId,
        TransferMode transferMode,
        CancellationToken cancellationToken = default) =>
        MutateRowAsync(sessionId, rowId, transferMode, row => row with
        {
            Disposition = "Excluded",
            DuplicateOfStagedRowId = null,
            ValidationMessage = "DRAFT_USER_EXCLUDED",
            PrecheckStatus = null,
            PrecheckErrorCode = null,
            PrecheckedAt = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

    public Task<DraftMutationResult> RestoreAsync(
        Guid sessionId,
        Guid rowId,
        TransferMode transferMode,
        CancellationToken cancellationToken = default) =>
        MutateRowAsync(sessionId, rowId, transferMode, row =>
        {
            if (row.Disposition != "Excluded")
                throw new DraftWorkflowException("DRAFT_ROW_NOT_EXCLUDED", "该草稿记录未被排除。");
            return row with
            {
                Disposition = row.NormalizedShareId is null ? "Invalid" : "Ready",
                ValidationMessage = row.NormalizedShareId is null ? "DRAFT_INVALID_LINK" : null,
                PrecheckStatus = null,
                PrecheckErrorCode = null,
                PrecheckedAt = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }, cancellationToken);

    public async Task<DraftMutationResult> ResolveConflictAsync(
        Guid sessionId,
        string normalizedShareId,
        Guid primaryRowId,
        TransferMode transferMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedShareId);
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await DraftReclassificationEngine.RequireMutableSessionAsync(unit.Imports, sessionId, cancellationToken)
                .ConfigureAwait(false);
            var selected = await unit.Imports.GetRowByIdAsync(primaryRowId, cancellationToken).ConfigureAwait(false);
            if (selected is null || selected.ImportSessionId != sessionId ||
                !string.Equals(selected.NormalizedShareId, normalizedShareId, StringComparison.Ordinal) ||
                selected.Disposition is not ("Ready" or "Duplicate" or "Conflict"))
                throw new DraftWorkflowException("DRAFT_CONFLICT_PRIMARY_INVALID", "冲突主记录不可用。");
            if (transferMode == TransferMode.Replication &&
                (selected.RequestedAccountName is not null || selected.MappedAccountId is not null))
                throw new DraftWorkflowException("DRAFT_REPLICATION_ACCOUNT_COLUMN", "复制模式必须先忽略逐行账号。");
            var after = 0;
            while (true)
            {
                var page = await unit.Imports.GetRowsByNormalizedShareIdAfterSourceRowAsync(
                    sessionId, normalizedShareId, after, PageSize, cancellationToken).ConfigureAwait(false);
                if (page.Count == 0) break;
                after = page[^1].SourceRowNumber;
                var classifiable = page.Where(row => row.Disposition is "Ready" or "Duplicate" or "Conflict").ToArray();
                if (transferMode == TransferMode.Replication &&
                    classifiable.Any(row => row.RequestedAccountName is not null || row.MappedAccountId is not null))
                    throw new DraftWorkflowException("DRAFT_REPLICATION_ACCOUNT_COLUMN", "复制模式必须先忽略逐行账号。");
                var changed = classifiable.Select(row => row with
                {
                    Disposition = row.Id == primaryRowId ? "Ready" : "Duplicate",
                    DuplicateOfStagedRowId = row.Id == primaryRowId ? null : primaryRowId,
                    ValidationMessage = null,
                    PrecheckStatus = row.Id == primaryRowId ? row.PrecheckStatus : null,
                    PrecheckErrorCode = row.Id == primaryRowId ? row.PrecheckErrorCode : null,
                    PrecheckedAt = row.Id == primaryRowId ? row.PrecheckedAt : null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }).ToArray();
                await unit.Imports.UpdateRowsAsync(changed, cancellationToken).ConfigureAwait(false);
            }
            var result = await RefreshCountersAsync(unit.Imports, session, cancellationToken).ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public Task<DraftMutationResult> ApplyBulkOverridesAsync(
        Guid sessionId,
        DraftBulkOverride overrides,
        TransferMode transferMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (overrides.TargetPath is not null)
        {
            try { _ = pathNormalizer.Normalize(overrides.TargetPath); }
            catch (ArgumentException) { throw new DraftWorkflowException("DRAFT_INVALID_TARGET_PATH", "目标目录格式无效。"); }
        }
        return MutateAsync(sessionId, transferMode, async (unit, session) =>
        {
            if (overrides.MappedAccountId is { } requestedMapping)
                await RequireActiveAccountAsync(unit.Accounts, requestedMapping, cancellationToken).ConfigureAwait(false);
            long cursor = 0;
            while (true)
            {
                var page = await unit.Imports.GetRowsKeysetPageAsync(
                    new(sessionId, null, null, PageSize, cursor, false), cancellationToken).ConfigureAwait(false);
                var changed = page.Items.Where(row => row.Disposition != "Excluded").Select(row => row with
                {
                    TargetPath = overrides.ClearTargetPath ? null : overrides.TargetPath ?? row.TargetPath,
                    CreateFolderName = overrides.ClearCreateFolderName ? null : overrides.CreateFolderName ?? row.CreateFolderName,
                    RequestedAccountName = overrides.ClearRequestedAccount ? null : overrides.RequestedAccountName ?? row.RequestedAccountName,
                    MappedAccountId = overrides.ClearRequestedAccount
                        ? null
                        : overrides.RequestedAccountName is not null
                            ? overrides.MappedAccountId
                            : overrides.MappedAccountId ?? row.MappedAccountId,
                    Note = overrides.ClearNote ? null : overrides.Note ?? row.Note,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }).ToArray();
                if (changed.Length != 0) await unit.Imports.UpdateRowsAsync(changed, cancellationToken).ConfigureAwait(false);
                if (page.NextCursor is null) break;
                cursor = page.NextCursor.Value;
            }
        }, cancellationToken);
    }

    public Task<DraftMutationResult> ExcludeFailedPrechecksAsync(
        Guid sessionId,
        TransferMode transferMode,
        CancellationToken cancellationToken = default) =>
        MutateAsync(sessionId, transferMode, async (unit, session) =>
        {
            long cursor = 0;
            while (true)
            {
                var page = await unit.Imports.GetRowsKeysetPageAsync(
                    new(sessionId, "Ready", "Failed", PageSize, cursor, false), cancellationToken).ConfigureAwait(false);
                var changed = page.Items.Select(row => row with
                {
                    Disposition = "Excluded",
                    DuplicateOfStagedRowId = (Guid?)null,
                    ValidationMessage = "DRAFT_PRECHECK_FAILED_EXCLUDED",
                    UpdatedAt = DateTimeOffset.UtcNow,
                }).ToArray();
                if (changed.Length != 0) await unit.Imports.UpdateRowsAsync(changed, cancellationToken).ConfigureAwait(false);
                if (page.NextCursor is null) break;
                cursor = page.NextCursor.Value;
            }
        }, cancellationToken);

    private Task<DraftMutationResult> MutateRowAsync(
        Guid sessionId,
        Guid rowId,
        TransferMode transferMode,
        Func<StagedImportRowRecord, StagedImportRowRecord> change,
        CancellationToken cancellationToken) =>
        MutateAsync(sessionId, transferMode, async (unit, session) =>
        {
            var row = await unit.Imports.GetRowByIdAsync(rowId, cancellationToken).ConfigureAwait(false)
                ?? throw new DraftWorkflowException("DRAFT_ROW_NOT_FOUND", "草稿记录不存在。");
            if (row.ImportSessionId != sessionId)
                throw new DraftWorkflowException("DRAFT_ROW_SESSION_MISMATCH", "草稿记录不属于当前导入。");
            await unit.Imports.UpdateRowsAsync([change(row)], cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<DraftMutationResult> MutateAsync(
        Guid sessionId,
        TransferMode transferMode,
        Func<IUnitOfWork, ImportSessionRecord, Task> mutation,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await DraftReclassificationEngine.RequireMutableSessionAsync(unit.Imports, sessionId, cancellationToken)
                .ConfigureAwait(false);
            await mutation(unit, session).ConfigureAwait(false);
            var result = await DraftReclassificationEngine.ReclassifyAsync(unit.Imports, session, transferMode, cancellationToken)
                .ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static async Task<DraftMutationResult> RefreshCountersAsync(
        IImportRepository repository,
        ImportSessionRecord session,
        CancellationToken cancellationToken)
    {
        var counts = await repository.GetDispositionCountsAsync(session.Id, cancellationToken).ConfigureAwait(false);
        await repository.UpdateSessionAsync(session with
        {
            Status = "Draft",
            TotalRows = counts.TotalRows,
            ReadyRows = counts.ReadyRows,
            InvalidRows = checked(counts.InvalidRows + counts.ConflictRows + counts.NeedsResolutionRows),
            DuplicateRows = counts.DuplicateRows,
            ExcludedRows = counts.ExcludedRows,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
        return new(counts.TotalRows, counts.ReadyRows, counts.InvalidRows, counts.DuplicateRows,
            counts.ConflictRows, counts.NeedsResolutionRows, counts.ExcludedRows);
    }

    private static async Task RequireActiveAccountAsync(
        IAccountRepository repository,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
            throw new DraftWorkflowException("DRAFT_ACCOUNT_TARGET_UNAVAILABLE", "指定账号不可用。");
        var account = await repository.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null || !account.IsEnabled || account.IsDeleted || account.Status != "Active")
            throw new DraftWorkflowException("DRAFT_ACCOUNT_TARGET_UNAVAILABLE", "指定账号不可用。");
    }
}
