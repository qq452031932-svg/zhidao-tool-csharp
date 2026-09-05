using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Updates;

internal sealed class SignedFeedStateStore
{
    private const int SlotSchemaVersion = 3;
    private const int PointerSchemaVersion = 1;
    private const int MaxArtifactBytes = 4096;
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(20);

    private readonly string[] slotPaths;
    private readonly string commitPointerPath;
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

        var primaryPath = Path.GetFullPath(statePath);
        slotPaths = [primaryPath, primaryPath + ".previous"];
        commitPointerPath = primaryPath + ".commit";
        LockFilePath = primaryPath + ".lock";
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
        var committed = LoadCommitted();
        if (committed is null)
        {
            return new SignedFeedAcceptedState(
                feedIdentity,
                currentApplicationVersion.ToString(),
                string.Empty);
        }

        var persisted = SignedFeedProductVersion.Parse(
            committed.Record.State.HighestAcceptedVersion,
            "state version");
        return persisted.CompareTo(currentApplicationVersion) >= 0
            ? committed.Record.State
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
        var committed = LoadCommitted();
        var existing = committed?.Record.State;
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
        var targetSlot = committed is null ? 0 : 1 - committed.Pointer.Slot;
        var generation = committed is null
            ? 1L
            : checked(committed.Pointer.Generation + 1);
        var slotDigest = WriteDurableSlot(targetSlot, generation, accepted);
        WriteDurableCommitPointer(targetSlot, generation, slotDigest);

        var verified = LoadCommitted();
        if (verified is null ||
            verified.Pointer.Slot != targetSlot ||
            verified.Pointer.Generation != generation ||
            verified.Record.State != accepted)
        {
            throw new IOException("The signed feed replay floor could not be verified after commit.");
        }

