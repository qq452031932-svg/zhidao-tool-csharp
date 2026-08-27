using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Win32.SafeHandles;

namespace BaiduBatchTransfer.Infrastructure.Security;

public enum ManagedBrowserProfileStatus { Ready = 1, IsolationRequired = 2 }

public enum ManagedBrowserProfileIsolationReason
{
    None = 0, MissingMarker = 1, InvalidMarker = 2, IdentityMismatch = 3,
    UnsafePath = 4, AccessControlMismatch = 5, ConcurrentMutation = 6,
    UnsupportedNamespace = 7, UnsupportedVolume = 8,
}

public sealed class ManagedBrowserProfileDescriptor
{
    internal ManagedBrowserProfileDescriptor(Guid accountId, BrowserFamily family, Guid profileId,
        Guid ownershipId, string canonicalSourcePath)
    {
        AccountId = accountId; Family = family; ProfileId = profileId;
        OwnershipId = ownershipId; CanonicalSourcePath = canonicalSourcePath;
    }
    public Guid AccountId { get; }
    public BrowserFamily Family { get; }
    public BrowserFamily BrowserFamily => Family;
    public Guid ProfileId { get; }
    public Guid OwnershipId { get; }
    public Guid ProfileOwnershipId => OwnershipId;
    public string CanonicalSourcePath { get; }
    public override string ToString() =>
        $"ManagedBrowserProfileDescriptor {{ AccountId = [REDACTED], Family = {Family}, " +
        "ProfileId = [REDACTED], OwnershipId = [REDACTED], CanonicalSourcePath = [REDACTED] }";
}

public sealed class ManagedBrowserProfileResult
{
    private ManagedBrowserProfileResult(ManagedBrowserProfileStatus status,
        ManagedBrowserProfileDescriptor? profile, ManagedBrowserProfileIsolationReason isolationReason)
    { Status = status; Profile = profile; IsolationReason = isolationReason; }
    public ManagedBrowserProfileStatus Status { get; }
    public ManagedBrowserProfileDescriptor? Profile { get; }
    public ManagedBrowserProfileIsolationReason IsolationReason { get; }
    internal static ManagedBrowserProfileResult Ready(ManagedBrowserProfileDescriptor profile) =>
        new(ManagedBrowserProfileStatus.Ready, profile, ManagedBrowserProfileIsolationReason.None);
    internal static ManagedBrowserProfileResult IsolationRequired(ManagedBrowserProfileIsolationReason reason) =>
        new(ManagedBrowserProfileStatus.IsolationRequired, null, reason);
    public override string ToString() =>
        $"ManagedBrowserProfileResult {{ Status = {Status}, Profile = " +
        (Profile is null ? "null" : "[REDACTED]") + $", IsolationReason = {IsolationReason} }}";
}

public interface IManagedBrowserProfilePolicy
{
    Task<ManagedBrowserProfileResult> GetOrCreateAsync(Guid accountId, BrowserFamily family,
        CancellationToken cancellationToken = default);
    Task<ManagedBrowserProfileResult> OpenExistingAsync(ManagedBrowserProfileDescriptor expected,
        CancellationToken cancellationToken = default);
}

internal sealed class ManagedBrowserProfilePolicyTestHooks
{
    public Action? AfterTrustedNamespacePinned { get; init; }
    public Action? AfterMarkerPrefixFlushed { get; init; }
    public Action? BeforePublish { get; init; }
    public Action<string>? BeforeMarkerPinned { get; init; }
    public Action<string>? AfterFirstMarkerRead { get; init; }
}

internal enum ManagedProfileDriveKind { Fixed = 1, Remote = 2, Other = 3 }

internal readonly record struct ManagedProfileVolumeCapabilities(
    ManagedProfileDriveKind DriveKind,
    bool SupportsPersistentAcls,
    bool SupportsStableFileIds,
    bool SupportsAtomicRename);

internal interface IManagedProfileVolumeCapabilityReader
{
    ManagedProfileVolumeCapabilities Read(SafeFileHandle pinnedVolumeRoot, string driveRoot);
}

