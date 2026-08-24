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
    IBaiduAcceptanceProbeProfilePathPolicy profilePathPolicy,
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
        if (!selection.CanOpen || selection.AccountId is null)
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

        BaiduAcceptanceProbeProfileLease? profileLease = null;
        try
        {
            profileLease = profilePathPolicy.Create(selection.AccountId.Value);
            await presenter.ShowAsync(
                selection.AccountId.Value,
                profileLease.ProfilePath,
                cancellationToken).ConfigureAwait(false);
            return await CleanupResultAsync(profileLease).ConfigureAwait(false) == BaiduAcceptanceProbeCleanupOutcome.Cleaned
                ? new(true, "现场验收窗口已关闭。脱敏证据只有在用户明确保存后才会写入本机。 ")
                : new(true, "PROBE_CLEANUP_DEFERRED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (profileLease is not null) await CleanupResultAsync(profileLease).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            if (profileLease is not null) await CleanupResultAsync(profileLease).ConfigureAwait(false);
            return new(false, "PROBE_PRESENTATION_FAILED");
        }
    }

    private static async Task<BaiduAcceptanceProbeCleanupOutcome> CleanupResultAsync(BaiduAcceptanceProbeProfileLease profileLease)
    {
        try { await profileLease.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { return BaiduAcceptanceProbeCleanupOutcome.Deferred; }
        return profileLease.CleanupOutcome;
    }
}

public interface IBaiduAcceptanceProbeUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

public interface IBaiduAcceptanceProbeWindow
{
    void Show();
    Task WaitForInitializationAsync(CancellationToken cancellationToken = default);
    Task WaitForClosedAsync(CancellationToken cancellationToken = default);
}

public interface IBaiduAcceptanceProbeWindowFactory
{
    IBaiduAcceptanceProbeWindow Create(Guid accountId, string profilePath);
}

public sealed class BaiduAcceptanceProbePresenter(
    IBaiduAcceptanceProbeUiDispatcher dispatcher,
    IBaiduAcceptanceProbeWindowFactory windowFactory) : IBaiduAcceptanceProbePresenter
{
    public async Task ShowAsync(
        Guid accountId,
        string profilePath,
        CancellationToken cancellationToken = default)
    {
        IBaiduAcceptanceProbeWindow? window = null;
        await dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = windowFactory.Create(accountId, profilePath);
            window.Show();
        }, cancellationToken).ConfigureAwait(false);
        await (window ?? throw new InvalidOperationException("现场验收窗口未创建。"))
            .WaitForInitializationAsync(cancellationToken).ConfigureAwait(false);
        await window.WaitForClosedAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class WpfBaiduAcceptanceProbeUiDispatcher : IBaiduAcceptanceProbeUiDispatcher
{
    public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        var application = System.Windows.Application.Current ??
            throw new InvalidOperationException("现场验收窗口只能从桌面应用中打开。");
        await application.Dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
    }
}

public sealed class BaiduAcceptanceProbeWindowFactory(IBaiduAcceptanceProbeOutputStore outputStore)
    : IBaiduAcceptanceProbeWindowFactory
{
    public IBaiduAcceptanceProbeWindow Create(Guid accountId, string profilePath) =>
        new BaiduAcceptanceProbeWindow(accountId, profilePath, outputStore)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
}
