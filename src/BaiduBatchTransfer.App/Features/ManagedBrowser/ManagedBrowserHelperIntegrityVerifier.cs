using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public enum ManagedBrowserHelperIntegrityError
{
    MissingManifest = 1,
    ManifestMismatch = 2,
    InvalidManifest = 3,
    IncompatibleVersion = 4,
    MissingPayload = 5,
    UnlistedPayload = 6,
    PayloadMismatch = 7,
    UnsafeFilesystemEntry = 8,
    VerificationIoFailure = 9,
    ResourceLimitExceeded = 10,
}

internal sealed record ManagedBrowserHelperIntegrityLimits(
    int MaximumTreeEntries,
    int MaximumTreeDepth,
    long MaximumTreeBytes)
{
    public static ManagedBrowserHelperIntegrityLimits Default { get; } = new(
        MaximumTreeEntries: 32_768,
        MaximumTreeDepth: 64,
        MaximumTreeBytes: 4L * 1024 * 1024 * 1024 + 4 * 1024 * 1024);
}

public sealed class ManagedBrowserHelperIntegrityException : Exception
{
    public ManagedBrowserHelperIntegrityException(ManagedBrowserHelperIntegrityError error)
        : base($"Managed browser helper integrity failure: {error}.") => Error = error;

    public ManagedBrowserHelperIntegrityError Error { get; }

    public override string ToString() =>
        $"ManagedBrowserHelperIntegrityException {{ Error = {Error}, Details = [REDACTED] }}";
}

public enum ManagedBrowserHelperCleanupState
{
    Active = 0,
    Cleaned = 1,
    DeferredCleanupRegistered = 2,
    OrphanRecoveryPending = 3,
}

internal enum ManagedBrowserHelperSnapshotPhase
{
    ContainerCreated = 0,
    CopyingPayload = 1,
    MarkerWritten = 2,
    PinningSnapshot = 3,
    SealingAcl = 4,
    OwnershipPersisted = 5,
    PayloadSealed = 6,
    OwnershipSealed = 7,
    ReadyPreparingSealed = 8,
    ContainerSealed = 9,
    ReadyCommitted = 10,
}

internal enum ManagedBrowserHelperCleanupPhase
{
    ActiveLockDeleted = 0,
}

internal sealed class ManagedBrowserHelperCleanupStatus
{
    private int state;
    private OrphanObservation? orphan;

    public ManagedBrowserHelperCleanupState State
    {
        get
        {
            var current = (ManagedBrowserHelperCleanupState)Volatile.Read(ref state);
            if (current != ManagedBrowserHelperCleanupState.OrphanRecoveryPending)
                return current;
            var observed = Volatile.Read(ref orphan);
            if (observed is null || OrphanStillExists(observed)) return current;
            Interlocked.CompareExchange(ref state, (int)ManagedBrowserHelperCleanupState.Cleaned,
                (int)ManagedBrowserHelperCleanupState.OrphanRecoveryPending);
            return (ManagedBrowserHelperCleanupState)Volatile.Read(ref state);
        }
    }

    public void Set(ManagedBrowserHelperCleanupState value)
    {
        if (value != ManagedBrowserHelperCleanupState.OrphanRecoveryPending)
            Volatile.Write(ref orphan, null);
        Volatile.Write(ref state, (int)value);
    }

    public void SetOrphanRecoveryPending(string path, uint volume, string fileId)
    {
        Volatile.Write(ref orphan, new OrphanObservation(path, volume, fileId));
        Volatile.Write(ref state, (int)ManagedBrowserHelperCleanupState.OrphanRecoveryPending);
    }

    private static bool OrphanStillExists(OrphanObservation observed)
    {
        try
        {
            using var current = PinnedHelperEntry.Open(observed.Path,
                shareMode: FileShare.Read | FileShare.Write | FileShare.Delete);
            return current.IsDirectory && current.VolumeSerialNumber == observed.Volume &&
                string.Equals(current.FileId, observed.FileId, StringComparison.Ordinal);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return false;
        }
        catch (UnsafeHelperFilesystemException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           Win32Exception or System.Security.SecurityException or
                                           ArgumentException)
        {
            return true;
        }
    }

    private sealed record OrphanObservation(string Path, uint Volume, string FileId);
}

/// <summary>
/// Owns the exact verified helper snapshot and its identity/cleanup handles. The client must
/// retain this lease until the helper has exited and redirected output has been fully drained.
/// Its ACL is a best-effort guard against ordinary runtime writes, not a boundary against an
/// actor running as the same Windows owner, who can deliberately regain WRITE_DAC.
/// </summary>
public sealed class VerifiedManagedBrowserHelperBundle : IDisposable
{
    private VerifiedHelperSnapshotLease? lease;
    private readonly string executablePath;
    private readonly string mainScriptPath;
    private readonly string buildIdentity;
    private readonly ManagedBrowserHelperCleanupStatus cleanupStatus;

    internal VerifiedManagedBrowserHelperBundle(
        VerifiedHelperSnapshotLease lease,
        string executablePath,
        string mainScriptPath,
        string buildIdentity)
    {
        this.lease = lease;
        this.executablePath = executablePath;
        this.mainScriptPath = mainScriptPath;
        this.buildIdentity = buildIdentity;
        cleanupStatus = lease.CleanupStatus;
    }

    public string ExecutablePath => IsDisposed
        ? throw new ObjectDisposedException(nameof(VerifiedManagedBrowserHelperBundle))
        : executablePath;
    public string MainScriptPath => IsDisposed
        ? throw new ObjectDisposedException(nameof(VerifiedManagedBrowserHelperBundle))
        : mainScriptPath;
    public string BuildIdentity => IsDisposed
        ? throw new ObjectDisposedException(nameof(VerifiedManagedBrowserHelperBundle))
        : buildIdentity;
    public bool IsDisposed => lease is null;
    public ManagedBrowserHelperCleanupState CleanupState => cleanupStatus.State;

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref lease, null);
        if (owned is null) return;
        owned.Release();
    }

    internal void AbandonForProcessCrashSimulation()
    {
        var owned = Interlocked.Exchange(ref lease, null);
        if (owned is null) return;
        owned.AbandonForProcessCrashSimulation();
    }

    public override string ToString() =>
        $"VerifiedManagedBrowserHelperBundle {{ Paths = [REDACTED], BuildIdentity = [REDACTED], IsDisposed = {IsDisposed} }}";
}

public sealed class ManagedBrowserHelperIntegrityVerifier
{
    public const int ManifestVersion = 1;
    public const string RequiredPythonVersion = "3.14.0";
    public const string RequiredDistributionMode = "PersonalNonCommercial";
    public const string EmbeddedManifestResourceName =
        "BaiduBatchTransfer.ManagedBrowserHelper.helper-manifest.json";

    private const string ManifestFileName = "helper-manifest.json";
    private const string ExecutableFileName = "python.exe";
    private const string MainScriptFileName = "main.py";
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumManifestFiles = 16_384;
    private const long MaximumPayloadFileBytes = 1024L * 1024 * 1024;
    private const long MaximumPayloadBytes = 4L * 1024 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RequiredFiles =
        [ExecutableFileName, "python314.dll", MainScriptFileName, "protocol.py",
            "requirements.lock", "THIRD_PARTY_NOTICES.txt"];

    private readonly byte[] expectedManifestBytes;
    private readonly ManagedBrowserHelperIntegrityLimits limits;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan orphanMinimumAge;
    private readonly string snapshotParent;
    private readonly Action<ManagedBrowserHelperSnapshotPhase>? snapshotCreationObserver;
    private readonly Action<string, string>? snapshotFileCopiedObserver;
    private readonly Action? cleanupAttemptObserver;
    private readonly Action<ManagedBrowserHelperCleanupPhase>? snapshotCleanupObserver;
    private readonly Action<string>? snapshotCopyHandoffObserver;

    public ManagedBrowserHelperIntegrityVerifier()
        : this(ReadEmbeddedManifest(), ManagedBrowserHelperIntegrityLimits.Default,
            TimeProvider.System, TimeSpan.FromHours(12))
    {
    }

    internal ManagedBrowserHelperIntegrityVerifier(
        byte[] expectedManifestBytes,
        ManagedBrowserHelperIntegrityLimits? limits = null,
        TimeProvider? timeProvider = null,
        TimeSpan? orphanMinimumAge = null,
        string? snapshotParent = null,
        Action<ManagedBrowserHelperSnapshotPhase>? snapshotCreationObserver = null,
        Action<string, string>? snapshotFileCopiedObserver = null,
        Action? cleanupAttemptObserver = null,
        Action<ManagedBrowserHelperCleanupPhase>? snapshotCleanupObserver = null,
        Action<string>? snapshotCopyHandoffObserver = null)
    {
        ArgumentNullException.ThrowIfNull(expectedManifestBytes);
        if (expectedManifestBytes.Length is < 1 or > MaximumManifestBytes)
            throw new ArgumentException("The embedded helper manifest is invalid.", nameof(expectedManifestBytes));
        this.expectedManifestBytes = expectedManifestBytes.ToArray();
        this.limits = limits ?? ManagedBrowserHelperIntegrityLimits.Default;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.orphanMinimumAge = orphanMinimumAge ?? TimeSpan.FromHours(12);
        this.snapshotParent = Path.GetFullPath(
            snapshotParent ?? VerifiedHelperSnapshotLease.SnapshotParentForDiagnostics);
        this.snapshotCreationObserver = snapshotCreationObserver;
        this.snapshotFileCopiedObserver = snapshotFileCopiedObserver;
        this.cleanupAttemptObserver = cleanupAttemptObserver;
        this.snapshotCleanupObserver = snapshotCleanupObserver;
        this.snapshotCopyHandoffObserver = snapshotCopyHandoffObserver;
        if (this.limits.MaximumTreeEntries < 1 || this.limits.MaximumTreeDepth < 1 ||
            this.limits.MaximumTreeBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (this.orphanMinimumAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(orphanMinimumAge));
    }

    public Task<VerifiedManagedBrowserHelperBundle> VerifyAsync(
        string helperRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(helperRoot))
            throw new ArgumentException("A helper root is required.", nameof(helperRoot));
        return Task.Run(() => Verify(helperRoot, cancellationToken), cancellationToken);
    }

