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
            path => RegularInspection(path),
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
                ? RegularInspection(path)
                : new LegacyPathInspection(targetState, path, 1, false),
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
                ? new LegacyPathInspection(LegacyPathState.ReparsePoint, path, 1, true)
                : RegularInspection(path),
            out _,
            out _));
    }

    [Theory]
    [InlineData("..\\evil.exe")]
    [InlineData("subdirectory\\evil.exe")]
    [InlineData("subdirectory/evil.exe")]
    [InlineData("C:\\evil.exe")]
    public void CreateRejectsNonLeafTargetBeforeInspectingFileSystem(string unsafeTarget)
    {
        var launcher = Path.GetFullPath(Path.Combine("install", "BaiduBatchTransfer.App.exe"));
        var inspectionCount = 0;

        Assert.False(LegacyLaunchPlan.TryCreateForTargetLeaf(
            launcher,
            unsafeTarget,
            [],
            _ =>
            {
                inspectionCount++;
                return RegularInspection(unsafeTarget);
            },
            out var plan,
            out var error));

        Assert.Null(plan);
        Assert.NotEmpty(error);
        Assert.Equal(0, inspectionCount);
    }

    [Fact]
    public void CreateRejectsTargetWhoseResolvedParentEscapesLauncherDirectory()
    {
        var launcher = Path.GetFullPath(Path.Combine("install", "BaiduBatchTransfer.App.exe"));
        var target = Path.GetFullPath(Path.Combine("install", "知道综合工具.exe"));
        var escapedTarget = Path.GetFullPath(Path.Combine("other-install", "知道综合工具.exe"));

        Assert.False(LegacyLaunchPlan.TryCreate(
            launcher,
            [],
            path => string.Equals(path, launcher, StringComparison.OrdinalIgnoreCase)
                ? RegularInspection(path)
                : RegularInspection(target, escapedTarget),
            out _,
            out _));
    }

    [Fact]
    public void CreateRejectsTargetWithMoreThanOneHardLink()
    {
        var launcher = Path.GetFullPath(Path.Combine("install", "BaiduBatchTransfer.App.exe"));

        Assert.False(LegacyLaunchPlan.TryCreate(
            launcher,
            [],
            path => string.Equals(path, launcher, StringComparison.OrdinalIgnoreCase)
                ? RegularInspection(path)
                : new LegacyPathInspection(LegacyPathState.RegularFile, path, 2, false),
            out _,
            out _));
    }

    [Fact]
    public void CreateRejectsReparsePointInInspectedPath()
    {
        var launcher = Path.GetFullPath(Path.Combine("install", "BaiduBatchTransfer.App.exe"));

        Assert.False(LegacyLaunchPlan.TryCreate(
            launcher,
            [],
            path => new LegacyPathInspection(LegacyPathState.RegularFile, path, 1, true),
            out _,
            out _));
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
            path => RegularInspection(path),
            out var plan,
            out var error), error);
        return Assert.IsType<LegacyLaunchPlan>(plan);
    }

    private static LegacyPathInspection RegularFiles(string path, params string[] regularFiles) =>
        regularFiles.Contains(path, StringComparer.OrdinalIgnoreCase)
            ? RegularInspection(path)
            : new LegacyPathInspection(LegacyPathState.Missing, null, 0, false);

    private static LegacyPathInspection RegularInspection(string path, string? finalPath = null) =>
        new(LegacyPathState.RegularFile, finalPath ?? path, 1, false);
}
