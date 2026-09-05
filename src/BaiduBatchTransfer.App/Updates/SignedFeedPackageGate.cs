using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BaiduBatchTransfer.App.Updates;

internal sealed class SignedFeedPackageGate
{
    internal static readonly TimeSpan ReceiptLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);
    private const string ExpectedPackageId = "SuperPan.BaiduBatchTransfer";
    private const string ExpectedChannel = "win-x64-stable";
    private const string ExpectedType = "Full";

    private readonly object sync = new();
    private readonly SignedPendingUpdateReceiptStore receiptStore;
    private readonly SignedFeedStateStore stateStore;
    private readonly TimeProvider timeProvider;
    private readonly string expectedPackageRoot;

    internal SignedFeedPackageGate(
        string receiptPath,
        SignedFeedStateStore stateStore,
        string expectedPackageRoot)
        : this(
            receiptPath,
            stateStore,
            new WindowsCurrentUserPendingReceiptProtector(),
            TimeProvider.System,
            expectedPackageRoot)
    {
    }

    private SignedFeedPackageGate(
        string receiptPath,
        SignedFeedStateStore stateStore,
        ISignedPendingUpdateReceiptProtector protector,
        TimeProvider timeProvider,
        string expectedPackageRoot)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackageRoot);
        if (!Path.IsPathFullyQualified(expectedPackageRoot))
            throw new ArgumentException("The Velopack package root must be absolute.", nameof(expectedPackageRoot));
        this.expectedPackageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPackageRoot));
        receiptStore = new SignedPendingUpdateReceiptStore(receiptPath, protector);
    }

    internal static SignedFeedPackageGate CreateForTesting(
        string receiptPath,
        SignedFeedStateStore stateStore,
        ISignedPendingUpdateReceiptProtector protector,
        TimeProvider timeProvider,
        string expectedPackageRoot) =>
        new(receiptPath, stateStore, protector, timeProvider, expectedPackageRoot);

    internal async Task<bool> AuthorizeDownloadedAsync(
        SignedFeedVerificationSnapshot snapshot,
        VelopackClientAsset pending,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pending);
        lock (sync) receiptStore.Delete();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!TryValidateSnapshot(snapshot, pending, out var asset))
                return false;

            var actualHash = await HashPackageAsync(pending, asset.Size, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!FixedTimeHashEquals(actualHash, asset.Sha256))
                return false;

            var receipt = new SignedPendingUpdateReceipt(
                snapshot.FeedIdentity,
                expectedPackageRoot,
                asset.PackageId,
                ExpectedChannel,
                asset.Version,
                asset.Type,
                asset.FileName,
                asset.Size,
                asset.Sha256.ToLowerInvariant(),
                snapshot.ManifestSha256,
                timeProvider.GetUtcNow().ToUnixTimeSeconds());
            lock (sync) receiptStore.Write(receipt);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (sync) receiptStore.Delete();
            throw;
        }
        catch (Exception exception) when (IsValidationFailure(exception))
        {
            lock (sync) receiptStore.Delete();
            return false;
        }
    }

    internal bool TryRevalidatePending(VelopackClientAsset pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        FileStream? lease = null;
        try
        {
            lock (sync)
            {
                var receipt = receiptStore.Read();
                if (!TryValidateReceipt(receipt, pending))
                {
                    receiptStore.Delete();
                    return false;
                }
                lease = OpenPackageForValidation(pending, receipt.Size);
                var hash = LowerHash(lease);
                var valid = FixedTimeHashEquals(hash, receipt.Sha256);
                if (!valid) receiptStore.Delete();
                return valid;
            }
        }
        catch (Exception exception) when (IsValidationFailure(exception))
        {
            TryInvalidateReceipt();
            return false;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    internal SignedPackageLaunchAuthorization? TryAcquireLaunchAuthorization(VelopackClientAsset pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        try
        {
            lock (sync)
            {
                var receipt = receiptStore.Read();
                if (!TryValidateReceipt(receipt, pending))
                {
                    receiptStore.Delete();
                    return null;
                }
                var lease = OpenPackageForValidation(pending, receipt.Size);
                try
                {
                    var hash = LowerHash(lease);
                    if (!FixedTimeHashEquals(hash, receipt.Sha256))
                    {
                        lease.Dispose();
                        receiptStore.Delete();
                        return null;
                    }
                    lease.Position = 0;
                    return new SignedPackageLaunchAuthorization(
                        Path.GetFullPath(pending.LocalPackagePath!),
                        lease);
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
            }
        }
        catch (Exception exception) when (IsValidationFailure(exception))
        {
            TryInvalidateReceipt();
            return null;
        }
    }

    private bool TryValidateSnapshot(
        SignedFeedVerificationSnapshot snapshot,
        VelopackClientAsset pending,
        out SignedFeedVerifiedAsset asset)
    {
        asset = null!;
        if (!string.Equals(snapshot.FeedIdentity, stateStore.FeedIdentity, StringComparison.Ordinal) ||
            !IsSha256(snapshot.ManifestSha256) ||
            string.IsNullOrEmpty(pending.Fingerprint.FileName) ||
            !snapshot.Assets.TryGetValue(pending.Fingerprint.FileName, out asset!) ||
            !AssetMatches(asset, pending.Fingerprint) ||
            !string.Equals(snapshot.HighestVersion, asset.Version, StringComparison.Ordinal))
        {
            return false;
        }

        var accepted = stateStore.Read();
        return string.Equals(accepted.FeedIdentity, snapshot.FeedIdentity, StringComparison.Ordinal) &&
               string.Equals(accepted.HighestAcceptedVersion, snapshot.HighestVersion, StringComparison.Ordinal) &&
               FixedTimeHashEquals(accepted.ManifestSha256, snapshot.ManifestSha256);
    }

    private bool TryValidateReceipt(SignedPendingUpdateReceipt receipt, VelopackClientAsset pending)
    {
        var fingerprint = pending.Fingerprint;
        if (!string.Equals(receipt.FeedIdentity, stateStore.FeedIdentity, StringComparison.Ordinal) ||
            !string.Equals(receipt.PackageRoot, expectedPackageRoot, PathComparison) ||
            !string.Equals(receipt.PackageId, ExpectedPackageId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Channel, ExpectedChannel, StringComparison.Ordinal) ||
            !string.Equals(receipt.Type, ExpectedType, StringComparison.Ordinal) ||
            !AssetMatches(receipt, fingerprint) ||
            !IsSha256(receipt.ManifestSha256))
        {
            return false;
        }

        DateTimeOffset authorized;
        try { authorized = DateTimeOffset.FromUnixTimeSeconds(receipt.AuthorizedAtUnixSeconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        var now = timeProvider.GetUtcNow();
        if (authorized > now + MaximumFutureClockSkew || now - authorized > ReceiptLifetime)
            return false;

        var accepted = stateStore.Read();
        return string.Equals(accepted.FeedIdentity, receipt.FeedIdentity, StringComparison.Ordinal) &&
               string.Equals(accepted.HighestAcceptedVersion, receipt.Version, StringComparison.Ordinal) &&
               FixedTimeHashEquals(accepted.ManifestSha256, receipt.ManifestSha256);
    }

    private static bool AssetMatches(SignedFeedVerifiedAsset asset, VelopackAssetFingerprint fingerprint) =>
        string.Equals(asset.PackageId, ExpectedPackageId, StringComparison.Ordinal) &&
        string.Equals(asset.PackageId, fingerprint.PackageId, StringComparison.Ordinal) &&
        string.Equals(asset.Version, fingerprint.Version, StringComparison.Ordinal) &&
        string.Equals(asset.Type, ExpectedType, StringComparison.Ordinal) &&
        string.Equals(asset.Type, fingerprint.AssetType, StringComparison.Ordinal) &&
        string.Equals(asset.FileName, fingerprint.FileName, StringComparison.Ordinal) &&
        asset.Size == fingerprint.Size &&
        asset.Size is > 0 and <= SignedFeedUpdateSource.MaxPackageBytes &&
        IsSha256(asset.Sha256) &&
        LocalHashMatchesSignedHash(asset.Sha256, fingerprint.Sha256) &&
        string.Equals(
            asset.FileName,
            $"{ExpectedPackageId}-{asset.Version}-full.nupkg",
            StringComparison.Ordinal);

    private static bool AssetMatches(SignedPendingUpdateReceipt receipt, VelopackAssetFingerprint fingerprint) =>
        string.Equals(receipt.PackageId, fingerprint.PackageId, StringComparison.Ordinal) &&
        string.Equals(receipt.Version, fingerprint.Version, StringComparison.Ordinal) &&
        string.Equals(receipt.Type, fingerprint.AssetType, StringComparison.Ordinal) &&
        string.Equals(receipt.FileName, fingerprint.FileName, StringComparison.Ordinal) &&
        receipt.Size == fingerprint.Size &&
        receipt.Size is > 0 and <= SignedFeedUpdateSource.MaxPackageBytes &&
        IsSha256(receipt.Sha256) &&
        LocalHashMatchesSignedHash(receipt.Sha256, fingerprint.Sha256) &&
        string.Equals(
            receipt.FileName,
            $"{ExpectedPackageId}-{receipt.Version}-full.nupkg",
            StringComparison.Ordinal);

    private async Task<string> HashPackageAsync(
        VelopackClientAsset pending,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        await using var stream = OpenPackageForValidation(pending, expectedSize);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hasher.AppendData(buffer, 0, read);
        }
        if (stream.Length != expectedSize)
            throw new InvalidDataException("The pending update package length changed during verification.");
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private FileStream OpenPackageForValidation(VelopackClientAsset pending, long expectedSize)
    {
        if (string.IsNullOrWhiteSpace(pending.LocalPackagePath) ||
            !Path.IsPathFullyQualified(pending.LocalPackagePath) ||
            !string.Equals(Path.GetFileName(pending.LocalPackagePath), pending.Fingerprint.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The pending update package path is invalid.");
        }
        var packagePath = Path.GetFullPath(pending.LocalPackagePath);
        var expectedPath = Path.GetFullPath(Path.Combine(expectedPackageRoot, pending.Fingerprint.FileName));
        if (!string.Equals(packagePath, expectedPath, PathComparison) ||
            !string.Equals(Path.GetDirectoryName(packagePath), expectedPackageRoot, PathComparison) ||
            new FileInfo(packagePath).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The pending update package is outside the pinned package root.");
        }
        ValidateRootAncestors();
        var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        ValidateOpenedHandlePath(stream.SafeFileHandle, packagePath);
        if (stream.Length != expectedSize)
        {
            stream.Dispose();
            throw new InvalidDataException("The pending update package length is invalid.");
        }
        return stream;
    }

    private static void ValidateOpenedHandlePath(SafeFileHandle handle, string expectedPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        var buffer = new StringBuilder(32_768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new InvalidDataException("The pending update package handle could not be resolved.");
        var resolved = NormalizeWindowsFinalPath(buffer.ToString());
        if (!string.Equals(Path.GetFullPath(expectedPath), resolved, PathComparison))
            throw new InvalidDataException("The pending update package handle resolves outside its trusted path.");
    }

    private static string NormalizeWindowsFinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[uncPrefix.Length..];
        return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
            ? path[devicePrefix.Length..]
            : path;
    }

    private void ValidateRootAncestors()
    {
        var current = new DirectoryInfo(expectedPackageRoot);
        while (current is not null)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The pinned package root contains a reparse point.");
            current = current.Parent;
        }
    }

    private void TryInvalidateReceipt()
    {
        try { lock (sync) receiptStore.Delete(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal void InvalidateReceipt() => TryInvalidateReceipt();

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string LowerHash(Stream stream)
    {
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool FixedTimeHashEquals(string left, string right)
    {
        if (!IsSha256(left) || !IsSha256(right)) return false;
        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static bool LocalHashMatchesSignedHash(string signedHash, string localHash) =>
        string.IsNullOrEmpty(localHash) || FixedTimeHashEquals(signedHash, localHash);

    private static bool IsValidationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or CryptographicException or
            InvalidDataException or ArgumentException or NotSupportedException;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathLength,
        uint flags);
}

internal sealed class SignedPackageLaunchAuthorization : IDisposable
{
    private FileStream? lease;

    internal SignedPackageLaunchAuthorization(string packagePath, FileStream lease)
    {
        PackagePath = packagePath;
        this.lease = lease;
    }

    internal string PackagePath { get; }

    public void Dispose() => Interlocked.Exchange(ref lease, null)?.Dispose();
}
