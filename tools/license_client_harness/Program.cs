using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuperPan.Licensing.Client;

internal static class Program
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var command = HarnessArguments.Parse(args);
            var options = LoadOptions(command.ConfigPath, command.ProductId);
            Directory.CreateDirectory(command.StoreRoot);
            var statePath = Path.Combine(command.StoreRoot, "licenses.dat");
            var timeProvider = command.UtcNow is null
                ? TimeProvider.System
                : new FixedTimeProvider(command.UtcNow.Value);
            using var client = new LicenseApiClient(options, timeProvider);
            var gate = new LicenseAuthorizationGate(
                command.ProductId,
                client,
                new DpapiLicenseStore(statePath),
                new FixtureFingerprintProvider(command.MachineFixture),
                timeProvider);

            LicenseGateResult result;
            if (command.Name == "activate")
            {
                Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                var card = Console.In.ReadLine();
                if (card is { Length: > 0 } && card[0] == '\uFEFF') card = card[1..];
                if (string.IsNullOrWhiteSpace(card) || card != card.Trim())
                    throw new HarnessInputException();
                result = await gate.ActivateAsync(card, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                result = await gate.AuthorizeAsync(command.Operation, CancellationToken.None).ConfigureAwait(false);
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                status = "complete",
                authorized = result.IsAuthorized,
                used_cached_lease = result.UsedCachedLease,
                code = SafeCode(result.Code),
                store_file_present = File.Exists(statePath),
            }, OutputJson));
            return result.IsAuthorized ? 0 : 3;
        }
        catch
        {
            Console.Out.WriteLine("{\"status\":\"error\",\"code\":\"harness_failed\"}");
            return 2;
        }
    }

    private static LicenseClientOptions LoadOptions(string configPath, string productId)
    {
        var configJson = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<AcceptanceConfiguration>(configJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        }) ?? throw new HarnessInputException();
        if (!config.AcceptanceOnly ||
            !Uri.TryCreate(config.LicenseBaseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Host != "127.0.0.1" ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new HarnessInputException();

        if (string.IsNullOrWhiteSpace(config.SigningPublicKeyPem) ||
            Path.IsPathFullyQualified(config.SigningPublicKeyPem))
            throw new HarnessInputException();
        var configRoot = Path.GetDirectoryName(configPath)!;
        var keyPath = Path.GetFullPath(Path.Combine(configRoot, config.SigningPublicKeyPem));
        var allowedRoot = configRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!keyPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new HarnessInputException();

        return new LicenseClientOptions(
            productId,
            endpoint,
            config.SigningKeyId ?? string.Empty,
            File.ReadAllText(keyPath),
            [],
            allowLoopbackHttpForAcceptance: true,
            isAcceptanceBuild: true);
    }

    private static string SafeCode(string code) =>
        LicenseCodes.All.Contains(code) || code is "activation_required" or "authorization_unavailable"
            ? code
            : "unrecognized";

    private sealed record AcceptanceConfiguration(
        [property: JsonPropertyName("license_base_url")] string? LicenseBaseUrl,
        [property: JsonPropertyName("signing_key_id")] string? SigningKeyId,
        [property: JsonPropertyName("signing_public_key_pem")] string? SigningPublicKeyPem,
        [property: JsonPropertyName("acceptance_only")] bool AcceptanceOnly);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FixtureFingerprintProvider : IMachineFingerprintProvider
    {
        private readonly string hash;

        public FixtureFingerprintProvider(string fixture)
        {
            if (!Guid.TryParseExact(fixture, "D", out var parsed) || parsed == Guid.Empty)
                throw new HarnessInputException();
            var normalized = parsed.ToString("D");
            hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "SuperPan.Licensing.AcceptanceFixture.v1\n" + normalized))).ToLowerInvariant();
        }

        public string GetMachineHash() => hash;
    }

    private sealed class HarnessInputException : Exception;
}

internal sealed record HarnessArguments(
    string Name,
    string ConfigPath,
    string ProductId,
    string StoreRoot,
    string MachineFixture,
    LicenseOperationKind Operation,
    DateTimeOffset? UtcNow)
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--config", "--product-id", "--store-root", "--machine-fixture", "--operation", "--utc-now",
    };

    public static HarnessArguments Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("activate" or "authorize"))
            throw new ArgumentException();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];
            if (name == "--card-stdin")
            {
                if (!flags.Add(name)) throw new ArgumentException();
                continue;
            }
            if (!ValueOptions.Contains(name) || index + 1 >= args.Length || !values.TryAdd(name, args[++index]))
                throw new ArgumentException();
        }

        var config = RequireFullPath(values, "--config", mustExist: true);
        var store = RequireFullPath(values, "--store-root", mustExist: false);
        var product = Require(values, "--product-id");
        if (!Guid.TryParseExact(product, "D", out var productId) || productId == Guid.Empty)
            throw new ArgumentException();
        var fixture = Require(values, "--machine-fixture");
        DateTimeOffset? utcNow = null;
        if (values.TryGetValue("--utc-now", out var utcText))
        {
            if (!DateTimeOffset.TryParseExact(utcText, "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) || parsed.Offset != TimeSpan.Zero)
                throw new ArgumentException();
            utcNow = parsed;
        }

        var operation = LicenseOperationKind.Startup;
        if (args[0] == "activate")
        {
            if (!flags.Contains("--card-stdin") || values.ContainsKey("--operation")) throw new ArgumentException();
        }
        else
        {
            if (flags.Count != 0 || !Enum.TryParse(Require(values, "--operation"), ignoreCase: false, out operation) ||
                !Enum.IsDefined(operation))
                throw new ArgumentException();
        }

        var expectedValueCount = args[0] == "authorize" ? 5 : 4;
        if (utcNow is not null) expectedValueCount++;
        if (values.Count != expectedValueCount) throw new ArgumentException();
        return new HarnessArguments(args[0], config, productId.ToString("D"), store, fixture, operation, utcNow);
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) && value == value.Trim()
            ? value
            : throw new ArgumentException();

    private static string RequireFullPath(IReadOnlyDictionary<string, string> values, string name, bool mustExist)
    {
        var value = Require(values, name);
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException();
        var full = Path.GetFullPath(value);
        if (mustExist && !File.Exists(full)) throw new ArgumentException();
        return full;
    }
}