    private VerifiedManagedBrowserHelperBundle Verify(
        string helperRoot,
        CancellationToken cancellationToken)
    {
        Manifest manifest;
        try
        {
            manifest = ParseManifest(expectedManifestBytes);
        }
        catch (ManagedBrowserHelperIntegrityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or OverflowException)
        {
            throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
        }

        PinnedHelperTree? sourceTree = null;
        VerifiedHelperSnapshotLease? snapshot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifiedHelperSnapshotLease.RecoverExpiredOrphans(
                snapshotParent, limits, timeProvider.GetUtcNow(), orphanMinimumAge, cancellationToken);
            sourceTree = PinnedHelperTree.Capture(helperRoot, limits, cancellationToken);
            VerifyPinnedTree(sourceTree, manifest, cancellationToken);

            snapshot = VerifiedHelperSnapshotLease.Create(
                snapshotParent, sourceTree, manifest.BuildIdentity, limits, timeProvider.GetUtcNow(),
                cancellationToken, snapshotCreationObserver, snapshotFileCopiedObserver,
                cleanupAttemptObserver, snapshotCleanupObserver, snapshotCopyHandoffObserver);
            sourceTree.Dispose();
            sourceTree = null;
            VerifyPinnedTree(snapshot.Tree, manifest, cancellationToken);
            snapshot.EnsureReadOnlyAcl();

            var executable = snapshot.Tree.Files[ExecutableFileName].FullPath;
            var mainScript = snapshot.Tree.Files[MainScriptFileName].FullPath;
            var result = new VerifiedManagedBrowserHelperBundle(
                snapshot, executable, mainScript, manifest.BuildIdentity);
            snapshot = null;
            return result;
        }
        catch (ManagedBrowserHelperIntegrityException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnsafeHelperFilesystemException)
        {
            throw Failure(ManagedBrowserHelperIntegrityError.UnsafeFilesystemEntry);
        }
        catch (HelperResourceLimitException)
        {
            throw Failure(ManagedBrowserHelperIntegrityError.ResourceLimitExceeded);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception or OverflowException)
        {
            throw Failure(ManagedBrowserHelperIntegrityError.VerificationIoFailure);
        }
        finally
        {
            sourceTree?.Dispose();
            snapshot?.Dispose();
        }
    }

    private void VerifyPinnedTree(
        PinnedHelperTree tree,
        Manifest manifest,
        CancellationToken cancellationToken)
    {
        if (!tree.Files.Keys.Contains(ManifestFileName, StringComparer.Ordinal) ||
            !tree.Files.TryGetValue(ManifestFileName, out var manifestEntry))
            throw Failure(ManagedBrowserHelperIntegrityError.MissingManifest);
        var externalBytes = manifestEntry.ReadAllBytes(MaximumManifestBytes, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(externalBytes, expectedManifestBytes))
            throw Failure(ManagedBrowserHelperIntegrityError.ManifestMismatch);

        VerifyFileSet(tree, manifest);
        long totalBytes = 0;
        foreach (var expected in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tree.Files.TryGetValue(expected.Path, out var actual))
                throw Failure(ManagedBrowserHelperIntegrityError.MissingPayload);
            totalBytes = checked(totalBytes + expected.Length);
            if (totalBytes > MaximumPayloadBytes || actual.Length != expected.Length)
                throw Failure(ManagedBrowserHelperIntegrityError.PayloadMismatch);
            var hash = actual.ComputeSha256(MaximumPayloadFileBytes, cancellationToken);
            if (!string.Equals(hash, expected.Sha256, StringComparison.Ordinal))
                throw Failure(ManagedBrowserHelperIntegrityError.PayloadMismatch);
            actual.EnsureUnchanged();
        }
        tree.EnsureDirectoriesUnchanged();
        tree.EnsureEnumerationUnchanged(cancellationToken);
    }

    private static void VerifyFileSet(PinnedHelperTree tree, Manifest manifest)
    {
        var expected = manifest.Files.Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        var actualFiles = tree.Files.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var required in RequiredFiles)
        {
            if (!expected.Contains(required))
                throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
        }
        foreach (var path in expected)
        {
            if (!actualFiles.Contains(path))
                throw Failure(ManagedBrowserHelperIntegrityError.MissingPayload);
        }
        foreach (var actual in tree.Files.Keys)
        {
            if (string.Equals(actual, ManifestFileName, StringComparison.Ordinal))
                continue;
            if (!expected.Contains(actual))
                throw Failure(ManagedBrowserHelperIntegrityError.UnlistedPayload);
        }

        var requiredDirectories = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        foreach (var path in expected)
        {
            var slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                requiredDirectories.Add(path[..slash]);
                slash = path.LastIndexOf('/', slash - 1);
            }
        }
        foreach (var directory in tree.Directories.Keys)
        {
            if (!requiredDirectories.Contains(directory))
                throw Failure(ManagedBrowserHelperIntegrityError.UnlistedPayload);
        }
    }

    private static Manifest ParseManifest(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumManifestBytes || bytes[^1] != (byte)'\n' ||
            bytes.AsSpan().IndexOf([(byte)'\r']) >= 0 ||
            (bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(
                new byte[] { 0xef, 0xbb, 0xbf })))
        {
            throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
        }
        _ = StrictUtf8.GetString(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        var root = document.RootElement;
        RequireProperties(root,
            "manifestVersion", "protocolVersion", "executionEngineVersion", "pythonVersion",
            "distributionMode", "buildIdentity", "files");
        if (RequireInt32(root, "manifestVersion") != ManifestVersion)
            throw Failure(ManagedBrowserHelperIntegrityError.IncompatibleVersion);
        if (RequireInt32(root, "protocolVersion") != ManagedBrowserProtocol.ProtocolVersion ||
            !string.Equals(RequireString(root, "executionEngineVersion"),
                ManagedBrowserProtocol.ExecutionEngineVersion, StringComparison.Ordinal) ||
            !string.Equals(RequireString(root, "pythonVersion"), RequiredPythonVersion,
                StringComparison.Ordinal))
        {
            throw Failure(ManagedBrowserHelperIntegrityError.IncompatibleVersion);
        }
        if (!string.Equals(RequireString(root, "distributionMode"), RequiredDistributionMode,
                StringComparison.Ordinal))
            throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
        var buildIdentity = RequireLowerHex(RequireString(root, "buildIdentity"));
        var filesElement = root.GetProperty("files");
        if (filesElement.ValueKind != JsonValueKind.Array ||
            filesElement.GetArrayLength() is < 1 or > MaximumManifestFiles)
            throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ManifestFile>(filesElement.GetArrayLength());
        string? previousPath = null;
        foreach (var element in filesElement.EnumerateArray())
        {
            RequireProperties(element, "path", "length", "sha256");
            var path = RequireString(element, "path");
            if (!IsSafeRelativePath(path) ||
                string.Equals(path, ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                !paths.Add(path) ||
                (previousPath is not null && StringComparer.Ordinal.Compare(previousPath, path) >= 0))
                throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
            if (!element.TryGetProperty("length", out var lengthElement) ||
                !lengthElement.TryGetInt64(out var length) || length < 0 ||
                length > MaximumPayloadFileBytes)
                throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
            files.Add(new ManifestFile(path, length,
                RequireLowerHex(RequireString(element, "sha256"))));
            previousPath = path;
        }
        var manifest = new Manifest(buildIdentity, files);
        if (!string.Equals(buildIdentity, ComputeBuildIdentity(manifest), StringComparison.Ordinal))
            throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
        return manifest;
    }

    private static string ComputeBuildIdentity(Manifest manifest)
    {
        var builder = new StringBuilder();
        builder.Append("manifestVersion=").Append(ManifestVersion).Append('\n')
            .Append("protocolVersion=").Append(ManagedBrowserProtocol.ProtocolVersion).Append('\n')
            .Append("executionEngineVersion=").Append(ManagedBrowserProtocol.ExecutionEngineVersion).Append('\n')
            .Append("pythonVersion=").Append(RequiredPythonVersion).Append('\n')
            .Append("distributionMode=").Append(RequiredDistributionMode).Append('\n');
        foreach (var file in manifest.Files)
            builder.Append(file.Path).Append('\0').Append(file.Length).Append('\0')
                .Append(file.Sha256).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path != path.Trim() ||
            path.Any(character => character < 0x20 || character == 0x7f) || path.Contains('\\') ||
            path.Contains(':') || path.Contains('\0') || path.StartsWith('/') || path.EndsWith('/') ||
            Path.IsPathRooted(path))
            return false;
        var segments = path.Split('/');
        return segments.All(segment => segment.Length is > 0 and <= 255 &&
            segment is not "." and not ".." && !segment.EndsWith(' ') &&
            !segment.EndsWith('.') && !IsDosDeviceName(segment));
    }

    private static bool IsDosDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
            "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
        var properties = element.EnumerateObject().ToArray();
        if (properties.Length != names.Length)
            throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
        for (var index = 0; index < names.Length; index++)
        {
            if (!string.Equals(properties[index].Name, names[index], StringComparison.Ordinal))
                throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
        }
    }

    private static int RequireInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);

    private static string RequireString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } result
            ? result
            : throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);

    private static string RequireLowerHex(string value)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Failure(ManagedBrowserHelperIntegrityError.InvalidManifest);
        return value;
    }

    private static byte[] ReadEmbeddedManifest()
    {
        using var stream = typeof(ManagedBrowserHelperIntegrityVerifier).Assembly
            .GetManifestResourceStream(EmbeddedManifestResourceName)
            ?? throw new InvalidOperationException("The managed browser helper manifest is not embedded.");
        if (stream.Length is < 1 or > MaximumManifestBytes)
            throw new InvalidOperationException("The embedded managed browser helper manifest is invalid.");
        using var memory = new MemoryStream((int)stream.Length);
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static ManagedBrowserHelperIntegrityException Failure(
        ManagedBrowserHelperIntegrityError error) => new(error);

    private sealed record Manifest(string BuildIdentity, IReadOnlyList<ManifestFile> Files);
    private sealed record ManifestFile(string Path, long Length, string Sha256);
}

