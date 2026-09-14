using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class LocalTerminalHistoryService(ILocalTerminalHistoryStore store)
{
    private readonly ILocalTerminalHistoryStore store =
        store ?? throw new ArgumentNullException(nameof(store));

    public async Task<LocalHistoryCleanupPreview> PreviewAsync(
        LocalHistoryKind kind,
        CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        cancellationToken.ThrowIfCancellationRequested();
        var preview = await store.PreviewAsync(kind, cancellationToken).ConfigureAwait(false);
        if (preview.Kind != kind)
            throw new InvalidOperationException("The local history preview kind does not match the request.");
        return preview;
    }

    public async Task<LocalHistoryCleanupResult> DeleteAsync(
        LocalHistoryCleanupPreview confirmedPreview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedPreview);
        cancellationToken.ThrowIfCancellationRequested();
        if (confirmedPreview.HasConflicts)
            return new(0, LocalHistoryCleanupDisposition.Conflict);
        if (confirmedPreview.TerminalBatchCount == 0)
            return new(0, LocalHistoryCleanupDisposition.NothingToDelete);

        return await store.DeleteAsync(confirmedPreview, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateKind(LocalHistoryKind kind)
    {
        if (!Enum.IsDefined(kind) || kind == LocalHistoryKind.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A known local history kind is required.");
    }
}
