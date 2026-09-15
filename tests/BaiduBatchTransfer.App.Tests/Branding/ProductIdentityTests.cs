using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using BaiduBatchTransfer.App.Branding;

namespace BaiduBatchTransfer.App.Tests.Branding;

public sealed class ProductIdentityTests
{
    [Fact]
    public void GeneratedRuntimeIdentityMatchesCanonicalIdentity()
    {
        Assert.Equal("知道综合工具", ProductIdentity.DisplayName);
        Assert.Equal("知道综合工具.exe", ProductIdentity.MainExecutableName);
        Assert.Equal("知道综合工具.dll", ProductIdentity.MainAssemblyName);
        Assert.Equal("BaiduBatchTransfer.App.exe", ProductIdentity.LegacyExecutableName);
        Assert.Equal("SuperPan.BaiduBatchTransfer", ProductIdentity.PackageId);
        Assert.Equal("BaiduBatchTransfer", ProductIdentity.DataDirectoryName);
        Assert.Equal("8270e8d7-1791-487d-ad76-0da7a0cbed12", ProductIdentity.LicenseProductId);
    }

    [Fact]
    public void CanonicalSourceContainsRequiredStableIdentity()
    {
        var properties = LoadCanonicalProperties();

        Assert.Equal("知道综合工具", properties["ProductDisplayName"]);
        Assert.Equal("知道综合工具", properties["ProductMainAssemblyName"]);
        Assert.Equal("知道综合工具.exe", properties["ProductMainExecutableName"]);
        Assert.Equal("知道综合工具.dll", properties["ProductMainDllName"]);
        Assert.Equal("BaiduBatchTransfer.App", properties["ProductLegacyAssemblyName"]);
        Assert.Equal("BaiduBatchTransfer.App.exe", properties["ProductLegacyExecutableName"]);
        Assert.Equal("SuperPan.BaiduBatchTransfer", properties["ProductPackageId"]);
        Assert.Equal("BaiduBatchTransfer", properties["ProductDataDirectoryName"]);
        Assert.Equal("8270e8d7-1791-487d-ad76-0da7a0cbed12", properties["ProductLicenseProductId"]);
    }

    [Fact]
    public void ProjectMetadataReferencesCanonicalSourceProperties()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "BaiduBatchTransfer.App", "BaiduBatchTransfer.App.csproj"));
        var projectText = project.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("<Import Project=\"..\\..\\eng\\ProductIdentity.props\"", projectText, StringComparison.Ordinal);
        Assert.Contains("<AssemblyName>$(ProductMainAssemblyName)</AssemblyName>", projectText, StringComparison.Ordinal);
        Assert.Contains("<AssemblyTitle>$(ProductDisplayName)</AssemblyTitle>", projectText, StringComparison.Ordinal);
        Assert.Contains("<Product>$(ProductDisplayName)</Product>", projectText, StringComparison.Ordinal);
        Assert.Contains("<Description>$(ProductDescription)</Description>", projectText, StringComparison.Ordinal);
        Assert.Contains("<LicenseProductId Condition=\"'$(LicenseProductId)' == ''\">$(ProductLicenseProductId)</LicenseProductId>", projectText, StringComparison.Ordinal);
        Assert.Contains("<PackageId Condition=\"'$(PackageId)' == ''\">$(ProductPackageId)</PackageId>", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("<AssemblyName>知道综合工具</AssemblyName>", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("<LicenseProductId>8270e8d7-1791-487d-ad76-0da7a0cbed12</LicenseProductId>", projectText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedFilesAreCurrent()
    {
        var result = await RunGeneratorAsync(RepositoryRoot(), verifyOnly: true);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task VerifyOnlyRejectsDriftInIsolatedCopyWithoutChangingIt()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"product-identity-{Guid.NewGuid():N}");
        try
        {
            CopyIdentityFixture(fixtureRoot);
            var generatedPath = Path.Combine(
                fixtureRoot, "src", "BaiduBatchTransfer.App", "Branding", "ProductIdentity.Generated.cs");
            const string driftedContent = "// deliberately stale\n";
            await File.WriteAllTextAsync(generatedPath, driftedContent);

            var result = await RunGeneratorAsync(fixtureRoot, verifyOnly: true);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("out of date", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(driftedContent, await File.ReadAllTextAsync(generatedPath));
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static Dictionary<string, string> LoadCanonicalProperties()
    {
        var document = XDocument.Load(Path.Combine(RepositoryRoot(), "eng", "ProductIdentity.props"));
        return document.Descendants()
            .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")
            .ToDictionary(element => element.Name.LocalName, element => element.Value, StringComparer.Ordinal);
    }

    private static void CopyIdentityFixture(string fixtureRoot)
    {
        var root = RepositoryRoot();
        var relativePaths = new[]
        {
            Path.Combine("eng", "ProductIdentity.props"),
            Path.Combine("src", "BaiduBatchTransfer.App", "Branding", "ProductIdentity.Generated.cs"),
            Path.Combine("src", "BaiduBatchTransfer.LegacyLauncher", "ProductIdentity.Generated.cs"),
            Path.Combine("services", "ota_admin", "internal", "release", "product_identity_generated.go"),
        };

        foreach (var relativePath in relativePaths)
        {
            var destination = Path.Combine(fixtureRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(root, relativePath), destination);
        }
    }

    private static async Task<ProcessResult> RunGeneratorAsync(string repositoryRoot, bool verifyOnly)
    {
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryRoot(),
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(RepositoryRoot(), "scripts", "Update-ProductIdentityGeneratedFiles.ps1"),
            "-RepositoryRoot", repositoryRoot,
        })
        {
            info.ArgumentList.Add(argument);
        }
        if (verifyOnly) info.ArgumentList.Add("-VerifyOnly");

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
