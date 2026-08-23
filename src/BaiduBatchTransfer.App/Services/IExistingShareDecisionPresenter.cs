using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Services;

public interface IExistingShareDecisionPresenter
{
    Task<ConfirmExistingShareDecisionCommand?> ConfirmAsync(
        ExistingShareBatchSummary summary,
        CancellationToken cancellationToken = default);
}
