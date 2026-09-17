using BaiduBatchTransfer.App.Features.Licensing;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LicenseActivationViewModelTests
{
    [Fact]
    public async Task SuccessfulActivationNormalizesTheCardAndRequestsAConfirmedClose()
    {
        string? submittedCard = null;
        var viewModel = new LicenseActivationViewModel(
            "SuperPan",
            (card, _) =>
            {
                submittedCard = card;
                return Task.FromResult(new LicenseActivationResult(true, "ok"));
            });
        LicenseActivationCloseRequestedEventArgs? closeRequest = null;
        viewModel.CloseRequested += (_, request) => closeRequest = request;
        // \u5168\u89D2\u3001\u5C0F\u5199\u3001\u7F3A\u6A2A\u7EBF\u3001\u9996\u5C3E\u7A7A\u767D\u7684\u6DF7\u5408\u8F93\u5165\uFF0C\u5E94\u5F52\u4E00\u5316\u4E3A\u670D\u52A1\u7AEF\u8981\u6C42\u7684 4 \u7EC4 8 \u4F4D\u5927\u5199\u5F62\u6001\u3002
        viewModel.Card = " \uFF41bcd2345efgh6723 jklm4567-npqr2345 ";

        await viewModel.ActivateCommand.ExecuteAsync(null);

        Assert.Equal("ABCD2345-EFGH6723-JKLM4567-NPQR2345", submittedCard);
        Assert.Equal("ABCD2345-EFGH6723-JKLM4567-NPQR2345", viewModel.Card);
        Assert.Equal("激活成功", viewModel.StatusMessage);
        Assert.True(Assert.IsType<LicenseActivationCloseRequestedEventArgs>(closeRequest).IsActivated);
    }

    [Fact]
    public async Task ActivationCommandDisablesDuplicateSubmissionsWhileARequestIsRunning()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<LicenseActivationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var closeRequests = 0;
        var viewModel = new LicenseActivationViewModel(
            "SuperPan",
            (_, _) =>
            {
                calls++;
                started.TrySetResult();
                return completion.Task;
            })
        {
            Card = "ABCD2345-EFGH6723-JKLM4567-NPQR2345"
        };
        viewModel.CloseRequested += (_, _) => closeRequests++;

        var activation = viewModel.ActivateCommand.ExecuteAsync(null);
        await started.Task;

        Assert.False(viewModel.ActivateCommand.CanExecute(null));
        Assert.False(viewModel.CloseCommand.CanExecute(null));
        viewModel.ActivateCommand.Execute(null);
        viewModel.CloseCommand.Execute(null);
        Assert.Equal(1, calls);
        Assert.Equal(0, closeRequests);

        completion.SetResult(new LicenseActivationResult(false, "license_not_found"));
        await activation;

        Assert.True(viewModel.ActivateCommand.CanExecute(null));
        Assert.True(viewModel.CloseCommand.CanExecute(null));
    }

    [Fact]
    public async Task ActivateButtonTextTracksActivationAndRaisesChangeNotifications()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<LicenseActivationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new LicenseActivationViewModel(
            "知道综合工具",
            (_, _) =>
            {
                started.TrySetResult();
                return completion.Task;
            })
        {
            Card = "ABCD2345-EFGH6723-JKLM4567-NPQR2345"
        };
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        Assert.Equal("立即激活", viewModel.ActivateButtonText);

        var activation = viewModel.ActivateCommand.ExecuteAsync(null);
        await started.Task;

        Assert.Equal("正在激活…", viewModel.ActivateButtonText);
        Assert.Equal("正在激活…", viewModel.StatusMessage);
        Assert.Contains(nameof(LicenseActivationViewModel.ActivateButtonText), changes);

        changes.Clear();
        completion.SetResult(new LicenseActivationResult(false, "license_not_found"));
        await activation;

        Assert.Equal("立即激活", viewModel.ActivateButtonText);
        Assert.Contains(nameof(LicenseActivationViewModel.ActivateButtonText), changes);
    }

    [Theory]
    [InlineData("invalid_request", "卡密无效")]
    [InlineData("unsupported_protocol", "卡密无效")]
    [InlineData("license_not_found", "卡密无效")]
    [InlineData("license_expired", "卡密已到期")]
    [InlineData("app_not_allowed", "该卡密未授权当前软件")]
    [InlineData("app_disabled", "当前软件已停用")]
    [InlineData("machine_mismatch", "卡密已绑定其他电脑，请联系管理员解绑")]
    [InlineData("rate_limited", "无法连接授权服务器，请检查网络后重试")]
    [InlineData("service_unavailable", "无法连接授权服务器，请检查网络后重试")]
    [InlineData("authorization_unavailable", "无法连接授权服务器，请检查网络后重试")]
    [InlineData("backend-private-error", "无法连接授权服务器，请检查网络后重试")]
    public async Task RejectionsUseOnlyApprovedChineseStatusMessages(string code, string expectedMessage)
    {
        var viewModel = new LicenseActivationViewModel(
            "SuperPan",
            (_, _) => Task.FromResult(new LicenseActivationResult(false, code)))
        {
            Card = "ABCD2345-EFGH6723-JKLM4567-NPQR2345"
        };
        var closeRequests = 0;
        viewModel.CloseRequested += (_, _) => closeRequests++;

        await viewModel.ActivateCommand.ExecuteAsync(null);

        Assert.Equal(expectedMessage, viewModel.StatusMessage);
        Assert.DoesNotContain("SECRET-CARD", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(code, viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0, closeRequests);
    }

    [Fact]
    public async Task EmptyCardIsRejectedLocallyWithoutClosingOrCallingTheBackend()
    {
        var calls = 0;
        var closeRequests = 0;
        var viewModel = new LicenseActivationViewModel(
            "SuperPan",
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new LicenseActivationResult(true, "ok"));
            })
        {
            Card = "  "
        };
        viewModel.CloseRequested += (_, _) => closeRequests++;

        await viewModel.ActivateCommand.ExecuteAsync(null);

        Assert.Equal("卡密无效", viewModel.StatusMessage);
        Assert.Equal(0, calls);
        Assert.Equal(0, closeRequests);
    }

    [Fact]
    public async Task BackendExceptionsAreReducedToTheApprovedNetworkMessage()
    {
        var viewModel = new LicenseActivationViewModel(
            "SuperPan",
            (_, _) => throw new InvalidOperationException("card=SECRET machine=PRIVATE signature=INTERNAL"))
        {
            Card = "ABCD2345-EFGH6723-JKLM4567-NPQR2345"
        };

        await viewModel.ActivateCommand.ExecuteAsync(null);

        Assert.Equal("无法连接授权服务器，请检查网络后重试", viewModel.StatusMessage);
        Assert.DoesNotContain("SECRET", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("INTERNAL", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseCommandRequestsAnExplicitUnconfirmedClose()
    {
        var viewModel = new LicenseActivationViewModel(
            "SuperPan",
            (_, _) => Task.FromResult(new LicenseActivationResult(false, "license_not_found")));
        LicenseActivationCloseRequestedEventArgs? closeRequest = null;
        viewModel.CloseRequested += (_, request) => closeRequest = request;

        viewModel.CloseCommand.Execute(null);

        Assert.False(Assert.IsType<LicenseActivationCloseRequestedEventArgs>(closeRequest).IsActivated);
    }
}
