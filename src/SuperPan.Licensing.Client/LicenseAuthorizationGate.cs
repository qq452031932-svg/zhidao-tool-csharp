namespace SuperPan.Licensing.Client;

public enum LicenseOperationKind
{
    Startup,
    Transfer,
    Share,
    ShareAnalyticsRefresh,
}

public interface ILicenseAuthorizationGate
{
    Task<LicenseGateResult> AuthorizeAsync(LicenseOperationKind operation, CancellationToken cancellationToken);
}

public interface ILicenseServiceClient : ICachedLicenseEnvelopeVerifier
{
    Task<LicenseApiResult> ActivateAsync(string activationRequestId, string card, string machineHash, CancellationToken cancellationToken);
    Task<LicenseApiResult> ValidateAsync(string requestId, string card, string machineHash, CancellationToken cancellationToken);
}

public sealed record AuthorizedLicenseSummary(string PackageName, DateTimeOffset ExpiresAt);

public sealed record LicenseGateResult(
    LicenseOperationKind Operation,
    bool IsAuthorized,
    bool UsedCachedLease,
    string Code)
{
    public override string ToString() =>
        $"LicenseGateResult {{ Operation = {Operation}, Authorized = {IsAuthorized}, Cached = {UsedCachedLease}, Code = {DiagnosticCode()} }}";

    private string DiagnosticCode() =>
        LicenseCodes.All.Contains(Code) || Code is "activation_required" or "authorization_unavailable"
            ? Code
            : "unrecognized";
}

public sealed class LicenseGateStatusEventArgs(LicenseGateResult result) : EventArgs
{
    public LicenseGateResult Result { get; } = result ?? throw new ArgumentNullException(nameof(result));
    public override string ToString() => Result.ToString();
}

public sealed class LicenseAuthorizationGate : ILicenseAuthorizationGate
{
    private const string ActivationRequired = "activation_required";
    private const string AuthorizationUnavailable = "authorization_unavailable";
    private readonly string softwareId;
    private readonly ILicenseServiceClient service;
    private readonly ILicenseStateStore store;
    private readonly IMachineFingerprintProvider fingerprintProvider;
    private readonly Action<string>? log;
    private readonly Action<AuthorizedLicenseSummary?>? publishSummary;
    private readonly SemaphoreSlim leaseMutation = new(1, 1);
    private readonly object publicationGate = new();
    private long authorizationGeneration;

