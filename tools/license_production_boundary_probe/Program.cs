using System.Reflection;
using System.Security.Cryptography;
using SuperPan.Licensing.Client;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 4 || args[0] != "--utc-now" || args[2] != "--store-root" ||
                !DateTimeOffset.TryParseExact(args[1], "O", null, System.Globalization.DateTimeStyles.RoundtripKind, out var now) || now.Offset != TimeSpan.Zero ||
                !Path.IsPathFullyQualified(args[3])) return Emit("invalid_invocation", 2);
            var root = Path.GetFullPath(args[3]);
            var approvedBase = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuperPan", "Licensing", "BoundaryProbe"));
            var rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !string.Equals(rootInfo.Parent?.FullName, approvedBase, StringComparison.OrdinalIgnoreCase) ||
                !rootInfo.Name.StartsWith("probe-", StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(root, ".superpan-boundary-probe-root")) ||
                !File.Exists(Path.Combine(root, "licenses.dat"))) return Emit("invalid_store_root", 2);
            var trust = ReadTrust();
            var clock = new FixedTimeProvider(now);
            using var verifier = new SignedLicenseEnvelopeVerifier(trust.Options, clock);
            using var client = new LicenseApiClient(trust.Options, new UnavailableHandler(), verifier, clock);
            var gate = new LicenseAuthorizationGate(trust.ProductId, client,
                new DpapiLicenseStore(Path.Combine(root, "licenses.dat")), new MachineFingerprintProvider(), clock);
            var result = await gate.AuthorizeAsync(LicenseOperationKind.Startup, CancellationToken.None).ConfigureAwait(false);
            return Emit(result.IsAuthorized && result.UsedCachedLease ? "cached_authorized" : "cached_denied", result.IsAuthorized ? 0 : 3);
        }
        catch { return Emit("probe_failed", 2); }
    }
    private static (string ProductId, LicenseClientOptions Options) ReadTrust()
    {
        var values = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(x => x.Key, x => x.Value ?? "", StringComparer.Ordinal);
        string Get(string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : throw new InvalidOperationException();
        var der = Convert.FromBase64String(Get("LicenseSigningPublicKey")); using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(der, out var read);
        if (read != der.Length || key.KeySize != 256) throw new CryptographicException();
        var product = Get("LicenseProductId");
        return (product, new LicenseClientOptions(product, new Uri(Get("LicenseOrigin")), Get("LicenseSigningKeyId"), new string(PemEncoding.Write("PUBLIC KEY", der)), [Get("LicenseCurrentSpkiPin"), Get("LicenseNextSpkiPin")]));
    }
    private static int Emit(string code, int exitCode) { Console.WriteLine(code); return exitCode; }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class UnavailableHandler : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new HttpRequestException(); }
}
