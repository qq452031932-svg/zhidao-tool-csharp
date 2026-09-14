using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public async Task AuthorizationGateRejectsOnlineAllowWithoutOriginalSignedEnvelope()
    {
        var response = Authorized(includeEnvelope: false);
        Assert.Null(response.SignedEnvelope);
        var store = new MemoryStore([Credential()]);
        var gate = CreateGate(new FakeService(response), store);

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Empty(Assert.Single(store.Snapshot.Credentials).Leases);
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
        var expired = Lease(leaseUntil: Now);
        var gate = CreateGate(new FakeService(new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null)), StoreWithLease(expired));

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.False(result.UsedCachedLease);
    }

    [Fact]
    public async Task AuthorizationGateRejectsDeletedCardEvenWhenItsCachedLeaseWasUnexpired()
    {
        var store = StoreWithLease(Lease());
        var gate = CreateGate(new FakeService(Rejected(LicenseCodes.LicenseNotFound)), store);

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.False(result.UsedCachedLease);
        Assert.Equal(LicenseCodes.LicenseNotFound, result.Code);
        Assert.Empty(Assert.Single(store.Snapshot.Credentials).Leases);
    }

    [Fact]
    public async Task AuthorizationGateUsesSignedLeaseOnlyBeforeInjectedVerifierExpiryWithoutSleeping()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var claims = SignedLicenseEnvelopeVerifierTests.CreateClaims("cached-boundary") with
        {
            ServerTime = Now,
            SoftwareId = "software-a",
            MachineHash = "machine-secret",
            ExpiresAt = Now.AddDays(1),
            LeaseUntil = Now.AddMinutes(30),
        };
        var envelope = SignedLicenseEnvelopeVerifierTests.Sign(claims, signer);
        var boundaryLease = new StoredLicenseLease(
            "card-id", "software-a", "machine-secret", "package", Now.AddDays(1), Now.AddMinutes(30),
            Convert.ToBase64String(envelope));
        var beforeStore = StoreWithLease(boundaryLease);
        var afterStore = StoreWithLease(boundaryLease);
        using var beforeService = new RealVerifierService(
            signer,
            new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null),
            new FixedTimeProvider(Now.AddMinutes(29)));
        using var afterService = new RealVerifierService(
            signer,
            new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null),
            new FixedTimeProvider(Now.AddMinutes(31)));

        var before = await new LicenseAuthorizationGate(
            "software-a", beforeService, beforeStore, new FakeFingerprintProvider("machine-secret"),
            new FixedTimeProvider(Now.AddMinutes(29))).AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);
        var after = await new LicenseAuthorizationGate(
            "software-a", afterService, afterStore, new FakeFingerprintProvider("machine-secret"),
            new FixedTimeProvider(Now.AddMinutes(31))).AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

        Assert.True(before.IsAuthorized, before.ToString());
        Assert.True(before.UsedCachedLease);
        Assert.False(after.IsAuthorized);
        Assert.False(after.UsedCachedLease);
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
    public async Task AuthorizationGateSkipsExpiredFirstCardAndUsesValidSecondCardLease()
    {
        var expired = Lease(leaseUntil: Now);
        var store = new MemoryStore([
            CredentialWithCard("CARD-FIRST", expired),
            CredentialWithCard("CARD-SECOND", Lease()),
        ]);
        var service = new FakeService(new LicenseApiResult(
            LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null));
        var gate = CreateGate(service, store);

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.True(result.UsedCachedLease);
        Assert.Equal("CARD-SECOND", Assert.Single(service.Calls).Card);
    }

    [Fact]
    public async Task AuthorizationGateContinuesAfterFirstCardNotApplicableAndAuthorizesSecondCard()
    {
        var store = new MemoryStore([
            CredentialWithCard("CARD-FIRST", Lease()),
            CredentialWithCard("CARD-SECOND"),
        ]);
        var service = new FakeService(Rejected(LicenseCodes.AppNotAllowed), Authorized());
        var gate = CreateGate(service, store);

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Share, CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(["CARD-FIRST", "CARD-SECOND"], service.Calls.Select(call => call.Card).ToArray());
        Assert.Empty(store.Snapshot.Credentials.Single(item => item.Card == "CARD-FIRST").Leases);
        Assert.Single(store.Snapshot.Credentials.Single(item => item.Card == "CARD-SECOND").Leases);
    }

    [Fact]
    public async Task AuthorizationGatePublishesOnlyTheOnlineClaimsThatActuallyAuthorized()
    {
        var store = new MemoryStore([
            CredentialWithCard("CARD-FIRST"),
            CredentialWithCard("CARD-SECOND"),
        ]);
        var expiry = Now.AddDays(7);
        var summaries = new List<AuthorizedLicenseSummary?>();
        var gate = CreateGate(
            new FakeService(Rejected(LicenseCodes.AppNotAllowed), Authorized("团队版", expiry)),
            store,
            publishSummary: summaries.Add);

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Share, CancellationToken.None);

        Assert.True(result.IsAuthorized);
        var summary = Assert.Single(summaries);
        Assert.Equal(new AuthorizedLicenseSummary("团队版", expiry), summary);
    }

    [Fact]
    public async Task AuthorizationGatePublishesOnlyTheCachedClaimsThatActuallyAuthorized()
    {
        var expiry = Now.AddDays(9);
        var store = new MemoryStore([
            CredentialWithCard("CARD-FIRST", Lease(packageName: "已拒绝套餐")),
            CredentialWithCard("CARD-SECOND", Lease(packageName: "离线套餐", expiresAt: expiry)),
        ]);
        var summaries = new List<AuthorizedLicenseSummary?>();
        var gate = CreateGate(
            new FakeService(
                Rejected(LicenseCodes.AppNotAllowed),
                new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null)),
            store,
            publishSummary: summaries.Add);

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.True(result.UsedCachedLease);
        var summary = Assert.Single(summaries);
        Assert.Equal(new AuthorizedLicenseSummary("离线套餐", expiry), summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizationGateClearsAStaleSummaryWhenAuthorizationFails(bool unavailable)
    {
        AuthorizedLicenseSummary? current = new("旧套餐", Now.AddDays(1));
        var response = unavailable
            ? new LicenseApiResult(LicenseApiResultKind.InvalidResponse, null, null)
            : Rejected(LicenseCodes.LicenseExpired);
        var gate = CreateGate(
            new FakeService(response),
            new MemoryStore([Credential()]),
            publishSummary: summary => current = summary);

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Startup, CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Null(current);
    }

    [Fact]
    public async Task AuthorizationGateRejectsDpapiRoundTripMetadataTamperingDuringOfflineFallback()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = new RealVerifierService(
            signer,
            new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null));
        var directory = Path.Combine(Path.GetTempPath(), $"superpan-gate-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "licenses.dat");
        var protector = new RoundTripProtector();
        var store = new DpapiLicenseStore(path, protector);
        try
        {
            var credential = await store.GetOrCreateCredentialAsync("CARD-SECRET", CancellationToken.None);
            var signedEnvelope = SignCachedEnvelope(signer, "software-a", "machine-secret");
            await store.SaveLeaseAsync(credential.Card, credential.ActivationRequestId,
                LeaseFromEnvelope("software-a", "machine-secret", signedEnvelope), CancellationToken.None);

            var plaintext = protector.Unprotect(await File.ReadAllBytesAsync(path));
            var serialized = Encoding.UTF8.GetString(plaintext)
                .Replace("\"software_id\":\"software-a\"", "\"software_id\":\"software-b\"", StringComparison.Ordinal)
                .Replace(Now.AddMinutes(20).ToString("O", CultureInfo.InvariantCulture), Now.AddDays(2).ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            await File.WriteAllBytesAsync(path, protector.Protect(Encoding.UTF8.GetBytes(serialized)));

            var gate = new LicenseAuthorizationGate(
                "software-b", service, store, new FakeFingerprintProvider("machine-secret"), new FixedTimeProvider(Now));
            var result = await gate.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

            Assert.False(result.IsAuthorized);
            Assert.False(result.UsedCachedLease);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuthorizationGateRejectsTamperedSignedEnvelopeDuringOfflineFallback()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = new RealVerifierService(
            signer,
            new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null));
        var validEnvelope = SignCachedEnvelope(signer, "software-a", "machine-secret");
        var tamperedEnvelope = validEnvelope.ToArray();
        tamperedEnvelope[^3] ^= 1;
        var store = new MemoryStore([CredentialWithCard(
            "CARD-SECRET", LeaseFromEnvelope("software-a", "machine-secret", tamperedEnvelope))]);
        var gate = new LicenseAuthorizationGate(
            "software-a", service, store, new FakeFingerprintProvider("machine-secret"), new FixedTimeProvider(Now));

        var result = await gate.AuthorizeAsync(LicenseOperationKind.Transfer, CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.False(result.UsedCachedLease);
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
        Action<string>? log = null,
        Action<AuthorizedLicenseSummary?>? publishSummary = null) =>
        new(softwareId, service, store, new FakeFingerprintProvider("machine-secret"), new FixedTimeProvider(Now), log, publishSummary);

    private static MemoryStore StoreWithLease(params StoredLicenseLease[] leases) => new([Credential(leases)]);

    private static StoredLicenseCredential Credential(params StoredLicenseLease[] leases) =>
        new("CARD-SECRET", "11111111-1111-4111-8111-111111111111", leases);

    private static StoredLicenseCredential CredentialWithCard(string card, params StoredLicenseLease[] leases) =>
        new(card, Guid.NewGuid().ToString("D"), leases);

    private static StoredLicenseLease Lease(
        string softwareId = "software-a",
        string? signedPayload = null,
        DateTimeOffset? leaseUntil = null,
        string packageName = "package",
        DateTimeOffset? expiresAt = null)
    {
        var effectiveLeaseUntil = leaseUntil ?? Now.AddMinutes(20);
        var effectiveExpiresAt = expiresAt ?? Now.AddDays(1);
        var envelope = signedPayload ?? Convert.ToBase64String(CreateTestEnvelope(
            softwareId, "machine-secret", effectiveLeaseUntil, effectiveExpiresAt, packageName));
        return new StoredLicenseLease(
            "card-id", softwareId, "machine-secret", packageName, effectiveExpiresAt, effectiveLeaseUntil, envelope);
    }

    private static LicenseApiResult Authorized(bool includeEnvelope = true) =>
        Authorized("package", Now.AddDays(1), includeEnvelope);

    private static LicenseApiResult Authorized(
        string packageName,
        DateTimeOffset expiresAt,
        bool includeEnvelope = true)
    {
        var claims = new LicenseClaims(LicenseProtocol.Version, "request", Now, LicenseDecisions.Allow, LicenseCodes.Ok,
            "card-id", "software-a", "machine-secret", packageName, expiresAt, Now.AddMinutes(20));
        return new LicenseApiResult(
            LicenseApiResultKind.Authorized,
            LicenseCodes.Ok,
            claims,
            includeEnvelope ? CreateTestEnvelope("software-a", "machine-secret", Now.AddMinutes(20), expiresAt, packageName) : null);
    }

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

        public LicenseClaims VerifyCached(ReadOnlySpan<byte> signedEnvelope, string expectedSoftwareId, string expectedMachineHash)
        {
            var fields = Encoding.UTF8.GetString(signedEnvelope).Split('|');
            if (fields.Length is not (6 or 7) || fields[0] != "test-envelope" || fields[1] != expectedSoftwareId || fields[2] != expectedMachineHash)
                throw new LicenseEnvelopeVerificationException("Invalid test envelope.");
            var leaseUntil = DateTimeOffset.ParseExact(fields[3], "O", CultureInfo.InvariantCulture);
            var expiresAt = DateTimeOffset.ParseExact(fields[4], "O", CultureInfo.InvariantCulture);
            if (fields[5] != "allow" || leaseUntil <= Now || expiresAt <= Now)
                throw new LicenseEnvelopeVerificationException("Invalid test lease.");
            return new LicenseClaims(LicenseProtocol.Version, "cached-request", Now, LicenseDecisions.Allow, LicenseCodes.Ok,
                "card-id", fields[1], fields[2], fields.Length == 7 ? fields[6] : "package", expiresAt, leaseUntil);
        }

        private Task<LicenseApiResult> Reply(bool activate, string requestId, string card, string machineHash)
        {
            Calls.Add((activate, requestId, card, machineHash));
            var response = responses.Count == 0 ? new LicenseApiResult(LicenseApiResultKind.InvalidResponse, null, null) : responses.Dequeue();
            return response is Exception exception ? Task.FromException<LicenseApiResult>(exception) : Task.FromResult((LicenseApiResult)response);
        }
    }

    private static byte[] CreateTestEnvelope(
        string softwareId,
        string machineHash,
        DateTimeOffset leaseUntil,
        DateTimeOffset expiresAt,
        string? packageName = null) =>
        Encoding.UTF8.GetBytes(string.Join('|',
            "test-envelope",
            softwareId,
            machineHash,
            leaseUntil.ToString("O", CultureInfo.InvariantCulture),
            expiresAt.ToString("O", CultureInfo.InvariantCulture),
            "allow",
            packageName ?? "package"));

    private static byte[] SignCachedEnvelope(ECDsa signer, string softwareId, string machineHash)
    {
        var claims = SignedLicenseEnvelopeVerifierTests.CreateClaims("cached-request") with
        {
            ServerTime = Now.AddMinutes(-10),
            SoftwareId = softwareId,
            MachineHash = machineHash,
            LeaseUntil = Now.AddMinutes(20),
        };
        return SignedLicenseEnvelopeVerifierTests.Sign(claims, signer);
    }

    private static StoredLicenseLease LeaseFromEnvelope(
        string softwareId,
        string machineHash,
        byte[] signedEnvelope) =>
        new("card-id", softwareId, machineHash, "package", Now.AddDays(1), Now.AddMinutes(20),
            Convert.ToBase64String(signedEnvelope));

    private sealed class RealVerifierService : ILicenseServiceClient, IDisposable
    {
        private readonly SignedLicenseEnvelopeVerifier verifier;
        private readonly LicenseApiResult response;

        public RealVerifierService(ECDsa signer, LicenseApiResult response, TimeProvider? timeProvider = null)
        {
            verifier = new SignedLicenseEnvelopeVerifier(
                SignedLicenseEnvelopeVerifierTests.CreateOptions(signer), timeProvider ?? new FixedTimeProvider(Now));
            this.response = response;
        }

        public Task<LicenseApiResult> ActivateAsync(string activationRequestId, string card, string machineHash, CancellationToken cancellationToken) =>
            Task.FromResult(response);
        public Task<LicenseApiResult> ValidateAsync(string requestId, string card, string machineHash, CancellationToken cancellationToken) =>
            Task.FromResult(response);
        public LicenseClaims VerifyCached(ReadOnlySpan<byte> signedEnvelope, string expectedSoftwareId, string expectedMachineHash) =>
            verifier.VerifyCached(signedEnvelope, expectedSoftwareId, expectedMachineHash);
        public void Dispose() => verifier.Dispose();
    }

    private sealed class RoundTripProtector : ILicenseStateProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
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
