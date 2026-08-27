using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
}

public sealed class ManagedBrowserHelperIntegrityException : Exception
{
    public ManagedBrowserHelperIntegrityException(ManagedBrowserHelperIntegrityError error)
        : base($"Managed browser helper integrity failure: {error}.") => Error = error;

    public ManagedBrowserHelperIntegrityError Error { get; }

    public override string ToString() =>
        $"ManagedBrowserHelperIntegrityException {{ Error = {Error}, Details = [REDACTED] }}";
}

public sealed class VerifiedManagedBrowserHelperBundle : IDisposable
{
    private PinnedHelperTree? tree;

    internal VerifiedManagedBrowserHelperBundle(
        PinnedHelperTree tree,
        string executablePath,
        string mainScriptPath,
        string buildIdentity)
    {
        this.tree = tree;
        ExecutablePath = executablePath;
        MainScriptPath = mainScriptPath;
        BuildIdentity = buildIdentity;
    }

    public string ExecutablePath { get; }
    public string MainScriptPath { get; }
    public string BuildIdentity { get; }
    public bool IsDisposed => tree is null;

    public void Dispose() => Interlocked.Exchange(ref tree, null)?.Dispose();

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

    public ManagedBrowserHelperIntegrityVerifier()
        : this(ReadEmbeddedManifest())
    {
    }

    internal ManagedBrowserHelperIntegrityVerifier(byte[] expectedManifestBytes)
    {
        ArgumentNullException.ThrowIfNull(expectedManifestBytes);
        if (expectedManifestBytes.Length is < 1 or > MaximumManifestBytes)
            throw new ArgumentException("The embedded helper manifest is invalid.", nameof(expectedManifestBytes));
        this.expectedManifestBytes = expectedManifestBytes.ToArray();
    }

    public Task<VerifiedManagedBrowserHelperBundle> VerifyAsync(
        string helperRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(helperRoot))
            throw new ArgumentException("A helper root is required.", nameof(helperRoot));
        return Task.Run(() => Verify(helperRoot, cancellationToken), cancellationToken);
    }

    public async Task<T> VerifyThenLaunchAsync<T>(
        string helperRoot,
        Func<VerifiedManagedBrowserHelperBundle, CancellationToken, Task<T>> launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        using var verified = await VerifyAsync(helperRoot, cancellationToken).ConfigureAwait(false);
        return await launch(verified, cancellationToken).ConfigureAwait(false);
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

        PinnedHelperTree? tree = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            tree = PinnedHelperTree.Capture(helperRoot, cancellationToken);
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

            var executable = tree.Files[ExecutableFileName].FullPath;
            var mainScript = tree.Files[MainScriptFileName].FullPath;
            var result = new VerifiedManagedBrowserHelperBundle(
                tree, executable, mainScript, manifest.BuildIdentity);
            tree = null;
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
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception or OverflowException)
        {
            throw Failure(ManagedBrowserHelperIntegrityError.VerificationIoFailure);
        }
        finally
        {
            tree?.Dispose();
        }
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
    private bool disposed;

    private PinnedHelperTree(string rootPath) => RootPath = rootPath;

    public string RootPath { get; }
    public IReadOnlyDictionary<string, PinnedHelperEntry> Files { get; private set; } =
        new Dictionary<string, PinnedHelperEntry>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, PinnedHelperEntry> Directories { get; private set; } =
        new Dictionary<string, PinnedHelperEntry>(StringComparer.OrdinalIgnoreCase);

    public static PinnedHelperTree Capture(string root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Managed browser helper verification requires Windows.");
        var canonical = Path.GetFullPath(root);
        if (!Path.IsPathFullyQualified(canonical) || !Directory.Exists(canonical))
            throw new IOException("Managed browser helper root is unavailable.");
        var tree = new PinnedHelperTree(canonical);
        try
        {
            var files = new Dictionary<string, PinnedHelperEntry>(StringComparer.OrdinalIgnoreCase);
            var directories = new Dictionary<string, PinnedHelperEntry>(StringComparer.OrdinalIgnoreCase);
            var rootEntry = PinnedHelperEntry.Open(canonical);
            if (!rootEntry.IsDirectory)
                throw new UnsafeHelperFilesystemException();
            tree.entries.Add(rootEntry);
            directories.Add(string.Empty, rootEntry);
            var pending = new Queue<(string Relative, string Full)>();
            pending.Enqueue((string.Empty, canonical));
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
                    var entry = PinnedHelperEntry.Open(candidate);
                    tree.entries.Add(entry);
                    if (entry.IsDirectory)
                    {
                        if (!directories.TryAdd(relative, entry))
                            throw new IOException("Duplicate helper directory.");
                        pending.Enqueue((relative, candidate));
                    }
                    else
                    {
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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (var index = entries.Count - 1; index >= 0; index--) entries[index].Dispose();
        entries.Clear();
    }
}

internal sealed class PinnedHelperEntry : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
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

    public static PinnedHelperEntry Open(string path)
    {
        var handle = CreateFileW(
            path,
            GenericRead,
            FileShare.Read,
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

    public void EnsureUnchanged()
    {
        if (!GetFileInformationByHandle(handle, out var current) || !original.Equals(current))
            throw new IOException("Helper filesystem entry changed during verification.");
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}

internal sealed class UnsafeHelperFilesystemException : IOException
{
    public UnsafeHelperFilesystemException()
        : base("Unsafe managed browser helper filesystem entry.")
    {
    }
}
