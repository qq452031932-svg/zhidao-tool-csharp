using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Updates;

internal sealed class SignedFeedStateStore
{
    private const int SchemaVersion = 1;
    private const int MaxStateBytes = 4096;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false, true);

    private readonly string statePath;
    private readonly string feedIdentity;
    private readonly SignedFeedProductVersion currentApplicationVersion;
    private readonly string mutexName;

    internal SignedFeedStateStore(
        string statePath,
        string feedIdentity,
        string currentApplicationVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedIdentity);
        if (!Path.IsPathFullyQualified(statePath))
            throw new ArgumentException("The signed feed state path must be absolute.", nameof(statePath));
        if (!Uri.TryCreate(feedIdentity, UriKind.Absolute, out var feedUri) ||
            feedUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(feedUri.UserInfo) ||
            !string.IsNullOrEmpty(feedUri.Query) ||
            !string.IsNullOrEmpty(feedUri.Fragment))
        {
            throw new ArgumentException("The signed feed identity must be an absolute HTTPS URI.", nameof(feedIdentity));
        }

        this.statePath = Path.GetFullPath(statePath);
        this.feedIdentity = feedUri.AbsoluteUri;
        this.currentApplicationVersion = SignedFeedProductVersion.Parse(
            currentApplicationVersion,
            "current application version");
        var mutexMaterial = Encoding.UTF8.GetBytes(this.statePath.ToUpperInvariant());
        mutexName = @"Local\BaiduBatchTransfer.SignedFeed.State." +
                    Convert.ToHexString(SHA256.HashData(mutexMaterial));
    }

    internal SignedFeedAcceptedState Read()
    {
        using var lease = EnterExclusive();
        var existing = LoadExisting();
        if (existing is not null &&
            !string.Equals(existing.FeedIdentity, feedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The signed feed state belongs to a different feed identity.");
        }

        return existing ?? new SignedFeedAcceptedState(
            feedIdentity,
            currentApplicationVersion.ToString(),
            string.Empty);
    }

    internal string FeedIdentity => feedIdentity;

    internal SignedFeedAcceptedState Accept(string version, string manifestSha256)
    {
        var candidate = SignedFeedProductVersion.Parse(version, "version");
        ValidateSha256(manifestSha256);
        using var lease = EnterExclusive();

        var existing = LoadExisting();
        if (existing is not null &&
            !string.Equals(existing.FeedIdentity, feedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The signed feed state belongs to a different feed identity.");
        }

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
        PersistAtomically(accepted);
        return accepted;
    }

    private SignedFeedAcceptedState? LoadExisting()
    {
        if (!File.Exists(statePath))
            return null;

        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxStateBytes)
                throw new InvalidDataException("The signed feed state has an invalid size.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The signed feed state cannot be read safely.", exception);
        }

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.BytesConsumed != bytes.Length || document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The signed feed state is malformed.");

            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != 4 ||
                names.Distinct(StringComparer.Ordinal).Count() != 4 ||
                !names.Contains("schemaVersion", StringComparer.Ordinal) ||
                !names.Contains("feedIdentity", StringComparer.Ordinal) ||
                !names.Contains("highestAcceptedVersion", StringComparer.Ordinal) ||
                !names.Contains("manifestSha256", StringComparer.Ordinal))
            {
                throw new InvalidDataException("The signed feed state schema is invalid.");
            }

            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
                root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            {
                throw new InvalidDataException("The signed feed state schema version is unsupported.");
            }

            var identity = root.GetProperty("feedIdentity").GetString();
            var version = root.GetProperty("highestAcceptedVersion").GetString();
            var manifestSha256 = root.GetProperty("manifestSha256").GetString();
            if (identity is null || version is null || manifestSha256 is null)
                throw new InvalidDataException("The signed feed state is incomplete.");
            _ = SignedFeedProductVersion.Parse(version, "state version");
            ValidateSha256(manifestSha256);
            return new SignedFeedAcceptedState(identity, version, manifestSha256);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The signed feed state is malformed.", exception);
        }
    }

    private void PersistAtomically(SignedFeedAcceptedState accepted)
    {
        var parent = Path.GetDirectoryName(statePath)
            ?? throw new InvalidOperationException("The signed feed state directory is unavailable.");
        Directory.CreateDirectory(parent);
        var temporaryPath = Path.Combine(
            parent,
            $".{Path.GetFileName(statePath)}.{Guid.NewGuid():N}.tmp");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SchemaVersion,
            feedIdentity = accepted.FeedIdentity,
            highestAcceptedVersion = accepted.HighestAcceptedVersion,
            manifestSha256 = accepted.ManifestSha256
        });
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // A stale same-directory temporary file never becomes accepted state.
            }
        }
    }

    private IDisposable EnterExclusive()
    {
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                    throw new IOException("Timed out while locking the signed feed state.");
            }
            catch (AbandonedMutexException)
            {
                // Ownership was transferred; the on-disk state is revalidated below.
            }

            return new MutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("The signed feed manifest SHA-256 is invalid.");
        }
    }

    private sealed class MutexLease : IDisposable
    {
        private Mutex? mutex;

        internal MutexLease(Mutex mutex) => this.mutex = mutex;

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref mutex, null);
            if (owned is null) return;
            try { owned.ReleaseMutex(); }
            finally { owned.Dispose(); }
        }
    }
}
