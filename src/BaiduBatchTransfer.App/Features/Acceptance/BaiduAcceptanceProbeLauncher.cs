using System.Windows;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed record BaiduAcceptanceProbeLaunchResult(bool Started, string SafeMessage);

public sealed class BaiduAcceptanceProbeLaunchOptions(bool isDebugBuild, IReadOnlyList<string> arguments)
{
    public const string Switch = "--baidu-field-probe";

    public bool IsEnabled => isDebugBuild && arguments.Any(argument =>
        string.Equals(argument, Switch, StringComparison.Ordinal));

    public static BaiduAcceptanceProbeLaunchOptions FromStartup(IReadOnlyList<string> arguments) =>
        new(IsDebugBuild, arguments);

    private static bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}

public interface IBaiduAcceptanceProbePresenter
{
    Task ShowAsync(
        Guid accountId,
        string profilePath,
        CancellationToken cancellationToken = default);
}

public interface IBaiduAcceptanceProbeLauncher
{
    Task<BaiduAcceptanceProbeLaunchResult> OpenAsync(
        BaiduAcceptanceProbeLaunchOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class BaiduAcceptanceProbeLauncher(
    IUnitOfWorkFactory unitOfWorkFactory,
    IBaiduAcceptanceProbeAccountSelector accountSelector,
    IBaiduAcceptanceProbeOutputStore outputStore,
    IBaiduAcceptanceProbePresenter presenter) : IBaiduAcceptanceProbeLauncher
{
    public async Task<BaiduAcceptanceProbeLaunchResult> OpenAsync(
        BaiduAcceptanceProbeLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsEnabled) return new(false, "PROBE_DISABLED");

        BaiduAcceptanceProbeAccountSelection selection;
        try
        {
            await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
            var page = await unit.Accounts
                .GetPageAsync(new PageQuery(1, 1_000), cancellationToken)
                .ConfigureAwait(false);
            selection = accountSelector.Select(page.Items);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, "PROBE_ACCOUNT_LOOKUP_FAILED");
        }
        if (!selection.CanOpen || selection.AccountId is null || selection.ProfilePath is null)
            return new(false, "PROBE_NO_ELIGIBLE_ACCOUNT");

        try
        {
            await outputStore.EnsureWritableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, "PROBE_OUTPUT_UNAVAILABLE");
        }

        try
        {
            await presenter.ShowAsync(
                selection.AccountId.Value,
                selection.ProfilePath,
                cancellationToken).ConfigureAwait(false);
            return new(true, "现场验收窗口已关闭。脱敏证据只有在用户明确保存后才会写入本机。 ");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "PROBE_PRESENTATION_FAILED");
        }
    }
}

public sealed class BaiduAcceptanceProbePresenter(
    WebView2ProfileCloser profileCloser,
    IBaiduAcceptanceProbeOutputStore outputStore) : IBaiduAcceptanceProbePresenter
{
    public async Task ShowAsync(
        Guid accountId,
        string profilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var application = System.Windows.Application.Current ??
            throw new InvalidOperationException("现场验收窗口只能从桌面应用中打开。");
        await application.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = new BaiduAcceptanceProbeWindow(
                accountId,
                profilePath,
                profileCloser,
                outputStore)
            {
                Owner = application.MainWindow,
            };
            window.ShowDialog();
        });
    }
}
