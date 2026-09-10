using System.Text.Json;

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

public interface ILicenseServiceClient
{
    Task<LicenseApiResult> ActivateAsync(string activationRequestId, string card, string machineHash, CancellationToken cancellationToken);
    Task<LicenseApiResult> ValidateAsync(string requestId, string card, string machineHash, CancellationToken cancellationToken);
}

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
    private readonly TimeProvider timeProvider;
    private readonly Action<string>? log;

    public LicenseAuthorizationGate(
        string softwareId,
        ILicenseServiceClient service,
        ILicenseStateStore store,
        IMachineFingerprintProvider fingerprintProvider,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(softwareId) || softwareId != softwareId.Trim())
            throw new ArgumentException("The software identifier is invalid.", nameof(softwareId));
        this.softwareId = softwareId;
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.fingerprintProvider = fingerprintProvider ?? throw new ArgumentNullException(nameof(fingerprintProvider));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.log = log;
    }

    public LicenseAuthorizationGate(
        string softwareId,
        LicenseApiClient service,
        ILicenseStateStore store,
        IMachineFingerprintProvider fingerprintProvider,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
        : this(softwareId, new LicenseApiClientAdapter(service), store, fingerprintProvider, timeProvider, log)
    {
    }

    public event EventHandler<LicenseGateStatusEventArgs>? StatusChanged;

    public async Task<LicenseGateResult> AuthorizeAsync(
        LicenseOperationKind operation,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var machineHash = fingerprintProvider.GetMachineHash();
            var snapshot = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            var credential = SelectCredential(snapshot, machineHash);
            if (credential is null)
            {
                return Publish(new LicenseGateResult(operation, false, false, ActivationRequired));
            }

            LicenseApiResult response;
            try
            {
                response = await service.ValidateAsync(
                    Guid.NewGuid().ToString("D"), credential.Card, machineHash, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                response = new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                response = new LicenseApiResult(LicenseApiResultKind.TemporaryFailure, LicenseCodes.ServiceUnavailable, null);
            }
            return await ApplyResponseAsync(operation, credential, machineHash, response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Publish(new LicenseGateResult(operation, false, false, AuthorizationUnavailable));
        }
    }

    public async Task<LicenseGateResult> ActivateAsync(string card, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var machineHash = fingerprintProvider.GetMachineHash();
            var credential = await store.GetOrCreateCredentialAsync(card, cancellationToken).ConfigureAwait(false);
            var response = await service.ActivateAsync(
                credential.ActivationRequestId, credential.Card, machineHash, cancellationToken).ConfigureAwait(false);
            return await ApplyResponseAsync(LicenseOperationKind.Startup, credential, machineHash, response, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Publish(new LicenseGateResult(LicenseOperationKind.Startup, false, false, AuthorizationUnavailable));
        }
    }

    public override string ToString() => nameof(LicenseAuthorizationGate);

    private StoredLicenseCredential? SelectCredential(LicenseStoreSnapshot snapshot, string machineHash) =>
        snapshot.Credentials.FirstOrDefault(credential => credential.Leases.Any(lease =>
            string.Equals(lease.SoftwareId, softwareId, StringComparison.Ordinal) &&
            string.Equals(lease.MachineHash, machineHash, StringComparison.Ordinal)))
        ?? snapshot.Credentials.FirstOrDefault();

    private async Task<LicenseGateResult> ApplyResponseAsync(
        LicenseOperationKind operation,
        StoredLicenseCredential credential,
        string machineHash,
        LicenseApiResult response,
        CancellationToken cancellationToken)
    {
        if (response.Kind == LicenseApiResultKind.Authorized && response.Claims is { } claims &&
            string.Equals(claims.SoftwareId, softwareId, StringComparison.Ordinal) &&
            string.Equals(claims.MachineHash, machineHash, StringComparison.Ordinal) &&
            claims.CardId is not null && claims.PackageName is not null && claims.ExpiresAt is not null && claims.LeaseUntil is not null)
        {
            var serializedClaims = JsonSerializer.Serialize(claims);
            var lease = new StoredLicenseLease(claims.CardId, claims.SoftwareId!, claims.MachineHash!, claims.PackageName,
                claims.ExpiresAt.Value, claims.LeaseUntil.Value, serializedClaims);
            await store.SaveLeaseAsync(credential.Card, credential.ActivationRequestId, lease, cancellationToken).ConfigureAwait(false);
            return Publish(new LicenseGateResult(operation, true, false, LicenseCodes.Ok));
        }

        if (response.Kind == LicenseApiResultKind.Rejected)
        {
            await store.RemoveLeaseAsync(credential.Card, softwareId, machineHash, cancellationToken).ConfigureAwait(false);
            return Publish(new LicenseGateResult(operation, false, false, SafeCode(response.Code)));
        }

        if (response.Kind == LicenseApiResultKind.TemporaryFailure &&
            response.Code is LicenseCodes.RateLimited or LicenseCodes.ServiceUnavailable &&
            HasUsableLease(credential, machineHash))
        {
            return Publish(new LicenseGateResult(operation, true, true, response.Code));
        }

        return Publish(new LicenseGateResult(operation, false, false,
            response.Kind == LicenseApiResultKind.TemporaryFailure ? SafeCode(response.Code) : AuthorizationUnavailable));
    }

    private bool HasUsableLease(StoredLicenseCredential credential, string machineHash)
    {
        var now = timeProvider.GetUtcNow();
        return credential.Leases.Any(lease =>
            string.Equals(lease.SoftwareId, softwareId, StringComparison.Ordinal) &&
            string.Equals(lease.MachineHash, machineHash, StringComparison.Ordinal) &&
            lease.LeaseUntil > now && lease.ExpiresAt > now);
    }

    private LicenseGateResult Publish(LicenseGateResult result)
    {
        var status = new LicenseGateStatusEventArgs(result);
        try { StatusChanged?.Invoke(this, status); } catch { }
        try { log?.Invoke(status.ToString()); } catch { }
        return result;
    }

    private static string SafeCode(string? code) => LicenseCodes.All.Contains(code ?? string.Empty)
        ? code!
        : AuthorizationUnavailable;

    private sealed class LicenseApiClientAdapter(LicenseApiClient client) : ILicenseServiceClient
    {
        private readonly LicenseApiClient client = client ?? throw new ArgumentNullException(nameof(client));
        public Task<LicenseApiResult> ActivateAsync(string activationRequestId, string card, string machineHash, CancellationToken cancellationToken) =>
            client.ActivateAsync(activationRequestId, card, machineHash, cancellationToken);
        public Task<LicenseApiResult> ValidateAsync(string requestId, string card, string machineHash, CancellationToken cancellationToken) =>
            client.ValidateAsync(requestId, card, machineHash, cancellationToken);
    }
}
