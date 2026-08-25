using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduCandidateDomProtocolTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task UniqueStableCompleteExactObservationCanMutateOnce()
    {
        var request = Request();
        var host = new FakeScriptHost(Observed(), Mutated());
        var authorizer = new ExactAuthorizer(request);
        var executor = new WebView2CandidateDomExecutor(host, authorizer);

        var observed = await executor.ObserveAsync(request);
        var mutated = await executor.MutateAsync(request, observed.Observation!);

        Assert.True(observed.IsSuccess);
        Assert.True(mutated.IsSuccess);
        Assert.Equal(1, host.MutationCalls);
        Assert.Equal(1, host.DomMutationCount);
        Assert.Equal(1, authorizer.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ZeroOrMultipleCandidateControlsFailBeforeMutation(int candidateCount)
    {
        var host = new FakeScriptHost(Observed(controlCount: candidateCount), Mutated());
        var executor = new WebView2CandidateDomExecutor(host, new ExactAuthorizer(Request()));

        var result = await executor.ObserveAsync(Request());

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduAutomationDenialReason.CandidateNotUnique, result.DenialReason);
        Assert.Equal(0, host.MutationCalls);
        Assert.Equal(0, host.DomMutationCount);
    }

    [Fact]
    public async Task WrongStateFailsBeforeMutation()
    {
        var host = new FakeScriptHost(Observed(state: "ExternalShare"), Mutated());
        var executor = new WebView2CandidateDomExecutor(host, new ExactAuthorizer(Request()));

        var result = await executor.ObserveAsync(Request());

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduAutomationDenialReason.ActionNotAllowedForState, result.DenialReason);
        Assert.Equal(0, host.DomMutationCount);
    }

    [Fact]
    public async Task WrongOrIncompleteExactSetFailsBeforeMutation()
    {
        var wrong = new FakeScriptHost(Observed(exactIds: ["other"]), Mutated());
        var incomplete = new FakeScriptHost(Observed(complete: false), Mutated());

        var wrongResult = await new WebView2CandidateDomExecutor(
            wrong, new ExactAuthorizer(Request())).ObserveAsync(Request());
        var incompleteResult = await new WebView2CandidateDomExecutor(
            incomplete, new ExactAuthorizer(Request())).ObserveAsync(Request());

        Assert.False(wrongResult.IsSuccess);
        Assert.False(incompleteResult.IsSuccess);
        Assert.Equal(BaiduAutomationDenialReason.ObservationNotValidated, wrongResult.DenialReason);
        Assert.Equal(BaiduAutomationDenialReason.ObservationNotValidated, incompleteResult.DenialReason);
        Assert.Equal(0, wrong.DomMutationCount + incomplete.DomMutationCount);
    }

    [Fact]
    public async Task SameTurnRevalidationDriftReturnsNotValidatedWithZeroDomMutation()
    {
        var request = Request();
        var host = new FakeScriptHost(
            Observed(),
            NotValidated("observation-changed"));
        var executor = new WebView2CandidateDomExecutor(host, new ExactAuthorizer(request));
        var observed = await executor.ObserveAsync(request);

        var result = await executor.MutateAsync(request, observed.Observation!);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduAutomationDenialReason.ObservationNotValidated, result.DenialReason);
        Assert.Equal(1, host.MutationCalls);
        Assert.Equal(0, host.DomMutationCount);
    }

    [Fact]
    public async Task DeniedExpiredRecoveryOrCrossIdentityBindingNeverCallsMutationScript()
    {
        var request = Request();
        foreach (var denied in new[]
                 {
                     request with { Binding = request.Binding with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) } },
                     request with { Authorization = request.Authorization with { IsRecovery = true } },
                     request with { Authorization = request.Authorization with { OperationId = Guid.NewGuid() } },
                     request with { Binding = request.Binding with { Source = AdapterWriteAuthorizationSource.ExactFormalRecord } },
                 })
        {
            var host = new FakeScriptHost(Observed(), Mutated());
            var executor = new WebView2CandidateDomExecutor(host, new ExactAuthorizer(request));
            var observed = await executor.ObserveAsync(denied);

            var result = await executor.MutateAsync(denied, observed.Observation!);

            Assert.False(result.IsSuccess);
            Assert.Equal(BaiduAutomationDenialReason.AcceptanceMissing, result.DenialReason);
            Assert.Equal(0, host.MutationCalls);
            Assert.Equal(0, host.DomMutationCount);
        }
    }

    [Fact]
    public async Task ExactFormalSourceCanAuthorizeRecoveryWithoutFallingBackToTemporarySource()
    {
        var formal = Request() with
        {
            Authorization = Request().Authorization with { IsRecovery = true },
            Binding = Request().Binding with
            {
                Source = AdapterWriteAuthorizationSource.ExactFormalRecord,
                IsRecovery = true,
                FormalEvidenceFingerprint = Token,
            },
        };
        var host = new FakeScriptHost(Observed(), Mutated());
        var executor = new WebView2CandidateDomExecutor(host, new ExactAuthorizer(formal));
        var observed = await executor.ObserveAsync(formal);

        var result = await executor.MutateAsync(formal, observed.Observation!);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, host.DomMutationCount);
    }

    [Fact]
    public async Task ProductionTemporarySourceUsesExactOriginalExpiryAndRejectsCrossIdentity()
    {
        var clock = new MutableTimeProvider(new(2026, 8, 26, 8, 0, 0, TimeSpan.Zero));
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(true, [AcceptanceWriteLeaseOptions.Switch]),
            new MemoryCapabilityStore(),
            clock);
        var template = Request();
        var prepare = template.Authorization with { Action = AdapterWriteAction.PrepareShare };
        Assert.True(coordinator.TryCreateLease(prepare, new(true, true, true)));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(prepare));
        var request = From(prepare, binding);
        var host = new FakeScriptHost(Observed(), Mutated());
        var executor = new WebView2CandidateDomExecutor(host, coordinator);
        var observed = await executor.ObserveAsync(request);

        Assert.True((await executor.MutateAsync(request, observed.Observation!)).IsSuccess);

        clock.Advance(TimeSpan.FromMinutes(6));
        var expiredHost = new FakeScriptHost(Observed(), Mutated());
        var expiredExecutor = new WebView2CandidateDomExecutor(expiredHost, coordinator);
        var expiredObservation = await expiredExecutor.ObserveAsync(request);
        var expired = await expiredExecutor.MutateAsync(request, expiredObservation.Observation!);
        var cross = request with
        {
            Authorization = request.Authorization with { OperationId = Guid.NewGuid() },
        };
        var crossResult = await expiredExecutor.MutateAsync(cross, expiredObservation.Observation!);

        Assert.False(expired.IsSuccess);
        Assert.False(crossResult.IsSuccess);
        Assert.Equal(0, expiredHost.DomMutationCount);
    }

    [Fact]
    public async Task ProductionExactFormalSourceAllowsBoundRecoveryAndRejectsChangedRecord()
    {
        var now = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var store = new MemoryCapabilityStore();
        var template = Request();
        var prepare = template.Authorization with
        {
            Action = AdapterWriteAction.PrepareShare,
            IsRecovery = true,
        };
        store.Record = new(
            prepare.Capability.AdapterKind,
            prepare.Capability.ProtocolVersion,
            prepare.Capability.SelectorVersion,
            prepare.Capability.BuildVersion,
            now,
            Token);
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(false, []), store, new MutableTimeProvider(now));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(prepare));
        var request = From(prepare, binding);
        var host = new FakeScriptHost(Observed(), Mutated());
        var executor = new WebView2CandidateDomExecutor(host, coordinator);
        var observed = await executor.ObserveAsync(request);

        Assert.True((await executor.MutateAsync(request, observed.Observation!)).IsSuccess);

        store.Record = store.Record with { AcceptedAt = now.AddMinutes(1), EvidenceHash = new('b', 64) };
        var changedHost = new FakeScriptHost(Observed(), Mutated());
        var changedExecutor = new WebView2CandidateDomExecutor(changedHost, coordinator);
        var changedObservation = await changedExecutor.ObserveAsync(request);
        Assert.False((await changedExecutor.MutateAsync(request, changedObservation.Observation!)).IsSuccess);
        Assert.Equal(0, changedHost.DomMutationCount);
    }

    [Fact]
    public async Task CancellationStopsBeforeScriptOrAuthorization()
    {
        var host = new FakeScriptHost(Observed(), Mutated());
        var authorizer = new ExactAuthorizer(Request());
        var executor = new WebView2CandidateDomExecutor(host, authorizer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            executor.ObserveAsync(Request(), cancellation.Token));

        Assert.Equal(0, host.TotalCalls);
        Assert.Equal(0, authorizer.CallCount);
    }

    [Fact]
    public void BuiltInScriptsContainNoNetworkCredentialOrExternalScriptSurface()
    {
        var scripts = WebView2CandidateDomExecutor.ObserveScript +
                      WebView2CandidateDomExecutor.MutateScript;

        Assert.DoesNotContain("fetch(", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XMLHttpRequest", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localStorage", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionStorage", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createElement('script", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("officialUri: location.href", scripts, StringComparison.Ordinal);
        Assert.Contains("location.href", WebView2CandidateDomExecutor.MutateScript, StringComparison.Ordinal);
        Assert.Contains("expectedObservationToken", WebView2CandidateDomExecutor.MutateScript, StringComparison.Ordinal);
        Assert.Contains("mutationCount: 0", WebView2CandidateDomExecutor.MutateScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionUnprovenFieldProtocolIsExplicitlyNotValidated()
    {
        var host = new BuiltInCandidateScriptTestHost();
        var executor = new WebView2CandidateDomExecutor(host, new ExactAuthorizer(Request()));

        var result = await executor.ObserveAsync(Request());

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduAutomationDenialReason.FieldEvidenceNotValidated, result.DenialReason);
        Assert.Equal(0, host.DomMutationCount);
    }

    private static BaiduCandidateDomRequest Request()
    {
        var capability = new AdapterCapabilityVersion("BaiduShare", "candidate-p1", "candidate-s1", "build-1");
        var authorization = new AdapterWriteAuthorizationRequest(
            capability,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            1,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            AdapterWriteAction.SubmitShare);
        var now = DateTimeOffset.UtcNow;
        var binding = new AdapterWriteAuthorizationBinding(
            AdapterWriteAuthorizationSource.ConsumedLease,
            capability,
            authorization.AccountId,
            authorization.OperationId,
            authorization.Generation,
            authorization.BatchId,
            now,
            now.AddMinutes(5),
            false,
            null);
        return new(
            authorization,
            binding,
            BaiduAutomationAction.ClickShare,
            new Uri("https://pan.baidu.com/disk/main"),
            BaiduCandidatePageState.OwnDrive,
            ["remote-1"]);
    }

    private static BaiduCandidateDomRequest From(
        AdapterWriteAuthorizationRequest authorization,
        AdapterWriteAuthorizationBinding binding) => new(
        authorization,
        binding,
        BaiduAutomationAction.ClickShare,
        new Uri("https://pan.baidu.com/disk/main"),
        BaiduCandidatePageState.OwnDrive,
        ["remote-1"]);

    private static string Observed(
        int controlCount = 1,
        string state = "OwnDrive",
        bool complete = true,
        string[]? exactIds = null) => $$"""
        {"status":"validated","reason":"none","officialUriMatched":true,"pageState":"{{state}}","controlCount":{{controlCount}},"complete":{{complete.ToString().ToLowerInvariant()}},"stable":true,"exactIds":[{{string.Join(',', (exactIds ?? ["remote-1"]).Select(value => $"\"{value}\""))}}],"observationToken":"{{Token}}","mutationCount":0}
        """;

    private static string Mutated() => $$"""
        {"status":"validated","reason":"none","officialUriMatched":true,"pageState":"OwnDrive","controlCount":1,"complete":true,"stable":true,"exactIds":["remote-1"],"observationToken":"{{Token}}","mutationCount":1}
        """;

    private static string NotValidated(string reason) => $$"""
        {"status":"notValidated","reason":"{{reason}}","officialUriMatched":true,"pageState":"OwnDrive","controlCount":1,"complete":true,"stable":true,"exactIds":["remote-1"],"observationToken":"{{Token}}","mutationCount":0}
        """;

    private sealed class FakeScriptHost(string observation, string mutation) : IBaiduCandidateDomScriptHost
    {
        public int TotalCalls { get; private set; }
        public int MutationCalls { get; private set; }
        public int DomMutationCount { get; private set; }

        public Task<string> ExecuteAsync(
            BaiduCandidateBuiltInScript script,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalCalls++;
            var response = script == BaiduCandidateBuiltInScript.Observe ? observation : mutation;
            if (script == BaiduCandidateBuiltInScript.Mutate)
            {
                MutationCalls++;
                if (response.Contains("\"mutationCount\":1", StringComparison.Ordinal)) DomMutationCount++;
            }
            return Task.FromResult(response);
        }
    }

    private sealed class ExactAuthorizer(BaiduCandidateDomRequest expected) : IBaiduCandidateActionAuthorizationSource
    {
        public int CallCount { get; private set; }

        public ValueTask<bool> IsAuthorizedAsync(
            BaiduCandidateDomRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(
                request == expected &&
                request.Binding.ExpiresAtUtc > now &&
                request.Authorization.IsRecovery == request.Binding.IsRecovery &&
                request.Authorization.Capability == request.Binding.Capability &&
                request.Authorization.AccountId == request.Binding.AccountId &&
                request.Authorization.OperationId == request.Binding.OperationId &&
                request.Authorization.Generation == request.Binding.Generation &&
                request.Authorization.BatchId == request.Binding.BatchId);
        }
    }

    private sealed class BuiltInCandidateScriptTestHost : IBaiduCandidateDomScriptHost
    {
        public int DomMutationCount { get; private set; }

        public Task<string> ExecuteAsync(
            BaiduCandidateBuiltInScript script,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (script == BaiduCandidateBuiltInScript.Mutate) DomMutationCount++;
            return Task.FromResult(WebView2CandidateDomExecutor.UnprovenFieldResponse);
        }
    }

    private sealed class MemoryCapabilityStore : IAdapterCapabilityStore
    {
        public AdapterCapabilityAcceptanceRecord? Record { get; set; }

        public Task AcceptAsync(
            AdapterCapabilityAcceptanceRecord acceptance,
            CancellationToken cancellationToken = default)
        {
            Record = acceptance;
            return Task.CompletedTask;
        }

        public Task<AdapterCapabilityAcceptanceRecord?> GetAsync(
            string adapterKind,
            string protocolVersion,
            string selectorVersion,
            string adapterBuildVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Record is { } record &&
                            record.AdapterKind == adapterKind &&
                            record.ProtocolVersion == protocolVersion &&
                            record.SelectorVersion == selectorVersion &&
                            record.AdapterBuildVersion == adapterBuildVersion
                ? record
                : null);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
