using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Features.Licensing;

public enum LicenseRuntimeMode
{
    Disabled,
    Acceptance,
    Production,
}

public sealed record LicenseRuntimeBuildInfo(
    LicenseRuntimeMode Mode,
    bool IsAcceptanceBuild,
    string ProductId,
    string? ServiceUrl,
    string? SigningKeyId,
    string? SigningPublicKey,
    IReadOnlyList<string> SpkiPins)
{
    public static LicenseRuntimeBuildInfo Current { get; } = ReadCurrent();

    public static LicenseRuntimeBuildInfo CreateForTests(
        LicenseRuntimeMode mode,
        bool? isAcceptanceBuild = null) =>
        new(
            mode,
            isAcceptanceBuild ?? mode == LicenseRuntimeMode.Acceptance,
            LicenseAcceptanceSmokeRunner.ProductId,
            null,
            null,
            null,
            []);

    public LicenseClientOptions CreateProductionOptions()
    {
        if (Mode != LicenseRuntimeMode.Production)
            throw new InvalidOperationException("当前构建不是生产许可证模式。");
        if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("许可证生产配置无效。");

        try
        {
            var der = Convert.FromBase64String(SigningPublicKey ?? string.Empty);
            var pem = new string(PemEncoding.Write("PUBLIC KEY", der));
            return new LicenseClientOptions(
                ProductId,
                endpoint,
                SigningKeyId ?? "production-v1",
                pem,
                SpkiPins);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or CryptographicException)
        {
            throw new InvalidOperationException("许可证生产配置无效。", exception);
        }
    }

    private static LicenseRuntimeBuildInfo ReadCurrent()
    {
        var metadata = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key is not null)
            .ToDictionary(attribute => attribute.Key!, attribute => attribute.Value, StringComparer.Ordinal);
        if (!Enum.TryParse<LicenseRuntimeMode>(Value("LicenseRuntimeMode"), out var mode))
            throw new InvalidOperationException("许可证构建模式无效。");

        var productId = Value("LicenseProductId");
        if (!string.Equals(productId, LicenseAcceptanceSmokeRunner.ProductId, StringComparison.Ordinal))
            throw new InvalidOperationException("许可证产品标识无效。");

        return new LicenseRuntimeBuildInfo(
            mode,
            bool.TryParse(Value("LicenseAcceptanceBuild"), out var acceptanceBuild) && acceptanceBuild,
            productId,
            Optional("LicenseServiceUrl"),
            Optional("LicenseSigningKeyId") ?? "production-v1",
            Optional("LicenseSigningPublicKey"),
            [Optional("LicenseCurrentSpkiPin") ?? string.Empty, Optional("LicenseNextSpkiPin") ?? string.Empty]);

        string Value(string key) => metadata.TryGetValue(key, out var value) && value is not null
            ? value
            : throw new InvalidOperationException("许可证构建配置不完整。");
        string? Optional(string key) => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}

public sealed record LicenseAcceptanceSmokeResult(int ExitCode, string SafeMessage);

public static class LicenseAcceptanceSmokeRunner
{
    public const string ProductId = "8270e8d7-1791-487d-ad76-0da7a0cbed12";
    private const string SmokeSwitch = "--license-acceptance-smoke";
    private const string ConfigPrefix = "--license-acceptance-config=";

