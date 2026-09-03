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
        const string appConstruction = "new App(updateRuntime)";
        var bootstrapIndex = program.IndexOf(velopackBootstrap, StringComparison.Ordinal);
        var appConstructionIndex = program.IndexOf(appConstruction, StringComparison.Ordinal);

        Assert.True(bootstrapIndex >= 0, "The complete Velopack bootstrap chain is required.");
        Assert.Contains("VelopackUpdateRuntime.Create(ApplicationBuildInfo.Current)", program, StringComparison.Ordinal);
        Assert.True(appConstructionIndex >= 0, "The WPF application must own the update runtime.");
        Assert.True(
            bootstrapIndex + velopackBootstrap.Length <= appConstructionIndex,
            "The complete Velopack bootstrap chain must finish before App is created.");
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
}
