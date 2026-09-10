using SuperPan.Licensing.Client;

namespace SuperPan.Licensing.Client.Tests;

public sealed class LicenseAuthorizationGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AuthorizationGateExposesThePlannedOperationScopesAndDoesNotPollWhileIdle()
    {
        Assert.Equal([LicenseOperationKind.Startup, LicenseOperationKind.Transfer, LicenseOperationKind.Share, LicenseOperationKind.ShareAnalyticsRefresh], Enum.GetValues<LicenseOperationKind>());
        var service = new FakeService();

        _ = CreateGate(service, new MemoryStore());

        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task AuthorizationGateValidatesAtStartup()
    {
        var store = StoreWithLease(Lease());
        var service = new FakeService(Authorized());
        ILicenseAuthorizationGate gate = CreateGate(service, store);

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Startup, CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(LicenseOperationKind.Startup, result.Operation);
        Assert.Single(service.Calls);
    }

    [Fact]
    public async Task AuthorizationGatePersistsSuccessfulOnlineLease()
    {
        var store = new MemoryStore([Credential()]);
        var gate = CreateGate(new FakeService(Authorized()), store);

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.False(result.UsedCachedLease);
        Assert.Equal("card-id", Assert.Single(Assert.Single(store.Snapshot.Credentials).Leases).CardId);
    }

    [Fact]
    public async Task AuthorizationGateReusesDurableActivationUuidAndPersistsActivationSuccess()
    {
        var store = new MemoryStore();
        var service = new FakeService(
            new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null),
            Authorized());
        var gate = CreateGate(service, store);

        await gate.ActivateAsync("CARD-SECRET", CancellationToken.None);
        var result = await gate.ActivateAsync("CARD-SECRET", CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(2, service.Calls.Count);
        Assert.Equal(service.Calls[0].RequestId, service.Calls[1].RequestId);
        Assert.True(Guid.TryParse(service.Calls[0].RequestId, out _));
        Assert.Single(Assert.Single(store.Snapshot.Credentials).Leases);
    }

    [Fact]
    public async Task AuthorizationGateDefinitiveRejectionClearsOnlyAffectedLease()
    {
        var store = StoreWithLease(Lease("software-a"), Lease("software-b"));
        var gate = CreateGate(new FakeService(Rejected(LicenseCodes.LicenseExpired)), store, "software-a");

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Share, CancellationToken.None);

        Assert.False(result.IsAuthorized);
        var remaining = Assert.Single(store.Snapshot.Credentials).Leases;
        Assert.Single(remaining);
        Assert.Equal("software-b", remaining[0].SoftwareId);
    }

    [Theory]
    [InlineData(LicenseCodes.RateLimited)]
    [InlineData(LicenseCodes.ServiceUnavailable)]
    public async Task AuthorizationGateUsesUnexpiredLeaseForTemporaryFailures(string code)
    {
        var gate = CreateGate(new FakeService(new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, code, null)), StoreWithLease(Lease()));

        var result = await gate.AuthorizeAsync(LicenseOperationKind.ShareAnalyticsRefresh, CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.True(result.UsedCachedLease);
    }

    [Fact]
    public async Task AuthorizationGateUsesUnexpiredLeaseWhenTransportThrows()
    {
        Exception[] failures =
        [
            new HttpRequestException("offline"),
            new IOException("connection reset"),
            new TaskCanceledException("transport timeout"),
        ];

        foreach (var failure in failures)
        {
            var gate = CreateGate(new FakeService(failure), StoreWithLease(Lease()));

            var result = await gate.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

            Assert.True(result.IsAuthorized);
            Assert.True(result.UsedCachedLease);
            Assert.Equal(LicenseCodes.ServiceUnavailable, result.Code);
        }
    }

    [Fact]
    public async Task AuthorizationGateStillPropagatesCallerCancellationAndRejectsInternalExceptions()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledGate = CreateGate(new FakeService(), StoreWithLease(Lease()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelledGate.AuthorizeAsync(LicenseOperationKind.Transfer, cancellation.Token));

        var internalFailure = CreateGate(new FakeService(new InvalidOperationException("internal")), StoreWithLease(Lease()));
        var result = await internalFailure.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.False(result.UsedCachedLease);
    }

    [Fact]
    public async Task AuthorizationGateRejectsExpiredLeaseDuringTemporaryFailure()
    {
        var expired = Lease() with { LeaseUntil = Now };
        var gate = CreateGate(new FakeService(new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null)), StoreWithLease(expired));

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.False(result.UsedCachedLease);
    }

    [Fact]
    public async Task AuthorizationGateNeverUsesLeaseForInvalidOrDefinitiveResponses()
    {
        foreach (var response in new[]
        {
            new LicenseApiResult(LicenseApiResultKind.InvalidResponse, null, null),
            Rejected(LicenseCodes.MachineMismatch),
        })
        {
            var result = await CreateGate(new FakeService(response), StoreWithLease(Lease())).AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);
            Assert.False(result.IsAuthorized);
            Assert.False(result.UsedCachedLease);
        }
    }

    [Fact]
    public async Task AuthorizationGateRedactsEverySensitiveValueFromResultsStatusEventsLogsAndExceptions()
    {
        string[] secrets = ["CARD-SECRET", "machine-secret", "serialized-lease-secret", "signed-payload-secret", "raw-service-internal-secret"];
        var store = StoreWithLease(Lease(signedPayload: secrets[3]));
        var service = new FakeService(new InvalidOperationException(string.Join('|', secrets)));
        var logs = new List<string>();
        var statuses = new List<string>();
        var gate = CreateGate(service, store, log: logs.Add);
        gate.StatusChanged += (_, status) => statuses.Add(status.ToString());

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Startup, CancellationToken.None);
        var diagnostics = string.Join('|', logs.Append(result.ToString()).Concat(statuses));

        Assert.False(result.IsAuthorized);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, diagnostics, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AuthorizationGateDiagnosticsRedactUnknownCodeInjectedThroughPublicResultConstructor()
    {
        const string rawCode = "raw-service-error:CARD-SECRET:MACHINE-HASH:SIGNED-PAYLOAD";
        var result = new LicenseGateResult(LicenseOperationKind.Transfer, false, false, rawCode);
        var status = new LicenseGateStatusEventArgs(result);

        Assert.Equal(rawCode, result.Code);
        Assert.DoesNotContain(rawCode, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rawCode, status.ToString(), StringComparison.Ordinal);
        Assert.Contains("unrecognized", result.ToString(), StringComparison.Ordinal);
    }

    private static LicenseAuthorizationGate CreateGate(
        FakeService service,
        MemoryStore store,
        string softwareId = "software-a",
        Action<string>? log = null) =>
        new(softwareId, service, store, new FakeFingerprintProvider("machine-secret"), new FixedTimeProvider(Now), log);

    private static MemoryStore StoreWithLease(params StoredLicenseLease[] leases) => new([Credential(leases)]);

    private static StoredLicenseCredential Credential(params StoredLicenseLease[] leases) =>
        new("CARD-SECRET", "11111111-1111-4111-8111-111111111111", leases);

    private static StoredLicenseLease Lease(string softwareId = "software-a", string signedPayload = "serialized-lease-secret") =>
        new("card-id", softwareId, "machine-secret", "package", Now.AddDays(1), Now.AddMinutes(20), signedPayload);

    private static LicenseApiResult Authorized() => new(
        LicenseApiResultKind.Authorized,
        LicenseCodes.Ok,
        new LicenseClaims(LicenseProtocol.Version, "request", Now, LicenseDecisions.Allow, LicenseCodes.Ok, "card-id", "software-a", "machine-secret", "package", Now.AddDays(1), Now.AddMinutes(20)));

    private static LicenseApiResult Rejected(string code) => new(
        LicenseApiResultKind.Rejected,
        code,
        new LicenseClaims(LicenseProtocol.Version, "request", Now, LicenseDecisions.Deny, code, null, null, null, null, null, null));

    private sealed class FakeFingerprintProvider(string hash) : IMachineFingerprintProvider
    {
        public string GetMachineHash() => hash;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeService : ILicenseServiceClient
    {
        private readonly Queue<object> responses;
        public FakeService(params object[] responses) => this.responses = new Queue<object>(responses);
        public List<(bool Activate, string RequestId, string Card, string MachineHash)> Calls { get; } = [];

        public Task<LicenseApiResult> ActivateAsync(string activationRequestId, string card, string machineHash, CancellationToken cancellationToken) =>
            Reply(true, activationRequestId, card, machineHash);

        public Task<LicenseApiResult> ValidateAsync(string requestId, string card, string machineHash, CancellationToken cancellationToken) =>
            Reply(false, requestId, card, machineHash);

        private Task<LicenseApiResult> Reply(bool activate, string requestId, string card, string machineHash)
        {
            Calls.Add((activate, requestId, card, machineHash));
            var response = responses.Count == 0 ? new LicenseApiResult(LicenseApiResultKind.InvalidResponse, null, null) : responses.Dequeue();
            return response is Exception exception ? Task.FromException<LicenseApiResult>(exception) : Task.FromResult((LicenseApiResult)response);
        }
    }

    private sealed class MemoryStore(IEnumerable<StoredLicenseCredential>? credentials = null) : ILicenseStateStore
    {
        public LicenseStoreSnapshot Snapshot { get; private set; } = new(credentials?.ToArray() ?? []);

        public Task<LicenseStoreSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);

        public Task<StoredLicenseCredential> GetOrCreateCredentialAsync(string card, CancellationToken cancellationToken)
        {
            var found = Snapshot.Credentials.FirstOrDefault(item => item.Card == card);
            if (found is not null) return Task.FromResult(found);
            found = new StoredLicenseCredential(card, Guid.NewGuid().ToString("D"), []);
            Snapshot = new LicenseStoreSnapshot(Snapshot.Credentials.Append(found).ToArray());
            return Task.FromResult(found);
        }

        public Task SaveLeaseAsync(string card, string activationRequestId, StoredLicenseLease lease, CancellationToken cancellationToken)
        {
            var updated = Snapshot.Credentials.Select(credential => credential.Card != card
                ? credential
                : new StoredLicenseCredential(card, activationRequestId, credential.Leases.Where(existing => existing.SoftwareId != lease.SoftwareId || existing.MachineHash != lease.MachineHash).Append(lease).ToArray())).ToArray();
            Snapshot = new LicenseStoreSnapshot(updated);
            return Task.CompletedTask;
        }

        public Task RemoveLeaseAsync(string card, string softwareId, string machineHash, CancellationToken cancellationToken)
        {
            Snapshot = new LicenseStoreSnapshot(Snapshot.Credentials.Select(credential => credential.Card != card
                ? credential
                : new StoredLicenseCredential(credential.Card, credential.ActivationRequestId, credential.Leases.Where(lease => lease.SoftwareId != softwareId || lease.MachineHash != machineHash).ToArray())).ToArray());
            return Task.CompletedTask;
        }
    }
}
