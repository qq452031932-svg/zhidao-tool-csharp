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
        Assert.Equal(
            "0cc6a91abed05fae671c4635e8903b9f2db3e6931b72bdad907b57840d7f97fa",
            properties["ProductIconSha256"]);
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

    [Fact]
    public async Task VerifyOnlyRejectsUtf8BomInIsolatedCopyWithoutChangingIt()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"product-identity-{Guid.NewGuid():N}");
        try
        {
            CopyIdentityFixture(fixtureRoot);
            var realGeneratedPath = Path.Combine(
                RepositoryRoot(), "src", "BaiduBatchTransfer.App", "Branding", "ProductIdentity.Generated.cs");
            var realGeneratedBytes = await File.ReadAllBytesAsync(realGeneratedPath);
            var generatedPath = Path.Combine(
                fixtureRoot, "src", "BaiduBatchTransfer.App", "Branding", "ProductIdentity.Generated.cs");
            var originalBytes = await File.ReadAllBytesAsync(generatedPath);
            var bytesWithBom = new byte[] { 0xef, 0xbb, 0xbf }.Concat(originalBytes).ToArray();
            await File.WriteAllBytesAsync(generatedPath, bytesWithBom);

            var result = await RunGeneratorAsync(fixtureRoot, verifyOnly: true);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("out of date", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(bytesWithBom, await File.ReadAllBytesAsync(generatedPath));
            Assert.Equal(realGeneratedBytes, await File.ReadAllBytesAsync(realGeneratedPath));
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LoaderRejectsConditionalPropertyGroupInIsolatedCopy()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"product-identity-{Guid.NewGuid():N}");
        try
        {
            CopyIdentityFixture(fixtureRoot);
            var propsPath = Path.Combine(fixtureRoot, "eng", "ProductIdentity.props");
            var props = await File.ReadAllTextAsync(propsPath);
            var conditionalProps = props.Replace(
                "<PropertyGroup>",
                "<PropertyGroup Condition=\"'$(Configuration)' == 'Release'\">",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(propsPath, conditionalProps);

            var result = await RunGeneratorAsync(fixtureRoot, verifyOnly: true);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("PropertyGroup", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    public static TheoryData<string, string> RejectedSchemaMutations => new()
    {
        { "</Project>", "  <Choose />\n</Project>" },
        { "</Project>", "  <Import Project=\"other.props\" />\n</Project>" },
        { "</Project>", "  <Target Name=\"Unexpected\" />\n</Project>" },
        { "<Project>", "<Project Unexpected=\"true\">" },
        { "</PropertyGroup>", "    <UnknownIdentity>value</UnknownIdentity>\n  </PropertyGroup>" },
        { "</PropertyGroup>", "    <ProductDisplayName>duplicate</ProductDisplayName>\n  </PropertyGroup>" },
        { "</Project>", "  <PropertyGroup />\n</Project>" },
        { "<ProductDescription>知道综合工具</ProductDescription>", "<ProductDescription><Value>知道综合工具</Value></ProductDescription>" },
        { "<ProductDescription>知道综合工具</ProductDescription>", "<ProductDescription>知道<!-- comment -->综合工具</ProductDescription>" },
        { "<ProductDescription>知道综合工具</ProductDescription>", "<ProductDescription><![CDATA[知道综合工具]]></ProductDescription>" },
        { "<ProductDescription>知道综合工具</ProductDescription>", "<ProductDescription Condition=\"'$(Configuration)' == 'Release'\">知道综合工具</ProductDescription>" },
        { "<ProductDescription>知道综合工具</ProductDescription>", "<productDescription>知道综合工具</productDescription>" },
        { "<ProductDescription>知道综合工具</ProductDescription>", "<x:ProductDescription xmlns:x=\"urn:test\">知道综合工具</x:ProductDescription>" },
        { "<PropertyGroup>", "<x:PropertyGroup xmlns:x=\"urn:test\">" },
    };

    [Theory]
    [MemberData(nameof(RejectedSchemaMutations))]
    public async Task LoaderRejectsAnythingOutsideClosedSchema(string original, string replacement)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"product-identity-{Guid.NewGuid():N}");
        try
        {
            CopyIdentityFixture(fixtureRoot);
            var propsPath = Path.Combine(fixtureRoot, "eng", "ProductIdentity.props");
            var props = await File.ReadAllTextAsync(propsPath);
            var mutated = props.Replace(original, replacement, StringComparison.Ordinal);
            Assert.NotEqual(props, mutated);
            await File.WriteAllTextAsync(propsPath, mutated);

            var result = await RunGeneratorAsync(fixtureRoot, verifyOnly: true);

            Assert.NotEqual(0, result.ExitCode);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\unsafe")]
    [InlineData("unsafe/name")]
    [InlineData(@"unsafe\name")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("unsafe:name")]
    public async Task LoaderRejectsUnsafeDataDirectoryLeaf(string unsafeValue)
    {
        var result = await RunGeneratorWithOverridesAsync(new Dictionary<string, string>
        {
            ["ProductDataDirectoryName"] = unsafeValue,
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task LoaderRejectsUnsafeMainFileComponentsEvenWhenSuffixesMatch()
    {
        var result = await RunGeneratorWithOverridesAsync(new Dictionary<string, string>
        {
            ["ProductMainAssemblyName"] = "unsafe/name",
            ["ProductMainExecutableName"] = "unsafe/name.exe",
            ["ProductMainDllName"] = "unsafe/name.dll",
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task LoaderRejectsUnsafeLegacyFileComponentsEvenWhenSuffixesMatch()
    {
        var result = await RunGeneratorWithOverridesAsync(new Dictionary<string, string>
        {
            ["ProductLegacyAssemblyName"] = @"unsafe\name",
            ["ProductLegacyExecutableName"] = @"unsafe\name.exe",
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("ProductDisplayName", "知道$(Configuration)")]
    [InlineData("ProductDescription", "描述@(Items)")]
    [InlineData("ProductPackageId", "Package%2fIdentity")]
    public async Task LoaderRejectsMsBuildEvaluationSyntaxInNonFileIdentity(
        string propertyName,
        string expressionLikeValue)
    {
        var result = await RunGeneratorWithOverridesAsync(new Dictionary<string, string>
        {
            [propertyName] = expressionLikeValue,
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task LoaderRejectsMsBuildEvaluationSyntaxInAssemblyIdentity()
    {
        var result = await RunGeneratorWithOverridesAsync(new Dictionary<string, string>
        {
            ["ProductMainAssemblyName"] = "工具%(Identity)",
            ["ProductMainExecutableName"] = "工具%(Identity).exe",
            ["ProductMainDllName"] = "工具%(Identity).dll",
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task LoaderAllowsOrdinaryChinesePunctuationAndPercent()
    {
        var result = await RunGeneratorWithOverridesAsync(new Dictionary<string, string>
        {
            ["ProductDescription"] = "进度 100%，请稍候（正常）。",
        });

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL.exe")]
    [InlineData("COM1.dll")]
    [InlineData("data.")]
    [InlineData("trailing ")]
    public async Task LoaderRejectsReservedOrNormalizedWindowsLeaf(string unsafeValue)
    {
        var result = await RunGeneratorWithOverridesAsync(new Dictionary<string, string>
        {
            ["ProductDataDirectoryName"] = unsafeValue,
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("0CC6A91ABED05FAE671C4635E8903B9F2DB3E6931B72BDAD907B57840D7F97FA")]
    [InlineData("xyz")]
    [InlineData("0cc6a91abed05fae671c4635e8903b9f2db3e6931b72bdad907b57840d7f97f")]
    public async Task LoaderRejectsNonCanonicalProductIconSha256(string value)
    {
        var result = await RunGeneratorWithOverridesAsync(new Dictionary<string, string>
        {
            ["ProductIconSha256"] = value,
        });

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task EvaluatedProductionMsBuildIdentityMatchesCanonicalSource()
    {
        var canonical = LoadCanonicalProperties();
        var loaderResult = await RunProductIdentityLoaderAsync();
        var result = await RunMsBuildPropertiesAsync(otaAcceptanceBuild: false);

        Assert.True(loaderResult.ExitCode == 0, loaderResult.Output);
        Assert.True(result.ExitCode == 0, result.Output);
        using var loaderJson = System.Text.Json.JsonDocument.Parse(loaderResult.Output);
        using var json = System.Text.Json.JsonDocument.Parse(result.Output);
        var loaded = loaderJson.RootElement;
        var properties = json.RootElement.GetProperty("Properties");
        Assert.Equal(canonical["ProductDisplayName"], loaded.GetProperty("ProductDisplayName").GetString());
        Assert.Equal(loaded.GetProperty("ProductDisplayName").GetString(), properties.GetProperty("ProductDisplayName").GetString());
        Assert.Equal(loaded.GetProperty("ProductMainAssemblyName").GetString(), properties.GetProperty("ProductMainAssemblyName").GetString());
        Assert.Equal(loaded.GetProperty("ProductMainAssemblyName").GetString(), properties.GetProperty("AssemblyName").GetString());
        Assert.Equal(loaded.GetProperty("ProductMainExecutableName").GetString(), properties.GetProperty("ProductMainExecutableName").GetString());
        Assert.Equal(loaded.GetProperty("ProductMainDllName").GetString(), properties.GetProperty("ProductMainDllName").GetString());
        Assert.Equal(loaded.GetProperty("ProductLegacyAssemblyName").GetString(), properties.GetProperty("ProductLegacyAssemblyName").GetString());
        Assert.Equal(loaded.GetProperty("ProductLegacyExecutableName").GetString(), properties.GetProperty("ProductLegacyExecutableName").GetString());
        Assert.Equal(loaded.GetProperty("ProductPackageId").GetString(), properties.GetProperty("PackageId").GetString());
        Assert.Equal(loaded.GetProperty("ProductLicenseProductId").GetString(), properties.GetProperty("LicenseProductId").GetString());
    }

    [Fact]
    public async Task EvaluatedAcceptanceMsBuildIdentityKeepsDedicatedPackageId()
    {
        var result = await RunMsBuildPropertiesAsync(otaAcceptanceBuild: true);

        Assert.True(result.ExitCode == 0, result.Output);
        using var json = System.Text.Json.JsonDocument.Parse(result.Output);
        Assert.Equal(
            "SuperPan.BaiduBatchTransfer.OtaAcceptance",
            json.RootElement.GetProperty("Properties").GetProperty("PackageId").GetString());
    }

    [Fact]
    public async Task GeneratorPreservesTokenLikeValuesAndUsesLanguageSpecificEscapes()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"product-identity-{Guid.NewGuid():N}");
        try
        {
            CopyIdentityFixture(fixtureRoot);
            const string tokenLikeDisplayName = "{ProductDescription}";
            const string complexDescription = "quote \" slash \\ braces {ProductPackageId} tab\t next\u0085line\u2028para\u2029emoji😀";
            await SetPropertiesAsync(fixtureRoot, new Dictionary<string, string>
            {
                ["ProductDisplayName"] = tokenLikeDisplayName,
                ["ProductDescription"] = complexDescription,
            });

            var result = await RunGeneratorAsync(fixtureRoot, verifyOnly: false);

            Assert.True(result.ExitCode == 0, result.Output);
            var mainCSharpPath = Path.Combine(
                fixtureRoot, "src", "BaiduBatchTransfer.App", "Branding", "ProductIdentity.Generated.cs");
            var mainCSharp = await File.ReadAllTextAsync(mainCSharpPath);
            var go = await File.ReadAllTextAsync(Path.Combine(
                fixtureRoot, "services", "ota_admin", "internal", "release", "product_identity_generated.go"));
            const string expectedEscapedDescription = "quote \\\" slash \\\\ braces {ProductPackageId} tab\\t next\\u0085line\\u2028para\\u2029emoji😀";
            Assert.Contains("internal const string DisplayName = \"{ProductDescription}\";", mainCSharp, StringComparison.Ordinal);
            Assert.Contains($"internal const string Description = \"{expectedEscapedDescription}\";", mainCSharp, StringComparison.Ordinal);
            Assert.Contains("productDisplayName          = \"{ProductDescription}\"", go, StringComparison.Ordinal);
            Assert.Contains($"productDescription          = \"{expectedEscapedDescription}\"", go, StringComparison.Ordinal);
            Assert.Contains(
                "productIconSHA256           = \"0cc6a91abed05fae671c4635e8903b9f2db3e6931b72bdad907b57840d7f97fa\"",
                go,
                StringComparison.Ordinal);

            var compileResult = await CompileGeneratedCSharpAsync(fixtureRoot, mainCSharpPath);
            Assert.True(compileResult.ExitCode == 0, compileResult.Output);
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

    private static async Task<ProcessResult> RunGeneratorWithOverridesAsync(
        IReadOnlyDictionary<string, string> overrides)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"product-identity-{Guid.NewGuid():N}");
        try
        {
            CopyIdentityFixture(fixtureRoot);
            await SetPropertiesAsync(fixtureRoot, overrides);
            return await RunGeneratorAsync(fixtureRoot, verifyOnly: false);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static async Task SetPropertiesAsync(
        string fixtureRoot,
        IReadOnlyDictionary<string, string> overrides)
    {
        var propsPath = Path.Combine(fixtureRoot, "eng", "ProductIdentity.props");
        var document = XDocument.Load(propsPath);
        foreach (var (name, value) in overrides)
        {
            document.Descendants().Single(element => element.Name.LocalName == name).Value = value;
        }
        await File.WriteAllTextAsync(
            propsPath,
            document.ToString(),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static async Task<ProcessResult> CompileGeneratedCSharpAsync(
        string fixtureRoot,
        string generatedSourcePath)
    {
        var compileRoot = Path.Combine(fixtureRoot, "compile");
        Directory.CreateDirectory(compileRoot);
        var sourcePath = Path.Combine(compileRoot, "ProductIdentity.Generated.cs");
        File.Copy(generatedSourcePath, sourcePath);
        var projectPath = Path.Combine(compileRoot, "GeneratedIdentity.csproj");
        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="ProductIdentity.Generated.cs" />
              </ItemGroup>
            </Project>
            """);
        return await RunProcessAsync("dotnet", ["build", projectPath, "--nologo"], compileRoot, TimeSpan.FromMinutes(1));
    }

    private static Task<ProcessResult> RunMsBuildPropertiesAsync(bool otaAcceptanceBuild)
    {
        var projectPath = Path.Combine(
            RepositoryRoot(), "src", "BaiduBatchTransfer.App", "BaiduBatchTransfer.App.csproj");
        return RunProcessAsync(
            "dotnet",
            [
                "msbuild",
                projectPath,
                "-nologo",
                "-getProperty:ProductDisplayName,ProductMainAssemblyName,AssemblyName,ProductMainExecutableName,ProductMainDllName,ProductLegacyAssemblyName,ProductLegacyExecutableName,PackageId,LicenseProductId",
                "-p:Configuration=Debug",
                $"-p:OtaAcceptanceBuild={otaAcceptanceBuild.ToString().ToLowerInvariant()}",
            ],
            RepositoryRoot(),
            TimeSpan.FromSeconds(30));
    }

    private static Task<ProcessResult> RunProductIdentityLoaderAsync()
    {
        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "ProductIdentity.ps1");
        var propsPath = Path.Combine(RepositoryRoot(), "eng", "ProductIdentity.props");
        var command = $". '{EscapePowerShellLiteral(scriptPath)}'; " +
            $"Get-ProductIdentity -Path '{EscapePowerShellLiteral(propsPath)}' | ConvertTo-Json -Compress";
        return RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
            RepositoryRoot(),
            TimeSpan.FromSeconds(30));
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

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

        return await RunProcessAsync(info, TimeSpan.FromSeconds(30));
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return await RunProcessAsync(info, timeout);
    }

    private static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo info, TimeSpan timeoutValue)
    {
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(timeoutValue);
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
