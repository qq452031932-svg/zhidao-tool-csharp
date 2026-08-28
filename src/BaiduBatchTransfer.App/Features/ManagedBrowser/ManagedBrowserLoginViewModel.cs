using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public interface IManagedBrowserLoginOperations
{
    Task<ManagedBrowserLoginResult> LaunchAsync(ManagedBrowserLoginRequest request,
        BrowserFamily? family = null, bool reselect = false, CancellationToken cancellationToken = default);
}

public sealed class ManagedBrowserLoginViewModel : ObservableObject
{
    private readonly ManagedBrowserLoginRequest request;
    private readonly IManagedBrowserLoginOperations operations;
    private CancellationTokenSource? pending;
    private bool ended;
    private bool isBusy;
    private BrowserFamily? selectedFamily;
    private ManagedBrowserLoginResult result = new(ManagedBrowserLoginStatus.Cancelled);
    private string statusMessage = "请选择浏览器，或直接打开专用登录窗口。";

    public ManagedBrowserLoginViewModel(ManagedBrowserLoginRequest request,
        IManagedBrowserLoginOperations operations, BrowserFamily? preferredFamily = null)
    {
        this.request = request ?? throw new ArgumentNullException(nameof(request));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        SelectedFamily = preferredFamily;
        CheckLoginCommand = new AsyncRelayCommand(CheckLoginAsync, () => CanEdit);
        ReselectDirectoryCommand = new AsyncRelayCommand(ReselectDirectoryAsync, () => CanEdit);
        CancelPendingCommand = new RelayCommand(CancelPending, () => IsBusy && !ended);
    }
    public string Title => request.Kind == ManagedBrowserLoginRequestKind.AddNew ? "添加百度网盘账号" : "重新登录所选账号";
    public BrowserFamily? SelectedFamily
    {
        get => selectedFamily;
        set
        {
            if (value is not (null or BrowserFamily.Chrome or BrowserFamily.Edge)) throw new ArgumentOutOfRangeException(nameof(value));
            if (!IsBusy && !ended && !IsBound) SetProperty(ref selectedFamily, value);
        }
    }
    public IReadOnlyList<BrowserFamilyOption> BrowserFamilies { get; } =
        [new(null, "请选择（仅一种时自动使用）"), new(BrowserFamily.Chrome, "Chrome"), new(BrowserFamily.Edge, "Edge")];
    public bool IsBusy => isBusy;
    public bool IsBound => result.Status == ManagedBrowserLoginStatus.Bound;
    public bool CanEdit => !IsBusy && !ended && !IsBound;
    public string Instructions => "首次使用请选择包含 chrome.exe 或 msedge.exe 的安装目录。浏览器打开后，请在专用窗口中登录百度网盘。";
    public string CapabilityNotice => "当前版本尚未完成真实扫码识别验收；浏览器打开不代表账号已绑定。新添加的专用浏览器账号暂不用于转存或分享。";
    public string StatusMessage => statusMessage;
    public ManagedBrowserLoginResult Result => result;
    public IAsyncRelayCommand CheckLoginCommand { get; }
    public IAsyncRelayCommand ReselectDirectoryCommand { get; }
    public IRelayCommand CancelPendingCommand { get; }
    public Task CheckLoginAsync(CancellationToken token = default) => RunAsync(false, token);
    public Task ReselectDirectoryAsync(CancellationToken token = default) => RunAsync(true, token);
    public void CancelPending() => pending?.Cancel();
    public void EndLifetime()
    {
        ended = true;
        CancelPending();
        NotifyState();
    }
    private async Task RunAsync(bool reselect, CancellationToken token)
    {
        if (!CanEdit) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        pending = cancellation;
        isBusy = true;
        SetProperty(ref statusMessage, "正在打开专用浏览器并核验登录，请稍候……", nameof(StatusMessage));
        NotifyState();
        try
        {
            result = await operations.LaunchAsync(request, SelectedFamily, reselect, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { result = new(ManagedBrowserLoginStatus.Cancelled); }
        catch (Exception) { result = new(ManagedBrowserLoginStatus.BrowserUnavailable); }
        finally
        {
            pending = null;
            isBusy = false;
            SetProperty(ref statusMessage, result.SafeMessage, nameof(StatusMessage));
            NotifyState();
        }
    }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsBound));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(Result));
        CheckLoginCommand.NotifyCanExecuteChanged();
        ReselectDirectoryCommand.NotifyCanExecuteChanged();
        CancelPendingCommand.NotifyCanExecuteChanged();
    }
}

public sealed record BrowserFamilyOption(BrowserFamily? Family, string DisplayName);
