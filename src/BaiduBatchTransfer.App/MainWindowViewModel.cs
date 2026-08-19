using BaiduBatchTransfer.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly object importPage;
    private readonly object createJobPage;
    private readonly IAccountLoginLauncher loginLauncher;
    private object currentContent;
    private string currentPageTitle = "导入与草稿";
    private string statusMessage = "就绪";

    public MainWindowViewModel(object importPage, object createJobPage, IAccountLoginLauncher loginLauncher)
    {
        this.importPage = importPage ?? throw new ArgumentNullException(nameof(importPage));
        this.createJobPage = createJobPage ?? throw new ArgumentNullException(nameof(createJobPage));
        this.loginLauncher = loginLauncher ?? throw new ArgumentNullException(nameof(loginLauncher));
        currentContent = importPage;
        ShowImportCommand = new RelayCommand(ShowImport);
        ShowCreateJobCommand = new RelayCommand(ShowCreateJob);
        OpenAccountLoginCommand = new AsyncRelayCommand(OpenAccountLoginAsync, () => loginLauncher.IsAvailable);
    }

    public string ApplicationTitle => "百度批量转存";
    public object CurrentContent { get => currentContent; private set => SetProperty(ref currentContent, value); }
    public string CurrentPageTitle { get => currentPageTitle; private set => SetProperty(ref currentPageTitle, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public string AdapterStatusMessage => loginLauncher.StatusMessage;
    public IRelayCommand ShowImportCommand { get; }
    public IRelayCommand ShowCreateJobCommand { get; }
    public IAsyncRelayCommand OpenAccountLoginCommand { get; }

    public async Task OpenAccountLoginAsync(CancellationToken cancellationToken = default)
    {
        if (!loginLauncher.IsAvailable)
        {
            StatusMessage = loginLauncher.StatusMessage;
            return;
        }
        try
        {
            await loginLauncher.OpenAsync(cancellationToken).ConfigureAwait(false);
            StatusMessage = "账号登录窗口已打开，请按提示扫码。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "已取消打开账号登录。";
        }
        catch (Exception)
        {
            StatusMessage = "无法打开账号登录，请稍后重试。";
        }
    }

    private void ShowImport()
    {
        CurrentContent = importPage;
        CurrentPageTitle = "导入与草稿";
    }

    private void ShowCreateJob()
    {
        CurrentContent = createJobPage;
        CurrentPageTitle = "创建任务";
    }
}
