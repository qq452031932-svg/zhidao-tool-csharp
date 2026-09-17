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

    // 与授权服务端 card_crypto.go 的固定格式一致：20 字节随机数经 base32（字母 A-Z、数字 2-7）
    // 编码为 32 位，分 4 组、每组 8 位，用横线连接。服务端会自行去横线、去空格并转大写，
    // 这里做同样的归一化只是为了让用户在输入时就看到规范形态，并在格式不全时免去一次联网往返。
    private const int CardGroupSize = 8;
    private const int CardGroupCount = 4;
    private const int CardCompactLength = CardGroupSize * CardGroupCount;

    public static string FormatCardInput(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var compact = new StringBuilder(CardCompactLength);
        foreach (var character in raw.Normalize(NormalizationForm.FormKC))
        {
            if (compact.Length == CardCompactLength) break;
            var upper = char.ToUpperInvariant(character);
            if (upper is (>= 'A' and <= 'Z') or (>= '2' and <= '7')) compact.Append(upper);
        }

        var grouped = new StringBuilder(CardCompactLength + CardGroupCount - 1);
        for (var index = 0; index < compact.Length; index++)
        {
            if (index > 0 && index % CardGroupSize == 0) grouped.Append('-');
            grouped.Append(compact[index]);
        }

        return grouped.ToString();
    }

    private static int CompactLengthOf(string formatted)
    {
        var length = 0;
        foreach (var character in formatted)
        {
            if (character != '-') length++;
        }

        return length;
    }
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
        CloseCommand = new RelayCommand(RequestExplicitClose, CanClose);
    }

    public event EventHandler<LicenseActivationCloseRequestedEventArgs>? CloseRequested;

    public IAsyncRelayCommand ActivateCommand { get; }
    public IRelayCommand CloseCommand { get; }
    public string ProductName { get; }
    public string ActivateButtonText => IsActivating ? "正在激活…" : "立即激活";

    // 购买渠道文案。留空时界面上不显示任何内容，因此未配置也不会出现占位残留。
    public string PurchaseHintText { get; init; } = string.Empty;

    public string Card
    {
        get => card;
        set
        {
            if (!SetProperty(ref card, FormatCardInput(value))) return;
            OnPropertyChanged(nameof(CardProgressHint));
            OnPropertyChanged(nameof(IsCardComplete));
        }
    }

    public bool IsCardComplete => CompactLengthOf(card) == CardCompactLength;

    public string CardProgressHint
    {
        get
        {
            var entered = CompactLengthOf(card);
            if (entered == 0) return string.Empty;
            return entered == CardCompactLength
                ? "格式正确"
                : $"已输入 {entered}/{CardCompactLength} 位";
        }
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
            CloseCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanActivate() => !IsActivating;
    private bool CanClose() => !IsActivating;

    private async Task ActivateAsync(CancellationToken cancellationToken)
    {
        if (IsActivating) return;
        IsActivating = true;
        StatusMessage = "正在激活…";
        try
        {
            var normalizedCard = FormatCardInput(Card);
            Card = normalizedCard;
            if (!IsCardComplete)
            {
                // 位数不足时本地就拒绝，不去打授权服务器：服务端同样要求恰好 32 位，
                // 提前判断只是省掉一次必然失败的往返。
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
                return;
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

    private void RequestExplicitClose()
    {
        if (IsActivating) return;
        CloseRequested?.Invoke(this, new LicenseActivationCloseRequestedEventArgs(isActivated: false));
    }

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
