using System.Windows;
using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public partial class AcceptanceConfirmationWindow : Window
{
    private readonly AcceptanceWriteLeaseCoordinator coordinator;
    private readonly AdapterWriteAuthorizationRequest request;

    public AcceptanceConfirmationWindow(
        AcceptanceWriteLeaseCoordinator coordinator,
        AdapterWriteAuthorizationRequest request)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.request = request ?? throw new ArgumentNullException(nameof(request));
        InitializeComponent();
    }

    public bool EnableVersionAcceptance(
        bool remoteResultVerified,
        bool recoveryVerified,
        bool localExportVerified,
        string evidenceHash)
    {
        var enabled = coordinator.RecordCompletedEvidence(
            request,
            remoteResultVerified,
            recoveryVerified,
            localExportVerified,
            evidenceHash);
        AcceptVersionButton.IsEnabled = enabled;
        StatusText.Text = enabled
            ? "远端结果、恢复证据和本机导出均已通过；可二次确认接受当前版本。"
            : "证据尚未全部通过，不能接受当前版本。";
        return enabled;
    }

    private void OnCreateLease(object sender, RoutedEventArgs e)
    {
        var created = coordinator.TryCreateLease(request, new(
            DisposableResourceCheck.IsChecked == true,
            RealOperationConsentCheck.IsChecked == true,
            SingleUseCheck.IsChecked == true));
        StatusText.Text = created
            ? "一次性租约已创建，仅当前操作可消费。"
            : "未创建租约：请确认处于 Debug 验收模式并逐项勾选。";
    }

    private async void OnAcceptVersion(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "确定接受当前适配器、协议、定位器和应用构建版本吗？",
                "二次确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        AcceptVersionButton.IsEnabled = false;
        try
        {
            var accepted = await coordinator.AcceptVersionAsync(request, userConfirmed: true);
            StatusText.Text = accepted ? "当前版本已正式接受。" : "版本接受失败，未写入记录。";
        }
        catch
        {
            StatusText.Text = "版本接受失败，未写入记录。";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
