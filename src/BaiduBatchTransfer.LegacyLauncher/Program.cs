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
    private static int Main(string[] args) => Run(Environment.ProcessPath, args, ShowError);

    // Trust boundary: package validation/signing establishes the publisher identity of shipped
    // files, and the per-user Velopack install ACL is trusted against other Windows principals.
    // These checks block accidental/path redirection, reparse points, and hard-link aliases. They
    // do not claim to defeat malicious code already executing as this same Windows user, including
    // the unavoidable validation-to-CreateProcess swap. Package-time same-publisher verification
    // remains mandatory and is implemented by the later packaging validation work.
    internal static int Run(
        string? launcherProcessPath,
        IReadOnlyList<string> args,
        Action<string> showError)
    {
        if (!LegacyLaunchPlan.TryCreate(
                launcherProcessPath,
                args,
                WindowsPathInspector.Inspect,
                out var plan,
                out var error))
        {
            showError(error);
            return PlanFailureExitCode;
        }

        try
        {
            using var child = Process.Start(plan!.CreateStartInfo());
            if (child is null)
            {
                showError("无法启动新版程序。");
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
            showError($"无法启动新版程序：{exception.Message}");
            return LaunchFailureExitCode;
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
