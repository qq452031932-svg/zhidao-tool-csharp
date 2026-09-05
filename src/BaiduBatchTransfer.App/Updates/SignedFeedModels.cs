using System.Globalization;
using System.IO;

namespace BaiduBatchTransfer.App.Updates;

internal sealed record SignedFeedAcceptedState(
    string FeedIdentity,
    string HighestAcceptedVersion,
    string ManifestSha256);

internal sealed record SignedFeedVerifiedAsset(
    string PackageId,
    string Version,
    string Type,
    string FileName,
    long Size,
    string Sha256);

internal sealed record SignedFeedVerificationSnapshot(
    string FeedIdentity,
    string HighestVersion,
    string ManifestSha256,
    IReadOnlyDictionary<string, SignedFeedVerifiedAsset> Assets);

internal readonly record struct SignedFeedProductVersion(
    ushort Major,
    ushort Minor,
    ushort Patch) : IComparable<SignedFeedProductVersion>
{
    internal static SignedFeedProductVersion Parse(string? value, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
            throw new InvalidDataException($"The signed feed {fieldName} is invalid.");

        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length != 3)
            throw new InvalidDataException($"The signed feed {fieldName} is invalid.");

        Span<ushort> numbers = stackalloc ushort[3];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 ||
                (part.Length > 1 && part[0] == '0') ||
                part.Any(character => character is < '0' or > '9') ||
                !ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                number > 65534)
            {
                throw new InvalidDataException($"The signed feed {fieldName} is invalid.");
            }

            numbers[index] = number;
        }

        return new SignedFeedProductVersion(numbers[0], numbers[1], numbers[2]);
    }

    public int CompareTo(SignedFeedProductVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
