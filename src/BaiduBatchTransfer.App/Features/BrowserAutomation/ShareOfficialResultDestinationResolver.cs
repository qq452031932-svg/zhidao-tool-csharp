using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public interface IShareOfficialResultDestinationResolver
{
    Task<string?> ResolveAsync(
        ShareOperationAutomationContext context,
        CancellationToken cancellationToken = default);
}

public sealed class ShareOfficialResultDestinationResolver(
    ILocalResultDestinationStore transferDestinations,
    IStandaloneShareStore standaloneShares) : IShareOfficialResultDestinationResolver
{
    private readonly ILocalResultDestinationStore transferDestinations =
        transferDestinations ?? throw new ArgumentNullException(nameof(transferDestinations));
    private readonly IStandaloneShareStore standaloneShares =
        standaloneShares ?? throw new ArgumentNullException(nameof(standaloneShares));

    public async Task<string?> ResolveAsync(
        ShareOperationAutomationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        if (context.OwnerKind == WebAutomationOwnerKind.TransferShare)
        {
            var destination = await transferDestinations.GetAsync(context.BatchId, cancellationToken)
                .ConfigureAwait(false);
            return destination?.DirectoryReference;
        }
        if (context.OwnerKind != WebAutomationOwnerKind.StandaloneShare) return null;

        var snapshot = await standaloneShares.GetBatchSnapshotAsync(context.BatchId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null) return null;
        var operation = snapshot.Operations.SingleOrDefault(candidate => candidate.Id == context.OperationId);
        if (operation is null || operation.AccountId != context.AccountId ||
            operation.Generation != context.Generation || operation.ExactRemoteItems != context.ExactRemoteItems)
            return null;
        return snapshot.Batch.ResultDirectoryReference;
    }
}
