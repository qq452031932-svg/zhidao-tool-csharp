using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Updates;

internal sealed class SignedFeedStateStore
{
    private const int SchemaVersion = 2;
    private const int MaxStateBytes = 4096;
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(20);

    private readonly string statePath;
    private readonly string previousStatePath;
    private readonly string feedIdentity;
    private readonly SignedFeedProductVersion currentApplicationVersion;
    private readonly TimeSpan lockTimeout;

    internal SignedFeedStateStore(
        string statePath,
        string feedIdentity,
        string currentApplicationVersion,
        TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedIdentity);
        if (!Path.IsPathFullyQualified(statePath))
            throw new ArgumentException("The signed feed state path must be absolute.", nameof(statePath));
        if (!Uri.TryCreate(feedIdentity, UriKind.Absolute, out var feedUri) ||
            feedUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(feedUri.Host) ||
            !string.IsNullOrEmpty(feedUri.UserInfo) ||
            !string.IsNullOrEmpty(feedUri.Query) ||
            !string.IsNullOrEmpty(feedUri.Fragment))
        {
            throw new ArgumentException("The signed feed identity must be an absolute HTTPS URI.", nameof(feedIdentity));
        }

        this.statePath = Path.GetFullPath(statePath);
        previousStatePath = this.statePath + ".previous";
        LockFilePath = this.statePath + ".lock";
        this.feedIdentity = feedUri.AbsoluteUri;
        if (this.feedIdentity.Length > 2048)
            throw new ArgumentException("The signed feed identity is too long.", nameof(feedIdentity));
        this.currentApplicationVersion = SignedFeedProductVersion.Parse(
            currentApplicationVersion,
            "current application version");
        this.lockTimeout = lockTimeout ?? DefaultLockTimeout;
        if (this.lockTimeout <= TimeSpan.Zero || this.lockTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
    }

    internal string FeedIdentity => feedIdentity;

    internal string LockFilePath { get; }

    internal SignedFeedAcceptedState Read()
    {
        using var lease = EnterExclusive();
        var journal = LoadJournal();
        if (journal.Selected is null)
        {
            return new SignedFeedAcceptedState(
                feedIdentity,
                currentApplicationVersion.ToString(),
                string.Empty);
        }

        var persisted = SignedFeedProductVersion.Parse(
            journal.Selected.State!.HighestAcceptedVersion,
            "state version");
        return persisted.CompareTo(currentApplicationVersion) >= 0
            ? journal.Selected.State
            : new SignedFeedAcceptedState(
                feedIdentity,
                currentApplicationVersion.ToString(),
                string.Empty);
    }

