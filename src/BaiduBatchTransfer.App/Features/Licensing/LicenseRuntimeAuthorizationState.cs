using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Features.Licensing;

public sealed class LicenseRuntimeAuthorizationState
{
    private readonly object summaryGate = new();
    private readonly TaskCompletionSource authorized = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim authorizationTransition = new(1, 1);
    private int startupValidationCompleted;
    private int isAuthorized;
    private AuthorizedLicenseSummary? currentSummary;
    // 最近一次授权门给出的结论码。"连不上服务器"与"这台机器没有卡密"必须分开，
    // 否则设置页只能在"已授权/未授权"里二选一，把网络故障说成用户没买。
    private string lastGateCode = LicenseCodes.Ok;

    public bool StartupValidationCompleted => Volatile.Read(ref startupValidationCompleted) != 0;
    public bool IsAuthorized => Volatile.Read(ref isAuthorized) != 0;
    public AuthorizedLicenseSummary? CurrentSummary
    {
        get
        {
            lock (summaryGate) return currentSummary;
        }
    }

    public event Action<AuthorizedLicenseSummary?>? SummaryChanged;

    public string LastGateCode
    {
        get { lock (summaryGate) return lastGateCode; }
    }

    // 授权结论未知（服务不可达、被限流），而不是被拒绝。
    public bool VerificationDeferred
    {
        get
        {
            lock (summaryGate)
            {
                return currentSummary is null && lastGateCode is
                    LicenseCodes.ServiceUnavailable or LicenseCodes.RateLimited or "authorization_unavailable";
            }
        }
    }

    public void RecordGateCode(string? code)
    {
        Action<AuthorizedLicenseSummary?>? changed;
        AuthorizedLicenseSummary? summary;
        lock (summaryGate)
        {
            var normalized = string.IsNullOrWhiteSpace(code) ? LicenseCodes.Ok : code;
            if (string.Equals(lastGateCode, normalized, StringComparison.Ordinal)) return;
            lastGateCode = normalized;
            summary = currentSummary;
            changed = SummaryChanged;
        }

        if (changed is null) return;
        foreach (Action<AuthorizedLicenseSummary?> handler in changed.GetInvocationList())
        {
            try { handler(summary); } catch { }
        }
    }

    public void PublishSummary(AuthorizedLicenseSummary? summary)
    {
        Action<AuthorizedLicenseSummary?>? changed;
        lock (summaryGate)
        {
            if (Equals(currentSummary, summary)) return;
            currentSummary = summary;
            changed = SummaryChanged;
        }

        if (changed is null) return;
        foreach (Action<AuthorizedLicenseSummary?> handler in changed.GetInvocationList())
        {
            try { handler(summary); } catch { }
        }
    }

    public void CompleteStartupValidation()
    {
        if (Interlocked.Exchange(ref startupValidationCompleted, 1) != 0)
            throw new InvalidOperationException("启动授权验证已经完成。");
    }

    public async Task AuthorizeAfterRecoveryAsync(
        Func<CancellationToken, Task> recoverAuthorizedAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recoverAuthorizedAsync);
        if (IsAuthorized) return;

        await authorizationTransition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsAuthorized) return;
            await recoverAuthorizedAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref isAuthorized, 1);
            authorized.TrySetResult();
        }
        finally
        {
            authorizationTransition.Release();
        }
    }

    public Task WaitUntilAuthorizedAsync(CancellationToken cancellationToken = default) =>
        authorized.Task.WaitAsync(cancellationToken);
}

public sealed class DisabledLicenseAuthorizationGate : ILicenseAuthorizationGate
{
    public Task<LicenseGateResult> AuthorizeAsync(
        LicenseOperationKind operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new LicenseGateResult(
            operation,
            IsAuthorized: true,
            UsedCachedLease: false,
            LicenseCodes.Ok));
    }
}

public sealed class LicenseActivationService
{
    private const string AuthorizationUnavailable = "authorization_unavailable";
    private readonly Func<string, CancellationToken, Task<LicenseGateResult>> activateAsync;
    private readonly LicenseRuntimeAuthorizationState authorizationState;
    private readonly IAppRecoveryRunner recoveryRunner;

    public LicenseActivationService(
        LicenseAuthorizationGate gate,
        LicenseRuntimeAuthorizationState authorizationState,
        IAppRecoveryRunner recoveryRunner)
        : this(CreateActivationDelegate(gate), authorizationState, recoveryRunner)
    {
    }

    public LicenseActivationService(
        Func<string, CancellationToken, Task<LicenseGateResult>> activateAsync,
        LicenseRuntimeAuthorizationState authorizationState,
        IAppRecoveryRunner recoveryRunner)
    {
        this.activateAsync = activateAsync ?? throw new ArgumentNullException(nameof(activateAsync));
        this.authorizationState = authorizationState ?? throw new ArgumentNullException(nameof(authorizationState));
        this.recoveryRunner = recoveryRunner ?? throw new ArgumentNullException(nameof(recoveryRunner));
    }

    public async Task<LicenseActivationResult> ActivateAsync(
        string card,
        CancellationToken cancellationToken)
    {
        var result = await activateAsync(card, cancellationToken).ConfigureAwait(false);
        if (result.IsAuthorized)
        {
            try
            {
                await authorizationState.AuthorizeAfterRecoveryAsync(
                    recoveryRunner.RecoverAuthorizedAsync,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new LicenseActivationResult(false, AuthorizationUnavailable);
            }
        }
        var safeCode = LicenseCodes.All.Contains(result.Code) ||
            string.Equals(result.Code, "activation_required", StringComparison.Ordinal) ||
            string.Equals(result.Code, AuthorizationUnavailable, StringComparison.Ordinal)
                ? result.Code
                : AuthorizationUnavailable;
        return new LicenseActivationResult(result.IsAuthorized, safeCode);
    }

    private static Func<string, CancellationToken, Task<LicenseGateResult>> CreateActivationDelegate(
        LicenseAuthorizationGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return gate.ActivateAsync;
    }
}
