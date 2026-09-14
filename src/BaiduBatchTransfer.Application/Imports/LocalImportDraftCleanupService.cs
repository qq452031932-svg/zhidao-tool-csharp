using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Imports;

public sealed class LocalImportDraftCleanupService
{
    private readonly ILocalImportDraftCleanupStore store;

    public LocalImportDraftCleanupService(ILocalImportDraftCleanupStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<LocalImportDraftCleanupResult> DeleteAsync(
        Guid importSessionId,
        CancellationToken cancellationToken = default)
    {
        if (importSessionId == Guid.Empty)
            throw new ArgumentException("An import session is required.", nameof(importSessionId));

        return store.DeleteAsync(importSessionId, cancellationToken);
    }
}