    public static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, SmokeSwitch, StringComparison.Ordinal));

    public static async Task<LicenseAcceptanceSmokeResult> RunAsync(
        IReadOnlyList<string> arguments,
        LicenseRuntimeBuildInfo buildInfo,
        Func<LicenseClientOptions, ILicenseAuthorizationGate>? gateFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(buildInfo);
        if (!IsRequested(arguments) ||
            buildInfo.Mode != LicenseRuntimeMode.Acceptance ||
            !buildInfo.IsAcceptanceBuild)
            return Failed("当前程序不允许运行许可证验收检查。");

        try
        {
            var configPath = GetAcceptanceConfigPath(arguments);
            var options = LoadAcceptanceOptions(configPath);
            var gate = (gateFactory ?? (value => LicenseAuthorizationGateFactory.Create(value)))(options);
            var result = await gate.AuthorizeAsync(LicenseOperationKind.Startup, cancellationToken)
                .ConfigureAwait(false);
            return result.IsAuthorized || string.Equals(result.Code, "activation_required", StringComparison.Ordinal)
                ? new LicenseAcceptanceSmokeResult(0, "许可证验收检查已完成。")
                : Failed("许可证验收检查未通过。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed("许可证验收检查已取消。");
        }
        catch
        {
            return Failed("许可证验收配置无效。");
        }
    }

    public static LicenseClientOptions LoadAcceptanceOptions(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !Path.IsPathFullyQualified(configPath))
            throw new InvalidOperationException("许可证验收配置路径无效。");

        var fullConfigPath = Path.GetFullPath(configPath);
        var json = File.ReadAllText(fullConfigPath);
        var config = JsonSerializer.Deserialize<AcceptanceClientConfiguration>(json)
            ?? throw new InvalidOperationException("许可证验收配置无效。");
        if (!config.AcceptanceOnly)
            throw new InvalidOperationException("许可证验收配置必须标记为仅限验收。");
        if (!Uri.TryCreate(config.LicenseBaseUrl, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException("许可证验收配置必须使用本机回环服务。");
        }

        if (string.IsNullOrWhiteSpace(config.SigningPublicKeyPem) || Path.IsPathFullyQualified(config.SigningPublicKeyPem))
            throw new InvalidOperationException("许可证验收公钥路径无效。");
        var configDirectory = Path.GetDirectoryName(fullConfigPath)!;
        var keyPath = Path.GetFullPath(Path.Combine(configDirectory, config.SigningPublicKeyPem));
        var allowedRoot = configDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!keyPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("许可证验收公钥路径无效。");

        return new LicenseClientOptions(
            ProductId,
            endpoint,
            config.SigningKeyId ?? string.Empty,
            File.ReadAllText(keyPath),
            [],
            allowLoopbackHttpForAcceptance: true,
            isAcceptanceBuild: true);
    }

    private static LicenseAcceptanceSmokeResult Failed(string message) => new(2, message);

    internal static string GetAcceptanceConfigPath(IReadOnlyList<string> arguments)
    {
        var configArguments = arguments
            .Where(argument => argument.StartsWith(ConfigPrefix, StringComparison.Ordinal))
            .ToArray();
        if (configArguments.Length != 1)
            throw new InvalidOperationException("许可证验收配置参数无效。");
        return configArguments[0][ConfigPrefix.Length..];
    }

    private sealed record AcceptanceClientConfiguration(
        [property: JsonPropertyName("license_base_url")] string? LicenseBaseUrl,
        [property: JsonPropertyName("signing_key_id")] string? SigningKeyId,
        [property: JsonPropertyName("signing_public_key_pem")] string? SigningPublicKeyPem,
        [property: JsonPropertyName("acceptance_only")] bool AcceptanceOnly);
}

public static class LicenseAuthorizationGateFactory
{
    public static ILicenseAuthorizationGate Create(LicenseClientOptions options, string? statePath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = new LicenseApiClient(options);
        return new LicenseAuthorizationGate(
            options.SoftwareId,
            client,
            new DpapiLicenseStore(statePath),
            new MachineFingerprintProvider());
    }
}

public static class LicenseServiceRegistration
{
    public static void Add(
        IServiceCollection services,
        LicenseRuntimeBuildInfo buildInfo,
        IReadOnlyList<string> startupArguments,
        string? statePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(buildInfo);
        ArgumentNullException.ThrowIfNull(startupArguments);
        services.AddSingleton(buildInfo);
        if (buildInfo.Mode == LicenseRuntimeMode.Disabled)
        {
            services.AddSingleton<ILicenseAuthorizationGate, DisabledLicenseAuthorizationGate>();
            return;
        }

        var options = buildInfo.Mode == LicenseRuntimeMode.Acceptance
            ? LicenseAcceptanceSmokeRunner.LoadAcceptanceOptions(
                LicenseAcceptanceSmokeRunner.GetAcceptanceConfigPath(startupArguments))
            : buildInfo.CreateProductionOptions();
        services.AddSingleton(options);
        services.AddSingleton(sp => new LicenseApiClient(
            sp.GetRequiredService<LicenseClientOptions>(),
            TimeProvider.System));
        services.AddSingleton<ILicenseStateStore>(_ => new DpapiLicenseStore(statePath));
        services.AddSingleton<IMachineFingerprintProvider, MachineFingerprintProvider>();
        services.AddSingleton(sp => new LicenseAuthorizationGate(
            options.SoftwareId,
            sp.GetRequiredService<LicenseApiClient>(),
            sp.GetRequiredService<ILicenseStateStore>(),
            sp.GetRequiredService<IMachineFingerprintProvider>(),
            TimeProvider.System));
        services.AddSingleton<ILicenseAuthorizationGate>(sp =>
            sp.GetRequiredService<LicenseAuthorizationGate>());
    }
}
