using System.Security.Cryptography;
using System.Text.Json;
using BaiduBatchTransfer.App.Features.Licensing;
using Microsoft.Extensions.DependencyInjection;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LicenseAcceptanceSmokeRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "BaiduBatchTransfer-LicenseSmoke", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(LicenseRuntimeMode.Disabled)]
    [InlineData(LicenseRuntimeMode.Production)]
    public async Task NonAcceptanceBuildRejectsSmokeSwitchWithoutCallingGate(LicenseRuntimeMode mode)
    {
        var calls = 0;
        var result = await LicenseAcceptanceSmokeRunner.RunAsync(
            ["--license-acceptance-smoke", $"--license-acceptance-config={Path.Combine(root, "client.json")}"],
            LicenseRuntimeBuildInfo.CreateForTests(mode),
            _ =>
            {
                calls++;
                return new StubGate(true, LicenseCodes.Ok);
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, calls);
        Assert.Contains("验收", result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptanceBuildRequiresAbsoluteConfigurationPath()
    {
        var result = await LicenseAcceptanceSmokeRunner.RunAsync(
            ["--license-acceptance-smoke", "--license-acceptance-config=relative\\client.json"],
            LicenseRuntimeBuildInfo.CreateForTests(LicenseRuntimeMode.Acceptance),
            _ => throw new InvalidOperationException("gate must not be created"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("配置", result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptanceModeWithoutAcceptanceStampRejectsSmokeSwitch()
    {
        var result = await LicenseAcceptanceSmokeRunner.RunAsync(
            ["--license-acceptance-smoke", $"--license-acceptance-config={Path.Combine(root, "client.json")}"],
            LicenseRuntimeBuildInfo.CreateForTests(
                LicenseRuntimeMode.Acceptance,
                isAcceptanceBuild: false),
            _ => throw new InvalidOperationException("gate must not be created"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("验收", result.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "ok")]
    [InlineData(false, "activation_required")]
    public async Task AcceptanceRunsStartupGateExactlyOnceAndAcceptsExpectedState(
        bool authorized,
        string code)
    {
        var configPath = WriteValidConfiguration();
        var gate = new StubGate(authorized, code);

        var result = await LicenseAcceptanceSmokeRunner.RunAsync(
            ["--license-acceptance-smoke", $"--license-acceptance-config={configPath}"],
            LicenseRuntimeBuildInfo.CreateForTests(LicenseRuntimeMode.Acceptance),
            options =>
            {
                Assert.Equal(LicenseAcceptanceSmokeRunner.ProductId, options.SoftwareId);
                Assert.True(options.IsAcceptanceBuild);
                Assert.True(options.AllowLoopbackHttpForAcceptance);
                return gate;
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([LicenseOperationKind.Startup], gate.Operations);
    }

    [Theory]
    [InlineData("http://localhost:19091", true)]
    [InlineData("https://license.example.test", true)]
    [InlineData("http://127.0.0.1:19091", false)]
    public async Task MalformedOrUntrustedAcceptanceConfigurationFailsBeforeGate(
        string serviceUrl,
        bool acceptanceOnly)
    {
        var configPath = WriteValidConfiguration(serviceUrl, acceptanceOnly);
        var calls = 0;

        var result = await LicenseAcceptanceSmokeRunner.RunAsync(
            ["--license-acceptance-smoke", $"--license-acceptance-config={configPath}"],
            LicenseRuntimeBuildInfo.CreateForTests(LicenseRuntimeMode.Acceptance),
            _ =>
            {
                calls++;
                return new StubGate(true, LicenseCodes.Ok);
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, calls);
        Assert.Contains("配置", result.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(serviceUrl, result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RealFactoryCreatesConcreteAuthorizationGate()
    {
        var options = LicenseAcceptanceSmokeRunner.LoadAcceptanceOptions(WriteValidConfiguration());

        var gate = LicenseAuthorizationGateFactory.Create(options, Path.Combine(root, "license-state.dat"));

        Assert.IsType<LicenseAuthorizationGate>(gate);
    }

    [Fact]
    public void RealFactoryRetainsTheOriginalPublicSignatureAlongsideTheCallbackOverload()
    {
        var methods = typeof(LicenseAuthorizationGateFactory).GetMethods()
            .Where(method => method.Name == nameof(LicenseAuthorizationGateFactory.Create))
            .Select(method => method.GetParameters().Select(parameter => parameter.ParameterType).ToArray())
            .ToArray();

        Assert.Contains(methods, parameters => parameters.SequenceEqual(
            [typeof(LicenseClientOptions), typeof(string)]));
        Assert.Contains(methods, parameters => parameters.SequenceEqual(
            [typeof(LicenseClientOptions), typeof(string), typeof(Action<AuthorizedLicenseSummary?>)]));
    }

    [Fact]
    public async Task ProductionRegistrationConnectsTheGateToTheRegisteredRuntimeState()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var buildInfo = new LicenseRuntimeBuildInfo(
            LicenseRuntimeMode.Production,
            IsAcceptanceBuild: false,
            LicenseAcceptanceSmokeRunner.ProductId,
            "https://ota.fanfh.cn/",
            "production-v1",
            Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo()),
            [new string('a', 64), new string('b', 64)]);
        var services = new ServiceCollection();
        LicenseServiceRegistration.Add(
            services,
            buildInfo,
            [],
            Path.Combine(root, "production-license-state.dat"));
        using var provider = services.BuildServiceProvider();
        var state = provider.GetRequiredService<LicenseRuntimeAuthorizationState>();
        state.PublishSummary(new AuthorizedLicenseSummary("旧套餐", DateTimeOffset.UtcNow.AddDays(1)));

        var result = await provider.GetRequiredService<ILicenseAuthorizationGate>()
            .AuthorizeAsync(LicenseOperationKind.Startup, CancellationToken.None);

        Assert.Equal("activation_required", result.Code);
        Assert.Null(state.CurrentSummary);
        Assert.Same(state, provider.GetRequiredService<LicenseRuntimeAuthorizationState>());
    }

    [Fact]
    public void AcceptanceRegistrationUsesTaskNineClientStoreFingerprintAndGate()
    {
        var services = new ServiceCollection();
        var configPath = WriteValidConfiguration();
        var statePath = Path.Combine(root, "registered-license-state.dat");

        LicenseServiceRegistration.Add(
            services,
            LicenseRuntimeBuildInfo.CreateForTests(LicenseRuntimeMode.Acceptance),
            [$"--license-acceptance-config={configPath}"],
            statePath);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<LicenseClientOptions>(provider.GetRequiredService<LicenseClientOptions>());
        Assert.IsType<LicenseApiClient>(provider.GetRequiredService<LicenseApiClient>());
        Assert.IsType<DpapiLicenseStore>(provider.GetRequiredService<ILicenseStateStore>());
        Assert.IsType<MachineFingerprintProvider>(provider.GetRequiredService<IMachineFingerprintProvider>());
        Assert.Same(
            provider.GetRequiredService<LicenseAuthorizationGate>(),
            provider.GetRequiredService<ILicenseAuthorizationGate>());
    }

    [Fact]
    public void LocalAcceptanceBuildScriptStampsOnlyPublicLoopbackConfiguration()
    {
        var scriptPath = Path.Combine(FindRepositoryRoot(), "scripts", "build_local_license_acceptance.ps1");

        Assert.True(File.Exists(scriptPath));
        var script = File.ReadAllText(scriptPath);
        Assert.Contains("LicenseAcceptanceBuild=true", script, StringComparison.Ordinal);
        Assert.Contains("LicenseAcceptanceConfigPath=$acceptanceConfigPath", script, StringComparison.Ordinal);
        Assert.Contains("LicenseServiceUrl=http://127.0.0.1:19091", script, StringComparison.Ordinal);
        Assert.Contains("LicenseRuntimeMode", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LicenseSigningPublicKey=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LicenseCurrentSpkiPin=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LicenseNextSpkiPin=", script, StringComparison.Ordinal);
    }

    private string WriteValidConfiguration(
        string serviceUrl = "http://127.0.0.1:19091",
        bool acceptanceOnly = true)
    {
        Directory.CreateDirectory(root);
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = new string(PemEncoding.Write("PUBLIC KEY", signingKey.ExportSubjectPublicKeyInfo()));
        File.WriteAllText(Path.Combine(root, "signing-public.pem"), publicKey);
        var path = Path.Combine(root, "client.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            license_base_url = serviceUrl,
            signing_key_id = "acceptance-v1",
            signing_public_key_pem = "signing-public.pem",
            acceptance_only = acceptanceOnly,
        }));
        return path;
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class StubGate(bool authorized, string code) : ILicenseAuthorizationGate
    {
        public List<LicenseOperationKind> Operations { get; } = [];

        public Task<LicenseGateResult> AuthorizeAsync(
            LicenseOperationKind operation,
            CancellationToken cancellationToken)
        {
            Operations.Add(operation);
            return Task.FromResult(new LicenseGateResult(operation, authorized, false, code));
        }
    }
}
