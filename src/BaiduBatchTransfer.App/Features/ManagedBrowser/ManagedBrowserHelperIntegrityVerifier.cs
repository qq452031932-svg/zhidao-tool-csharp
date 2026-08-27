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
}

internal enum ManagedBrowserHelperSnapshotPhase
{
    ContainerCreated = 0,
    CopyingPayload = 1,
    MarkerWritten = 2,
    PinningSnapshot = 3,
    SealingAcl = 4,
}

internal sealed class ManagedBrowserHelperCleanupStatus
{
    private int state;

    public ManagedBrowserHelperCleanupState State =>
        (ManagedBrowserHelperCleanupState)Volatile.Read(ref state);

    public void Set(ManagedBrowserHelperCleanupState value) =>
        Volatile.Write(ref state, (int)value);
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
    private readonly Action? cleanupAttemptObserver;

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
        Action? cleanupAttemptObserver = null)
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
        this.cleanupAttemptObserver = cleanupAttemptObserver;
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
                cancellationToken, snapshotCreationObserver, cleanupAttemptObserver);
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
    private readonly ManagedBrowserHelperIntegrityLimits limits;
    private bool disposed;

    private PinnedHelperTree(string rootPath, ManagedBrowserHelperIntegrityLimits limits)
    {
        RootPath = rootPath;
        this.limits = limits;
    }

    public string RootPath { get; }
    public IReadOnlyDictionary<string, PinnedHelperEntry> Files { get; private set; } =
        new Dictionary<string, PinnedHelperEntry>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, PinnedHelperEntry> Directories { get; private set; } =
        new Dictionary<string, PinnedHelperEntry>(StringComparer.OrdinalIgnoreCase);
    public int HandleCount => entries.Count;

    public static PinnedHelperTree Capture(
        string root,
        ManagedBrowserHelperIntegrityLimits limits,
        CancellationToken cancellationToken,
        bool deleteCapable = false,
        FileShare? shareMode = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Managed browser helper verification requires Windows.");
        var canonical = Path.GetFullPath(root);
        if (!Path.IsPathFullyQualified(canonical) || !Directory.Exists(canonical))
            throw new IOException("Managed browser helper root is unavailable.");
        var tree = new PinnedHelperTree(canonical, limits);
        try
        {
            var files = new Dictionary<string, PinnedHelperEntry>(StringComparer.OrdinalIgnoreCase);
            var directories = new Dictionary<string, PinnedHelperEntry>(StringComparer.OrdinalIgnoreCase);
            var rootEntry = PinnedHelperEntry.Open(canonical, deleteCapable, shareMode: shareMode);
            if (!rootEntry.IsDirectory)
                throw new UnsafeHelperFilesystemException();
            tree.entries.Add(rootEntry);
            directories.Add(string.Empty, rootEntry);
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
                    var entry = PinnedHelperEntry.Open(candidate, deleteCapable, shareMode: shareMode);
                    tree.entries.Add(entry);
                    if (entry.IsDirectory)
                    {
                        if (!directories.TryAdd(relative, entry))
                            throw new IOException("Duplicate helper directory.");
                        pending.Enqueue((relative, candidate));
                    }
                    else
                    {
                        totalBytes = checked(totalBytes + entry.Length);
                        if (totalBytes > limits.MaximumTreeBytes)
                            throw new HelperResourceLimitException();
                        if (!files.TryAdd(relative, entry))
                            throw new IOException("Duplicate helper file.");
                    }
                }
            }
            tree.Files = files;
            tree.Directories = directories;
            return tree;
        }
        catch
        {
            tree.Dispose();
            throw;
        }
    }

    public void EnsureDirectoriesUnchanged()
    {
        foreach (var directory in Directories.Values) directory.EnsureUnchanged();
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
    private const int OwnershipMarkerVersion = 1;
    private const int MaximumOrphanCandidates = 128;
    private const int MaximumOrphanCursorParents = 64;
    private const string MarkerFileName = "ownership.json";
    private const string ActiveLockFileName = "active.lock";
    private const string PayloadDirectoryName = "payload";
    private const FileSystemRights DeniedMutationRights = FileSystemRights.Write |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
    private const FileSystemRights AllowedRuntimeRights = FileSystemRights.ReadAndExecute |
        FileSystemRights.Synchronize;
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
    private readonly string markerPath;
    private readonly string activeLockPath;
    private readonly SecurityIdentifier user;
    private readonly ManagedBrowserHelperIntegrityLimits limits;
    private readonly Action? cleanupAttemptObserver;
    private readonly ManagedBrowserHelperCleanupStatus cleanupStatus = new();
    private PinnedHelperEntry? parentFence;
    private PinnedHelperEntry? containerFence;
    private PinnedHelperEntry? markerFence;
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
        PinnedHelperEntry activeLock,
        PinnedHelperTree tree,
        ManagedBrowserHelperIntegrityLimits limits,
        Action? cleanupAttemptObserver)
    {
        this.snapshotParent = snapshotParent;
        this.containerPath = containerPath;
        rootPath = Path.Combine(containerPath, PayloadDirectoryName);
        markerPath = Path.Combine(containerPath, MarkerFileName);
        activeLockPath = Path.Combine(containerPath, ActiveLockFileName);
        this.user = user;
        this.parentFence = parentFence;
        this.containerFence = containerFence;
        this.markerFence = markerFence;
        this.activeLock = activeLock;
        this.tree = tree;
        this.limits = limits;
        this.cleanupAttemptObserver = cleanupAttemptObserver;
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
        Action? cleanupAttemptObserver)
    {
        var user = CurrentUser();
        var parentFence = PrepareSnapshotParent(snapshotParent, user);
        var snapshotId = Guid.NewGuid();
        var containerName = $"snapshot-{snapshotId:N}";
        var container = Path.Combine(snapshotParent, containerName);
        var payload = Path.Combine(container, PayloadDirectoryName);
        PinnedHelperEntry? containerFence = null;
        PinnedHelperEntry? markerFence = null;
        PinnedHelperEntry? activeLock = null;
        PinnedHelperTree? pinned = null;
        try
        {
            containerFence = PinnedHelperEntry.OpenRelativeDirectory(
                parentFence, containerName, createIfMissing: true, deleteCapable: true);
            containerFence.ApplyWritableDirectoryAcl(user);
            using (var payloadFence = PinnedHelperEntry.OpenRelativeDirectory(
                       containerFence, PayloadDirectoryName, createIfMissing: true))
                payloadFence.ApplyWritableDirectoryAcl(user);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.ContainerCreated);
            foreach (var relative in source.Directories.Keys.Where(path => path.Length > 0)
                         .OrderBy(path => path.Count(character => character == '/'))
                         .ThenBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(ToSnapshotPath(payload, relative));
            }
            foreach (var pair in source.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                observer?.Invoke(ManagedBrowserHelperSnapshotPhase.CopyingPayload);
                cancellationToken.ThrowIfCancellationRequested();
                pair.Value.CopyTo(ToSnapshotPath(payload, pair.Key), cancellationToken);
            }

            var marker = new OwnershipMarker(OwnershipMarkerVersion, snapshotId.ToString("N"),
                buildIdentity, createdUtc.ToUniversalTime(), containerFence.VolumeSerialNumber,
                containerFence.FileId, user.Value);
            WriteMarker(Path.Combine(container, MarkerFileName), marker);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.MarkerWritten);
            File.WriteAllBytes(Path.Combine(container, ActiveLockFileName), []);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.PinningSnapshot);
            pinned = PinnedHelperTree.Capture(payload, limits, cancellationToken);
            markerFence = PinnedHelperEntry.Open(Path.Combine(container, MarkerFileName), deleteCapable: true);
            activeLock = PinnedHelperEntry.Open(Path.Combine(container, ActiveLockFileName),
                deleteCapable: true, shareMode: FileShare.None);
            observer?.Invoke(ManagedBrowserHelperSnapshotPhase.SealingAcl);
            SealTreeReadOnly(container, user);
            var lease = new VerifiedHelperSnapshotLease(snapshotParent, container, user, parentFence,
                containerFence, markerFence, activeLock, pinned, limits, cleanupAttemptObserver);
            parentFence = null!;
            containerFence = null;
            markerFence = null;
            activeLock = null;
            pinned = null;
            lease.EnsureReadOnlyAcl();
            return lease;
        }
        catch
        {
            pinned?.Dispose();
            activeLock?.Dispose();
            markerFence?.Dispose();
            TryDeleteIncompleteSnapshot(container, containerFence, user, limits);
            containerFence?.Dispose();
            parentFence?.Dispose();
            throw;
        }
    }

    public void EnsureReadOnlyAcl()
    {
        foreach (var entry in Tree.Files.Values) EnsureReadOnlyAcl(entry.FullPath, false, user);
        foreach (var entry in Tree.Directories.Values) EnsureReadOnlyAcl(entry.FullPath, true, user);
        EnsureReadOnlyAcl(markerPath, false, user);
        EnsureReadOnlyAcl(activeLockPath, false, user);
        EnsureReadOnlyAcl(containerPath, true, user);
    }

    private static void TryDeleteIncompleteSnapshot(
        string container,
        PinnedHelperEntry? containerFence,
        SecurityIdentifier user,
        ManagedBrowserHelperIntegrityLimits limits)
    {
        if (containerFence is null) return;
        try
        {
            containerFence.EnsureSameIdentity();
            var cleanupLimits = new ManagedBrowserHelperIntegrityLimits(
                checked(limits.MaximumTreeEntries + 8),
                checked(limits.MaximumTreeDepth + 2),
                checked(limits.MaximumTreeBytes + 16 * 1024));
            using (var observed = PinnedHelperTree.Capture(
                       container, cleanupLimits, CancellationToken.None,
                       shareMode: FileShare.Read | FileShare.Delete))
            {
                foreach (var entry in observed.Files.Values)
                {
                    entry.EnsureUnchanged();
                    ApplyWritableAcl(entry.FullPath, isDirectory: false, user);
                    entry.EnsureUnchanged();
                }
                foreach (var entry in observed.Directories.Values.OrderByDescending(e => e.FullPath.Length))
                {
                    entry.EnsureSameIdentity();
                    ApplyWritableAcl(entry.FullPath, isDirectory: true, user);
                    entry.EnsureSameIdentity();
                }
                observed.EnsureEnumerationUnchanged(CancellationToken.None);
                containerFence.EnsureSameIdentity();
            }
            using var cleanup = PinnedHelperTree.Capture(
                container, cleanupLimits, CancellationToken.None, deleteCapable: true,
                shareMode: FileShare.Read | FileShare.Delete);
            var root = cleanup.Directories[string.Empty];
            if (root.VolumeSerialNumber != containerFence.VolumeSerialNumber ||
                !string.Equals(root.FileId, containerFence.FileId, StringComparison.Ordinal))
                throw new UnsafeHelperFilesystemException();
            cleanup.DeletePinnedTree();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           Win32Exception or OverflowException or
                                           System.Security.SecurityException or ArgumentException)
        {
            // A valid marker is preferred for later recovery, but creation failures before the
            // marker are still cleaned exactly when their pinned identity remains available.
        }
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
            tree?.Dispose(); tree = null;
            activeLock?.Dispose(); activeLock = null;
            markerFence?.Dispose(); markerFence = null;
            containerFence?.Dispose(); containerFence = null;
            parentFence?.Dispose(); parentFence = null;
            cleanupStatus.Set(ManagedBrowserHelperCleanupState.DeferredCleanupRegistered);
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
            (markerFence is null ? 0 : 1) + (activeLock is null ? 0 : 1);
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
            if (Directory.Exists(containerPath)) SealTreeReadOnly(containerPath, user);
            containerFence?.EnsureSameIdentity();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           Win32Exception or OverflowException or
                                           System.Security.SecurityException or ArgumentException)
        {
            // Recovery will leave candidates with invalid or incomplete boundaries untouched.
        }
        tree?.Dispose(); tree = null;
        activeLock?.Dispose(); activeLock = null;
        markerFence?.Dispose(); markerFence = null;
        containerFence?.Dispose(); containerFence = null;
        parentFence?.Dispose(); parentFence = null;
        cleanupStatus.Set(ManagedBrowserHelperCleanupState.DeferredCleanupRegistered);
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
            if (!containerFence.IsDirectory || !HasProtectedOwnerAcl(container, user)) return;
            var marker = ReadMarker(Path.Combine(container, MarkerFileName));
            if (marker is null || marker.Version != OwnershipMarkerVersion ||
                !string.Equals(marker.SnapshotId, Path.GetFileName(container)[9..], StringComparison.Ordinal) ||
                marker.VolumeSerialNumber != containerFence.VolumeSerialNumber ||
                !string.Equals(marker.FileId, containerFence.FileId, StringComparison.Ordinal) ||
                !string.Equals(marker.OwnerSid, user.Value, StringComparison.Ordinal) ||
                !IsLowerHex64(marker.BuildIdentity) ||
                now - marker.CreatedUtc < minimumAge) return;
            try
            {
                using var activeProbe = PinnedHelperEntry.Open(
                    Path.Combine(container, ActiveLockFileName), shareMode: FileShare.None);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 32 or 33)
            {
                return;
            }

            using (var observed = PinnedHelperTree.Capture(container, limits, cancellationToken))
            {
                foreach (var entry in observed.Files.Values)
                    EnsureReadOnlyAcl(entry.FullPath, false, user);
                foreach (var entry in observed.Directories.Values)
                    EnsureReadOnlyAcl(entry.FullPath, true, user);
                foreach (var entry in observed.Files.Values)
                {
                    entry.EnsureUnchanged(); ApplyWritableAcl(entry.FullPath, false, user);
                    entry.EnsureUnchanged();
                }
                foreach (var entry in observed.Directories.Values.OrderByDescending(e => e.FullPath.Length))
                {
                    entry.EnsureUnchanged(); ApplyWritableAcl(entry.FullPath, true, user);
                    entry.EnsureUnchanged();
                }
                observed.EnsureEnumerationUnchanged(cancellationToken);
                containerFence.EnsureUnchanged();
            }
            using var cleanup = PinnedHelperTree.Capture(
                container, limits, cancellationToken, deleteCapable: true);
            if (cleanup.Directories[string.Empty].VolumeSerialNumber != marker.VolumeSerialNumber ||
                !string.Equals(cleanup.Directories[string.Empty].FileId, marker.FileId,
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

    private void CleanupExact()
    {
        parentFence?.EnsureSameIdentity();
        containerFence?.EnsureSameIdentity();
        if (tree is not null)
        {
            tree.EnsureEnumerationUnchanged(CancellationToken.None);
            foreach (var entry in tree.Files.Values)
            {
                entry.EnsureUnchanged(); ApplyWritableAcl(entry.FullPath, false, user); entry.EnsureUnchanged();
            }
            foreach (var entry in tree.Directories.Values.OrderByDescending(e => e.FullPath.Length))
            {
                entry.EnsureUnchanged(); ApplyWritableAcl(entry.FullPath, true, user); entry.EnsureUnchanged();
            }
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
            activeLock.EnsureUnchanged(); ApplyWritableAcl(activeLockPath, false, user);
            activeLock.EnsureUnchanged(); activeLock.MarkDeleteAndDispose(); activeLock = null;
        }
        if (markerFence is not null)
        {
            markerFence.EnsureUnchanged(); ApplyWritableAcl(markerPath, false, user);
            markerFence.EnsureUnchanged(); markerFence.MarkDeleteAndDispose(); markerFence = null;
        }
        if (containerFence is not null)
        {
            containerFence.EnsureSameIdentity(); ApplyWritableAcl(containerPath, true, user);
            containerFence.EnsureSameIdentity(); containerFence.MarkDeleteAndDispose(); containerFence = null;
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
            current.ApplyWritableDirectoryAcl(user);
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static string ToSnapshotPath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(
            root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnsafeHelperFilesystemException();
        return full;
    }

    private static void SealTreeReadOnly(string root, SecurityIdentifier user)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            ApplyReadOnlyAcl(file, isDirectory: false, user);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            ApplyReadOnlyAcl(directory, isDirectory: true, user);
        ApplyReadOnlyAcl(root, isDirectory: true, user);
    }

    private static void ApplyReadOnlyAcl(
        string path,
        bool isDirectory,
        SecurityIdentifier user)
    {
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(
            user, DeniedMutationRights, inheritance, PropagationFlags.None,
            AccessControlType.Deny));
        security.AddAccessRule(new FileSystemAccessRule(
            user, AllowedRuntimeRights, inheritance, PropagationFlags.None,
            AccessControlType.Allow));
        if (isDirectory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    private static void ApplyWritableAcl(
        string path,
        bool isDirectory,
        SecurityIdentifier user)
    {
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl, inheritance, PropagationFlags.None,
            AccessControlType.Allow));
        if (isDirectory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    private static void EnsureReadOnlyAcl(
        string path,
        bool isDirectory,
        SecurityIdentifier user)
    {
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
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
    }

    private static void WriteMarker(string path, OwnershipMarker marker)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private static OwnershipMarker? ReadMarker(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        if (stream.Length is < 2 or > 4096) return null;
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (bytes[^1] != (byte)'\n') return null;
        return JsonSerializer.Deserialize<OwnershipMarker>(bytes.AsSpan(0, bytes.Length - 1));
    }

    private static bool HasProtectedOwnerAcl(string path, SecurityIdentifier user)
    {
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        return security.AreAccessRulesProtected &&
            security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner == user;
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
        string SnapshotId,
        string BuildIdentity,
        DateTimeOffset CreatedUtc,
        uint VolumeSerialNumber,
        string FileId,
        string OwnerSid);
}

internal sealed class PinnedHelperEntry : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileOpenIf = 3;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint SeFileObject = 1;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private readonly SafeFileHandle handle;
    private readonly ByHandleFileInformation original;

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
            FileReadAttributes | FileTraverse | ReadControl | WriteDac | Synchronize |
            (deleteCapable ? DeleteAccess : 0),
            ref attributes,
            out _,
            IntPtr.Zero,
            FileAttributeDirectory,
            (uint)(FileShare.Read | FileShare.Write | FileShare.Delete),
            createIfMissing ? FileOpenIf : 1u,
            FileDirectoryFile | FileSynchronousIoNonAlert | FileFlagOpenReparsePoint,
            IntPtr.Zero,
            0);
        if (status < 0)
        {
            handle?.Dispose();
            throw new Win32Exception(status);
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

    public void ApplyWritableDirectoryAcl(SecurityIdentifier user)
    {
        if (!IsDirectory) throw new UnsafeHelperFilesystemException();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
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

    public void CopyTo(string destination, CancellationToken cancellationToken)
    {
        if (IsDirectory) throw new IOException("A helper directory cannot be copied as a file.");
        using var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var input = new FileStream(borrowed, FileAccess.Read, 64 * 1024, isAsync: false);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        input.Position = 0;
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