internal sealed class PinnedHelperTree : IDisposable
{
    private readonly List<PinnedHelperEntry> entries = [];
    private readonly Dictionary<string, PinnedHelperEntry> files =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PinnedHelperEntry> directories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ManagedBrowserHelperIntegrityLimits limits;
    private long totalBytes;
    private bool disposed;

    private PinnedHelperTree(string rootPath, ManagedBrowserHelperIntegrityLimits limits)
    {
        RootPath = rootPath;
        this.limits = limits;
    }

    public string RootPath { get; }
    public IReadOnlyDictionary<string, PinnedHelperEntry> Files => files;
    public IReadOnlyDictionary<string, PinnedHelperEntry> Directories => directories;
    public int HandleCount => entries.Count;

    public static PinnedHelperTree Capture(
        string root,
        ManagedBrowserHelperIntegrityLimits limits,
        CancellationToken cancellationToken,
        bool deleteCapable = false,
        FileShare? shareMode = null,
        bool writeDac = false,
        Action? beforeRootOpen = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Managed browser helper verification requires Windows.");
        var canonical = Path.GetFullPath(root);
        if (!Path.IsPathFullyQualified(canonical) || !Directory.Exists(canonical))
            throw new IOException("Managed browser helper root is unavailable.");
        var tree = new PinnedHelperTree(canonical, limits);
        try
        {
            beforeRootOpen?.Invoke();
            var rootEntry = PinnedHelperEntry.Open(
                canonical, deleteCapable, writeDac, shareMode);
            tree.entries.Add(rootEntry);
            if (!rootEntry.IsDirectory)
                throw new UnsafeHelperFilesystemException();
            tree.directories.Add(string.Empty, rootEntry);
            var pending = new Queue<(string Relative, string Full)>();
            pending.Enqueue((string.Empty, canonical));
            var entryCount = 0;
            long totalBytes = 0;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Dequeue();
                foreach (var candidate in Directory.EnumerateFileSystemEntries(current.Full, "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        AttributesToSkip = 0,
                        ReturnSpecialDirectories = false,
                    }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(candidate);
                    if (string.IsNullOrEmpty(name) || name is "." or "..")
                        throw new UnsafeHelperFilesystemException();
                    var relative = string.IsNullOrEmpty(current.Relative)
                        ? name
                        : $"{current.Relative}/{name}";
                    entryCount = checked(entryCount + 1);
                    var depth = relative.Count(character => character == '/') + 1;
                    if (entryCount > limits.MaximumTreeEntries || depth > limits.MaximumTreeDepth)
                        throw new HelperResourceLimitException();
                    var entry = PinnedHelperEntry.Open(
                        candidate, deleteCapable, writeDac, shareMode);
                    tree.entries.Add(entry);
                    if (entry.IsDirectory)
                    {
                        if (!tree.directories.TryAdd(relative, entry))
                            throw new IOException("Duplicate helper directory.");
                        pending.Enqueue((relative, candidate));
                    }
                    else
                    {
                        totalBytes = checked(totalBytes + entry.Length);
                        if (totalBytes > limits.MaximumTreeBytes)
                            throw new HelperResourceLimitException();
                        if (!tree.files.TryAdd(relative, entry))
                            throw new IOException("Duplicate helper file.");
                    }
                }
            }
            tree.totalBytes = totalBytes;
            return tree;
        }
        catch
        {
            tree.Dispose();
            throw;
        }
    }

    public static PinnedHelperTree CreateRelative(
        PinnedHelperEntry parent,
        string name,
        ManagedBrowserHelperIntegrityLimits limits,
        SecurityIdentifier user,
        Action? beforeRootAcl = null)
    {
        var root = Path.GetFullPath(Path.Combine(parent.FullPath, name));
        var tree = new PinnedHelperTree(root, limits);
        try
        {
            var rootEntry = PinnedHelperEntry.OpenRelativeDirectory(
                parent, name, createIfMissing: false, deleteCapable: false,
                shareMode: FileShare.Read, createNew: true);
            tree.entries.Add(rootEntry);
            tree.directories.Add(string.Empty, rootEntry);
            beforeRootAcl?.Invoke();
            rootEntry.ApplyWritableAcl(user, inheritToChildren: true);
            parent.FlushDirectory();
            return tree;
        }
        catch
        {
            tree.Dispose();
            throw;
        }
    }

    public void CreateRelativeDirectory(string relative, SecurityIdentifier user)
    {
        ThrowIfInvalidNewRelative(relative);
        EnforceNextEntryLimits(relative, 0);
        var (parentRelative, name) = SplitRelative(relative);
        if (!directories.TryGetValue(parentRelative, out var parent))
            throw new UnsafeHelperFilesystemException();
        var entry = PinnedHelperEntry.OpenRelativeDirectory(
            parent, name, createIfMissing: false, deleteCapable: false,
            shareMode: FileShare.Read, createNew: true);
        var treeOwnsEntry = false;
        try
        {
            entry.ApplyWritableAcl(user, inheritToChildren: true);
            if (!directories.TryAdd(relative, entry))
                throw new UnsafeHelperFilesystemException();
            entries.Add(entry);
            treeOwnsEntry = true;
            parent.FlushDirectory();
        }
        catch
        {
            if (!treeOwnsEntry) entry.Dispose();
            throw;
        }
    }

    public string CopyRelativeFile(
        PinnedHelperEntry source,
        string relative,
        SecurityIdentifier user,
        CancellationToken cancellationToken,
        Action<string>? handoffObserver = null)
    {
        ThrowIfInvalidNewRelative(relative);
        EnforceNextEntryLimits(relative, source.Length);
        var (parentRelative, name) = SplitRelative(relative);
        if (!directories.TryGetValue(parentRelative, out var parent))
            throw new UnsafeHelperFilesystemException();
        var entry = PinnedHelperEntry.CreateRelativeCopy(
            parent, name, source, cancellationToken, handoffObserver);
        var treeOwnsEntry = false;
        try
        {
            entry.ApplyWritableAcl(user);
            if (!files.TryAdd(relative, entry))
                throw new UnsafeHelperFilesystemException();
            entries.Add(entry);
            treeOwnsEntry = true;
            totalBytes = checked(totalBytes + entry.Length);
            parent.FlushDirectory();
            return entry.FullPath;
        }
        catch
        {
            if (!treeOwnsEntry) entry.Dispose();
            throw;
        }
    }

    private void EnforceNextEntryLimits(string relative, long length)
    {
        var nextCount = checked(files.Count + directories.Count);
        var depth = relative.Count(character => character == '/') + 1;
        var nextBytes = checked(totalBytes + length);
        if (nextCount > limits.MaximumTreeEntries || depth > limits.MaximumTreeDepth ||
            nextBytes > limits.MaximumTreeBytes)
            throw new HelperResourceLimitException();
    }

    private static (string Parent, string Name) SplitRelative(string relative)
    {
        var separator = relative.LastIndexOf('/');
        return separator < 0
            ? (string.Empty, relative)
            : (relative[..separator], relative[(separator + 1)..]);
    }

    private static void ThrowIfInvalidNewRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') ||
            relative.EndsWith('/') || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." || segment.Any(char.IsControl)))
            throw new UnsafeHelperFilesystemException();
    }

    public void EnsureDirectoriesUnchanged()
    {
        foreach (var directory in Directories.Values) directory.EnsureUnchanged();
    }

    public void ApplyReadOnlyAcl(SecurityIdentifier user)
    {
        foreach (var entry in Files.Values) entry.ApplyReadOnlyAcl(user);
        foreach (var entry in Directories.Values.OrderByDescending(entry => entry.FullPath.Length))
            entry.ApplyReadOnlyAcl(user);
    }

    public void ApplyWritableAcl(SecurityIdentifier user)
    {
        foreach (var entry in Files.Values) entry.ApplyWritableAcl(user);
        foreach (var entry in Directories.Values.OrderByDescending(entry => entry.FullPath.Length))
            entry.ApplyWritableAcl(user);
    }

    public void EnsureReadOnlyAcl(SecurityIdentifier user)
    {
        foreach (var entry in Files.Values) entry.EnsureReadOnlyAcl(user);
        foreach (var entry in Directories.Values) entry.EnsureReadOnlyAcl(user);
    }

    public void EnsureEnumerationUnchanged(CancellationToken cancellationToken)
    {
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };
        var pending = new Queue<(string Relative, string Full)>();
        pending.Enqueue((string.Empty, RootPath));
        var entryCount = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            foreach (var candidate in Directory.EnumerateFileSystemEntries(current.Full, "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false,
                }))
            {
                var name = Path.GetFileName(candidate);
                var relative = string.IsNullOrEmpty(current.Relative)
                    ? name
                    : $"{current.Relative}/{name}";
                entryCount = checked(entryCount + 1);
                var depth = relative.Count(character => character == '/') + 1;
                if (entryCount > limits.MaximumTreeEntries || depth > limits.MaximumTreeDepth)
                    throw new HelperResourceLimitException();
                if (Directories.ContainsKey(relative))
                {
                    if (!seenDirectories.Add(relative))
                        throw new IOException("Helper directory enumeration changed.");
                    pending.Enqueue((relative, candidate));
                }
                else if (Files.ContainsKey(relative))
                {
                    if (!seenFiles.Add(relative))
                        throw new IOException("Helper file enumeration changed.");
                }
                else
                {
                    throw new IOException("Helper filesystem contents changed.");
                }
            }
        }
        if (seenFiles.Count != Files.Count || seenDirectories.Count != Directories.Count)
            throw new IOException("Helper filesystem contents changed.");
    }

    public void DeletePinnedTree()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PinnedHelperTree));
        foreach (var entry in Files.OrderByDescending(pair => pair.Key.Length)
                     .Select(pair => pair.Value))
            entry.MarkDeleteAndDispose();
        foreach (var entry in Directories.OrderByDescending(pair =>
                     pair.Key.Count(character => character == '/'))
                     .ThenByDescending(pair => pair.Key.Length)
                     .Select(pair => pair.Value))
            entry.MarkDeleteAndDispose();
        disposed = true;
        entries.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (var index = entries.Count - 1; index >= 0; index--) entries[index].Dispose();
        entries.Clear();
    }
}

