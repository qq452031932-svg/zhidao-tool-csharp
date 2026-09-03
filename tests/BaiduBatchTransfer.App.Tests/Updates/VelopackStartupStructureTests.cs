using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class VelopackStartupStructureTests
{
    [Fact]
    public void ExplicitEntryPointBootstrapsVelopackBeforeCreatingWpfApplication()
    {
        var program = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "Program.cs"));

        const string velopackBootstrap = "VelopackApp.Build().SetAutoApplyOnStartup(false).Run()";
        const string runtimeCreation = "VelopackUpdateRuntime.Create(ApplicationBuildInfo.Current)";
        const string appConstruction = "new App(updateRuntime)";
        var orderedStartupMarkers = new[]
        {
            "[STAThread]",
            velopackBootstrap,
            runtimeCreation,
            "try",
            appConstruction,
            "app.InitializeComponent()",
            "app.Run()",
            "finally",
            "updateRuntime.Dispose()"
        };

        AssertMarkersAppearInOrder(program, orderedStartupMarkers);
        var bootstrapEnd = program.IndexOf(velopackBootstrap, StringComparison.Ordinal) + velopackBootstrap.Length;
        Assert.True(bootstrapEnd <= program.IndexOf(appConstruction, StringComparison.Ordinal));
    }

    [Fact]
    public void UpdateRuntimeDisposalIsIdempotentAndBaseExitRemainsInFinally()
    {
        var repositoryRoot = FindRepositoryRoot();
        var app = NormalizeLineEndings(File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "BaiduBatchTransfer.App",
            "App.xaml.cs")));
        var velopackRuntime = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "BaiduBatchTransfer.App",
            "Updates",
            "VelopackUpdateRuntime.cs"));
        var unavailableRuntime = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "BaiduBatchTransfer.App",
            "Updates",
            "UnavailableApplicationUpdateRuntime.cs"));

        Assert.Contains("Interlocked.Exchange(ref disposed, 1) != 0", velopackRuntime, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref disposed, 1) != 0", unavailableRuntime, StringComparison.Ordinal);
        Assert.Contains(
            "finally\n        {\n            try\n            {\n                updateRuntime.Dispose();\n            }\n            finally\n            {\n                base.OnExit(e);\n            }\n        }",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppXamlIsCompiledAsPageAndRetainsApplicationResources()
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "BaiduBatchTransfer.App",
            "BaiduBatchTransfer.App.csproj"));
        var appXaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "BaiduBatchTransfer.App",
            "App.xaml"));

        Assert.Contains("<ApplicationDefinition Remove=\"App.xaml\"", project, StringComparison.Ordinal);
        Assert.Contains("<Page Include=\"App.xaml\"", project, StringComparison.Ordinal);
        Assert.Contains("<Application.Resources>", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"BooleanToVisibilityConverter\"", appXaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void AssertMarkersAppearInOrder(string source, IReadOnlyList<string> markers)
    {
        var searchStart = 0;
        foreach (var marker in markers)
        {
            var index = source.IndexOf(marker, searchStart, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected marker after offset {searchStart}: {marker}");
            searchStart = index + marker.Length;
        }
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