    public LicenseAuthorizationGate(
        string softwareId,
        ILicenseServiceClient service,
        ILicenseStateStore store,
        IMachineFingerprintProvider fingerprintProvider,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
        : this(softwareId, service, store, fingerprintProvider, timeProvider, log, null)
    {
    }

    public LicenseAuthorizationGate(
        string softwareId,
        ILicenseServiceClient service,
        ILicenseStateStore store,
        IMachineFingerprintProvider fingerprintProvider,
        TimeProvider? timeProvider,
        Action<string>? log,
        Action<AuthorizedLicenseSummary?>? publishSummary)
    {
        if (string.IsNullOrWhiteSpace(softwareId) || softwareId != softwareId.Trim())
            throw new ArgumentException("The software identifier is invalid.", nameof(softwareId));
        this.softwareId = softwareId;
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.fingerprintProvider = fingerprintProvider ?? throw new ArgumentNullException(nameof(fingerprintProvider));
        _ = timeProvider;
        this.log = log;
        this.publishSummary = publishSummary;
    }

    public LicenseAuthorizationGate(
        string softwareId,
        LicenseApiClient service,
        ILicenseStateStore store,
        IMachineFingerprintProvider fingerprintProvider,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
        : this(softwareId, new LicenseApiClientAdapter(service), store, fingerprintProvider, timeProvider, log, null)
    {
    }

    public LicenseAuthorizationGate(
        string softwareId,
        LicenseApiClient service,
        ILicenseStateStore store,
        IMachineFingerprintProvider fingerprintProvider,
        TimeProvider? timeProvider,
        Action<string>? log,
        Action<AuthorizedLicenseSummary?>? publishSummary)
        : this(softwareId, new LicenseApiClientAdapter(service), store, fingerprintProvider, timeProvider, log, publishSummary)
    {
    }

    public event EventHandler<LicenseGateStatusEventArgs>? StatusChanged;

    public async Task<LicenseGateResult> AuthorizeAsync(
        LicenseOperationKind operation,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        var generation = BeginAuthorization();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var machineHash = fingerprintProvider.GetMachineHash();
            var snapshot = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Credentials.Count == 0)
            {
                return Publish(new LicenseGateResult(operation, false, false, ActivationRequired), generation);
            }

            var candidates = snapshot.Credentials
                .Select(credential => new CredentialCandidate(credential, FindVerifiedCachedClaims(credential, machineHash)))
                .OrderByDescending(candidate => candidate.CachedClaims is not null)
                .ToArray();
            LicenseGateResult? lastRejection = null;
            foreach (var candidate in candidates)
            {
                var response = await ValidateCredentialAsync(candidate.Credential, machineHash, cancellationToken).ConfigureAwait(false);
                if (response.Kind == LicenseApiResultKind.Authorized)
                {
                    return await PersistAuthorizedAsync(operation, candidate.Credential, machineHash, response, generation, cancellationToken)
                        .ConfigureAwait(false);
                }
                if (response.Kind == LicenseApiResultKind.Rejected)
                {
                    await RemoveLeaseIfCurrentAsync(candidate.Credential.Card, machineHash, generation, cancellationToken)
                        .ConfigureAwait(false);
                    lastRejection = new LicenseGateResult(operation, false, false, SafeCode(response.Code));
                    continue;
                }
                if (response.Kind == LicenseApiResultKind.TemporaryFailure)
                {
                    return Publish(candidate.CachedClaims is not null
                        ? new LicenseGateResult(operation, true, true, SafeCode(response.Code))
                        : new LicenseGateResult(operation, false, false, SafeCode(response.Code)),
                        generation,
                        candidate.CachedClaims is not null ? CreateSummary(candidate.CachedClaims) : null);
                }
                return Publish(new LicenseGateResult(operation, false, false, AuthorizationUnavailable), generation);
            }
            return Publish(lastRejection ?? new LicenseGateResult(operation, false, false, ActivationRequired), generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Publish(new LicenseGateResult(operation, false, false, AuthorizationUnavailable), generation);
        }
    }

    public async Task<LicenseGateResult> ActivateAsync(string card, CancellationToken cancellationToken)
    {
        var generation = BeginAuthorization();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var machineHash = fingerprintProvider.GetMachineHash();
            var credential = await store.GetOrCreateCredentialAsync(card, cancellationToken).ConfigureAwait(false);
            var response = await service.ActivateAsync(
                credential.ActivationRequestId, credential.Card, machineHash, cancellationToken).ConfigureAwait(false);
            if (response.Kind == LicenseApiResultKind.Authorized)
            {
                return await PersistAuthorizedAsync(
                    LicenseOperationKind.Startup, credential, machineHash, response, generation, cancellationToken).ConfigureAwait(false);
            }
            if (response.Kind == LicenseApiResultKind.Rejected)
            {
                await RemoveLeaseIfCurrentAsync(credential.Card, machineHash, generation, cancellationToken).ConfigureAwait(false);
                return Publish(new LicenseGateResult(LicenseOperationKind.Startup, false, false, SafeCode(response.Code)), generation);
            }
            if (response.Kind == LicenseApiResultKind.TemporaryFailure &&
                FindVerifiedCachedClaims(credential, machineHash) is { } cachedClaims)
            {
                return Publish(
                    new LicenseGateResult(LicenseOperationKind.Startup, true, true, SafeCode(response.Code)),
                    generation,
                    CreateSummary(cachedClaims));
            }
            return Publish(new LicenseGateResult(LicenseOperationKind.Startup, false, false,
                response.Kind == LicenseApiResultKind.TemporaryFailure ? SafeCode(response.Code) : AuthorizationUnavailable), generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Publish(new LicenseGateResult(LicenseOperationKind.Startup, false, false, AuthorizationUnavailable), generation);
        }
    }

    public override string ToString() => nameof(LicenseAuthorizationGate);

    private async Task<LicenseGateResult> PersistAuthorizedAsync(
        LicenseOperationKind operation,
        StoredLicenseCredential credential,
        string machineHash,
        LicenseApiResult response,
        long generation,
        CancellationToken cancellationToken)
    {
        var signedEnvelope = response.SignedEnvelope;
        if (signedEnvelope is null || signedEnvelope.Length == 0)
        {
            return Publish(new LicenseGateResult(operation, false, false, AuthorizationUnavailable), generation);
        }
        try
        {
            var claims = service.VerifyCached(signedEnvelope, softwareId, machineHash);
            var lease = new StoredLicenseLease(claims.CardId!, claims.SoftwareId!, claims.MachineHash!, claims.PackageName!,
                claims.ExpiresAt!.Value, claims.LeaseUntil!.Value, Convert.ToBase64String(signedEnvelope));
            await SaveLeaseAsync(credential.Card, credential.ActivationRequestId, lease, cancellationToken)
                .ConfigureAwait(false);
            return Publish(
                new LicenseGateResult(operation, true, false, LicenseCodes.Ok),
                generation,
                CreateSummary(claims));
        }
        catch (Exception exception) when (exception is LicenseEnvelopeVerificationException or FormatException or ArgumentException)
        {
            return Publish(new LicenseGateResult(operation, false, false, AuthorizationUnavailable), generation);
        }
    }

    private LicenseClaims? FindVerifiedCachedClaims(StoredLicenseCredential credential, string machineHash)
    {
        foreach (var lease in credential.Leases)
        {
            try
            {
                return service.VerifyCached(Convert.FromBase64String(lease.SignedPayload), softwareId, machineHash);
            }
            catch (Exception exception) when (exception is LicenseEnvelopeVerificationException or FormatException or ArgumentException)
            {
            }
        }
        return null;
    }

    private async Task<LicenseApiResult> ValidateCredentialAsync(
        StoredLicenseCredential credential,
        string machineHash,
        CancellationToken cancellationToken)
    {
        try
        {
            return await service.ValidateAsync(
                Guid.NewGuid().ToString("D"), credential.Card, machineHash, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null);
        }
    }

    private LicenseGateResult Publish(
        LicenseGateResult result,
        long generation,
        AuthorizedLicenseSummary? summary = null)
    {
        lock (publicationGate)
        {
            if (generation == authorizationGeneration)
            {
                try { publishSummary?.Invoke(summary); } catch { }
            }
        }
        var status = new LicenseGateStatusEventArgs(result);
        try { StatusChanged?.Invoke(this, status); } catch { }
        try { log?.Invoke(status.ToString()); } catch { }
        return result;
    }

    private static string SafeCode(string? code) => LicenseCodes.All.Contains(code ?? string.Empty)
        ? code!
        : AuthorizationUnavailable;

    private static AuthorizedLicenseSummary CreateSummary(LicenseClaims claims) =>
        new(claims.PackageName!, claims.ExpiresAt!.Value);

    private long BeginAuthorization()
    {
        lock (publicationGate) return ++authorizationGeneration;
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (publicationGate) return generation == authorizationGeneration;
    }

    private async Task SaveLeaseAsync(
        string card,
        string activationRequestId,
        StoredLicenseLease lease,
        CancellationToken cancellationToken)
    {
        await leaseMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await store.SaveLeaseAsync(card, activationRequestId, lease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            leaseMutation.Release();
        }
    }

    private async Task RemoveLeaseIfCurrentAsync(
        string card,
        string machineHash,
        long generation,
        CancellationToken cancellationToken)
    {
        await leaseMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsCurrentGeneration(generation))
            {
                await store.RemoveLeaseAsync(card, softwareId, machineHash, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            leaseMutation.Release();
        }
    }

    private sealed class LicenseApiClientAdapter(LicenseApiClient client) : ILicenseServiceClient
    {
        private readonly LicenseApiClient client = client ?? throw new ArgumentNullException(nameof(client));
        public Task<LicenseApiResult> ActivateAsync(string activationRequestId, string card, string machineHash, CancellationToken cancellationToken) =>
            client.ActivateAsync(activationRequestId, card, machineHash, cancellationToken);
        public Task<LicenseApiResult> ValidateAsync(string requestId, string card, string machineHash, CancellationToken cancellationToken) =>
            client.ValidateAsync(requestId, card, machineHash, cancellationToken);
        public LicenseClaims VerifyCached(ReadOnlySpan<byte> signedEnvelope, string expectedSoftwareId, string expectedMachineHash) =>
            client.VerifyCached(signedEnvelope, expectedSoftwareId, expectedMachineHash);
    }

    private sealed record CredentialCandidate(StoredLicenseCredential Credential, LicenseClaims? CachedClaims);
}
