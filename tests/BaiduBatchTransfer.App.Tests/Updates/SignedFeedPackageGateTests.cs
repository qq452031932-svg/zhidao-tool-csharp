using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.App.Updates;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class SignedFeedPackageGateTests : IDisposable
{
    private const string FeedIdentity = "https://updates.example.invalid/releases/win-x64/stable/";
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"signed-package-gate-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExactDownloadedPackageCreatesAuthenticatedReceiptAndRevalidates()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));

        Assert.True(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            CancellationToken.None));
        Assert.True(File.Exists(fixture.ReceiptPath));
        Assert.True(fixture.Gate.TryRevalidatePending(fixture.Pending));
    }

    [Fact]
    public async Task ChangedPackageBytesNeverProduceOrRetainAReceipt()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        await fixture.Gate.AuthorizeDownloadedAsync(fixture.Snapshot, fixture.Pending, CancellationToken.None);
        await File.WriteAllTextAsync(fixture.PackagePath, "tampered bytes");

        Assert.False(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            CancellationToken.None));
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Theory]
    [InlineData(1, "00")]
    [InlineData(14, "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public async Task WrongSignedLengthOrHashIsRejected(long declaredSize, string declaredHash)
    {
        var fixture = CreateFixture(
            "2.0.0",
            Encoding.UTF8.GetBytes("signed package"),
            declaredSize,
            declaredHash);

        Assert.False(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            CancellationToken.None));
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task FilenameNotListedByLastVerifiedSnapshotIsRejected()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        var unlisted = fixture.Pending with
        {
            Fingerprint = fixture.Pending.Fingerprint with { FileName = "unlisted.nupkg" }
        };

        Assert.False(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            unlisted,
            CancellationToken.None));
    }

    [Theory]
    [InlineData("Other.Package", "Full")]
    [InlineData("SuperPan.BaiduBatchTransfer", "Delta")]
    public async Task WrongPackageIdentityOrTypeIsRejected(string packageId, string type)
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        var wrong = fixture.Pending with
        {
            Fingerprint = fixture.Pending.Fingerprint with
            {
                PackageId = packageId,
                AssetType = type
            }
        };

        Assert.False(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            wrong,
            CancellationToken.None));
    }

    [Fact]
    public async Task MissingCorruptOrUnauthenticatedReceiptFailsClosed()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        Assert.False(fixture.Gate.TryRevalidatePending(fixture.Pending));

        await File.WriteAllBytesAsync(fixture.ReceiptPath, [1, 2, 3, 4]);
        Assert.False(fixture.Gate.TryRevalidatePending(fixture.Pending));

        await File.WriteAllBytesAsync(
            fixture.ReceiptPath,
            fixture.Protector.Protect(Encoding.UTF8.GetBytes("{\"schemaVersion\":1}")));
        Assert.False(fixture.Gate.TryRevalidatePending(fixture.Pending));
    }

    [Fact]
    public async Task MalformedPendingPackagePathFailsClosed()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        Assert.True(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            CancellationToken.None));
        var malformed = fixture.Pending with
        {
            LocalPackagePath = $"C:\\invalid{(char)0}{fixture.Pending.Fingerprint.FileName}"
        };

        Assert.False(fixture.Gate.TryRevalidatePending(malformed));
        Assert.Null(fixture.Gate.TryAcquireLaunchAuthorization(malformed));
    }

    [Fact]
    public async Task ReceiptOlderThanSevenDaysIsRejected()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        Assert.True(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            CancellationToken.None));

        fixture.Time.SetUtcNow(Now.AddDays(7).AddTicks(1));

        Assert.False(fixture.Gate.TryRevalidatePending(fixture.Pending));
    }

    [Fact]
    public async Task ReceiptFutureClockSkewIsBoundedToFiveMinutes()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        Assert.True(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            CancellationToken.None));

        fixture.Time.SetUtcNow(Now.AddMinutes(-5));
        Assert.True(fixture.Gate.TryRevalidatePending(fixture.Pending));
        fixture.Time.SetUtcNow(Now.AddMinutes(-5).AddSeconds(-1));
        Assert.False(fixture.Gate.TryRevalidatePending(fixture.Pending));
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task CancellationThatArrivesDuringReceiptProtectionDeletesCommittedReceipt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protector = new BlockingProtector(entered, release);
        var fixture = CreateFixture(
            "2.0.0",
            Encoding.UTF8.GetBytes("signed package"),
            protector: protector);
        using var cancellation = new CancellationTokenSource();

        var authorization = Task.Run(() => fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            cancellation.Token));
        await entered.Task;
        cancellation.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorization);
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task PendingPackageMustRemainADirectChildOfPinnedVelopackPackageRoot()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        Assert.True(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            CancellationToken.None));
        var outsideRoot = Directory.CreateTempSubdirectory("signed-package-outside-");
        try
        {
            var outsidePath = Path.Combine(outsideRoot.FullName, fixture.Pending.Fingerprint.FileName);
            File.Copy(fixture.PackagePath, outsidePath);
            var outsidePending = fixture.Pending with { LocalPackagePath = outsidePath };

            Assert.False(fixture.Gate.TryRevalidatePending(outsidePending));
            Assert.False(File.Exists(fixture.ReceiptPath));
        }
        finally
        {
            outsideRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GuardRevalidatesAfterParentExitBeforeInvokingUpdater()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        Assert.True(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            CancellationToken.None));
        var updaterCalls = 0;

        var launched = SignedUpdateGuard.ValidateAndLaunchAfterParentExitForTesting(
            () => File.WriteAllText(fixture.PackagePath, "tampered while parent exits"),
            () => fixture.Pending,
            fixture.Gate,
            _ => updaterCalls++);

        Assert.False(launched);
        Assert.Equal(0, updaterCalls);
    }

    [Fact]
    public async Task PendingVersionBelowPersistedReplayFloorIsRejected()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        Assert.True(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            CancellationToken.None));
        fixture.StateStore.Accept("3.0.0", new string('b', 64));

        Assert.False(fixture.Gate.TryRevalidatePending(fixture.Pending));
    }

    [Fact]
    public async Task CancellationDuringHashingRestoresNoReceipt()
    {
        var bytes = new byte[8 * 1024 * 1024];
        RandomNumberGenerator.Fill(bytes);
        var fixture = CreateFixture("2.0.0", bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Gate.AuthorizeDownloadedAsync(
                fixture.Snapshot,
                fixture.Pending,
                cancellation.Token));
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task ManifestDigestMustMatchTheAcceptedReplayState()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        var wrongSnapshot = fixture.Snapshot with { ManifestSha256 = new string('c', 64) };

        Assert.False(await fixture.Gate.AuthorizeDownloadedAsync(
            wrongSnapshot,
            fixture.Pending,
            CancellationToken.None));
    }

    [Fact]
    public async Task RevalidationImmediatelyBeforeLaunchRejectsPostPreparationTampering()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        Assert.True(await fixture.Gate.AuthorizeDownloadedAsync(
            fixture.Snapshot,
            fixture.Pending,
            CancellationToken.None));
        Assert.True(fixture.Gate.TryRevalidatePending(fixture.Pending));

        await File.WriteAllTextAsync(fixture.PackagePath, "tampered after preparation");

        Assert.Null(fixture.Gate.TryAcquireLaunchAuthorization(fixture.Pending));
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task RuntimeNeverBecomesReadyWhenPostDownloadGateRejectsThePackage()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        await File.WriteAllTextAsync(fixture.PackagePath, "tampered package");
        var client = new GateClient(fixture.Pending, fixture.Pending);
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0", fixture.Gate);
        await runtime.CheckAsync(CancellationToken.None);

        var result = await runtime.DownloadAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.VerificationFailed, result.Status);
        Assert.False(runtime.TryPrepareRestart());
        Assert.False(runtime.LaunchPreparedUpdateAndRestart());
        Assert.Equal(0, client.LaunchCalls);
    }

    [Fact]
    public async Task RuntimeRevalidatesPackageAgainImmediatelyBeforeUpdaterLaunch()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        var client = new GateClient(fixture.Pending, fixture.Pending);
        var guardLaunches = 0;
        using var runtime = new VelopackUpdateRuntime(
            client,
            "1.0.0",
            fixture.Gate,
            () =>
            {
                guardLaunches++;
                return true;
            });
        await runtime.CheckAsync(CancellationToken.None);
        Assert.Equal(
            ApplicationUpdateStatus.ReadyToRestart,
            (await runtime.DownloadAsync(CancellationToken.None)).Status);
        Assert.True(runtime.TryPrepareRestart());

        await File.WriteAllTextAsync(fixture.PackagePath, "tampered after prepare");

        Assert.Throws<ApplicationUpdateVerificationException>(
            () => runtime.LaunchPreparedUpdateAndRestart());
        Assert.Equal(0, guardLaunches);
        Assert.Equal(0, client.LaunchCalls);
    }

    [Fact]
    public async Task SignedRuntimeStartsGuardInsteadOfCallingUpdaterDirectly()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        var client = new GateClient(fixture.Pending, fixture.Pending);
        var guardLaunches = 0;
        using var runtime = new VelopackUpdateRuntime(
            client,
            "1.0.0",
            fixture.Gate,
            () =>
            {
                guardLaunches++;
                return true;
            });
        await runtime.CheckAsync(CancellationToken.None);
        await runtime.DownloadAsync(CancellationToken.None);
        Assert.True(runtime.TryPrepareRestart());

        Assert.True(runtime.LaunchPreparedUpdateAndRestart());

        Assert.Equal(1, guardLaunches);
        Assert.Equal(0, client.LaunchCalls);
    }

    [Fact]
    public async Task PreparedVerificationRejectionIsReportedWhileOrdinaryCloseRemainsSilent()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        var client = new GateClient(fixture.Pending, fixture.Pending);
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0", fixture.Gate, () => true);
        await runtime.CheckAsync(CancellationToken.None);
        await runtime.DownloadAsync(CancellationToken.None);
        Assert.True(runtime.TryPrepareRestart());
        File.WriteAllText(fixture.PackagePath, "tampered after user requested restart");
        var errors = new List<string>();
        var exits = 0;
        var orchestrator = new UpdateAwareShutdownOrchestrator(
            _ => Task.FromResult(true),
            runtime.LaunchPreparedUpdateAndRestart,
            () => { },
            errors.Add,
            () => exits++);

        Assert.True(await orchestrator.ShutdownAsync());

        Assert.Equal([UpdateAwareShutdownOrchestrator.UpdaterLaunchFailureMessage], errors);
        Assert.Equal(1, exits);
        Assert.Equal(0, client.LaunchCalls);
    }

    [Fact]
    public async Task RuntimeTreatsHexDigestCasingAsEquivalentBeforeTheByteGate()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        var upperPending = fixture.Pending with
        {
            Fingerprint = fixture.Pending.Fingerprint with
            {
                Sha256 = fixture.Pending.Fingerprint.Sha256.ToUpperInvariant()
            }
        };
        var client = new GateClient(fixture.Pending, upperPending);
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0", fixture.Gate);
        await runtime.CheckAsync(CancellationToken.None);

        var result = await runtime.DownloadAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.ReadyToRestart, result.Status);
    }

    [Fact]
    public async Task RestartedRuntimeExposesPendingOnlyWhenReceiptStillValid()
    {
        var fixture = CreateFixture("2.0.0", Encoding.UTF8.GetBytes("signed package"));
        var client = new GateClient(fixture.Pending, fixture.Pending);
        using (var firstRuntime = new VelopackUpdateRuntime(client, "1.0.0", fixture.Gate))
        {
            await firstRuntime.CheckAsync(CancellationToken.None);
            Assert.Equal(
                ApplicationUpdateStatus.ReadyToRestart,
                (await firstRuntime.DownloadAsync(CancellationToken.None)).Status);
        }

        using var restarted = new VelopackUpdateRuntime(client, "1.0.0", fixture.Gate);
        Assert.Equal(ApplicationUpdateStatus.ReadyToRestart, restarted.Snapshot.Status);

        File.Delete(fixture.ReceiptPath);
        using var withoutReceipt = new VelopackUpdateRuntime(client, "1.0.0", fixture.Gate);
        Assert.Equal(ApplicationUpdateStatus.Idle, withoutReceipt.Snapshot.Status);
        Assert.False(withoutReceipt.TryPrepareRestart());
    }

    [Fact]
    public void ProductionProtectorFailsClosedOutsideWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        var protector = new WindowsCurrentUserPendingReceiptProtector();
        Assert.Throws<PlatformNotSupportedException>(() => protector.Protect([1, 2, 3]));
        Assert.Throws<PlatformNotSupportedException>(() => protector.Unprotect([1, 2, 3]));
    }

    [Fact]
    public void ProductionProtectorUsesCurrentUserDpapiOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var protector = new WindowsCurrentUserPendingReceiptProtector();
        byte[] plaintext = [1, 2, 3, 4, 5];
        var protectedBytes = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, protectedBytes);
        Assert.Equal(plaintext, protector.Unprotect(protectedBytes));
    }

    private Fixture CreateFixture(
        string version,
        byte[] packageBytes,
        long? declaredSize = null,
        string? declaredHash = null,
        ISignedPendingUpdateReceiptProtector? protector = null)
    {
        Directory.CreateDirectory(root);
        var packageName = $"SuperPan.BaiduBatchTransfer-{version}-full.nupkg";
        var packagePath = Path.Combine(root, packageName);
        File.WriteAllBytes(packagePath, packageBytes);
        var hash = declaredHash ?? Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var size = declaredSize ?? packageBytes.LongLength;
        var manifestHash = new string('a', 64);
        var fingerprint = new VelopackAssetFingerprint(
            "SuperPan.BaiduBatchTransfer",
            version,
            "Full",
            packageName,
            size,
            hash);
        var asset = new SignedFeedVerifiedAsset(
            fingerprint.PackageId,
            fingerprint.Version,
            fingerprint.AssetType,
            fingerprint.FileName,
            fingerprint.Size,
            fingerprint.Sha256);
        var snapshot = new SignedFeedVerificationSnapshot(
            FeedIdentity,
            version,
            manifestHash,
            new Dictionary<string, SignedFeedVerifiedAsset>(StringComparer.Ordinal)
            {
                [packageName] = asset
            });
        var statePath = Path.Combine(root, $"state-{Guid.NewGuid():N}.json");
        var stateStore = new SignedFeedStateStore(statePath, FeedIdentity, "1.0.0");
        stateStore.Accept(version, manifestHash);
        var receiptPath = Path.Combine(root, $"receipt-{Guid.NewGuid():N}.bin");
        protector ??= new AuthenticatedTestProtector();
        var time = new MutableTimeProvider(Now);
        var gate = SignedFeedPackageGate.CreateForTesting(
            receiptPath,
            stateStore,
            protector,
            time,
            root);
        var pending = new VelopackClientAsset(fingerprint, null, new object())
        {
            LocalPackagePath = packagePath,
            SignedFeedSnapshot = snapshot
        };
        return new Fixture(
            gate,
            stateStore,
            protector,
            time,
            snapshot,
            pending,
            packagePath,
            receiptPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Fixture(
        SignedFeedPackageGate Gate,
        SignedFeedStateStore StateStore,
        ISignedPendingUpdateReceiptProtector Protector,
        MutableTimeProvider Time,
        SignedFeedVerificationSnapshot Snapshot,
        VelopackClientAsset Pending,
        string PackagePath,
        string ReceiptPath);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public void SetUtcNow(DateTimeOffset value) => utcNow = value;
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class AuthenticatedTestProtector : ISignedPendingUpdateReceiptProtector
    {
        private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("test receipt key"));

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var payload = plaintext.ToArray();
            var tag = HMACSHA256.HashData(Key, payload);
            return [.. tag, .. payload];
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
        {
            if (protectedData.Length < 32)
                throw new CryptographicException("bad receipt");
            var tag = protectedData[..32];
            var payload = protectedData[32..];
            var expected = HMACSHA256.HashData(Key, payload);
            if (!CryptographicOperations.FixedTimeEquals(tag, expected))
                throw new CryptographicException("bad receipt");
            return payload.ToArray();
        }
    }

    private sealed class BlockingProtector(
        TaskCompletionSource entered,
        TaskCompletionSource release) : ISignedPendingUpdateReceiptProtector
    {
        private readonly AuthenticatedTestProtector inner = new();

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return inner.Protect(plaintext);
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => inner.Unprotect(protectedData);
    }

    private sealed class GateClient(
        VelopackClientAsset checkResult,
        VelopackClientAsset pending) : IApplicationUpdateClient
    {
        public bool IsInstalled => true;
        public string? CurrentVersion => "1.0.0";
        public VelopackClientAsset? UpdatePendingRestart => pending;
        public int LaunchCalls { get; private set; }
        public Task<VelopackClientAsset?> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<VelopackClientAsset?>(checkResult);
        public Task DownloadUpdatesAsync(
            VelopackClientAsset update,
            Action<int> progress,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public void WaitExitThenApplyUpdates(VelopackClientAsset update) => LaunchCalls++;
        public void Dispose() { }
    }
}
