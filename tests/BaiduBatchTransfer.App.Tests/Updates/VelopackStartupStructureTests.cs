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

        Assert.Contains("VelopackApp.Build()", program, StringComparison.Ordinal);
        Assert.Contains("SetAutoApplyOnStartup(false)", program, StringComparison.Ordinal);
        Assert.Contains("VelopackUpdateRuntime.Create(ApplicationBuildInfo.Current)", program, StringComparison.Ordinal);
        Assert.Contains("new App(updateRuntime)", program, StringComparison.Ordinal);
        Assert.True(
            program.IndexOf("VelopackApp.Build()", StringComparison.Ordinal) <
            program.IndexOf("new App(updateRuntime)", StringComparison.Ordinal));
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