        return accepted;
    }

    private CommittedSnapshot? LoadCommitted()
    {
        var pointerArtifact = ReadArtifact(commitPointerPath);
        if (!pointerArtifact.Exists)
        {
            var firstSlot = ReadArtifact(slotPaths[0]);
            var secondSlot = ReadArtifact(slotPaths[1]);
            if (firstSlot.Exists || secondSlot.Exists)
            {
                throw new InvalidDataException(
                    "The signed feed state journal has data slots but no durable commit pointer.");
            }

            return null;
        }

        var pointer = ParseCommitPointer(pointerArtifact.Bytes!);
        var slotArtifact = ReadArtifact(slotPaths[pointer.Slot]);
        if (!slotArtifact.Exists)
            throw new InvalidDataException("The committed signed feed state slot is missing.");
        var record = ParseSlot(slotArtifact.Bytes!);
        var actualSlotDigest = LowerHash(slotArtifact.Bytes!);
        if (record.Generation != pointer.Generation ||
            !FixedTimeHexEquals(actualSlotDigest, pointer.SlotDigestSha256))
        {
            throw new InvalidDataException(
                "The committed signed feed slot does not match its durable pointer.");
        }

        if (!string.Equals(record.State.FeedIdentity, feedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The signed feed state belongs to a different feed identity.");
        }

        return new CommittedSnapshot(pointer, record);
    }

    private ArtifactSnapshot ReadArtifact(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxArtifactBytes)
                throw new InvalidDataException("The signed feed state artifact has an invalid size.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return new ArtifactSnapshot(Exists: true, bytes);
        }
        catch (FileNotFoundException)
        {
            return new ArtifactSnapshot(Exists: false, Bytes: null);
        }
        catch (DirectoryNotFoundException)
        {
            return new ArtifactSnapshot(Exists: false, Bytes: null);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "The signed feed state artifact cannot be read safely.",
                exception);
        }
    }

    private static SlotRecord ParseSlot(byte[] bytes)
    {
        try
        {
            using var document = ParseExactJson(bytes);
            var root = document.RootElement;
            RequireExactProperties(
                root,
                "schemaVersion",
                "generation",
                "feedIdentity",
                "highestAcceptedVersion",
                "manifestSha256",
                "recordChecksumSha256");
            if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
                root.GetProperty("schemaVersion").GetInt32() != SlotSchemaVersion)
            {
                throw new InvalidDataException("The signed feed slot schema is unsupported.");
            }

            var generation = root.GetProperty("generation").GetInt64();
            var identity = root.GetProperty("feedIdentity").GetString();
            var version = root.GetProperty("highestAcceptedVersion").GetString();
            var manifestSha256 = root.GetProperty("manifestSha256").GetString();
            var checksum = root.GetProperty("recordChecksumSha256").GetString();
            if (generation <= 0 ||
                identity is null ||
                version is null ||
                manifestSha256 is null ||
                checksum is null)
            {
                throw new InvalidDataException("The signed feed slot is incomplete.");
            }

            _ = SignedFeedProductVersion.Parse(version, "state version");
            ValidateSha256(manifestSha256, "manifest SHA-256");
            ValidateSha256(checksum, "record checksum");
            var expected = ComputeSlotChecksum(generation, identity, version, manifestSha256);
            if (!FixedTimeHexEquals(checksum, expected))
                throw new InvalidDataException("The signed feed slot checksum is invalid.");
            return new SlotRecord(
                generation,
                new SignedFeedAcceptedState(identity, version, manifestSha256));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or
                                          InvalidOperationException or
                                          FormatException or
                                          DecoderFallbackException or
                                          OverflowException)
        {
            throw new InvalidDataException("The signed feed slot is malformed.", exception);
        }
    }

    private static CommitPointer ParseCommitPointer(byte[] bytes)
    {
        try
        {
            using var document = ParseExactJson(bytes);
            var root = document.RootElement;
            RequireExactProperties(
                root,
                "schemaVersion",
                "slot",
                "generation",
                "slotDigestSha256",
                "pointerChecksumSha256");
            if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
                root.GetProperty("schemaVersion").GetInt32() != PointerSchemaVersion)
            {
                throw new InvalidDataException("The signed feed commit pointer schema is unsupported.");
            }

            var slot = root.GetProperty("slot").GetInt32();
            var generation = root.GetProperty("generation").GetInt64();
            var slotDigest = root.GetProperty("slotDigestSha256").GetString();
            var checksum = root.GetProperty("pointerChecksumSha256").GetString();
            if (slot is < 0 or > 1 || generation <= 0 || slotDigest is null || checksum is null)
                throw new InvalidDataException("The signed feed commit pointer is incomplete.");
            ValidateSha256(slotDigest, "slot digest");
            ValidateSha256(checksum, "pointer checksum");
            var expected = ComputePointerChecksum(slot, generation, slotDigest);
            if (!FixedTimeHexEquals(checksum, expected))
                throw new InvalidDataException("The signed feed commit pointer checksum is invalid.");
            return new CommitPointer(slot, generation, slotDigest);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or
                                          InvalidOperationException or
                                          FormatException or
                                          DecoderFallbackException or
                                          OverflowException)
        {
            throw new InvalidDataException("The signed feed commit pointer is malformed.", exception);
        }
    }

    private string WriteDurableSlot(
        int slot,
        long generation,
        SignedFeedAcceptedState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SlotSchemaVersion,
            generation,
            feedIdentity = state.FeedIdentity,
            highestAcceptedVersion = state.HighestAcceptedVersion,
            manifestSha256 = state.ManifestSha256,
            recordChecksumSha256 = ComputeSlotChecksum(
                generation,
                state.FeedIdentity,
                state.HighestAcceptedVersion,
                state.ManifestSha256)
        });
        WriteAndFlush(slotPaths[slot], bytes);
        return LowerHash(bytes);
    }

    private void WriteDurableCommitPointer(int slot, long generation, string slotDigest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = PointerSchemaVersion,
            slot,
            generation,
            slotDigestSha256 = slotDigest,
            pointerChecksumSha256 = ComputePointerChecksum(slot, generation, slotDigest)
        });
        var parent = Path.GetDirectoryName(commitPointerPath)!;
        var temporaryPath = Path.Combine(
            parent,
            $".{Path.GetFileName(commitPointerPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteAndFlush(temporaryPath, bytes, createNew: true);
            ReplaceCommitPointerDurably(temporaryPath, commitPointerPath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static void WriteAndFlush(string path, byte[] bytes, bool createNew = false)
    {
        if (bytes.Length > MaxArtifactBytes)
            throw new InvalidDataException("The signed feed state artifact exceeds its size limit.");
        using var stream = new FileStream(
            path,
            createNew ? FileMode.CreateNew : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceCommitPointerDurably(string temporaryPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(
                    temporaryPath,
                    targetPath,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The signed feed commit pointer could not be durably replaced.");
            }

            return;
        }

        // The application targets Windows; this fallback keeps hermetic non-Windows tooling usable.
        File.Move(temporaryPath, targetPath, overwrite: true);
        using var target = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.WriteThrough);
        target.Flush(flushToDisk: true);
    }

    private FileStream EnterExclusive()
    {
        var parent = Path.GetDirectoryName(slotPaths[0])
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

    private static JsonDocument ParseExactJson(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4
        });
        var document = JsonDocument.ParseValue(ref reader);
        if (reader.BytesConsumed != bytes.Length || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("The signed feed state artifact is malformed.");
        }

        return document;
    }

    private static void RequireExactProperties(JsonElement root, params string[] expected)
    {
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Length ||
            names.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            expected.Any(name => !names.Contains(name, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The signed feed state artifact schema is invalid.");
        }
    }

    private static string ComputeSlotChecksum(
        long generation,
        string identity,
        string version,
        string manifestSha256)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SlotSchemaVersion,
            generation,
            feedIdentity = identity,
            highestAcceptedVersion = version,
            manifestSha256
        });
        return LowerHash(canonical);
    }

    private static string ComputePointerChecksum(int slot, long generation, string slotDigest)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = PointerSchemaVersion,
            slot,
            generation,
            slotDigestSha256 = slotDigest
        });
        return LowerHash(canonical);
    }

    private static string LowerHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);

    private sealed record ArtifactSnapshot(bool Exists, byte[]? Bytes);

    private sealed record SlotRecord(long Generation, SignedFeedAcceptedState State);

    private sealed record CommitPointer(int Slot, long Generation, string SlotDigestSha256);

    private sealed record CommittedSnapshot(CommitPointer Pointer, SlotRecord Record);
}