    internal SignedFeedAcceptedState Accept(string version, string manifestSha256)
    {
        var candidate = SignedFeedProductVersion.Parse(version, "version");
        ValidateSha256(manifestSha256, "manifest SHA-256");
        using var lease = EnterExclusive();
        var journal = LoadJournal();
        var existing = journal.Selected?.State;
        var persisted = existing is null
            ? currentApplicationVersion
            : SignedFeedProductVersion.Parse(existing.HighestAcceptedVersion, "state version");
        var floor = persisted.CompareTo(currentApplicationVersion) >= 0
            ? persisted
            : currentApplicationVersion;
        if (candidate.CompareTo(floor) < 0)
            throw new InvalidDataException("The signed feed version is below the replay-protection floor.");

        if (existing is not null && candidate.CompareTo(persisted) == 0)
        {
            if (!string.Equals(existing.ManifestSha256, manifestSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The signed feed reuses an accepted version with different content.");
            }

            return existing;
        }

        var accepted = new SignedFeedAcceptedState(
            feedIdentity,
            candidate.ToString(),
            manifestSha256);
        var target = SelectWriteTarget(journal);
        WriteDurableSlot(target, accepted);
        var verified = ReadSlot(target);
        if (!verified.IsValid || verified.State != accepted)
            throw new IOException("The signed feed replay floor could not be verified after persistence.");
        return accepted;
    }

    private JournalSnapshot LoadJournal()
    {
        var slots = new[] { ReadSlot(statePath), ReadSlot(previousStatePath) };
        var existingSlots = slots.Where(slot => slot.Exists).ToArray();
        var validSlots = existingSlots.Where(slot => slot.IsValid).ToArray();
        if (existingSlots.Length > 0 && validSlots.Length == 0)
            throw new InvalidDataException("The signed feed state journal contains no valid record.");

        foreach (var slot in validSlots)
        {
            if (!string.Equals(slot.State!.FeedIdentity, feedIdentity, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The signed feed state belongs to a different feed identity.");
            }
        }

        foreach (var group in validSlots.GroupBy(
                     slot => slot.State!.HighestAcceptedVersion,
                     StringComparer.Ordinal))
        {
            if (group.Select(slot => slot.State!.ManifestSha256)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any())
            {
                throw new InvalidDataException(
                    "The signed feed journal contains conflicting records for one version.");
            }
        }

        var selected = validSlots
            .OrderByDescending(slot => SignedFeedProductVersion.Parse(
                slot.State!.HighestAcceptedVersion,
                "state version"))
            .ThenBy(slot => slot.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        return new JournalSnapshot(slots, selected);
    }

    private SlotSnapshot ReadSlot(string path)
    {
        if (!File.Exists(path))
            return new SlotSnapshot(path, Exists: false, State: null);

        try
        {
            byte[] bytes;
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            {
                if (stream.Length is <= 0 or > MaxStateBytes)
                    throw new InvalidDataException("The signed feed state has an invalid size.");
                bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
            }

            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.BytesConsumed != bytes.Length || document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The signed feed state is malformed.");
            var root = document.RootElement;
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != 5 ||
                names.Distinct(StringComparer.Ordinal).Count() != 5 ||
                !names.Contains("schemaVersion", StringComparer.Ordinal) ||
                !names.Contains("feedIdentity", StringComparer.Ordinal) ||
                !names.Contains("highestAcceptedVersion", StringComparer.Ordinal) ||
                !names.Contains("manifestSha256", StringComparer.Ordinal) ||
                !names.Contains("recordChecksumSha256", StringComparer.Ordinal))
            {
                throw new InvalidDataException("The signed feed state schema is invalid.");
            }

            if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
                root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            {
                throw new InvalidDataException("The signed feed state schema version is unsupported.");
            }

            var identity = root.GetProperty("feedIdentity").GetString();
            var version = root.GetProperty("highestAcceptedVersion").GetString();
            var manifestSha256 = root.GetProperty("manifestSha256").GetString();
            var checksum = root.GetProperty("recordChecksumSha256").GetString();
            if (identity is null || version is null || manifestSha256 is null || checksum is null)
                throw new InvalidDataException("The signed feed state is incomplete.");
            _ = SignedFeedProductVersion.Parse(version, "state version");
            ValidateSha256(manifestSha256, "manifest SHA-256");
            ValidateSha256(checksum, "record checksum");
            var expectedChecksum = ComputeRecordChecksum(identity, version, manifestSha256);
            if (!FixedTimeHexEquals(checksum, expectedChecksum))
                throw new InvalidDataException("The signed feed state checksum is invalid.");
            return new SlotSnapshot(
                path,
                Exists: true,
                new SignedFeedAcceptedState(identity, version, manifestSha256));
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                          JsonException or
                                          InvalidOperationException or
                                          FormatException or
                                          DecoderFallbackException)
        {
            return new SlotSnapshot(path, Exists: true, State: null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The signed feed state cannot be read safely.", exception);
        }
    }

    private string SelectWriteTarget(JournalSnapshot journal)
    {
        var reusable = journal.Slots.FirstOrDefault(slot => !slot.IsValid);
        if (reusable is not null)
            return reusable.Path;
        return journal.Slots
            .OrderBy(slot => SignedFeedProductVersion.Parse(
                slot.State!.HighestAcceptedVersion,
                "state version"))
            .ThenBy(slot => slot.Path, StringComparer.Ordinal)
            .First()
            .Path;
    }

    private void WriteDurableSlot(string path, SignedFeedAcceptedState state)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SchemaVersion,
            feedIdentity = state.FeedIdentity,
            highestAcceptedVersion = state.HighestAcceptedVersion,
            manifestSha256 = state.ManifestSha256,
            recordChecksumSha256 = ComputeRecordChecksum(
                state.FeedIdentity,
                state.HighestAcceptedVersion,
                state.ManifestSha256)
        });
        if (payload.Length > MaxStateBytes)
            throw new InvalidDataException("The signed feed state exceeds its size limit.");
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    private FileStream EnterExclusive()
    {
        var parent = Path.GetDirectoryName(statePath)
            ?? throw new InvalidOperationException("The signed feed state directory is unavailable.");
        Directory.CreateDirectory(parent);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    LockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (stopwatch.Elapsed < lockTimeout)
            {
                Thread.Sleep(LockRetryInterval);
            }
        }
    }

    private static string ComputeRecordChecksum(
        string identity,
        string version,
        string manifestSha256)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SchemaVersion,
            feedIdentity = identity,
            highestAcceptedVersion = version,
            manifestSha256
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static bool FixedTimeHexEquals(string left, string right)
    {
        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static void ValidateSha256(string value, string fieldName)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"The signed feed {fieldName} is invalid.");
        }
    }

    private sealed record SlotSnapshot(
        string Path,
        bool Exists,
        SignedFeedAcceptedState? State)
    {
        internal bool IsValid => State is not null;
    }

    private sealed record JournalSnapshot(
        IReadOnlyList<SlotSnapshot> Slots,
        SlotSnapshot? Selected);
}