internal sealed class VerifiedHelperSnapshotLease : IDisposable
{
    private const int OwnershipMarkerVersion = 2;
    private const int MaximumOrphanCandidates = 128;
    private const int MaximumOrphanCursorParents = 64;
    private const string MarkerFileName = "ownership.json";
    private const string ActiveLockFileName = "active.lock";
    private const string ReadyMarkerFileName = "ownership.ready.json";
    private const string PayloadDirectoryName = "payload";
    private const string IncompleteState = "Incomplete";
    private const string ReadyPreparingState = "Ready0";
    private const string ReadyState = "Ready1";
    private static readonly string DefaultSnapshotParent = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BaiduBatchTransfer", "verified-helper-snapshots"));
    internal const int MaximumDeferredLeaseCount = 4;
    internal const int MaximumDeferredHandleCount = 4096;
    internal const int MaximumDeferredCleanupAttempts = 3;
    private static readonly ConcurrentQueue<VerifiedHelperSnapshotLease> DeferredCleanup = new();
    private static readonly object DeferredCleanupSync = new();
    private static readonly object OrphanCursorSync = new();
    private static readonly Dictionary<string, int> OrphanCursorOffsets =
        new(StringComparer.OrdinalIgnoreCase);
    private static int deferredLeaseCount;
    private static int deferredHandleCount;

    private readonly object sync = new();
    private readonly string containerPath;
    private readonly string snapshotParent;
    private readonly string rootPath;
    private readonly uint containerVolume;
    private readonly string containerFileId;
    private readonly SecurityIdentifier user;
    private readonly ManagedBrowserHelperIntegrityLimits limits;
    private readonly Action? cleanupAttemptObserver;
    private readonly Action<ManagedBrowserHelperCleanupPhase>? cleanupObserver;
    private readonly ManagedBrowserHelperCleanupStatus cleanupStatus = new();
    private PinnedHelperEntry? parentFence;
    private PinnedHelperEntry? containerFence;
    private PinnedHelperEntry? markerFence;
    private PinnedHelperEntry? readyMarkerFence;
    private PinnedHelperEntry? activeLock;
    private PinnedHelperTree? tree;
    private IReadOnlyDictionary<string, (uint Volume, string FileId)>? cleanupFileIdentities;
    private IReadOnlyDictionary<string, (uint Volume, string FileId)>? cleanupDirectoryIdentities;
    private int cleanupAttempts;
    private int retainedDeferredHandleCount;
    private bool registeredForDeferredCleanup;

    private VerifiedHelperSnapshotLease(
        string snapshotParent,
        string containerPath,
        SecurityIdentifier user,
        PinnedHelperEntry parentFence,
        PinnedHelperEntry containerFence,
        PinnedHelperEntry markerFence,
        PinnedHelperEntry readyMarkerFence,
        PinnedHelperEntry activeLock,
        PinnedHelperTree tree,
        ManagedBrowserHelperIntegrityLimits limits,
        Action? cleanupAttemptObserver,
        Action<ManagedBrowserHelperCleanupPhase>? cleanupObserver)
    {
        this.snapshotParent = snapshotParent;
        this.containerPath = containerPath;
        rootPath = Path.Combine(containerPath, PayloadDirectoryName);
        containerVolume = containerFence.VolumeSerialNumber;
        containerFileId = containerFence.FileId;
        this.user = user;
        this.parentFence = parentFence;
        this.containerFence = containerFence;
        this.markerFence = markerFence;
        this.readyMarkerFence = readyMarkerFence;
        this.activeLock = activeLock;
        this.tree = tree;
        this.limits = limits;
        this.cleanupAttemptObserver = cleanupAttemptObserver;
        this.cleanupObserver = cleanupObserver;
    }

    internal static string SnapshotParentForDiagnostics => DefaultSnapshotParent;
    internal static int DeferredLeaseCountForDiagnostics => Volatile.Read(ref deferredLeaseCount);
    internal static int DeferredHandleCountForDiagnostics => Volatile.Read(ref deferredHandleCount);
    internal ManagedBrowserHelperCleanupStatus CleanupStatus => cleanupStatus;

    public PinnedHelperTree Tree => tree ??
        throw new ObjectDisposedException(nameof(VerifiedHelperSnapshotLease));

