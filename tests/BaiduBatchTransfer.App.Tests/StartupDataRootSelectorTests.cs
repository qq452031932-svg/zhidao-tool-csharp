using System.Diagnostics;
using System.Reflection;
using BaiduBatchTransfer.App.Updates;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;

namespace BaiduBatchTransfer.App.Tests;

public sealed class StartupDataRootSelectorTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "bbt-startup-root-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void OrdinaryBuildUsesTheProductDataRootUnderInjectedLocalAppData()
    {
        var info = ApplicationBuildInfo.CreateForTests("1.2.3", "adapter-v7", null);

        var selected = StartupDataRootSelector.ResolveFromLocalAppData([], root, info);

        Assert.Equal(Path.Combine(root, "BaiduBatchTransfer"), selected);
    }

    [Fact]
    public async Task OtaAcceptanceBuildIgnoresDataRootOverrideAndNeverMigratesTheProductionDatabase()
    {
        var requestedOverride = Path.Combine(root, "requested-override");
        var acceptanceFeed = Path.Combine(root, "feed");
        var info = ApplicationBuildInfo.CreateForTests(
            "1.2.3",
            "adapter-v7",
            null,
            channel: "win-x64-ota-acceptance",
            acceptanceFeedPath: acceptanceFeed,
            isOtaAcceptanceBuild: true);

        var selected = StartupDataRootSelector.ResolveFromLocalAppData(
            ["--data-root", requestedOverride],
            root,
            info);
        using var host = AppBootstrap.CreateHost(selected);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();

        Assert.Equal(Path.Combine(root, "BaiduBatchTransfer.OtaAcceptance"), selected);
        Assert.True(File.Exists(Path.Combine(selected, "data", "baidu-batch-transfer.db")));
        Assert.False(Directory.Exists(Path.Combine(root, "BaiduBatchTransfer")));
        Assert.False(Directory.Exists(requestedOverride));
    }

    [Fact]
    public void OtaAcceptanceBuildRejectsPreexistingJunctionToProductionRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(root);
        var productionRoot = Path.Combine(root, "BaiduBatchTransfer");
        var acceptanceRoot = Path.Combine(root, "BaiduBatchTransfer.OtaAcceptance");
        Directory.CreateDirectory(productionRoot);
        var sentinel = Path.Combine(productionRoot, "production-sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        CreateDirectoryJunction(acceptanceRoot, productionRoot);
        try
        {
            var info = ApplicationBuildInfo.CreateForTests(
                "1.2.3",
                "adapter-v7",
                null,
                channel: "win-x64-ota-acceptance",
                acceptanceFeedPath: Path.Combine(root, "feed"),
                isOtaAcceptanceBuild: true);

            Assert.Throws<InvalidOperationException>(() =>
                StartupDataRootSelector.ResolveFromLocalAppData([], root, info));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(acceptanceRoot))
                Directory.Delete(acceptanceRoot, recursive: false);
        }
    }

    [Fact]
    public void BuildConfigurationEnforcesAcceptanceRootBoundary()
    {
        var normalRoot = Path.Combine(root, "normal");
        var overrideRoot = Path.Combine(root, "override");
        Directory.CreateDirectory(overrideRoot);

#if DEBUG
        var error = Assert.Throws<TargetInvocationException>(() =>
            Resolve(["--data-root", overrideRoot], normalRoot));
        Assert.IsType<InvalidOperationException>(error.InnerException);
#else
        Assert.Equal(Path.GetFullPath(normalRoot), Resolve(["--data-root", overrideRoot], normalRoot));
#endif
    }

#if DEBUG
    [Fact]
    public void DebugBuildAcceptsOnlyMarkedAbsoluteAcceptanceRoot()
    {
        var normalRoot = Path.Combine(root, "normal");
        var overrideRoot = Path.Combine(root, "override");
        Directory.CreateDirectory(overrideRoot);
        File.WriteAllText(
            Path.Combine(overrideRoot, ".acceptance-marker"),
            "BaiduBatchTransfer account-management acceptance v1\nscenario=keep-history\n");

        Assert.Equal(Path.GetFullPath(overrideRoot), Resolve(["--data-root", overrideRoot], normalRoot));
    }
#endif

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static string Resolve(string[] args, string normalRoot)
    {
        var selector = typeof(AppBootstrap).Assembly.GetType(
            "BaiduBatchTransfer.App.StartupDataRootSelector");
        Assert.NotNull(selector);
        var method = selector.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, [args, normalRoot]));
    }

    private static void CreateDirectoryJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            ArgumentList = { "/d", "/c", "mklink", "/J", junction, target },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("The junction helper did not start.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        Assert.True(File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint));
    }
}
