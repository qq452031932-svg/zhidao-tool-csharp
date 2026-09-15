using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BaiduBatchTransfer.LegacyLauncher;

internal static class Program
{
    private const int PlanFailureExitCode = 2;
    private const int LaunchFailureExitCode = 3;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxOk = 0x00000000;
    private const string SuppressDialogEnvironmentVariable =
        "BAIDU_BATCH_TRANSFER_LEGACY_LAUNCHER_SUPPRESS_DIALOG";

    [STAThread]
    private static int Main(string[] args)
    {
        if (!LegacyLaunchPlan.TryCreate(
                Environment.ProcessPath,
                args,
                InspectPath,
                out var plan,
                out var error))
        {
            ShowError(error);
            return PlanFailureExitCode;
        }

        try
        {
            using var child = Process.Start(plan!.CreateStartInfo());
            if (child is null)
            {
                ShowError("无法启动新版程序。");
                return LaunchFailureExitCode;
            }

            if (plan.WaitForExit)
            {
                child.WaitForExit();
            }

            return plan.GetSuccessfulExitCode(plan.WaitForExit ? child.ExitCode : 0);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            ShowError($"无法启动新版程序：{exception.Message}");
            return LaunchFailureExitCode;
        }
    }

    private static LegacyPathState InspectPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return LegacyPathState.ReparsePoint;
            }

            return (attributes & FileAttributes.Directory) != 0
                ? LegacyPathState.Directory
                : LegacyPathState.RegularFile;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException or
            UnauthorizedAccessException or IOException or ArgumentException or
            NotSupportedException)
        {
            return LegacyPathState.Missing;
        }
    }

    private static void ShowError(string message)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(SuppressDialogEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        _ = MessageBoxW(
            IntPtr.Zero,
            message,
            ProductIdentity.DisplayName,
            MessageBoxOk | MessageBoxIconError);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(
        IntPtr windowHandle,
        string text,
        string caption,
        uint type);
}
