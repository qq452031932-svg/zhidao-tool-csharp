using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class ProductIdentityBuildTests
{
    [Fact]
    public void DebugBuildUsesBrandedPhysicalNamesAndMetadata()
    {
        var outputDirectory = Path.Combine(
            RepositoryRoot(), "src", "BaiduBatchTransfer.App", "bin", "Debug", "net8.0-windows");
        var executablePath = Path.Combine(outputDirectory, "知道综合工具.exe");

        Assert.True(File.Exists(executablePath), $"Missing branded executable: {executablePath}");
        Assert.True(File.Exists(Path.Combine(outputDirectory, "知道综合工具.dll")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "知道综合工具.deps.json")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "知道综合工具.runtimeconfig.json")));

        Assert.False(File.Exists(Path.Combine(outputDirectory, "BaiduBatchTransfer.App.exe")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "BaiduBatchTransfer.App.dll")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "BaiduBatchTransfer.App.deps.json")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "BaiduBatchTransfer.App.runtimeconfig.json")));

        var version = FileVersionInfo.GetVersionInfo(executablePath);
        Assert.Equal("知道综合工具", version.ProductName);
        Assert.Equal("知道综合工具", version.FileDescription);
        Assert.Equal("1.0.0", version.ProductVersion);
        Assert.Equal("1.0.0.0", version.FileVersion);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
