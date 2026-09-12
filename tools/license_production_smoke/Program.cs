using System.Reflection;
using System.Security.Cryptography;
using SuperPan.Licensing.Client;

internal static class Program
{
    private static readonly HashSet<string> SafeCodes = new(LicenseCodes.All, StringComparer.Ordinal)
        { "activation_required", "authorization_unavailable" };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length is < 1 or > 2 || args[0] is not ("activate" or "authorize") ||
                (args[0] == "activate" && (args.Length != 2 || args[1] != "--card-stdin")) ||
                (args[0] == "authorize" && (args.Length != 2 || !Enum.TryParse<LicenseOperationKind>(args[1], false, out _))))
                return Emit("invalid_invocation", 2);

            var trust = ProductionTrust.Read();
            using var client = new LicenseApiClient(trust.Options);
            var gate = new LicenseAuthorizationGate(trust.ProductId, client,
                new DpapiLicenseStore(), new MachineFingerprintProvider());
            LicenseGateResult result;
            if (args[0] == "activate")
            {
                var card = Console.In.ReadLine();
                if (string.IsNullOrWhiteSpace(card) || card != card.Trim()) return Emit("invalid_input", 2);
                result = await gate.ActivateAsync(card, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                result = await gate.AuthorizeAsync(Enum.Parse<LicenseOperationKind>(args[1], false), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            return Emit(SafeCodes.Contains(result.Code) ? result.Code : "unrecognized", result.IsAuthorized ? 0 : 3);
        }
        catch { return Emit("smoke_failed", 2); }
    }

    private static int Emit(string code, int exitCode) { Console.WriteLine(code); return exitCode; }
}

internal sealed record ProductionTrust(string ProductId, LicenseClientOptions Options)
{
    public static ProductionTrust Read()
    {
        var values = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(x => x.Key, x => x.Value ?? "", StringComparer.Ordinal);
        string Get(string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : throw new InvalidOperationException();
        var product = Get("LicenseProductId");
        var der = Convert.FromBase64String(Get("LicenseSigningPublicKey"));
        using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(der, out var read);
        if (read != der.Length || key.KeySize != 256) throw new CryptographicException();
        var pem = new string(PemEncoding.Write("PUBLIC KEY", der));
        var options = new LicenseClientOptions(product, new Uri(Get("LicenseOrigin")), Get("LicenseSigningKeyId"), pem,
            [Get("LicenseCurrentSpkiPin"), Get("LicenseNextSpkiPin")]);
        return new ProductionTrust(product, options);
    }
}