internal sealed class PhysicalManagedProfileVolumeCapabilityReader : IManagedProfileVolumeCapabilityReader
{
    private const uint DriveFixed = 3;
    private const uint DriveRemote = 4;
    private const uint FileRemoteDevice = 0x00000010;
    private const uint FilePersistentAcls = 0x00000008;
    private const uint FileSupportsOpenByFileId = 0x01000000;
    private const int FileFsDeviceInformationClass = 4;
    public static PhysicalManagedProfileVolumeCapabilityReader Instance { get; } = new();
    private PhysicalManagedProfileVolumeCapabilityReader() { }
    public ManagedProfileVolumeCapabilities Read(SafeFileHandle pinnedVolumeRoot, string driveRoot)
    {
        if (!OperatingSystem.IsWindows() || pinnedVolumeRoot.IsInvalid || pinnedVolumeRoot.IsClosed)
            return new(ManagedProfileDriveKind.Other, false, false, false);
        var driveType = GetDriveTypeW(driveRoot);
        var deviceStatus = NtQueryVolumeInformationFile(pinnedVolumeRoot, out _, out var deviceInformation,
            (uint)Marshal.SizeOf<FileFsDeviceInformation>(), FileFsDeviceInformationClass);
        if (deviceStatus < 0)
            return new(ManagedProfileDriveKind.Other, false, false, false);
        var driveKind = (deviceInformation.Characteristics & FileRemoteDevice) != 0
            ? ManagedProfileDriveKind.Remote
            : MapDriveKind(driveType);
        var fileSystemName = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(pinnedVolumeRoot, null, 0, out _, out _, out var flags,
            fileSystemName, fileSystemName.Capacity))
            return new(driveKind, false, false, false);
        var fileSystem = fileSystemName.ToString();
        return new(
            driveKind,
            (flags & FilePersistentAcls) != 0,
            (flags & FileSupportsOpenByFileId) != 0,
            string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileSystem, "ReFS", StringComparison.OrdinalIgnoreCase));
    }

    private static ManagedProfileDriveKind MapDriveKind(uint driveType) => driveType switch
    {
        DriveFixed => ManagedProfileDriveKind.Fixed,
        DriveRemote => ManagedProfileDriveKind.Remote,
        _ => ManagedProfileDriveKind.Other,
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle fileHandle,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryVolumeInformationFile(
        SafeFileHandle fileHandle,
        out VolumeIoStatusBlock ioStatusBlock,
        out FileFsDeviceInformation fileSystemInformation,
        uint length,
        int fileSystemInformationClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct VolumeIoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileFsDeviceInformation
    {
        public uint DeviceType;
        public uint Characteristics;
    }
}

public sealed class FileSystemManagedBrowserProfilePolicy : IManagedBrowserProfilePolicy
{
    internal const string MarkerName = ".bbt-managed-profile-owner.v1";
    private const string ManagedRootName = "managed-browser-profiles";
    private readonly string appDataRoot;
    [SupportedOSPlatformGuard("windows")]
    private readonly bool supportedNamespace;
    private readonly Action<string>? beforeMarkerOpen;
    private readonly Action<string>? afterDirectoryPin;
    private readonly ManagedBrowserProfilePolicyTestHooks? testHooks;
    private readonly IManagedProfileVolumeCapabilityReader volumeCapabilityReader;

    public FileSystemManagedBrowserProfilePolicy(string appDataRoot) :
        this(appDataRoot, PhysicalProfilePathFileSystem.Instance)
    { }
    internal FileSystemManagedBrowserProfilePolicy(string appDataRoot, Action<string>? beforeMarkerOpen) :
        this(appDataRoot, PhysicalProfilePathFileSystem.Instance, beforeMarkerOpen)
    { }
    internal FileSystemManagedBrowserProfilePolicy(string appDataRoot, Action<string>? beforeMarkerOpen,
        Action<string>? afterDirectoryPin) :
        this(appDataRoot, PhysicalProfilePathFileSystem.Instance, beforeMarkerOpen, afterDirectoryPin)
    { }
    internal FileSystemManagedBrowserProfilePolicy(string appDataRoot,
        ManagedBrowserProfilePolicyTestHooks testHooks) :
        this(appDataRoot, PhysicalProfilePathFileSystem.Instance)
    { this.testHooks = testHooks ?? throw new ArgumentNullException(nameof(testHooks)); }
    internal FileSystemManagedBrowserProfilePolicy(string appDataRoot,
        IManagedProfileVolumeCapabilityReader volumeCapabilityReader) :
        this(appDataRoot, PhysicalProfilePathFileSystem.Instance, null, null, volumeCapabilityReader)
    { }
    internal FileSystemManagedBrowserProfilePolicy(string appDataRoot, IProfilePathFileSystem fileSystem,
        Action<string>? beforeMarkerOpen = null, Action<string>? afterDirectoryPin = null,
        IManagedProfileVolumeCapabilityReader? volumeCapabilityReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        ArgumentNullException.ThrowIfNull(fileSystem);
        this.appDataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataRoot));
        supportedNamespace = OperatingSystem.IsWindows() && IsSupportedLocalDrivePath(this.appDataRoot);
        this.beforeMarkerOpen = beforeMarkerOpen;
        this.afterDirectoryPin = afterDirectoryPin;
        this.volumeCapabilityReader = volumeCapabilityReader ?? PhysicalManagedProfileVolumeCapabilityReader.Instance;
        if (supportedNamespace)
            _ = new FileSystemAccountProfilePathPolicy(Path.Combine(this.appDataRoot, ManagedRootName), fileSystem);
    }

    public Task<ManagedBrowserProfileResult> GetOrCreateAsync(Guid accountId, BrowserFamily family,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(accountId, family);
        cancellationToken.ThrowIfCancellationRequested();
        if (!supportedNamespace) return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.UnsupportedNamespace));
        try
        {
            using var ns = PinnedNamespace.Open(appDataRoot, createAppRoot: true, volumeCapabilityReader);
            testHooks?.AfterTrustedNamespacePinned?.Invoke();
            if (!ns.Revalidate()) return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.ConcurrentMutation));
            cancellationToken.ThrowIfCancellationRequested();
            var managed = ns.OpenOrCreateOwnedDirectory(PinnedNamespace.AppRootKey, ManagedRootName);
            var familyName = FamilyName(family);
            var familyRoot = ns.OpenOrCreateOwnedDirectory(ManagedRootName, familyName);
            var accountName = accountId.ToString("N");
            var canonicalPath = Path.Combine(appDataRoot, ManagedRootName, familyName, accountName);
            beforeMarkerOpen?.Invoke(canonicalPath);
            if (!ns.Revalidate())
                return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.ConcurrentMutation));
            var existing = ns.TryOpenOwnedDirectory(familyRoot, accountName);
            if (existing is not null)
            {
                using (existing)
                {
                    ns.TrackOwned(canonicalPath, existing);
                    afterDirectoryPin?.Invoke(canonicalPath);
                    if (!ns.RelativeIdentityMatches(familyRoot, accountName, existing))
                        return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.ConcurrentMutation));
                    var result = ReadExistingProfile(ns, existing, accountId, family, canonicalPath, null, cancellationToken);
                    return Task.FromResult(ns.Revalidate() ? result :
                        Isolation(ManagedBrowserProfileIsolationReason.ConcurrentMutation));
                }
            }
            return Task.FromResult(CreateAndPublish(ns, familyRoot, accountName, accountId, family,
                canonicalPath, cancellationToken));
        }
        catch (ManagedProfilePolicyException exception) { return Task.FromResult(Isolation(exception.Reason)); }
        catch (UnauthorizedAccessException) { return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.AccessControlMismatch)); }
        catch (IOException) { return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.UnsafePath)); }
    }

    public Task<ManagedBrowserProfileResult> OpenExistingAsync(ManagedBrowserProfileDescriptor expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateArguments(expected.AccountId, expected.Family);
        cancellationToken.ThrowIfCancellationRequested();
        if (!supportedNamespace) return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.UnsupportedNamespace));
        var familyName = FamilyName(expected.Family);
        var canonicalPath = Path.Combine(appDataRoot, ManagedRootName, familyName, expected.AccountId.ToString("N"));
        if (!string.Equals(canonicalPath, expected.CanonicalSourcePath, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.IdentityMismatch));
        try
        {
            using var ns = PinnedNamespace.Open(appDataRoot, createAppRoot: false, volumeCapabilityReader);
            var managed = ns.TryOpenOwnedDirectory(ns.AppRoot, ManagedRootName);
            if (managed is null) return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.MissingMarker));
            using (managed)
            {
                var managedPath = Path.Combine(appDataRoot, ManagedRootName);
                ns.TrackOwned(managedPath, managed);
                var familyRoot = ns.TryOpenOwnedDirectory(managed, familyName);
                if (familyRoot is null) return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.MissingMarker));
                using (familyRoot)
                {
                    ns.TrackOwned(Path.Combine(managedPath, familyName), familyRoot);
                    var account = ns.TryOpenOwnedDirectory(familyRoot, expected.AccountId.ToString("N"));
                    if (account is null) return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.MissingMarker));
                    using (account)
                    {
                        ns.TrackOwned(canonicalPath, account);
                        var result = ReadExistingProfile(ns, account, expected.AccountId, expected.Family,
                            canonicalPath, expected, cancellationToken);
                        return Task.FromResult(ns.Revalidate() ? result :
                            Isolation(ManagedBrowserProfileIsolationReason.ConcurrentMutation));
                    }
                }
            }
        }
        catch (ManagedProfilePolicyException exception) { return Task.FromResult(Isolation(exception.Reason)); }
        catch (UnauthorizedAccessException) { return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.AccessControlMismatch)); }
        catch (IOException) { return Task.FromResult(Isolation(ManagedBrowserProfileIsolationReason.UnsafePath)); }
    }

    private ManagedBrowserProfileResult CreateAndPublish(PinnedNamespace ns, PinnedEntry familyRoot,
        string accountName, Guid accountId, BrowserFamily family, string canonicalPath,
        CancellationToken cancellationToken)
    {
        var marker = new Marker(accountId, family, Guid.NewGuid(), Guid.NewGuid());
        using var staging = ns.CreateOwnedDirectory(familyRoot, ".staging-" + Guid.NewGuid().ToString("N"), true);
        cancellationToken.ThrowIfCancellationRequested();
        using (var markerFile = ns.CreateOwnedMarker(staging, MarkerName))
        {
            markerFile.WriteCanonicalText(Format(marker), testHooks?.AfterMarkerPrefixFlushed);
            if (!markerFile.HasCurrentUserOnlyAcl()) throw Policy(ManagedBrowserProfileIsolationReason.AccessControlMismatch);
            if (TryParse(markerFile.ReadCanonicalText()) != marker) throw Policy(ManagedBrowserProfileIsolationReason.InvalidMarker);
        }
        testHooks?.BeforePublish?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (!ns.TryPublish(staging, familyRoot, accountName))
        {
            var winner = ns.TryOpenOwnedDirectory(familyRoot, accountName);
            if (winner is null) return Isolation(ManagedBrowserProfileIsolationReason.ConcurrentMutation);
            using (winner)
            {
                ns.TrackOwned(canonicalPath, winner);
                var result = ReadExistingProfile(ns, winner, accountId, family, canonicalPath, null, cancellationToken);
                return ns.Revalidate() ? result : Isolation(ManagedBrowserProfileIsolationReason.ConcurrentMutation);
            }
        }
        if (!ns.RelativeIdentityMatches(familyRoot, accountName, staging))
            return Isolation(ManagedBrowserProfileIsolationReason.ConcurrentMutation);
        ns.TrackPublished(canonicalPath, staging);
        return ReadExistingProfile(ns, staging, accountId, family, canonicalPath,
            ToDescriptor(marker, canonicalPath), cancellationToken);
    }

    private ManagedBrowserProfileResult ReadExistingProfile(PinnedNamespace ns, PinnedEntry account, Guid accountId,
        BrowserFamily family, string path, ManagedBrowserProfileDescriptor? expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var markerPath = Path.Combine(path, MarkerName);
        testHooks?.BeforeMarkerPinned?.Invoke(markerPath);
        using var markerFile = PinnedNamespace.TryOpenMarkerStatic(account, MarkerName);
        if (markerFile is null) return Isolation(ManagedBrowserProfileIsolationReason.MissingMarker);
        if (!markerFile.IsOrdinaryFile) return Isolation(ManagedBrowserProfileIsolationReason.UnsafePath);
        var firstAcl = markerFile.CaptureAcl();
        if (!firstAcl.IsCurrentUserOnly) return Isolation(ManagedBrowserProfileIsolationReason.AccessControlMismatch);
        var markerText = markerFile.ReadCanonicalText();
        var marker = TryParse(markerText);
        if (marker is null) return Isolation(ManagedBrowserProfileIsolationReason.InvalidMarker);
        if (marker.AccountId != accountId || marker.Family != family || expected is not null &&
            (marker.ProfileId != expected.ProfileId || marker.OwnershipId != expected.OwnershipId))
            return Isolation(ManagedBrowserProfileIsolationReason.IdentityMismatch);
        testHooks?.AfterFirstMarkerRead?.Invoke(markerPath);
        if (!ns.Revalidate()) return Isolation(ManagedBrowserProfileIsolationReason.ConcurrentMutation);
        var secondText = markerFile.ReadCanonicalText();
        var secondAcl = markerFile.CaptureAcl();
        if (!secondAcl.IsCurrentUserOnly)
            return Isolation(ManagedBrowserProfileIsolationReason.AccessControlMismatch);
        if (!firstAcl.BinaryDescriptor.AsSpan().SequenceEqual(secondAcl.BinaryDescriptor) ||
            !string.Equals(markerText, secondText, StringComparison.Ordinal) ||
            !PinnedNamespace.RelativeIdentityMatchesStatic(account, MarkerName, markerFile))
            return Isolation(ManagedBrowserProfileIsolationReason.ConcurrentMutation);
        return ManagedBrowserProfileResult.Ready(ToDescriptor(marker, path));
    }

    private static string FamilyName(BrowserFamily family) => family == BrowserFamily.Chrome ? "chrome" : "edge";
    private static bool IsSupportedLocalDrivePath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
        var root = Path.GetPathRoot(path);
        return root is { Length: 3 } && char.IsAsciiLetter(root[0]) && root[1] == ':' &&
            root[2] == Path.DirectorySeparatorChar && !string.Equals(Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
    }
    private static void ValidateArguments(Guid accountId, BrowserFamily family)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("A local account ID is required.", nameof(accountId));
        if (family is not (BrowserFamily.Chrome or BrowserFamily.Edge))
            throw new ArgumentOutOfRangeException(nameof(family), family, "A managed browser family is required.");
    }
    private static string Format(Marker marker) =>
        $"bbt-managed-browser-profile:1\naccount-id={marker.AccountId:N}\n" +
        $"browser-family={marker.Family}\nprofile-id={marker.ProfileId:N}\n" +
        $"profile-ownership-id={marker.OwnershipId:N}\n";
    private static Marker? TryParse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length != 6 || lines[0] != "bbt-managed-browser-profile:1" || lines[5].Length != 0 ||
            !lines[1].StartsWith("account-id=", StringComparison.Ordinal) ||
            !lines[2].StartsWith("browser-family=", StringComparison.Ordinal) ||
            !lines[3].StartsWith("profile-id=", StringComparison.Ordinal) ||
            !lines[4].StartsWith("profile-ownership-id=", StringComparison.Ordinal) ||
            !Guid.TryParseExact(lines[1]["account-id=".Length..], "N", out var accountId) ||
            !Enum.TryParse<BrowserFamily>(lines[2]["browser-family=".Length..], false, out var family) ||
            family is not (BrowserFamily.Chrome or BrowserFamily.Edge) ||
            !Guid.TryParseExact(lines[3]["profile-id=".Length..], "N", out var profileId) ||
            !Guid.TryParseExact(lines[4]["profile-ownership-id=".Length..], "N", out var ownershipId) ||
            accountId == Guid.Empty || profileId == Guid.Empty || ownershipId == Guid.Empty) return null;
        var marker = new Marker(accountId, family, profileId, ownershipId);
        return text == Format(marker) ? marker : null;
    }
    private static ManagedBrowserProfileDescriptor ToDescriptor(Marker marker, string path) =>
        new(marker.AccountId, marker.Family, marker.ProfileId, marker.OwnershipId, path);
    private static ManagedBrowserProfileResult Isolation(ManagedBrowserProfileIsolationReason reason) =>
        ManagedBrowserProfileResult.IsolationRequired(reason);
    private static ManagedProfilePolicyException Policy(ManagedBrowserProfileIsolationReason reason) => new(reason);
    private sealed record Marker(Guid AccountId, BrowserFamily Family, Guid ProfileId, Guid OwnershipId);
    private sealed class ManagedProfilePolicyException(ManagedBrowserProfileIsolationReason reason) :
        IOException("The managed browser profile namespace is not safe.")
    { public ManagedBrowserProfileIsolationReason Reason { get; } = reason; }

    private sealed class PinnedNamespace : IDisposable
    {
        internal const string AppRootKey = "app";
        private readonly Dictionary<string, PinnedEntry> named = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Path, PinnedEntry Entry, bool RequireOwnedAcl)> tracked = [];
        private readonly List<PinnedEntry> owned = [];
        private PinnedNamespace(PinnedEntry appRoot) { AppRoot = appRoot; named[AppRootKey] = appRoot; }
        public PinnedEntry AppRoot { get; private set; }

        public static PinnedNamespace Open(string appDataRoot, bool createAppRoot,
            IManagedProfileVolumeCapabilityReader volumeCapabilityReader)
        {
            var driveRoot = Path.GetPathRoot(appDataRoot) ?? throw Policy(ManagedBrowserProfileIsolationReason.UnsupportedNamespace);
            var drive = OpenAbsoluteDirectory(driveRoot);
            ManagedProfileVolumeCapabilities capabilities;
            try { capabilities = volumeCapabilityReader.Read(drive.Handle, driveRoot); }
            catch
            {
                drive.Dispose();
                throw Policy(ManagedBrowserProfileIsolationReason.UnsupportedVolume);
            }
            if (capabilities.DriveKind != ManagedProfileDriveKind.Fixed ||
                !capabilities.SupportsPersistentAcls || !capabilities.SupportsStableFileIds ||
                !capabilities.SupportsAtomicRename)
            {
                drive.Dispose();
                throw Policy(ManagedBrowserProfileIsolationReason.UnsupportedVolume);
            }
            var ns = new PinnedNamespace(drive);
            ns.owned.Add(drive); ns.tracked.Add((driveRoot, drive, false));
            var currentPath = driveRoot; var parent = drive;
            try
            {
                foreach (var component in Path.GetRelativePath(driveRoot, appDataRoot).Split(
                    Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    ValidateRelativeName(component);
                    var child = TryOpenRelativeDirectory(parent, component, false);
                    if (child is null)
                    {
                        if (!createAppRoot) throw Policy(ManagedBrowserProfileIsolationReason.MissingMarker);
                        try { child = CreateRelativeDirectory(parent, component, false); }
                        catch (ManagedProfilePolicyException exception) when (
                            exception.Reason == ManagedBrowserProfileIsolationReason.ConcurrentMutation)
                        {
                            child = TryOpenRelativeDirectory(parent, component, true) ??
                                throw Policy(ManagedBrowserProfileIsolationReason.ConcurrentMutation);
                        }
                    }
                    currentPath = Path.Combine(currentPath, component);
                    ns.owned.Add(child); ns.tracked.Add((currentPath, child, false)); parent = child;
                }
                ns.AppRoot = parent; ns.named[AppRootKey] = parent;
                return ns;
            }
            catch { ns.Dispose(); throw; }
        }

        public PinnedEntry OpenOrCreateOwnedDirectory(string parentKey, string name)
        {
            var parent = named[parentKey];
            var entry = TryOpenRelativeDirectory(parent, name, true);
            if (entry is null)
            {
                try { entry = CreateRelativeDirectory(parent, name, false); }
                catch (ManagedProfilePolicyException exception) when (
                    exception.Reason == ManagedBrowserProfileIsolationReason.ConcurrentMutation)
                {
                    entry = TryOpenRelativeDirectory(parent, name, true) ??
                        throw Policy(ManagedBrowserProfileIsolationReason.ConcurrentMutation);
                }
            }
            named[name] = entry; owned.Add(entry);
            var parentPath = tracked.First(x => ReferenceEquals(x.Entry, parent)).Path;
            tracked.Add((Path.Combine(parentPath, name), entry, true)); return entry;
        }
        public PinnedEntry CreateOwnedDirectory(PinnedEntry parent, string name, bool rename) =>
            CreateRelativeDirectory(parent, name, rename);
        public PinnedEntry? TryOpenOwnedDirectory(PinnedEntry parent, string name) =>
            TryOpenRelativeDirectory(parent, name, true);
        public PinnedEntry CreateOwnedMarker(PinnedEntry parent, string name) => CreateRelativeMarker(parent, name);
        public static PinnedEntry? TryOpenMarkerStatic(PinnedEntry parent, string name) =>
            OpenRelative(parent, name, NativeMethods.GenericRead | NativeMethods.ReadControl | NativeMethods.Synchronize,
                NativeMethods.FileOpen, NativeMethods.FileNonDirectoryFile | NativeMethods.FileSynchronousIoNonAlert |
                NativeMethods.FileOpenReparsePoint, null, true, false, false, NativeMethods.FileShareRead);

        public static bool RelativeIdentityMatchesStatic(PinnedEntry parent, string name, PinnedEntry expected)
        {
            var current = TryOpenMarkerStatic(parent, name);
            if (current is null) return false;
            using (current) return current.SameIdentity(expected);
        }

        public bool TryPublish(PinnedEntry staging, PinnedEntry familyRoot, string accountName)
        {
            ValidateRelativeName(accountName);
            var bytes = Encoding.Unicode.GetBytes(accountName);
            var offset = Marshal.OffsetOf<NativeMethods.FileRenameInformation>(nameof(NativeMethods.FileRenameInformation.FileName)).ToInt32();
            var size = offset + bytes.Length; var buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteIntPtr(buffer, Marshal.OffsetOf<NativeMethods.FileRenameInformation>(
                    nameof(NativeMethods.FileRenameInformation.RootDirectory)).ToInt32(), familyRoot.Handle.DangerousGetHandle());
                Marshal.WriteInt32(buffer, Marshal.OffsetOf<NativeMethods.FileRenameInformation>(
                    nameof(NativeMethods.FileRenameInformation.FileNameLength)).ToInt32(), bytes.Length);
                Marshal.Copy(bytes, 0, IntPtr.Add(buffer, offset), bytes.Length);
                var status = NativeMethods.NtSetInformationFile(staging.Handle, out _, buffer, (uint)size,
                    NativeMethods.FileRenameInformationClass);
                if (status >= 0) return true;
                var existing = TryOpenRelativeDirectory(familyRoot, accountName, false);
                existing?.Dispose(); return existing is not null ? false :
                    throw Policy(status == NativeMethods.StatusAccessDenied
                        ? ManagedBrowserProfileIsolationReason.AccessControlMismatch
                        : ManagedBrowserProfileIsolationReason.ConcurrentMutation);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public bool RelativeIdentityMatches(PinnedEntry parent, string name, PinnedEntry expected)
        {
            var current = TryOpenRelativeDirectory(parent, name, false); if (current is null) return false;
            using (current) return current.SameIdentity(expected);
        }
        public void TrackPublished(string path, PinnedEntry published) => TrackOwned(path, published);
        public void TrackOwned(string path, PinnedEntry entry) => tracked.Add((path, entry, true));
        public bool Revalidate()
        {
            foreach (var item in tracked)
            {
                if (item.RequireOwnedAcl && !item.Entry.HasCurrentUserOnlyAcl())
                    throw Policy(ManagedBrowserProfileIsolationReason.AccessControlMismatch);
                PinnedEntry? current;
                try { current = TryOpenAbsoluteDirectory(item.Path); }
                catch (ManagedProfilePolicyException) { return false; }
                if (current is null) return false;
                using (current) if (!current.SameIdentity(item.Entry)) return false;
            }
            return true;
        }
        public void Dispose() { for (var i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); }
    }

    private sealed class PinnedEntry : IDisposable
    {
        private const int MaxMarkerBytes = 512;
        private readonly NativeMethods.ByHandleFileInformation information;
        public PinnedEntry(SafeFileHandle handle, NativeMethods.ByHandleFileInformation information)
        {
            Handle = handle;
            this.information = information;
        }
        public SafeFileHandle Handle { get; }
        public bool IsDirectory => (information.FileAttributes & FileAttributes.Directory) != 0;
        public bool IsReparsePoint => (information.FileAttributes & FileAttributes.ReparsePoint) != 0;
        public bool IsOrdinaryFile => !IsDirectory && !IsReparsePoint;
        public bool SameIdentity(PinnedEntry other) => information.VolumeSerialNumber == other.information.VolumeSerialNumber &&
            information.FileIndexHigh == other.information.FileIndexHigh && information.FileIndexLow == other.information.FileIndexLow;
        public PinnedAclSnapshot CaptureAcl()
        {
            if (!OperatingSystem.IsWindows()) return new([], false);
            var result = NativeMethods.GetSecurityInfo(Handle, NativeMethods.SeFileObject,
                NativeMethods.OwnerSecurityInformation | NativeMethods.DaclSecurityInformation,
                out _, out _, out _, out _, out var pointer);
            if (result != 0 || pointer == IntPtr.Zero) return new([], false);
            try
            {
                var bytes = new byte[checked((int)NativeMethods.GetSecurityDescriptorLength(pointer))];
                Marshal.Copy(pointer, bytes, 0, bytes.Length); var descriptor = new RawSecurityDescriptor(bytes, 0);
                var user = WindowsIdentity.GetCurrent().User;
                if (user is null || descriptor.Owner != user ||
                    (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                    descriptor.DiscretionaryAcl is null) return new(bytes, false);
                var count = 0;
                foreach (GenericAce generic in descriptor.DiscretionaryAcl)
                {
                    if (generic is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed ||
                        ace.SecurityIdentifier != user || (ace.AccessMask & NativeMethods.FileAllAccess) != NativeMethods.FileAllAccess)
                        return new(bytes, false);
                    count++;
                }
                return new(bytes, count > 0);
            }
            finally { NativeMethods.LocalFree(pointer); }
        }
        public bool HasCurrentUserOnlyAcl() => CaptureAcl().IsCurrentUserOnly;
        public void WriteCanonicalText(string text, Action? hook)
        {
            var bytes = Encoding.ASCII.GetBytes(text); var prefix = bytes.Length / 2;
            Write(bytes.AsSpan(0, prefix).ToArray());
            if (!NativeMethods.FlushFileBuffers(Handle)) ThrowNative("marker flush");
            hook?.Invoke(); Write(bytes.AsSpan(prefix).ToArray());
            if (!NativeMethods.FlushFileBuffers(Handle)) ThrowNative("marker flush");
        }
        public string ReadCanonicalText()
        {
            if (!NativeMethods.SetFilePointerEx(Handle, 0, out _, NativeMethods.FileBegin)) ThrowNative("marker seek");
            var buffer = new byte[MaxMarkerBytes + 1];
            if (!NativeMethods.ReadFile(Handle, buffer, (uint)buffer.Length, out var read, IntPtr.Zero)) ThrowNative("marker read");
            if (read > MaxMarkerBytes) throw Policy(ManagedBrowserProfileIsolationReason.InvalidMarker);
            return Encoding.ASCII.GetString(buffer, 0, checked((int)read));
        }
        private void Write(byte[] bytes)
        {
            if (!NativeMethods.WriteFile(Handle, bytes, (uint)bytes.Length, out var written, IntPtr.Zero) || written != bytes.Length)
                ThrowNative("marker write");
        }
        public void Dispose() => Handle.Dispose();
    }

    private readonly record struct PinnedAclSnapshot(byte[] BinaryDescriptor, bool IsCurrentUserOnly);

    private static PinnedEntry OpenAbsoluteDirectory(string path) => TryOpenAbsoluteDirectory(path) ??
        throw Policy(ManagedBrowserProfileIsolationReason.UnsafePath);
    private static PinnedEntry? TryOpenAbsoluteDirectory(string path)
    {
        var handle = NativeMethods.CreateFileW(path, NativeMethods.FileReadAttributes | NativeMethods.ReadControl,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite, IntPtr.Zero, NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error is NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound) return null;
            throw Policy(error == NativeMethods.ErrorAccessDenied ? ManagedBrowserProfileIsolationReason.AccessControlMismatch :
                ManagedBrowserProfileIsolationReason.UnsafePath);
        }
        return Inspect(handle, true);
    }
    private static PinnedEntry? TryOpenRelativeDirectory(PinnedEntry parent, string name, bool owned) =>
        OpenRelative(parent, name, NativeMethods.FileReadAttributes | NativeMethods.ReadControl | NativeMethods.Synchronize,
            NativeMethods.FileOpen, NativeMethods.FileDirectoryFile | NativeMethods.FileSynchronousIoNonAlert |
            NativeMethods.FileOpenReparsePoint, null, true, true, owned);
    private static PinnedEntry CreateRelativeDirectory(PinnedEntry parent, string name, bool rename)
    {
        using var security = SecurityDescriptorBuffer.Create(true);
        var access = NativeMethods.FileReadAttributes | NativeMethods.ReadControl | NativeMethods.Synchronize |
            (rename ? NativeMethods.Delete : 0);
        return OpenRelative(parent, name, access, NativeMethods.FileCreate,
            NativeMethods.FileDirectoryFile | NativeMethods.FileSynchronousIoNonAlert | NativeMethods.FileOpenReparsePoint,
            security, false, true, true) ?? throw Policy(ManagedBrowserProfileIsolationReason.ConcurrentMutation);
    }
    private static PinnedEntry CreateRelativeMarker(PinnedEntry parent, string name)
    {
        using var security = SecurityDescriptorBuffer.Create(false);
        return OpenRelative(parent, name, NativeMethods.GenericRead | NativeMethods.GenericWrite | NativeMethods.ReadControl |
            NativeMethods.Synchronize, NativeMethods.FileCreate, NativeMethods.FileNonDirectoryFile |
            NativeMethods.FileSynchronousIoNonAlert | NativeMethods.FileOpenReparsePoint | NativeMethods.FileWriteThrough,
            security, false, false, true) ?? throw Policy(ManagedBrowserProfileIsolationReason.ConcurrentMutation);
    }
    private static PinnedEntry? OpenRelative(PinnedEntry parent, string name, uint access, uint disposition,
        uint options, SecurityDescriptorBuffer? security, bool missingNull, bool directory, bool owned,
        uint shareAccess = NativeMethods.FileShareRead | NativeMethods.FileShareWrite)
    {
        ValidateRelativeName(name); using var relative = new RelativeName(name);
        var attributes = new NativeMethods.ObjectAttributes
        {
            Length = Marshal.SizeOf<NativeMethods.ObjectAttributes>(),
            RootDirectory = parent.Handle.DangerousGetHandle(),
            ObjectName = relative.UnicodeStringPointer,
            Attributes = NativeMethods.ObjCaseInsensitive,
            SecurityDescriptor = security?.Pointer ?? IntPtr.Zero
        };
        var status = NativeMethods.NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero,
            directory ? NativeMethods.FileAttributeDirectory : NativeMethods.FileAttributeNormal,
            shareAccess, disposition, options, IntPtr.Zero, 0);
        if (status < 0)
        {
            handle?.Dispose();
            if (missingNull && status is NativeMethods.StatusObjectNameNotFound or NativeMethods.StatusObjectPathNotFound) return null;
            throw Policy(status == NativeMethods.StatusAccessDenied ? ManagedBrowserProfileIsolationReason.AccessControlMismatch :
              status == NativeMethods.StatusSharingViolation ? ManagedBrowserProfileIsolationReason.ConcurrentMutation :
              status == NativeMethods.StatusObjectNameCollision ? ManagedBrowserProfileIsolationReason.ConcurrentMutation :
                ManagedBrowserProfileIsolationReason.UnsafePath);
        }
        var entry = Inspect(handle, directory);
        if (owned && !entry.HasCurrentUserOnlyAcl()) { entry.Dispose(); throw Policy(ManagedBrowserProfileIsolationReason.AccessControlMismatch); }
        return entry;
    }
    private static PinnedEntry Inspect(SafeFileHandle handle, bool directory)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var info)) { handle.Dispose(); ThrowNative("entry inspection"); }
        var entry = new PinnedEntry(handle, info);
        if (entry.IsReparsePoint || entry.IsDirectory != directory) { entry.Dispose(); throw Policy(ManagedBrowserProfileIsolationReason.UnsafePath); }
        return entry;
    }
    private static void ValidateRelativeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Any(char.IsControl) ||
            name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar) || name.Contains(':'))
            throw Policy(ManagedBrowserProfileIsolationReason.UnsupportedNamespace);
    }
    private static void ThrowNative(string operation) => throw new IOException(
        $"The managed browser profile {operation} failed.", new Win32Exception(Marshal.GetLastWin32Error()));

    private sealed class RelativeName : IDisposable
    {
        private readonly IntPtr text;
        public RelativeName(string name)
        {
            text = Marshal.StringToHGlobalUni(name); var unicode = new NativeMethods.UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = text
            };
            UnicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.UnicodeString>());
            Marshal.StructureToPtr(unicode, UnicodeStringPointer, false);
        }
        public IntPtr UnicodeStringPointer { get; }
        public void Dispose() { Marshal.FreeHGlobal(UnicodeStringPointer); Marshal.FreeHGlobal(text); }
    }
    private sealed class SecurityDescriptorBuffer : IDisposable
    {
        private readonly GCHandle pinned;
        private SecurityDescriptorBuffer(byte[] bytes) { pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned); Pointer = pinned.AddrOfPinnedObject(); }
        public IntPtr Pointer { get; }
        public static SecurityDescriptorBuffer Create(bool directory)
        {
            if (!OperatingSystem.IsWindows())
                throw Policy(ManagedBrowserProfileIsolationReason.UnsupportedNamespace);
            var user = WindowsIdentity.GetCurrent().User ?? throw Policy(ManagedBrowserProfileIsolationReason.AccessControlMismatch);
            FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
            security.SetOwner(user); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(directory ? new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow) :
                new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            return new SecurityDescriptorBuffer(security.GetSecurityDescriptorBinaryForm());
        }
        public void Dispose() { if (pinned.IsAllocated) pinned.Free(); }
    }

    private static class NativeMethods
    {
        internal const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, Delete = 0x00010000,
            ReadControl = 0x00020000, Synchronize = 0x00100000, FileReadAttributes = 0x80,
            FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3, FileFlagBackupSemantics = 0x02000000,
            FileFlagOpenReparsePoint = 0x00200000, FileCreate = 2, FileOpen = 1, FileDirectoryFile = 1,
            FileWriteThrough = 2, FileSynchronousIoNonAlert = 0x20, FileNonDirectoryFile = 0x40,
            FileOpenReparsePoint = 0x00200000, FileAttributeDirectory = 0x10, FileAttributeNormal = 0x80,
            ObjCaseInsensitive = 0x40, OwnerSecurityInformation = 1, DaclSecurityInformation = 4, FileBegin = 0;
        internal const int StatusObjectNameNotFound = unchecked((int)0xC0000034),
            StatusObjectPathNotFound = unchecked((int)0xC000003A), StatusObjectNameCollision = unchecked((int)0xC0000035),
            StatusAccessDenied = unchecked((int)0xC0000022), StatusSharingViolation = unchecked((int)0xC0000043),
            ErrorFileNotFound = 2, ErrorPathNotFound = 3,
            ErrorAccessDenied = 5, FileAllAccess = 0x001F01FF, SeFileObject = 1;
        internal const int FileRenameInformationClass = 10;
        [StructLayout(LayoutKind.Sequential)] internal struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ObjectAttributes
        { public int Length; public IntPtr RootDirectory, ObjectName; public uint Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct FileRenameInformation
        { public int ReplaceIfExists; public IntPtr RootDirectory; public uint FileNameLength; public char FileName; }
        [StructLayout(LayoutKind.Sequential)] internal struct FileTime { public uint LowDateTime, HighDateTime; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public FileAttributes FileAttributes; public FileTime CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
        [DllImport("ntdll.dll")]
        internal static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess,
            ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize,
            uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);
        [DllImport("ntdll.dll")]
        internal static extern int NtSetInformationFile(SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int fileInformationClass);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle file, out ByHandleFileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(
            SafeFileHandle file, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteFile(
            SafeFileHandle file, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FlushFileBuffers(SafeFileHandle file);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFilePointerEx(
            SafeFileHandle file, long distance, out long newPointer, uint method);
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern uint GetSecurityInfo(SafeFileHandle handle,
            int objectType, uint securityInfo, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl,
            out IntPtr securityDescriptor);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
