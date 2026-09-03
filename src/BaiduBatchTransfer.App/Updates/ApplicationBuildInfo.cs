using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Updates;

public sealed record ApplicationBuildInfo(
    string ProductVersion,
    string AdapterBuildVersion,
    string Channel,
    string? HttpsFeedUrl,
    string? AcceptanceFeedPath,
    bool IsOtaAcceptanceBuild)
{
    private const string ProductionChannel = "win-x64-stable";
    private static readonly Regex ThreePartVersion = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+$",
        RegexOptions.CultureInvariant);

    public bool IsFeedConfigured => HttpsFeedUrl is not null || AcceptanceFeedPath is not null;

    public static ApplicationBuildInfo Current { get; } = ReadCurrent();

    public static bool TryCreateProduction(string feedUrl, out ApplicationBuildInfo? info)
    {
        if (!TryValidateHttpsFeed(feedUrl, out var normalizedFeed))
        {
            info = null;
            return false;
        }

        var assembly = Assembly.GetExecutingAssembly();
        info = CreateForTests(
            ReadProductVersion(assembly),
            ReadMetadata(assembly, "AdapterBuildVersion") ?? "managed-browser-v3",
            normalizedFeed);
        return true;
    }

    public static ApplicationBuildInfo CreateForTests(
        string productVersion,
        string adapterBuildVersion,
        string? httpsFeedUrl,
        string channel = ProductionChannel,
        string? acceptanceFeedPath = null,
        bool isOtaAcceptanceBuild = false)
    {
        ValidateProductVersion(productVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterBuildVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        string? normalizedHttpsFeed = null;
        if (httpsFeedUrl is not null &&
            !TryValidateHttpsFeed(httpsFeedUrl, out normalizedHttpsFeed))
        {
            throw new ArgumentException(
                "Production update feed must be an immutable absolute HTTPS URL.",
                nameof(httpsFeedUrl));
        }

        string? normalizedAcceptanceFeed = null;
        if (acceptanceFeedPath is not null)
        {
            if (string.IsNullOrWhiteSpace(acceptanceFeedPath) ||
                !Path.IsPathFullyQualified(acceptanceFeedPath))
            {
                throw new ArgumentException(
                    "Acceptance update feed must be an absolute local path.",
                    nameof(acceptanceFeedPath));
            }

            normalizedAcceptanceFeed = Path.GetFullPath(acceptanceFeedPath);
        }

        return new ApplicationBuildInfo(
            productVersion,
            adapterBuildVersion.Trim(),
            channel.Trim(),
            normalizedHttpsFeed,
            normalizedAcceptanceFeed,
            isOtaAcceptanceBuild);
    }

    public static ApplicationAssemblyVersions DeriveAssemblyVersions(string productVersion)
    {
        ValidateProductVersion(productVersion);
        var parts = productVersion.Split('.').Select(int.Parse).ToArray();
        return new ApplicationAssemblyVersions(
            $"{parts[0]}.0.0.0",
            $"{parts[0]}.{parts[1]}.{parts[2]}.0");
    }

    private static ApplicationBuildInfo ReadCurrent()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return CreateForTests(
            ReadProductVersion(assembly),
            ReadMetadata(assembly, "AdapterBuildVersion") ?? "managed-browser-v3",
            NullIfWhiteSpace(ReadMetadata(assembly, "UpdateFeedUrl")),
            ReadMetadata(assembly, "UpdateChannel") ?? ProductionChannel,
            NullIfWhiteSpace(ReadMetadata(assembly, "OtaAcceptanceFeedPath")),
            bool.TryParse(ReadMetadata(assembly, "OtaAcceptanceBuild"), out var acceptance) && acceptance);
    }

    private static string ReadProductVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "1.0.0";

    private static string? ReadMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value;

    private static bool TryValidateHttpsFeed(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static void ValidateProductVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !ThreePartVersion.IsMatch(value))
            throw new ArgumentException(
                "Product version must contain exactly three numeric parts.",
                nameof(value));
        try
        {
            _ = value.Split('.').Select(int.Parse).ToArray();
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                "Product version contains an out-of-range numeric part.",
                nameof(value),
                exception);
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ApplicationAssemblyVersions(string AssemblyVersion, string FileVersion);
