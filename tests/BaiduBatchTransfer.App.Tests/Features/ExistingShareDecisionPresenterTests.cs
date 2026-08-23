using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ExistingShareDecisionPresenterTests
{
    [Fact]
    public void PresenterImplementsNullableDecisionContractWithoutChangingTheApplicationToken()
    {
        Assert.Contains(typeof(IExistingShareDecisionPresenter), typeof(ExistingShareDecisionPresenter).GetInterfaces());

        var method = Assert.Single(typeof(IExistingShareDecisionPresenter).GetMethods());
        Assert.Equal("ConfirmAsync", method.Name);
        Assert.Equal(typeof(Task<ConfirmExistingShareDecisionCommand>), method.ReturnType);
        Assert.Equal(
            [typeof(ExistingShareBatchSummary), typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(NullabilityState.Nullable,
            new NullabilityInfoContext().Create(method.ReturnParameter).GenericTypeArguments[0].ReadState);
    }

    [Fact]
    public void WindowUsesARecyclingVirtualizedGridAndKeyboardAccessibleChineseActions()
    {
        RunInSta(() =>
        {
            var viewModel = new ExistingShareDecisionViewModel(new(
                Guid.Parse("20000000-0000-0000-0000-000000000021"),
                "opaque-token",
                [new(Guid.Parse("10000000-0000-0000-0000-000000000021"), 3, 2, 1)]));
            var window = new ExistingShareDecisionWindow(viewModel);
            var grid = Assert.IsType<DataGrid>(window.FindName("AccountGrid"));
            var summary = Assert.IsType<TextBlock>(window.FindName("SummaryText"));
            var confirm = Assert.IsType<Button>(window.FindName("ConfirmButton"));
            var close = Assert.IsType<Button>(window.FindName("CloseButton"));

            Assert.True(grid.EnableRowVirtualization);
            Assert.True(grid.EnableColumnVirtualization);
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(grid));
            Assert.True(ScrollViewer.GetCanContentScroll(grid));
            Assert.Equal(viewModel.DecisionSummaryText, summary.Text);
            Assert.True(confirm.IsDefault);
            Assert.True(close.IsCancel);
            Assert.Equal("确认已有分享决定", AutomationProperties.GetName(confirm));
            Assert.Equal("关闭已有分享决定窗口", AutomationProperties.GetName(close));
            Assert.DoesNotContain("Reuse", string.Concat(confirm.Content, close.Content), StringComparison.Ordinal);
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null) throw new TargetInvocationException(exception);
    }
}
