using System.Diagnostics;
using BaiduBatchTransfer.LegacyLauncher;

namespace BaiduBatchTransfer.App.Tests.Branding;

public sealed class LegacyLaunchPlanTests
{
    [Fact]
    public void CreateTargetsExactMainExecutableInLauncherDirectory()
    {
        var launcher = Path.GetFullPath(Path.Combine("install", "BaiduBatchTransfer.App.exe"));
        var expectedTarget = Path.GetFullPath(Path.Combine("install", "知道综合工具.exe"));

        var result = LegacyLaunchPlan.TryCreate(
            launcher,
            [],
            path => RegularFiles(path, launcher, expectedTarget),
            out var plan,
            out var error);

        Assert.True(result, error);
        Assert.NotNull(plan);
        Assert.Equal(expectedTarget, plan.TargetPath, ignoreCase: true);
        Assert.Equal(Path.GetDirectoryName(launcher), plan.WorkingDirectory, ignoreCase: true);
        Assert.Equal("知道综合工具.exe", Path.GetFileName(plan.TargetPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.exe")]
    public void CreateFailsClosedWithoutAbsoluteLauncherPath(string? launcher)
    {
        Assert.False(LegacyLaunchPlan.TryCreate(
            launcher,
            [],
            _ => LegacyPathState.RegularFile,
            out var plan,
            out var error));

        Assert.Null(plan);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(LegacyPathState.Missing)]
    [InlineData(LegacyPathState.Directory)]
    [InlineData(LegacyPathState.ReparsePoint)]
    public void CreateFailsClosedWhenTargetIsMissingOrUnsafe(LegacyPathState targetState)
    {
        var launcher = Path.GetFullPath(Path.Combine("install", "BaiduBatchTransfer.App.exe"));

        Assert.False(LegacyLaunchPlan.TryCreate(
            launcher,
            [],
            path => string.Equals(path, launcher, StringComparison.OrdinalIgnoreCase)
                ? LegacyPathState.RegularFile
                : targetState,
            out var plan,
            out _));

        Assert.Null(plan);
    }

    [Fact]
    public void CreateFailsClosedWhenLauncherIsAReparsePoint()
    {
        var launcher = Path.GetFullPath(Path.Combine("install", "BaiduBatchTransfer.App.exe"));

        Assert.False(LegacyLaunchPlan.TryCreate(
            launcher,
            [],
            path => string.Equals(path, launcher, StringComparison.OrdinalIgnoreCase)
                ? LegacyPathState.ReparsePoint
                : LegacyPathState.RegularFile,
            out _,
            out _));
    }

    [Fact]
    public void CreateCannotEscapeLauncherDirectory()
    {
        var launcher = Path.GetFullPath(Path.Combine("install", "BaiduBatchTransfer.App.exe"));
        var expectedDirectory = Path.GetDirectoryName(launcher)!;

        Assert.True(LegacyLaunchPlan.TryCreate(
            launcher,
            [],
            _ => LegacyPathState.RegularFile,
            out var plan,
            out var error), error);

        Assert.NotNull(plan);
        Assert.Equal(
            expectedDirectory,
            Path.GetDirectoryName(plan.TargetPath),
            ignoreCase: true);
        Assert.False(Path.GetFileName(plan.TargetPath).Contains(Path.DirectorySeparatorChar));
        Assert.False(Path.GetFileName(plan.TargetPath).Contains(Path.AltDirectorySeparatorChar));
    }

    [Theory]
    [InlineData("--veloapp-install", true)]
    [InlineData("prefix", true, "--veloapp-migrate", "suffix")]
    [InlineData("--VELOAPP-UPDATED", true)]
    [InlineData("--not-veloapp", false)]
    [InlineData("ordinary", false, "--flag")]
    public void LifecycleArgumentsDetermineWhetherLauncherWaits(
        string firstArgument,
        bool expectedWait,
        params string[] remainingArguments)
    {
        var arguments = new[] { firstArgument }.Concat(remainingArguments).ToArray();
        var plan = CreatePlan(arguments);

        Assert.Equal(expectedWait, plan.WaitForExit);
    }

    [Fact]
    public void StartInfoPreservesEveryArgumentElementExactly()
    {
        string[] arguments = ["", "two words", "\"quoted\"", "中文参数", "trailing\\"];
        var plan = CreatePlan(arguments);

        ProcessStartInfo startInfo = plan.CreateStartInfo();

        Assert.Equal(plan.TargetPath, startInfo.FileName);
        Assert.Equal(plan.WorkingDirectory, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(arguments, startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Theory]
    [InlineData(false, 57, 0)]
    [InlineData(true, 57, 57)]
    public void SuccessfulExitCodeDependsOnWaitMode(bool waitForExit, int childExitCode, int expected)
    {
        var arguments = waitForExit ? new[] { "--veloapp-test" } : new[] { "ordinary" };
        var plan = CreatePlan(arguments);

        Assert.Equal(expected, plan.GetSuccessfulExitCode(childExitCode));
    }

    private static LegacyLaunchPlan CreatePlan(IReadOnlyList<string> arguments)
    {
        var launcher = Path.GetFullPath(Path.Combine("install", "BaiduBatchTransfer.App.exe"));
        Assert.True(LegacyLaunchPlan.TryCreate(
            launcher,
            arguments,
            _ => LegacyPathState.RegularFile,
            out var plan,
            out var error), error);
        return Assert.IsType<LegacyLaunchPlan>(plan);
    }

    private static LegacyPathState RegularFiles(string path, params string[] regularFiles) =>
        regularFiles.Contains(path, StringComparer.OrdinalIgnoreCase)
            ? LegacyPathState.RegularFile
            : LegacyPathState.Missing;
}
