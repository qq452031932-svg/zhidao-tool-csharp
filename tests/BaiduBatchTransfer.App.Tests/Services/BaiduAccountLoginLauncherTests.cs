using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class BaiduAccountLoginLauncherTests
{
    [Fact]
    public async Task EachOpenUsesANewNonEmptyAccountAndOwnershipToken()
    {
        var presenter = new RecordingPresenter();
        var launcher = new BaiduAccountLoginLauncher(presenter);

        await launcher.OpenAsync();
        await launcher.OpenAsync();

        Assert.True(launcher.IsAvailable);
        Assert.Equal("可添加百度网盘账号。", launcher.StatusMessage);
        Assert.Equal(2, presenter.Requests.Count);
        Assert.All(presenter.Requests, request =>
        {
            Assert.NotEqual(Guid.Empty, request.AccountId);
            Assert.NotEqual(Guid.Empty, request.OwnershipToken);
        });
        Assert.NotEqual(presenter.Requests[0].AccountId, presenter.Requests[1].AccountId);
        Assert.NotEqual(presenter.Requests[0].OwnershipToken, presenter.Requests[1].OwnershipToken);
    }

    private sealed class RecordingPresenter : IBaiduLoginWindowPresenter
    {
        public List<BaiduLoginWindowRequest> Requests { get; } = [];

        public Task ShowAsync(BaiduLoginWindowRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