    public static VerifiedHelperSnapshotLease Create(
        string snapshotParent,
        PinnedHelperTree source,
        string buildIdentity,
        ManagedBrowserHelperIntegrityLimits limits,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken,
        Action<ManagedBrowserHelperSnapshotPhase>? observer,
        Action<string, string>? fileCopiedObserver,
        Action? cleanupAttemptObserver,
        Action<ManagedBrowserHelperCleanupPhase>? cleanupObserver,
        Action<string>? copyHandoffObserver)
    {
        var user = CurrentUser();
        var parentFence = PrepareSnapshotParent(snapshotParent, user);
        var snapshotId = Guid.NewGuid();
        var containerName = $"snapshot-{snapshotId:N}";
        var container = Path.Combine(snapshotParent, containerName);
        PinnedHelperEntry? containerFence = null;
        PinnedHelperEntry? markerFence = null;
        PinnedHelperEntry? activeLock = null;
        PinnedHelperEntry? readyMarkerFence = null;
        PinnedHelperEntry? readyCommitFence = null;
        PinnedHelperTree? pinned = null;
        try
        {
            containerFence = PinnedHelperEntry.OpenRelativeDirectory(
                parentFence, containerName, createIfMissing: true, deleteCapable: true);
            containerFence.ApplyWritableAcl(user, inheritToChildren: true);
            var incompleteMarker = new OwnershipMarker(
                OwnershipMarkerVersion, IncompleteState, snapshotId.ToString("N"),
                buildIdentity, createdUtc.ToUniversalTime(), containerFence.VolumeSerialNumber,
                containerFence.FileId, user.Value);
            using (PinnedHelperEntry.CreateRelativeFile(
                       containerFence, MarkerFileName, SerializeMarker(incompleteMarker),
                       FileShare.Read | FileShare.Write | FileShare.Delete, deleteCapable: true)) { }
            markerFence = PinnedHelperEntry.OpenRelativeFile(
                containerFence, MarkerFileName, deleteCapable: true,
                shareMode: FileShare.Read | FileShare.Delete);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.OwnershipPersisted);
            activeLock = PinnedHelperEntry.CreateRelativeFile(
                containerFence, ActiveLockFileName, [], FileShare.None, deleteCapable: true);
            pinned = PinnedHelperTree.CreateRelative(
                containerFence, PayloadDirectoryName, limits, user);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.ContainerCreated);
            foreach (var relative in source.Directories.Keys.Where(path => path.Length > 0)
                         .OrderBy(path => path.Count(character => character == '/'))
                         .ThenBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                pinned.CreateRelativeDirectory(relative, user);
            }
            foreach (var pair in source.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                observer?.Invoke(ManagedBrowserHelperSnapshotPhase.CopyingPayload);
                cancellationToken.ThrowIfCancellationRequested();
                var destination = pinned.CopyRelativeFile(
                    pair.Value, pair.Key, user, cancellationToken, copyHandoffObserver);
                fileCopiedObserver?.Invoke(pair.Key, destination);
            }
            pinned.EnsureEnumerationUnchanged(cancellationToken);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.MarkerWritten);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.PinningSnapshot);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.SealingAcl);
            var readyMarker = incompleteMarker with { State = ReadyPreparingState };
            var readyMarkerBytes = SerializeMarker(readyMarker);
            var readyCommitOffset = FindReadyCommitOffset(readyMarkerBytes);
            readyCommitFence = PinnedHelperEntry.CreateRelativeFile(
                containerFence, ReadyMarkerFileName, readyMarkerBytes,
                FileShare.Read | FileShare.Write | FileShare.Delete, deleteCapable: true);
            readyMarkerFence = PinnedHelperEntry.OpenRelativeFile(
                containerFence, ReadyMarkerFileName, deleteCapable: true,
                shareMode: FileShare.Read | FileShare.Write | FileShare.Delete);
            pinned.ApplyReadOnlyAcl(user);
            pinned.EnsureEnumerationUnchanged(cancellationToken);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.PayloadSealed);
            activeLock.ApplyReadOnlyAcl(user);
            markerFence.ApplyReadOnlyAcl(user);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.OwnershipSealed);
            readyMarkerFence.ApplyReadOnlyAcl(user);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.ReadyPreparingSealed);
            containerFence.ApplyReadOnlyAcl(user);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.ContainerSealed);
            containerFence.EnsureSameIdentity();
            readyCommitFence.WriteCommitByte(readyCommitOffset, (byte)'1');
            readyMarkerFence.RefreshAfterExpectedContentWrite();
            readyCommitFence.Dispose();
            readyCommitFence = null;
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.ReadyCommitted);
            var lease = new VerifiedHelperSnapshotLease(snapshotParent, container, user, parentFence,
                containerFence, markerFence, readyMarkerFence, activeLock, pinned, limits,
                cleanupAttemptObserver, cleanupObserver);
            parentFence = null!;
            containerFence = null;
            markerFence = null;
            readyMarkerFence = null;
            activeLock = null;
            pinned = null;
            lease.EnsureReadOnlyAcl();
            return lease;
        }
        catch
        {
            readyCommitFence?.Dispose();
            readyCommitFence = null;
            TryDeleteIncompleteSnapshot(
                containerFence, pinned, payloadFence: null, activeLock, markerFence,
                readyMarkerFence, user, limits);
            pinned?.Dispose();
            activeLock?.Dispose();
            markerFence?.Dispose();
            readyMarkerFence?.Dispose();
            containerFence?.Dispose();
            parentFence?.Dispose();
            throw;
        }
    }

    public void EnsureReadOnlyAcl()
    {
        Tree.EnsureReadOnlyAcl(user);
        markerFence?.EnsureReadOnlyAcl(user);
        readyMarkerFence?.EnsureReadOnlyAcl(user);
        activeLock?.EnsureReadOnlyAcl(user);
        containerFence?.EnsureReadOnlyAcl(user);
    }

    private static void TryDeleteIncompleteSnapshot(
        PinnedHelperEntry? containerFence,
        PinnedHelperTree? pinned,
        PinnedHelperEntry? payloadFence,
        PinnedHelperEntry? activeLock,
        PinnedHelperEntry? markerFence,
        PinnedHelperEntry? readyMarkerFence,
        SecurityIdentifier user,
        ManagedBrowserHelperIntegrityLimits limits)
    {
        if (containerFence is null) return;
        try
        {
            if (pinned is not null)
            {
                pinned.EnsureEnumerationUnchanged(CancellationToken.None);
                pinned.ApplyWritableAcl(user);
                var expectedFiles = pinned.Files.ToDictionary(
                    pair => pair.Key, pair => (pair.Value.VolumeSerialNumber, pair.Value.FileId),
                    StringComparer.Ordinal);
                var expectedDirectories = pinned.Directories.ToDictionary(
                    pair => pair.Key, pair => (pair.Value.VolumeSerialNumber, pair.Value.FileId),
                    StringComparer.Ordinal);
                pinned.Dispose();
                using var cleanupPayload = PinnedHelperTree.Capture(
                    pinned.RootPath, limits, CancellationToken.None, deleteCapable: true);
                EnsureExactIdentities(cleanupPayload.Files, expectedFiles);
                EnsureExactIdentities(cleanupPayload.Directories, expectedDirectories);
                cleanupPayload.DeletePinnedTree();
            }
            else if (payloadFence is not null)
            {
                payloadFence.ApplyWritableAcl(user);
                payloadFence.MarkDeleteAndDispose();
            }
            activeLock?.ApplyWritableAcl(user);
            activeLock?.MarkDeleteAndDispose();
            readyMarkerFence?.ApplyWritableAcl(user);
            readyMarkerFence?.MarkDeleteAndDispose();
            markerFence?.ApplyWritableAcl(user);
            markerFence?.MarkDeleteAndDispose();
            containerFence.ApplyWritableAcl(user);
            containerFence.MarkDeleteAndDispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           Win32Exception or OverflowException or
                                           System.Security.SecurityException or ArgumentException)
        {
            // A valid marker is preferred for later recovery, but creation failures before the
            // marker are still cleaned exactly when their pinned identity remains available.
        }
    }

    private static ManagedBrowserHelperIntegrityLimits ContainerLimits(
        ManagedBrowserHelperIntegrityLimits sourceLimits) => new(
        checked(sourceLimits.MaximumTreeEntries + 8),
        checked(sourceLimits.MaximumTreeDepth + 2),
        checked(sourceLimits.MaximumTreeBytes + 16 * 1024));

    private static void EnsureSameIdentity(PinnedHelperEntry actual, PinnedHelperEntry expected)
    {
        if (actual.VolumeSerialNumber != expected.VolumeSerialNumber ||
            !string.Equals(actual.FileId, expected.FileId, StringComparison.Ordinal))
            throw new UnsafeHelperFilesystemException();
    }

    public void Dispose() => _ = Release();

    public ManagedBrowserHelperCleanupState Release()
    {
        lock (sync)
        {
            if (cleanupStatus.State != ManagedBrowserHelperCleanupState.Active)
                return cleanupStatus.State;
            try
            {
                cleanupAttemptObserver?.Invoke();
                CleanupExact();
                cleanupStatus.Set(ManagedBrowserHelperCleanupState.Cleaned);
            }
            catch
            {
                cleanupAttempts = 1;
                cleanupStatus.Set(ManagedBrowserHelperCleanupState.DeferredCleanupRegistered);
                if (!TryRegisterDeferredCleanup()) ReleaseHandlesForOrphanRecovery();
            }
            return cleanupStatus.State;
        }
    }

    internal void AbandonForProcessCrashSimulation()
    {
        lock (sync)
        {
            cleanupStatus.SetOrphanRecoveryPending(
                containerPath, containerVolume, containerFileId);
            tree?.Dispose(); tree = null;
            activeLock?.Dispose(); activeLock = null;
            markerFence?.Dispose(); markerFence = null;
            readyMarkerFence?.Dispose(); readyMarkerFence = null;
            containerFence?.Dispose(); containerFence = null;
            parentFence?.Dispose(); parentFence = null;
        }
    }

    public static void RecoverExpiredOrphans(
        string snapshotParent,
        ManagedBrowserHelperIntegrityLimits limits,
        DateTimeOffset now,
        TimeSpan minimumAge,
        CancellationToken cancellationToken)
    {
        RetryDeferredCleanup();
        var user = CurrentUser();
        using var parent = PrepareSnapshotParent(snapshotParent, user);
        foreach (var candidate in TakeNextOrphanCandidateWindow(snapshotParent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryRecoverOrphan(snapshotParent, candidate, limits, user, now, minimumAge, cancellationToken);
        }
        parent.EnsureSameIdentity();
    }

    private static IReadOnlyList<string> TakeNextOrphanCandidateWindow(string snapshotParent)
    {
        int offset;
        lock (OrphanCursorSync)
            offset = OrphanCursorOffsets.TryGetValue(snapshotParent, out var stored) ? stored : 0;

        static IEnumerator<string> Open(string parent) => Directory
            .EnumerateDirectories(parent, "snapshot-*", SearchOption.TopDirectoryOnly)
            .GetEnumerator();

        var window = new List<string>(MaximumOrphanCandidates);
        var enumerator = Open(snapshotParent);
        try
        {
            var skipped = 0;
            while (skipped < offset && enumerator.MoveNext()) skipped++;
            if (skipped < offset)
            {
                enumerator.Dispose();
                enumerator = Open(snapshotParent);
                offset = 0;
            }
            while (window.Count < MaximumOrphanCandidates && enumerator.MoveNext())
                window.Add(enumerator.Current);
        }
        finally
        {
            enumerator.Dispose();
        }

        lock (OrphanCursorSync)
        {
            if (!OrphanCursorOffsets.ContainsKey(snapshotParent) &&
                OrphanCursorOffsets.Count >= MaximumOrphanCursorParents)
                OrphanCursorOffsets.Remove(OrphanCursorOffsets.Keys.First());
            OrphanCursorOffsets[snapshotParent] = window.Count < MaximumOrphanCandidates
                ? 0
                : checked(offset + window.Count);
        }
        return window;
    }

    private static void RetryDeferredCleanup()
    {
        var attempts = DeferredCleanup.Count;
        while (attempts-- > 0 && DeferredCleanup.TryDequeue(out var lease))
        {
            lock (lease.sync)
            {
                try
                {
                    lease.cleanupAttemptObserver?.Invoke();
                    lease.CleanupExact();
                    lease.UnregisterDeferredCleanup();
                    lease.cleanupStatus.Set(ManagedBrowserHelperCleanupState.Cleaned);
                }
                catch
                {
                    lease.cleanupAttempts++;
                    if (lease.cleanupAttempts >= MaximumDeferredCleanupAttempts)
                    {
                        lease.UnregisterDeferredCleanup();
                        lease.ReleaseHandlesForOrphanRecovery();
                    }
                    else
                    {
                        DeferredCleanup.Enqueue(lease);
                    }
                }
            }
        }
    }

    private bool TryRegisterDeferredCleanup()
    {
        var handleCount = (tree?.HandleCount ?? 0) +
            (parentFence is null ? 0 : 1) + (containerFence is null ? 0 : 1) +
            (markerFence is null ? 0 : 1) + (readyMarkerFence is null ? 0 : 1) +
            (activeLock is null ? 0 : 1);
        lock (DeferredCleanupSync)
        {
            if (deferredLeaseCount >= MaximumDeferredLeaseCount ||
                handleCount > MaximumDeferredHandleCount - deferredHandleCount)
                return false;
            retainedDeferredHandleCount = handleCount;
            registeredForDeferredCleanup = true;
            deferredLeaseCount++;
            deferredHandleCount += handleCount;
            DeferredCleanup.Enqueue(this);
            return true;
        }
    }

    private void UnregisterDeferredCleanup()
    {
        lock (DeferredCleanupSync)
        {
            if (!registeredForDeferredCleanup) return;
            registeredForDeferredCleanup = false;
            deferredLeaseCount--;
            deferredHandleCount -= retainedDeferredHandleCount;
            retainedDeferredHandleCount = 0;
        }
    }

    private void ReleaseHandlesForOrphanRecovery()
    {
        try
        {
            containerFence?.EnsureSameIdentity();
            tree?.ApplyReadOnlyAcl(user);
            activeLock?.ApplyReadOnlyAcl(user);
            markerFence?.ApplyReadOnlyAcl(user);
            readyMarkerFence?.ApplyReadOnlyAcl(user);
            containerFence?.ApplyReadOnlyAcl(user);
            containerFence?.EnsureSameIdentity();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           Win32Exception or OverflowException or
                                           System.Security.SecurityException or ArgumentException)
        {
            // Recovery will leave candidates with invalid or incomplete boundaries untouched.
        }
        cleanupStatus.SetOrphanRecoveryPending(containerPath, containerVolume, containerFileId);
        tree?.Dispose(); tree = null;
        activeLock?.Dispose(); activeLock = null;
        markerFence?.Dispose(); markerFence = null;
        readyMarkerFence?.Dispose(); readyMarkerFence = null;
        containerFence?.Dispose(); containerFence = null;
        parentFence?.Dispose(); parentFence = null;
    }

    private static void TryRecoverOrphan(
        string snapshotParent,
        string container,
        ManagedBrowserHelperIntegrityLimits limits,
        SecurityIdentifier user,
        DateTimeOffset now,
        TimeSpan minimumAge,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsOwnedSnapshotBoundary(snapshotParent, container) ||
                (File.GetAttributes(container) & FileAttributes.ReparsePoint) != 0) return;
            using var containerFence = PinnedHelperEntry.Open(
                container, shareMode: FileShare.Read | FileShare.Delete);
            if (!containerFence.IsDirectory || !containerFence.HasProtectedOwnerAcl(user)) return;
            var activeMissing = false;
            try
            {
                using var activeProbe = PinnedHelperEntry.Open(
                    Path.Combine(container, ActiveLockFileName), shareMode: FileShare.None);
                if (activeProbe.IsDirectory) return;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
            {
                activeMissing = true;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 32 or 33)
            {
                return;
            }

            var containerLimits = ContainerLimits(limits);
            using (var observed = PinnedHelperTree.Capture(
                       container, containerLimits, cancellationToken, writeDac: true))
            {
                EnsureSameIdentity(observed.Directories[string.Empty], containerFence);
                if (!observed.Files.TryGetValue(MarkerFileName, out var markerEntry) ||
                    observed.Directories.ContainsKey(MarkerFileName)) return;
                var marker = ReadMarker(markerEntry, cancellationToken);
                if (marker is null || marker.Version != OwnershipMarkerVersion ||
                    !string.Equals(marker.State, IncompleteState, StringComparison.Ordinal) ||
                    !string.Equals(marker.SnapshotId, Path.GetFileName(container)[9..],
                        StringComparison.Ordinal) ||
                    marker.VolumeSerialNumber != containerFence.VolumeSerialNumber ||
                    !string.Equals(marker.FileId, containerFence.FileId, StringComparison.Ordinal) ||
                    !string.Equals(marker.OwnerSid, user.Value, StringComparison.Ordinal) ||
                    !IsLowerHex64(marker.BuildIdentity) ||
                    now - marker.CreatedUtc < minimumAge) return;
                if (observed.Directories.ContainsKey(ReadyMarkerFileName)) return;
                var hasReady = observed.Files.TryGetValue(ReadyMarkerFileName, out var readyEntry);
                var readyMarker = hasReady ? ReadMarker(readyEntry!, cancellationToken) : null;
                if (hasReady &&
                    (readyMarker is null || !MarkersDescribeSameSnapshot(marker, readyMarker) ||
                     (!string.Equals(readyMarker.State, ReadyPreparingState, StringComparison.Ordinal) &&
                      !string.Equals(readyMarker.State, ReadyState, StringComparison.Ordinal)))) return;
                var hasActiveFile = observed.Files.ContainsKey(ActiveLockFileName);
                if (observed.Directories.ContainsKey(ActiveLockFileName) ||
                    activeMissing == hasActiveFile || (activeMissing && hasReady)) return;
                var isReady = string.Equals(readyMarker?.State, ReadyState, StringComparison.Ordinal);
                if (isReady)
                    observed.EnsureReadOnlyAcl(user);
                observed.ApplyWritableAcl(user);
                observed.EnsureEnumerationUnchanged(cancellationToken);
                containerFence.EnsureUnchanged();
            }
            using var cleanup = PinnedHelperTree.Capture(
                container, containerLimits, cancellationToken, deleteCapable: true);
            if (cleanup.Directories[string.Empty].VolumeSerialNumber != containerFence.VolumeSerialNumber ||
                !string.Equals(cleanup.Directories[string.Empty].FileId, containerFence.FileId,
                    StringComparison.Ordinal)) return;
            cleanup.DeletePinnedTree();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           Win32Exception or JsonException or OverflowException or
                                           System.Security.SecurityException or ArgumentException)
        {
            // Foreign, active, malformed, or racing candidates are intentionally left untouched.
        }
    }

    private static bool MarkersDescribeSameSnapshot(OwnershipMarker ownership, OwnershipMarker ready) =>
        ready.Version == ownership.Version &&
        string.Equals(ready.SnapshotId, ownership.SnapshotId, StringComparison.Ordinal) &&
        string.Equals(ready.BuildIdentity, ownership.BuildIdentity, StringComparison.Ordinal) &&
        ready.CreatedUtc == ownership.CreatedUtc &&
        ready.VolumeSerialNumber == ownership.VolumeSerialNumber &&
        string.Equals(ready.FileId, ownership.FileId, StringComparison.Ordinal) &&
        string.Equals(ready.OwnerSid, ownership.OwnerSid, StringComparison.Ordinal);

    private void CleanupExact()
    {
        parentFence?.EnsureSameIdentity();
        containerFence?.EnsureSameIdentity();
        if (tree is not null)
        {
            tree.EnsureEnumerationUnchanged(CancellationToken.None);
            tree.ApplyWritableAcl(user);
            cleanupFileIdentities = tree.Files.ToDictionary(
                pair => pair.Key,
                pair => (pair.Value.VolumeSerialNumber, pair.Value.FileId),
                StringComparer.Ordinal);
            cleanupDirectoryIdentities = tree.Directories.ToDictionary(
                pair => pair.Key,
                pair => (pair.Value.VolumeSerialNumber, pair.Value.FileId),
                StringComparer.Ordinal);
            tree.Dispose(); tree = null;
        }
        if (cleanupFileIdentities is not null && cleanupDirectoryIdentities is not null)
        {
            var cleanupTree = PinnedHelperTree.Capture(
                rootPath, limits, CancellationToken.None, deleteCapable: true);
            try
            {
                EnsureExactIdentities(cleanupTree.Files, cleanupFileIdentities);
                EnsureExactIdentities(cleanupTree.Directories, cleanupDirectoryIdentities);
                cleanupTree.DeletePinnedTree();
                cleanupFileIdentities = null;
                cleanupDirectoryIdentities = null;
            }
            catch
            {
                cleanupTree.Dispose();
                throw;
            }
        }
        if (activeLock is not null)
        {
            activeLock.ApplyWritableAcl(user);
            activeLock.MarkDeleteAndDispose(); activeLock = null;
            cleanupObserver?.Invoke(ManagedBrowserHelperCleanupPhase.ActiveLockDeleted);
        }
        if (readyMarkerFence is not null)
        {
            readyMarkerFence.ApplyWritableAcl(user);
            readyMarkerFence.MarkDeleteAndDispose();
            readyMarkerFence = null;
        }
        if (markerFence is not null)
        {
            markerFence.ApplyWritableAcl(user);
            markerFence.MarkDeleteAndDispose(); markerFence = null;
        }
        if (containerFence is not null)
        {
            containerFence.ApplyWritableAcl(user);
            containerFence.MarkDeleteAndDispose(); containerFence = null;
        }
        if (parentFence is not null)
        {
            parentFence.EnsureSameIdentity(); parentFence.Dispose(); parentFence = null;
        }
    }

    private static void EnsureExactIdentities(
        IReadOnlyDictionary<string, PinnedHelperEntry> actual,
        IReadOnlyDictionary<string, (uint Volume, string FileId)> expected)
    {
        if (actual.Count != expected.Count) throw new UnsafeHelperFilesystemException();
        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out var entry) ||
                entry.VolumeSerialNumber != pair.Value.Volume ||
                !string.Equals(entry.FileId, pair.Value.FileId, StringComparison.Ordinal))
                throw new UnsafeHelperFilesystemException();
        }
    }

    private static PinnedHelperEntry PrepareSnapshotParent(
        string snapshotParent,
        SecurityIdentifier user)
    {
        var missing = new Stack<string>();
        var finalName = Path.GetFileName(snapshotParent.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(finalName))
            throw new IOException("The helper snapshot parent is invalid.");
        missing.Push(finalName);
        var existing = Path.GetDirectoryName(snapshotParent.TrimEnd(Path.DirectorySeparatorChar)) ??
            throw new IOException("The helper snapshot parent is invalid.");
        while (!Directory.Exists(existing))
        {
            var name = Path.GetFileName(existing);
            if (string.IsNullOrEmpty(name)) throw new IOException("The helper snapshot parent is invalid.");
            missing.Push(name);
            existing = Path.GetDirectoryName(existing) ??
                throw new IOException("The helper snapshot parent is invalid.");
        }
        var current = PinnedHelperEntry.Open(existing);
        try
        {
            while (missing.TryPop(out var name))
            {
                var next = PinnedHelperEntry.OpenRelativeDirectory(current, name, createIfMissing: true);
                current.Dispose();
                current = next;
            }
            if (!string.Equals(current.FullPath.TrimEnd(Path.DirectorySeparatorChar),
                    snapshotParent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new UnsafeHelperFilesystemException();
            current.ApplyWritableAcl(user, inheritToChildren: true);
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static byte[] SerializeMarker(OwnershipMarker marker)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker);
        return [.. bytes, (byte)'\n'];
    }

    private static long FindReadyCommitOffset(byte[] bytes)
    {
        var needle = Encoding.UTF8.GetBytes($"\"State\":\"{ReadyPreparingState}\"");
        var index = bytes.AsSpan().IndexOf(needle);
        if (index < 0 || bytes.AsSpan(index + needle.Length).IndexOf(needle) >= 0)
            throw new InvalidOperationException("The helper ready marker commit field is invalid.");
        var offset = checked(index + needle.Length - 2);
        if (bytes[offset] != (byte)'0')
            throw new InvalidOperationException("The helper ready marker commit byte is invalid.");
        return offset;
    }

    private static OwnershipMarker? ReadMarker(
        PinnedHelperEntry entry,
        CancellationToken cancellationToken)
    {
        var bytes = entry.ReadAllBytes(4096, cancellationToken);
        if (bytes.Length < 2) return null;
        if (bytes[^1] != (byte)'\n') return null;
        return JsonSerializer.Deserialize<OwnershipMarker>(bytes.AsSpan(0, bytes.Length - 1));
    }

    private static bool IsOwnedSnapshotBoundary(string snapshotParent, string root)
    {
        var canonical = Path.GetFullPath(root);
        var parent = Path.GetDirectoryName(canonical);
        var name = Path.GetFileName(canonical);
        return string.Equals(parent, snapshotParent, StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith("snapshot-", StringComparison.Ordinal) && name.Length == 41 &&
            Guid.TryParseExact(name[9..], "N", out _);
    }

    private static bool IsLowerHex64(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is { } user
            ? new SecurityIdentifier(user.Value)
            : throw new UnauthorizedAccessException("The current Windows identity is unavailable.");
    }

    private sealed record OwnershipMarker(
        int Version,
        string State,
        string SnapshotId,
        string BuildIdentity,
        DateTimeOffset CreatedUtc,
        uint VolumeSerialNumber,
        string FileId,
        string OwnerSid);
}

internal sealed class PinnedHelperEntry : IDisposable
{
    private const FileSystemRights DeniedMutationRights = FileSystemRights.Write |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
    private const FileSystemRights AllowedRuntimeRights = FileSystemRights.ReadAndExecute |
        FileSystemRights.Synchronize;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileOpenIf = 3;
    private const uint FileCreate = 2;
    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileWriteThrough = 0x00000002;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint SeFileObject = 1;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private readonly SafeFileHandle handle;
    private ByHandleFileInformation original;

    private PinnedHelperEntry(
        string fullPath,
        SafeFileHandle handle,
        ByHandleFileInformation original,
        bool isDirectory)
    {
        FullPath = fullPath;
        this.handle = handle;
        this.original = original;
        IsDirectory = isDirectory;
        Length = isDirectory ? 0 : checked(((long)original.FileSizeHigh << 32) | original.FileSizeLow);
    }

    public string FullPath { get; }
    public bool IsDirectory { get; }
    public long Length { get; }
    public uint VolumeSerialNumber => original.VolumeSerialNumber;
    public string FileId => $"{original.FileIndexHigh:x8}{original.FileIndexLow:x8}";

    public static PinnedHelperEntry Open(
        string path,
        bool deleteCapable = false,
        bool writeDac = false,
        FileShare? shareMode = null)
    {
        var handle = CreateFileW(
            path,
            GenericRead | (deleteCapable ? DeleteAccess : 0) | (writeDac ? WriteDac : 0),
            shareMode ?? FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var actualDirectory = (info.FileAttributes & FileAttributeDirectory) != 0;
            if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
                throw new UnsafeHelperFilesystemException();
            var canonical = Path.GetFullPath(path);
            var finalPath = ReadFinalPath(handle);
            if (!string.Equals(canonical.TrimEnd(Path.DirectorySeparatorChar),
                    finalPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new UnsafeHelperFilesystemException();
            return new PinnedHelperEntry(canonical, handle, info, actualDirectory);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static PinnedHelperEntry OpenRelativeDirectory(
        PinnedHelperEntry parent,
        string name,
        bool createIfMissing,
        bool deleteCapable = false,
        FileShare? shareMode = null,
        bool createNew = false)
    {
        if (!parent.IsDirectory || string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.Any(char.IsControl) || name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar) || name.Contains(':'))
            throw new UnsafeHelperFilesystemException();
        using var relative = new RelativeName(name);
        var attributes = new ObjectAttributes
        {
            Length = Marshal.SizeOf<ObjectAttributes>(),
            RootDirectory = parent.handle.DangerousGetHandle(),
            ObjectName = relative.UnicodeStringPointer,
            Attributes = ObjCaseInsensitive,
        };
        var status = NtCreateFile(
            out var handle,
            FileReadAttributes | FileTraverse | ReadControl | WriteDac | Synchronize |
            (deleteCapable ? DeleteAccess : 0),
            ref attributes,
            out _,
            IntPtr.Zero,
            FileAttributeDirectory,
            (uint)(shareMode ?? (FileShare.Read | FileShare.Write | FileShare.Delete)),
            createNew ? FileCreate : createIfMissing ? FileOpenIf : FileOpen,
            FileDirectoryFile | FileSynchronousIoNonAlert | FileFlagOpenReparsePoint,
            IntPtr.Zero,
            0);
        if (status < 0)
        {
            handle?.Dispose();
            throw NtStatusException(status);
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.FileAttributes & FileAttributeDirectory) == 0 ||
                (info.FileAttributes & FileAttributeReparsePoint) != 0)
                throw new UnsafeHelperFilesystemException();
            var expected = Path.GetFullPath(Path.Combine(parent.FullPath, name));
            var finalPath = ReadFinalPath(handle);
            if (!string.Equals(expected.TrimEnd(Path.DirectorySeparatorChar),
                    finalPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new UnsafeHelperFilesystemException();
            return new PinnedHelperEntry(expected, handle, info, isDirectory: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static PinnedHelperEntry CreateRelativeCopy(
        PinnedHelperEntry parent,
        string name,
        PinnedHelperEntry source,
        CancellationToken cancellationToken,
        Action<string>? handoffObserver = null)
    {
        if (!parent.IsDirectory || source.IsDirectory || string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." || name.Any(char.IsControl) ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar) || name.Contains(':'))
            throw new UnsafeHelperFilesystemException();
        using var relative = new RelativeName(name);
        var attributes = new ObjectAttributes
        {
            Length = Marshal.SizeOf<ObjectAttributes>(),
            RootDirectory = parent.handle.DangerousGetHandle(),
            ObjectName = relative.UnicodeStringPointer,
            Attributes = ObjCaseInsensitive,
        };
        var status = NtCreateFile(
            out var handle,
            GenericRead | GenericWrite | ReadControl | WriteDac | Synchronize,
            ref attributes,
            out _,
            IntPtr.Zero,
            FileAttributeNormal,
            (uint)FileShare.Read,
            FileCreate,
            FileNonDirectoryFile | FileSynchronousIoNonAlert | FileFlagOpenReparsePoint |
            FileWriteThrough,
            IntPtr.Zero,
            0);
        if (status < 0)
        {
            handle?.Dispose();
            throw NtStatusException(status);
        }
        PinnedHelperEntry? temporaryReadFence = null;
        PinnedHelperEntry? finalReadFence = null;
        try
        {
            source.CopyToHandle(handle, cancellationToken);
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                throw new UnsafeHelperFilesystemException();
            var length = checked(((long)info.FileSizeHigh << 32) | info.FileSizeLow);
            if (length != source.Length)
                throw new IOException("Helper file changed while snapshotting.");
            var expected = Path.GetFullPath(Path.Combine(parent.FullPath, name));
            var finalPath = ReadFinalPath(handle);
            if (!string.Equals(expected, finalPath, StringComparison.OrdinalIgnoreCase))
                throw new UnsafeHelperFilesystemException();
            temporaryReadFence = OpenRelativeFile(
                parent, name, deleteCapable: false,
                shareMode: FileShare.Read | FileShare.Write);
            if (temporaryReadFence.VolumeSerialNumber != info.VolumeSerialNumber ||
                !string.Equals(temporaryReadFence.FileId,
                    $"{info.FileIndexHigh:x8}{info.FileIndexLow:x8}", StringComparison.Ordinal))
                throw new UnsafeHelperFilesystemException();
            // The original writer denies external writers until it closes. The temporary
            // read fence continuously denies replacement. A racing writer after this close
            // must make the final no-write-share open fail; there is deliberately no retry.
            handle.Dispose();
            handoffObserver?.Invoke(expected);
            finalReadFence = OpenRelativeFile(
                parent, name, deleteCapable: false, shareMode: FileShare.Read);
            if (finalReadFence.VolumeSerialNumber != temporaryReadFence.VolumeSerialNumber ||
                !string.Equals(finalReadFence.FileId, temporaryReadFence.FileId,
                    StringComparison.Ordinal) || finalReadFence.Length != source.Length)
                throw new UnsafeHelperFilesystemException();
            temporaryReadFence.Dispose();
            return finalReadFence;
        }
        catch
        {
            finalReadFence?.Dispose();
            temporaryReadFence?.Dispose();
            handle.Dispose();
            throw;
        }
    }

    public static PinnedHelperEntry CreateRelativeFile(
        PinnedHelperEntry parent,
        string name,
        ReadOnlySpan<byte> contents,
        FileShare shareMode,
        bool deleteCapable = false)
    {
        if (!parent.IsDirectory || string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.Any(char.IsControl) || name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar) || name.Contains(':'))
            throw new UnsafeHelperFilesystemException();
        using var relative = new RelativeName(name);
        var attributes = new ObjectAttributes
        {
            Length = Marshal.SizeOf<ObjectAttributes>(),
            RootDirectory = parent.handle.DangerousGetHandle(),
            ObjectName = relative.UnicodeStringPointer,
            Attributes = ObjCaseInsensitive,
        };
        var status = NtCreateFile(
            out var handle,
            GenericRead | GenericWrite | ReadControl | WriteDac | Synchronize |
            (deleteCapable ? DeleteAccess : 0),
            ref attributes,
            out _,
            IntPtr.Zero,
            FileAttributeNormal,
            (uint)shareMode,
            FileCreate,
            FileNonDirectoryFile | FileSynchronousIoNonAlert | FileFlagOpenReparsePoint |
            FileWriteThrough,
            IntPtr.Zero,
            0);
        if (status < 0)
        {
            handle?.Dispose();
            throw NtStatusException(status);
        }
        try
        {
            if (!contents.IsEmpty) RandomAccess.Write(handle, contents, 0);
            RandomAccess.FlushToDisk(handle);
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                throw new UnsafeHelperFilesystemException();
            var length = checked(((long)info.FileSizeHigh << 32) | info.FileSizeLow);
            if (length != contents.Length) throw new IOException("Helper marker write was incomplete.");
            var expected = Path.GetFullPath(Path.Combine(parent.FullPath, name));
            var finalPath = ReadFinalPath(handle);
            if (!string.Equals(expected, finalPath, StringComparison.OrdinalIgnoreCase))
                throw new UnsafeHelperFilesystemException();
            return new PinnedHelperEntry(expected, handle, info, isDirectory: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static PinnedHelperEntry OpenRelativeFile(
        PinnedHelperEntry parent,
        string name,
        bool deleteCapable = false,
        FileShare shareMode = FileShare.Read)
    {
        if (!parent.IsDirectory || string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.Any(char.IsControl) || name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar) || name.Contains(':'))
            throw new UnsafeHelperFilesystemException();
        using var relative = new RelativeName(name);
        var attributes = new ObjectAttributes
        {
            Length = Marshal.SizeOf<ObjectAttributes>(),
            RootDirectory = parent.handle.DangerousGetHandle(),
            ObjectName = relative.UnicodeStringPointer,
            Attributes = ObjCaseInsensitive,
        };
        var status = NtCreateFile(
            out var handle,
            GenericRead | ReadControl | WriteDac | Synchronize |
            (deleteCapable ? DeleteAccess : 0),
            ref attributes,
            out _,
            IntPtr.Zero,
            FileAttributeNormal,
            (uint)shareMode,
            FileOpen,
            FileNonDirectoryFile | FileSynchronousIoNonAlert | FileFlagOpenReparsePoint,
            IntPtr.Zero,
            0);
        if (status < 0)
        {
            handle?.Dispose();
            throw NtStatusException(status);
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                throw new UnsafeHelperFilesystemException();
            var expected = Path.GetFullPath(Path.Combine(parent.FullPath, name));
            var finalPath = ReadFinalPath(handle);
            if (!string.Equals(expected, finalPath, StringComparison.OrdinalIgnoreCase))
                throw new UnsafeHelperFilesystemException();
            return new PinnedHelperEntry(expected, handle, info, isDirectory: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void ApplyReadOnlyAcl(SecurityIdentifier user)
    {
        FileSystemSecurity security = IsDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(
            user, DeniedMutationRights, inheritance, PropagationFlags.None,
            AccessControlType.Deny));
        security.AddAccessRule(new FileSystemAccessRule(
            user, AllowedRuntimeRights, inheritance, PropagationFlags.None,
            AccessControlType.Allow));
        ApplyAcl(security);
    }

    public void ApplyWritableAcl(SecurityIdentifier user, bool inheritToChildren = false)
    {
        FileSystemSecurity security = IsDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = IsDirectory && inheritToChildren
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl, inheritance, PropagationFlags.None,
            AccessControlType.Allow));
        ApplyAcl(security);
    }

    public void EnsureReadOnlyAcl(SecurityIdentifier user)
    {
        var security = ReadAcl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != user ||
            !security.AreAccessRulesProtected)
            throw new UnsafeHelperFilesystemException();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
                targetType: typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        var denied = rules.Where(rule => rule.IdentityReference == user &&
            rule.AccessControlType == AccessControlType.Deny)
            .Aggregate((FileSystemRights)0, (rights, rule) => rights | rule.FileSystemRights);
        var allowed = rules.Where(rule => rule.IdentityReference == user &&
            rule.AccessControlType == AccessControlType.Allow)
            .Aggregate((FileSystemRights)0, (rights, rule) => rights | rule.FileSystemRights);
        if ((denied & DeniedMutationRights) != DeniedMutationRights ||
            (allowed & DeniedMutationRights) != 0 ||
            (allowed & AllowedRuntimeRights) != AllowedRuntimeRights ||
            rules.Any(rule => rule.IdentityReference != user))
            throw new UnsafeHelperFilesystemException();
        EnsureSameIdentity();
    }

    public bool HasProtectedOwnerAcl(SecurityIdentifier user)
    {
        var security = ReadAcl();
        return security.AreAccessRulesProtected &&
            security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner == user;
    }

    private void ApplyAcl(FileSystemSecurity security)
    {
        var bytes = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            if (!GetSecurityDescriptorDacl(
                    pinned.AddrOfPinnedObject(), out var present, out var dacl, out _) ||
                !present || dacl == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = SetSecurityInfo(handle, SeFileObject,
                DaclSecurityInformation | ProtectedDaclSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
            if (error != 0) throw new Win32Exception(checked((int)error));
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
        }
        EnsureSameIdentity();
    }

    private FileSystemSecurity ReadAcl()
    {
        var error = GetSecurityInfo(handle, SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw new Win32Exception(checked((int)error));
        try
        {
            var length = checked((int)GetSecurityDescriptorLength(descriptor));
            if (length <= 0) throw new UnsafeHelperFilesystemException();
            var bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, length);
            FileSystemSecurity security = IsDirectory ? new DirectorySecurity() : new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(bytes,
                AccessControlSections.Access | AccessControlSections.Owner);
            EnsureSameIdentity();
            return security;
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    public byte[] ReadAllBytes(int maximumBytes, CancellationToken cancellationToken)
    {
        if (IsDirectory || Length < 0 || Length > maximumBytes)
            throw new IOException("Helper file is outside the verification bound.");
        var bytes = new byte[checked((int)Length)];
        using var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(borrowed, FileAccess.Read, 4096, isAsync: false);
        stream.Position = 0;
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        if (stream.ReadByte() != -1) throw new IOException("Helper file changed during verification.");
        EnsureUnchanged();
        return bytes;
    }

    public string ComputeSha256(long maximumBytes, CancellationToken cancellationToken)
    {
        if (IsDirectory || Length < 0 || Length > maximumBytes)
            throw new IOException("Helper file is outside the verification bound.");
        using var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(borrowed, FileAccess.Read, 64 * 1024, isAsync: false);
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes) throw new IOException("Helper file exceeds verification bound.");
            hash.AppendData(buffer, 0, read);
        }
        if (total != Length) throw new IOException("Helper file changed during verification.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private void CopyToHandle(SafeFileHandle destination, CancellationToken cancellationToken)
    {
        if (IsDirectory) throw new IOException("A helper directory cannot be copied as a file.");
        using var borrowedInput = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var borrowedOutput = new SafeFileHandle(destination.DangerousGetHandle(), ownsHandle: false);
        using var input = new FileStream(borrowedInput, FileAccess.Read, 64 * 1024, isAsync: false);
        using var output = new FileStream(borrowedOutput, FileAccess.Write, 64 * 1024, isAsync: false);
        input.Position = 0;
        output.Position = 0;
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            total = checked(total + read);
            if (total > Length) throw new IOException("Helper file changed while snapshotting.");
        }
        output.Flush(flushToDisk: true);
        if (total != Length) throw new IOException("Helper file changed while snapshotting.");
        EnsureUnchanged();
    }

    public void FlushDirectory()
    {
        if (!IsDirectory) throw new IOException("A helper file cannot be flushed as a directory.");
        RandomAccess.FlushToDisk(handle);
        if (!GetFileInformationByHandle(handle, out var current) ||
            current.VolumeSerialNumber != original.VolumeSerialNumber ||
            current.FileIndexHigh != original.FileIndexHigh ||
            current.FileIndexLow != original.FileIndexLow ||
            (current.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) !=
            (original.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)))
            throw new IOException("Helper directory identity changed while snapshotting.");
        original = current;
    }

    public void EnsureUnchanged()
    {
        if (!GetFileInformationByHandle(handle, out var current) || !original.Equals(current))
            throw new IOException("Helper filesystem entry changed during verification.");
    }

    public void EnsureSameIdentity()
    {
        if (!GetFileInformationByHandle(handle, out var current) ||
            current.VolumeSerialNumber != original.VolumeSerialNumber ||
            current.FileIndexHigh != original.FileIndexHigh ||
            current.FileIndexLow != original.FileIndexLow ||
            (current.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) !=
            (original.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)))
            throw new IOException("Helper filesystem entry identity changed.");
    }

    public void WriteCommitByte(long offset, byte value)
    {
        if (IsDirectory || offset < 0 || offset >= Length)
            throw new IOException("The helper ready marker commit offset is invalid.");
        EnsureSameIdentity();
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        RandomAccess.Write(handle, buffer, offset);
        RandomAccess.FlushToDisk(handle);
        RefreshAfterExpectedContentWrite();
    }

    public void RefreshAfterExpectedContentWrite()
    {
        if (!GetFileInformationByHandle(handle, out var current))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var currentLength = checked(((long)current.FileSizeHigh << 32) | current.FileSizeLow);
        if (current.VolumeSerialNumber != original.VolumeSerialNumber ||
            current.FileIndexHigh != original.FileIndexHigh ||
            current.FileIndexLow != original.FileIndexLow ||
            (current.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) !=
            (original.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) ||
            currentLength != Length)
            throw new IOException("Helper ready marker identity changed during commit.");
        original = current;
    }

    public void MarkDeleteAndDispose()
    {
        EnsureSameIdentity();
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle, FileInformationClass.FileDispositionInfo,
                ref disposition, (uint)Marshal.SizeOf<FileDispositionInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Dispose();
    }

    public void Dispose() => handle.Dispose();

    internal static int MapNtStatusToWin32Error(int status) =>
        checked((int)RtlNtStatusToDosError(status));

    private static Win32Exception NtStatusException(int status) =>
        new(MapNtStatusToWin32Error(status));

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32768)
        {
            var builder = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length < builder.Capacity)
            {
                var value = builder.ToString();
                if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                    return "\\\\" + value[8..];
                if (value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
                    return value[4..];
                return value;
            }
            capacity = checked((int)length + 1);
        }
        throw new IOException("Helper path exceeds the verification bound.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime : IEquatable<FileTime>
    {
        public readonly uint Low;
        public readonly uint High;
        public bool Equals(FileTime other) => Low == other.Low && High == other.High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ByHandleFileInformation : IEquatable<ByHandleFileInformation>
    {
        public readonly uint FileAttributes;
        public readonly FileTime CreationTime;
        public readonly FileTime LastAccessTime;
        public readonly FileTime LastWriteTime;
        public readonly uint VolumeSerialNumber;
        public readonly uint FileSizeHigh;
        public readonly uint FileSizeLow;
        public readonly uint NumberOfLinks;
        public readonly uint FileIndexHigh;
        public readonly uint FileIndexLow;

        public bool Equals(ByHandleFileInformation other) =>
            FileAttributes == other.FileAttributes &&
            CreationTime.Equals(other.CreationTime) &&
            LastWriteTime.Equals(other.LastWriteTime) &&
            VolumeSerialNumber == other.VolumeSerialNumber &&
            FileSizeHigh == other.FileSizeHigh && FileSizeLow == other.FileSizeLow &&
            NumberOfLinks == other.NumberOfLinks &&
            FileIndexHigh == other.FileIndexHigh && FileIndexLow == other.FileIndexLow;
    }

    private enum FileInformationClass
    {
        FileDispositionInfo = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    private sealed class RelativeName : IDisposable
    {
        private readonly IntPtr text;

        public RelativeName(string name)
        {
            text = Marshal.StringToHGlobalUni(name);
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = text,
            };
            UnicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, UnicodeStringPointer, false);
        }

        public IntPtr UnicodeStringPointer { get; }

        public void Dispose()
        {
            Marshal.FreeHGlobal(UnicodeStringPointer);
            Marshal.FreeHGlobal(text);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafeFileHandle handle,
        uint objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        uint objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed class UnsafeHelperFilesystemException : IOException
{
    public UnsafeHelperFilesystemException()
        : base("Unsafe managed browser helper filesystem entry.")
    {
    }
}

internal sealed class HelperResourceLimitException : IOException
{
    public HelperResourceLimitException()
        : base("Managed browser helper filesystem resource limit exceeded.")
    {
    }
}
