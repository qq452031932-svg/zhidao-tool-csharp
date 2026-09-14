using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Licensing;

public sealed record LicenseActivationResult(bool IsAuthorized, string Code);

public sealed class LicenseActivationCloseRequestedEventArgs(bool isActivated) : EventArgs
{
    public bool IsActivated { get; } = isActivated;
}

public sealed record LicenseSettingsSnapshot
{
    public LicenseSettingsSnapshot(
        string? packageName,
        DateTimeOffset? expiresAt,
        bool isAuthorized,
        bool isMachineBound)
    {
        PackageName = packageName;
        ExpiresAt = expiresAt;
        IsAuthorized = isAuthorized;
        IsMachineBound = isMachineBound;
    }

    public string? PackageName { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public bool IsAuthorized { get; }
    public bool IsMachineBound { get; }

    public static LicenseSettingsSnapshot NotActivated { get; } =
        new(null, null, isAuthorized: false, isMachineBound: false);

    public string ExpiryDisplay => ExpiresAt?.ToString(
        "yyyy-MM-dd HH:mm zzz",
        CultureInfo.InvariantCulture) ?? "—";
}

public sealed class LicenseActivationViewModel : ObservableObject
{
    private const string InvalidCardMessage = "卡密无效";
    private const string NetworkMessage = "无法连接授权服务器，请检查网络后重试";
    private readonly Func<string, CancellationToken, Task<LicenseActivationResult>> activateAsync;
    private string card = string.Empty;
    private string statusMessage = string.Empty;
    private bool isActivating;

    public LicenseActivationViewModel(
        string productName,
        Func<string, CancellationToken, Task<LicenseActivationResult>> activateAsync)
    {
        if (string.IsNullOrWhiteSpace(productName))
            throw new ArgumentException("A product name is required.", nameof(productName));

        ProductName = productName.Trim();
        this.activateAsync = activateAsync ?? throw new ArgumentNullException(nameof(activateAsync));
        ActivateCommand = new AsyncRelayCommand(ActivateAsync, CanActivate);
        CloseCommand = new RelayCommand(RequestExplicitClose);
    }

    public event EventHandler<LicenseActivationCloseRequestedEventArgs>? CloseRequested;

    public IAsyncRelayCommand ActivateCommand { get; }
    public IRelayCommand CloseCommand { get; }
    public string ProductName { get; }
    public string ActivateButtonText => IsActivating ? "正在激活…" : "立即激活";

    public string Card
    {
        get => card;
        set => SetProperty(ref card, value ?? string.Empty);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsActivating
    {
        get => isActivating;
        private set
        {
            if (!SetProperty(ref isActivating, value)) return;
            OnPropertyChanged(nameof(ActivateButtonText));
            ActivateCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanActivate() => !IsActivating;

    private async Task ActivateAsync(CancellationToken cancellationToken)
    {
        if (IsActivating) return;
        IsActivating = true;
        try
        {
            var normalizedCard = Card.Normalize(NormalizationForm.FormKC).Trim();
            Card = normalizedCard;
            if (normalizedCard.Length == 0)
            {
                StatusMessage = InvalidCardMessage;
                return;
            }

            LicenseActivationResult result;
            try
            {
                result = await activateAsync(normalizedCard, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                StatusMessage = NetworkMessage;
                return;
            }

            if (!result.IsAuthorized)
            {
                StatusMessage = ToUserMessage(result.Code);
                return;
            }

            StatusMessage = "激活成功";
            CloseRequested?.Invoke(this, new LicenseActivationCloseRequestedEventArgs(isActivated: true));
        }
        finally
        {
            IsActivating = false;
        }
    }

    private void RequestExplicitClose() =>
        CloseRequested?.Invoke(this, new LicenseActivationCloseRequestedEventArgs(isActivated: false));

    private static string ToUserMessage(string? code) => code switch
    {
        "invalid_request" or "unsupported_protocol" or "license_not_found" => InvalidCardMessage,
        "license_expired" => "卡密已到期",
        "app_not_allowed" => "该卡密未授权当前软件",
        "app_disabled" => "当前软件已停用",
        "machine_mismatch" => "卡密已绑定其他电脑，请联系管理员解绑",
        _ => NetworkMessage,
    };
}
