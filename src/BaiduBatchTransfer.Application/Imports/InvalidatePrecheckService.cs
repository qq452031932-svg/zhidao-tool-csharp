using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Imports;

public sealed class InvalidatePrecheckService
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory;
    private readonly TimeProvider timeProvider;
    private readonly BaiduShareNormalizer normalizer = new();

    public InvalidatePrecheckService(IUnitOfWorkFactory unitOfWorkFactory, TimeProvider? timeProvider = null)
    {
        this.unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task UpdateLinkOrExtractionCodeAsync(
        Guid rowId,
        string shareUrl,
        string? extractionCode,
        CancellationToken cancellationToken = default)
    {
        if (rowId == Guid.Empty) throw new ArgumentException("A staging row is required.", nameof(rowId));
        ArgumentException.ThrowIfNullOrWhiteSpace(shareUrl);
        var normalized = normalizer.Normalize(shareUrl);
        var now = timeProvider.GetUtcNow();

        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var row = await unit.Imports.GetRowByIdAsync(rowId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The staging row does not exist.");
        var import = await unit.Imports.GetSessionAsync(row.ImportSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The import session does not exist.");
        if (import.Status is not ("Draft" or "Prechecking" or "ReadyToConfirm"))
            throw new InvalidOperationException("Only a completed import draft can be edited.");
        if (row.Disposition != "Ready")
            throw new InvalidOperationException("Precheck invalidation only edits a ready draft row.");
        if (row.NormalizedShareId is { } oldNormalizedId)
        {
            var laterGroupRows = await unit.Imports.GetRowsByNormalizedShareIdAfterSourceRowAsync(
                row.ImportSessionId,
                oldNormalizedId,
                row.SourceRowNumber,
                1,
                cancellationToken).ConfigureAwait(false);
            if (laterGroupRows.Count != 0)
                throw new InvalidOperationException("A duplicate group must be resolved before editing its prechecked row.");
        }
        var existing = await unit.Imports.GetFirstByNormalizedShareIdAsync(
            row.ImportSessionId,
            normalized.ShareId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Id != row.Id)
            throw new InvalidOperationException("The edited link conflicts with another staged row.");

        await unit.Imports.UpdateRowsAsync(
            [row with
            {
                ShareUrl = normalized.CanonicalUrl,
                NormalizedShareId = normalized.ShareId,
                ExtractionCode = string.IsNullOrWhiteSpace(extractionCode)
                    ? normalized.ExtractionCode
                    : extractionCode.Trim(),
                Disposition = "Ready",
                DuplicateOfStagedRowId = null,
                ValidationMessage = null,
                PrecheckStatus = null,
                PrecheckErrorCode = null,
                PrecheckedAt = null,
                UpdatedAt = now,
            }],
            cancellationToken).ConfigureAwait(false);
        if (import.Status == "ReadyToConfirm")
        {
            await unit.Imports.UpdateSessionAsync(
                import with { Status = "Draft", UpdatedAt = now },
                cancellationToken).ConfigureAwait(false);
        }
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
