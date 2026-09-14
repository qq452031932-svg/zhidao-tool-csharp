using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class LocalTerminalHistoryService(ILocalTerminalHistoryStore store)
{
    private readonly ILocalTerminalHistoryStore store =
        store ?? throw new ArgumentNullException(nameof(store));

    public Task<LocalHistoryCleanupPreview> PreviewAsync(
        LocalHistoryKind kind,
        CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        return store.PreviewAsync(kind, cancellationToken);
    }

    public async Task<LocalHistoryCleanupResult> DeleteAsync(
        LocalHistoryKind kind,
        CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        var preview = await store.PreviewAsync(kind, cancellationToken).ConfigureAwait(false);
        if (preview.HasConflicts)
            return new(0, LocalHistoryCleanupDisposition.Conflict);
        if (preview.TerminalBatchCount == 0)
            return new(0, LocalHistoryCleanupDisposition.NothingToDelete);

        return await store.DeleteAsync(kind, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateKind(LocalHistoryKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A known local history kind is required.");
    }
}
