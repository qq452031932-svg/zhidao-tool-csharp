using System.Text;
using SuperPan.Licensing.Client;
using Xunit.Sdk;

namespace SuperPan.Licensing.Client.Tests;

public sealed class DpapiLicenseStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"superpan-license-{Guid.NewGuid():N}");

    [Fact]
    public void DpapiStoreUsesOneProductIndependentPath()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuperPan", "Licensing", "v1", "licenses.dat");

        Assert.Equal(expected, DpapiLicenseStore.GetDefaultPath());
        Assert.Equal("SuperPan.Licensing.State.v1", DpapiLicenseStore.ProtectionPurpose);
    }

    [Fact]
    public async Task DpapiStoreSharesStateAndReusesDurableActivationUuidAcrossInstances()
    {
        var path = Path.Combine(directory, "licenses.dat");
        var first = new DpapiLicenseStore(path, PassThroughProtector.Instance);
        var second = new DpapiLicenseStore(path, PassThroughProtector.Instance);

        var created = await first.GetOrCreateCredentialAsync("CARD-SECRET", CancellationToken.None);
        var reused = await second.GetOrCreateCredentialAsync("CARD-SECRET", CancellationToken.None);

        Assert.True(Guid.TryParse(created.ActivationRequestId, out _));
        Assert.Equal(created.ActivationRequestId, reused.ActivationRequestId);
        Assert.Equal("CARD-SECRET", reused.Card);
    }

    [Fact]
    public async Task DpapiStorePersistsSuccessfulLeaseAndReloadsIt()
    {
        var path = Path.Combine(directory, "licenses.dat");
        var store = new DpapiLicenseStore(path, PassThroughProtector.Instance);
        var credential = await store.GetOrCreateCredentialAsync("CARD-SECRET", CancellationToken.None);
        var lease = Lease("software-a", "machine-secret");

        await store.SaveLeaseAsync(credential.Card, credential.ActivationRequestId, lease, CancellationToken.None);
        var snapshot = await new DpapiLicenseStore(path, PassThroughProtector.Instance).ReadAsync(CancellationToken.None);

        var reloaded = Assert.Single(snapshot.Credentials);
        Assert.Equal("CARD-SECRET", reloaded.Card);
        Assert.Equal(lease, Assert.Single(reloaded.Leases));
    }

    [Fact]
    public async Task DpapiStoreRemovesOnlyTheAffectedSoftwareLease()
    {
        var store = CreateStore();
        var credential = await store.GetOrCreateCredentialAsync("CARD-SECRET", CancellationToken.None);
        await store.SaveLeaseAsync(credential.Card, credential.ActivationRequestId, Lease("software-a", "machine-secret"), CancellationToken.None);
        await store.SaveLeaseAsync(credential.Card, credential.ActivationRequestId, Lease("software-b", "machine-secret"), CancellationToken.None);

        await store.RemoveLeaseAsync("CARD-SECRET", "software-a", "machine-secret", CancellationToken.None);
        var remaining = Assert.Single((await store.ReadAsync(CancellationToken.None)).Credentials).Leases;

        Assert.Single(remaining);
        Assert.Equal("software-b", remaining[0].SoftwareId);
    }

    [Fact]
    public async Task DpapiStoreRejectsOversizedOrSecretBearingCorruptionWithoutEchoingIt()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "licenses.dat");
        const string secret = "CARD-RAW-SERIALIZED-LEASE-SIGNED-PAYLOAD-MACHINE-HASH";
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(secret + new string('x', DpapiLicenseStore.MaximumFileBytes)));
        var store = new DpapiLicenseStore(path, PassThroughProtector.Instance);

        var exception = await Assert.ThrowsAsync<LicenseStoreException>(() => store.ReadAsync(CancellationToken.None));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DpapiStoreEnforcesCredentialAndLeaseCountLimits()
    {
        var credentialStore = CreateStore();
        for (var index = 0; index < DpapiLicenseStore.MaximumCredentialCount; index++)
        {
            await credentialStore.GetOrCreateCredentialAsync($"CARD-{index:D2}", CancellationToken.None);
        }
        await Assert.ThrowsAsync<LicenseStoreException>(() =>
            credentialStore.GetOrCreateCredentialAsync("CARD-OVER-LIMIT", CancellationToken.None));

        var leasePath = Path.Combine(directory, "lease-count.dat");
        var leaseStore = new DpapiLicenseStore(leasePath, PassThroughProtector.Instance);
        var credential = await leaseStore.GetOrCreateCredentialAsync("CARD-LEASES", CancellationToken.None);
        for (var index = 0; index < DpapiLicenseStore.MaximumLeaseCountPerCredential; index++)
        {
            await leaseStore.SaveLeaseAsync(credential.Card, credential.ActivationRequestId,
                Lease($"software-{index:D2}", "machine-secret"), CancellationToken.None);
        }
        await Assert.ThrowsAsync<LicenseStoreException>(() => leaseStore.SaveLeaseAsync(
            credential.Card,
            credential.ActivationRequestId,
            Lease("software-over-limit", "machine-secret"),
            CancellationToken.None));
    }

    [Fact]
    public async Task DpapiStoreFileLockSerializesSameUserWritersAndAtomicWritesLeaveNoTemporaryFiles()
    {
        var path = Path.Combine(directory, "concurrent.dat");
        var stores = Enumerable.Range(0, 12)
            .Select(_ => new DpapiLicenseStore(path, PassThroughProtector.Instance))
            .ToArray();

        await Task.WhenAll(stores.Select((store, index) => Task.Run(() =>
            store.GetOrCreateCredentialAsync($"CARD-CONCURRENT-{index:D2}", CancellationToken.None))));

        var snapshot = await stores[0].ReadAsync(CancellationToken.None);
        Assert.Equal(12, snapshot.Credentials.Count);
        Assert.Equal(
            ["concurrent.dat", "concurrent.dat.lock"],
            Directory.GetFiles(directory).Select(file => Path.GetFileName(file)!).Order().ToArray());
    }

    [Fact]
    public async Task DpapiStoreCreatesPrivateAclFileLockAndCancellationStopsWaitingWriter()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows file ACL integration requires Windows.");
        }
        var path = Path.Combine(directory, "locked.dat");
        var lockPath = path + ".lock";
        var blocker = new BlockingProtector();
        var firstStore = new DpapiLicenseStore(path, blocker);
        var secondStore = new DpapiLicenseStore(path, PassThroughProtector.Instance);

        var firstWrite = Task.Run(() => firstStore.GetOrCreateCredentialAsync("CARD-FIRST", CancellationToken.None));
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.True(File.Exists(lockPath));
            Assert.True(DpapiLicenseStore.HasRestrictiveAcl(lockPath));
            Assert.Equal(0, new FileInfo(lockPath).Length);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                secondStore.GetOrCreateCredentialAsync("CARD-SECOND", cancellation.Token));

            blocker.Release.TrySetResult();
            await firstWrite;
        }
        finally
        {
            blocker.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task DpapiCurrentUserEncryptionDoesNotWriteCardInPlaintextAndAppliesRestrictedAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("DPAPI and Windows ACL integration requires Windows.");
        }
        var path = Path.Combine(directory, "licenses.dat");
        var store = new DpapiLicenseStore(path);

        await store.GetOrCreateCredentialAsync("CARD-DPAPI-SECRET", CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(path);

        Assert.DoesNotContain("CARD-DPAPI-SECRET", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.True(DpapiLicenseStore.HasRestrictiveAcl(path));
    }

    [Fact]
    public void DpapiModelsAndExceptionsRedactCardMachineLeaseAndPayloadValues()
    {
        const string card = "CARD-SECRET";
        const string machine = "MACHINE-HASH-SECRET";
        const string payload = "SIGNED-PAYLOAD-SECRET";
        var credential = new StoredLicenseCredential(card, Guid.NewGuid().ToString("D"), [Lease("software-a", machine, payload)]);
        var snapshot = new LicenseStoreSnapshot([credential]);

        var diagnostics = credential + "|" + credential.Leases[0] + "|" + snapshot;

        Assert.DoesNotContain(card, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(machine, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, diagnostics, StringComparison.Ordinal);
    }

    private DpapiLicenseStore CreateStore() =>
        new(Path.Combine(directory, "licenses.dat"), PassThroughProtector.Instance);

    internal static StoredLicenseLease Lease(string softwareId, string machineHash, string signedPayload = "proof-secret") =>
        new("card-id", softwareId, machineHash, "package", new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 10, 12, 30, 0, TimeSpan.Zero), signedPayload);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class PassThroughProtector : ILicenseStateProtector
    {
        public static readonly PassThroughProtector Instance = new();
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private sealed class BlockingProtector : ILicenseStateProtector
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] Protect(byte[] plaintext)
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return plaintext.ToArray();
        }

        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }
}
