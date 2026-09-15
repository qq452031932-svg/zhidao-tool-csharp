using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class ProductIdentityBuildTests
{
    [Fact]
    public async Task FreshDebugBuildUsesBrandedPhysicalNamesAndMetadata()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(), "BaiduBatchTransfer.App.Tests", $"product-identity-build-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(testRoot, "output");
        try
        {
            var project = Path.Combine(
                RepositoryRoot(), "src", "BaiduBatchTransfer.App", "BaiduBatchTransfer.App.csproj");
            var result = await RunBuildAsync(project, outputDirectory);
            Assert.True(result.ExitCode == 0, result.Output);

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
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunBuildAsync(string project, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryRoot(),
        };
        foreach (var argument in new[]
        {
            "build", project, "-c", "Debug", "--no-restore", "--nologo",
            $"-p:OutputPath={outputDirectory}{Path.DirectorySeparatorChar}",
            "-p:AppendTargetFrameworkToOutputPath=false",
            "-p:AppendRuntimeIdentifierToOutputPath=false",
        })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, await stdout, await stderr));
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

    private sealed record ProcessResult(int ExitCode, string Output);
}
