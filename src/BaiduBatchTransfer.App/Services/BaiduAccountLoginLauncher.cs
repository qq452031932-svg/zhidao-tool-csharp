namespace BaiduBatchTransfer.App.Services;

public enum BaiduLoginWindowPurpose
{
    AddAccount,
    RefreshAccountSession,
}

public sealed record BaiduLoginWindowRequest(
    Guid AccountId,
    Guid OwnershipToken,
    BaiduLoginWindowPurpose Purpose = BaiduLoginWindowPurpose.AddAccount)
{
    public override string ToString() =>
        "BaiduLoginWindowRequest { AccountId = [REDACTED], OwnershipToken = [REDACTED] }";
}

public interface IBaiduLoginWindowPresenter
{
    Task ShowAsync(BaiduLoginWindowRequest request, CancellationToken cancellationToken);
}

public sealed class BaiduAccountLoginLauncher(IBaiduLoginWindowPresenter presenter) : IAccountLoginLauncher
{
    private readonly IBaiduLoginWindowPresenter presenter =
        presenter ?? throw new ArgumentNullException(nameof(presenter));

    public bool IsAvailable => true;
    public string StatusMessage => "可添加百度网盘账号。";

    public Task OpenAsync(CancellationToken cancellationToken = default) =>
        presenter.ShowAsync(new(Guid.NewGuid(), Guid.NewGuid()), cancellationToken);
}
