using System.Diagnostics;
using System.Globalization;
using System.IO;
using Velopack.Locators;

namespace BaiduBatchTransfer.App.Updates;

internal static class SignedUpdateGuard
{
    internal const string ModeArgument = "--superpan-ota-guard";
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(90);

    internal static bool Start()
    {
        if (!OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(Environment.ProcessPath) ||
            !Path.IsPathFullyQualified(Environment.ProcessPath))
        {
            return false;
        }

        using var current = Process.GetCurrentProcess();
        var startTicks = current.StartTime.ToUniversalTime().Ticks;
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath,
            WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(ModeArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(startTicks.ToString(CultureInfo.InvariantCulture));
        try
        {
            using var guard = Process.Start(startInfo);
            return guard is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.ComponentModel.Win32Exception or
                                          IOException or
                                          UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryRun(
        string[] args,
        ApplicationBuildInfo buildInfo,
        out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(buildInfo);
        if (args.Length == 0 || !string.Equals(args[0], ModeArgument, StringComparison.Ordinal))
        {
            exitCode = 0;
            return false;
        }

        exitCode = RunGuard(args, buildInfo) ? 0 : 2;
        return true;
    }

    private static bool RunGuard(string[] args, ApplicationBuildInfo buildInfo)
    {
        if (!OperatingSystem.IsWindows() ||
            args.Length != 3 ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid) ||
            parentPid <= 0 ||
            parentPid == Environment.ProcessId ||
            !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentStartTicks) ||
            parentStartTicks <= 0 ||
            buildInfo.HttpsFeedUrl is not { } feedIdentity)
        {
            return false;
        }

        var packagesRoot = VelopackLocator.Current.PackagesDir;
        if (string.IsNullOrWhiteSpace(packagesRoot) || !WaitForParentExit(parentPid, parentStartTicks))
            return false;

        using var client = VelopackUpdateClient.Create(buildInfo);
        if (!client.IsInstalled)
            return false;
        var currentVersion = client.CurrentVersion ?? buildInfo.ProductVersion;
        var gate = new SignedFeedPackageGate(
            buildInfo.SignedPendingReceiptPath,
            new SignedFeedStateStore(
                buildInfo.SignedFeedStatePath,
                feedIdentity,
                currentVersion),
            packagesRoot);
        return ValidateAndLaunchAfterParentExitForTesting(
            static () => { },
            () => client.UpdatePendingRestart,
            gate,
            client.WaitExitThenApplyUpdates);
    }

    private static bool WaitForParentExit(int parentPid, long expectedStartTicks)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != expectedStartTicks)
                return false;
            return parent.WaitForExit(checked((int)ParentExitTimeout.TotalMilliseconds));
        }
        catch (ArgumentException)
        {
            // The original process exited before the guard acquired its handle.
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.ComponentModel.Win32Exception or
                                          NotSupportedException)
        {
            return false;
        }
    }

    internal static bool ValidateAndLaunchAfterParentExitForTesting(
        Action waitForParentExit,
        Func<VelopackClientAsset?> readPending,
        SignedFeedPackageGate gate,
        Action<VelopackClientAsset> invokeUpdater)
    {
        ArgumentNullException.ThrowIfNull(waitForParentExit);
        ArgumentNullException.ThrowIfNull(readPending);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(invokeUpdater);

        waitForParentExit();
        var pending = readPending();
        if (pending is null)
            return false;
        using var authorization = gate.TryAcquireLaunchAuthorization(pending);
        if (authorization is null)
            return false;
        invokeUpdater(pending);
        return true;
    }
}
