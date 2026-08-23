using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Features.Jobs;

public sealed record ExistingShareInteractionOutcome(
    bool Completed,
    bool UserClosed,
    bool IsStale,
    string StatusText);

public interface IExistingShareDecisionInteraction
{
    Task<IReadOnlyList<Guid>> GetPendingBatchIdsAsync(
        int maximumCount = 1,
        CancellationToken cancellationToken = default);
    Task<ExistingShareInteractionOutcome> ResolveAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default);
}

public interface IExistingShareDecisionApplication
{
    Task<IReadOnlyList<Guid>> GetPendingBatchIdsAsync(int maximumCount, CancellationToken cancellationToken = default);
    Task<ExistingShareBatchSummary> DiscoverAsync(Guid batchJobId, CancellationToken cancellationToken = default);
    Task<ExistingShareDecisionConfirmation> ConfirmAsync(
        ConfirmExistingShareDecisionCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class ExistingShareDecisionApplication(ExistingShareDecisionService service)
    : IExistingShareDecisionApplication
{
    public Task<IReadOnlyList<Guid>> GetPendingBatchIdsAsync(
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        service.GetPendingBatchIdsAsync(maximumCount, cancellationToken);

    public Task<ExistingShareBatchSummary> DiscoverAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default) =>
        service.DiscoverAsync(batchJobId, cancellationToken);

    public Task<ExistingShareDecisionConfirmation> ConfirmAsync(
        ConfirmExistingShareDecisionCommand command,
        CancellationToken cancellationToken = default) =>
        service.ConfirmAsync(command, cancellationToken);
}

public sealed class ExistingShareDecisionInteraction(
    IExistingShareDecisionApplication service,
    IExistingShareDecisionPresenter presenter) : IExistingShareDecisionInteraction
{
    private readonly IExistingShareDecisionApplication service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly IExistingShareDecisionPresenter presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));

    public Task<IReadOnlyList<Guid>> GetPendingBatchIdsAsync(
        int maximumCount = 1,
        CancellationToken cancellationToken = default) =>
        service.GetPendingBatchIdsAsync(maximumCount, cancellationToken);

    public async Task<ExistingShareInteractionOutcome> ResolveAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var summary = await service.DiscoverAsync(batchJobId, cancellationToken).ConfigureAwait(false);
            if (!summary.Accounts.Any(account => account.RequiresDecision))
                return new(true, false, false, "没有需要处理的已有分享。");

            var command = await presenter.ConfirmAsync(summary, cancellationToken).ConfigureAwait(false);
            if (command is null)
                return new(false, true, false, "已关闭已有分享确认，任务仍保持暂停。");

            var result = await service.ConfirmAsync(command, cancellationToken).ConfigureAwait(false);
            if (result.Accepted)
                return new(true, false, false, "已有分享处理决定已保存，任务可以继续调度。");
            if (!result.IsStale)
                return new(false, false, false, "已有分享处理决定未被接受，请重新检查。");
        }

        return new(false, false, true, "已有分享状态已经变化，请重新打开确认。");
    }
}
