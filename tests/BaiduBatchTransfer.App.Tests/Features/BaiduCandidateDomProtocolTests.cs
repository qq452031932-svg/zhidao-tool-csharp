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
        var request = From(prepare, binding) with { Action = BaiduAutomationAction.SelectExactItems };
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
        var request = From(prepare, binding) with { Action = BaiduAutomationAction.SelectExactItems };
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedProductionBindingImmediatelyDeniesOldSessionBeforeMutation(bool formal)
    {
        var now = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var store = new MemoryCapabilityStore();
        var template = Request();
        var prepare = template.Authorization with { Action = AdapterWriteAction.PrepareShare };
        if (formal)
        {
            store.Record = new(
                prepare.Capability.AdapterKind,
                prepare.Capability.ProtocolVersion,
                prepare.Capability.SelectorVersion,
                prepare.Capability.BuildVersion,
                now,
                Token);
        }
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(!formal, formal ? [] : [AcceptanceWriteLeaseOptions.Switch]),
            store,
            new MutableTimeProvider(now));
        if (!formal)
            Assert.True(coordinator.TryCreateLease(prepare, new(true, true, true)));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(prepare));
        var request = From(prepare, binding) with { Action = BaiduAutomationAction.SelectExactItems };
        var host = new FakeScriptHost(Observed(), Mutated());
        var executor = new WebView2CandidateDomExecutor(host, coordinator);
        var observed = await executor.ObserveAsync(request);
        Assert.True(await coordinator.IsAuthorizedAsync(request));

        await coordinator.ReleaseBindingAsync(binding);
        var result = await executor.MutateAsync(request, observed.Observation!);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduAutomationDenialReason.AcceptanceMissing, result.DenialReason);
        Assert.Equal(0, host.MutationCalls);
        Assert.Equal(0, host.DomMutationCount);
    }

    [Fact]
    public async Task MutationPermitAtomicallyBlocksReleaseAndAnyLaterAcquisition()
    {
        var now = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(true, [AcceptanceWriteLeaseOptions.Switch]),
            new MemoryCapabilityStore(),
            new MutableTimeProvider(now));
        var prepare = Request().Authorization with { Action = AdapterWriteAction.PrepareShare };
        Assert.True(coordinator.TryCreateLease(prepare, new(true, true, true)));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(prepare));
        var request = From(prepare, binding) with { Action = BaiduAutomationAction.SelectExactItems };
        var permit = Assert.IsAssignableFrom<IBaiduCandidateMutationPermit>(
            await coordinator.TryAcquireMutationPermitAsync(request));

        Assert.False(await coordinator.TryValidateBindingAsync(
            prepare with { Action = AdapterWriteAction.SubmitShare }, binding));

        using var cancelledRelease = new CancellationTokenSource();
        cancelledRelease.Cancel();
        var release = coordinator.ReleaseBindingAsync(binding, cancelledRelease.Token).AsTask();

        Assert.False(release.IsCompleted);
        Assert.Null(await coordinator.TryAcquireMutationPermitAsync(request));
        await permit.DisposeAsync();
        await release;
        Assert.Null(await coordinator.TryAcquireMutationPermitAsync(request));
    }

    [Fact]
    public async Task InFlightMutationPermitFencesReleaseUntilScriptAndParsingComplete()
    {
        var now = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(true, [AcceptanceWriteLeaseOptions.Switch]),
            new MemoryCapabilityStore(),
            new MutableTimeProvider(now));
        var prepare = Request().Authorization with { Action = AdapterWriteAction.PrepareShare };
        Assert.True(coordinator.TryCreateLease(prepare, new(true, true, true)));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(prepare));
        var request = From(prepare, binding) with { Action = BaiduAutomationAction.SelectExactItems };
        var host = new BlockingMutationScriptHost();
        var executor = new WebView2CandidateDomExecutor(host, coordinator);
        var observed = await executor.ObserveAsync(request);

        var mutation = executor.MutateAsync(request, observed.Observation!);
        await host.MutationEntered;
        var release = coordinator.ReleaseBindingAsync(binding).AsTask();
        var deniedHost = new FakeScriptHost(Observed(), Mutated());
        var denied = await new WebView2CandidateDomExecutor(deniedHost, coordinator)
            .MutateAsync(request, observed.Observation!);

        Assert.False(release.IsCompleted);
        Assert.False(denied.IsSuccess);
        Assert.Equal(0, deniedHost.MutationCalls);
        host.AllowMutationToComplete();
        Assert.True((await mutation).IsSuccess);
        await release;

        var afterReleaseHost = new FakeScriptHost(Observed(), Mutated());
        var afterRelease = await new WebView2CandidateDomExecutor(afterReleaseHost, coordinator)
            .MutateAsync(request, observed.Observation!);
        Assert.False(afterRelease.IsSuccess);
        Assert.Equal(0, afterReleaseHost.MutationCalls);
    }

    [Fact]
    public async Task CancellationAfterPermitAcquisitionReleasesPermitWithoutDomMutation()
    {
        var now = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(true, [AcceptanceWriteLeaseOptions.Switch]),
            new MemoryCapabilityStore(),
            new MutableTimeProvider(now));
        var prepare = Request().Authorization with { Action = AdapterWriteAction.PrepareShare };
        Assert.True(coordinator.TryCreateLease(prepare, new(true, true, true)));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(prepare));
        var request = From(prepare, binding) with { Action = BaiduAutomationAction.SelectExactItems };
        var host = new BlockingMutationScriptHost();
        var executor = new WebView2CandidateDomExecutor(host, coordinator);
        var observed = await executor.ObserveAsync(request);
        using var cancellation = new CancellationTokenSource();
        var mutation = executor.MutateAsync(request, observed.Observation!, cancellation.Token);
        await host.MutationEntered;

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        await coordinator.ReleaseBindingAsync(binding);

        Assert.Equal(0, host.DomMutationCount);
    }

    [Fact]
    public async Task PruningAnotherOperationCannotReleaseThroughAnInFlightMutationPermit()
    {
        var now = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(true, [AcceptanceWriteLeaseOptions.Switch]),
            new MemoryCapabilityStore(),
            clock);
        var capability = new AdapterCapabilityVersion(
            "BaiduTransfer", "candidate-p1", "candidate-s1", "build-1");
        var inner = new PermitTransferAdapter();
        var wrapped = new CoordinatedOperationBoundBaiduNetdiskAdapter(
            inner, new BaiduAdapterConcurrencyGate(), coordinator, capability, clock);
        var accountId = Guid.NewGuid();
        var share = new ShareReference("share", "https://pan.baidu.com/s/share", null);
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, share);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            now);
        var ensure = new AdapterWriteAuthorizationRequest(
            capability, accountId, context.OperationId, context.Generation,
            context.BatchId, AdapterWriteAction.EnsureDirectory);
        Assert.True(coordinator.TryCreateLease(ensure, new(true, true, true)));
        var destination = Assert.IsType<RemoteDirectory>((await wrapped
            .EnsureDirectoryForOperationAsync(
                context, session, new RemotePath("/target"), CancellationToken.None)).Value);
        Assert.True((await wrapped.PrepareWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None)).IsSuccess);
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            inner.PreparedContext!.AuthorizationBinding);
        var candidate = new BaiduCandidateDomRequest(
            ensure with { Action = AdapterWriteAction.PrepareTransfer },
            binding,
            BaiduAutomationAction.SelectExactItems,
            new Uri("https://pan.baidu.com/disk/main"),
            BaiduCandidatePageState.ExternalShare,
            ["source-1"]);
        var permit = Assert.IsAssignableFrom<IBaiduCandidateMutationPermit>(
            await coordinator.TryAcquireMutationPermitAsync(candidate));

        clock.Advance(TimeSpan.FromMinutes(6));
        var next = context with { OperationId = Guid.NewGuid(), Generation = 2 };
        var nextEnsure = ensure with
        {
            OperationId = next.OperationId,
            Generation = next.Generation,
        };
        Assert.True(coordinator.TryCreateLease(nextEnsure, new(true, true, true)));
        var pruningOperation = wrapped.EnsureDirectoryForOperationAsync(
            next, session, new RemotePath("/next"), CancellationToken.None);
        await Task.Yield();

        Assert.False(pruningOperation.IsCompleted);
        await permit.DisposeAsync();
        Assert.True((await pruningOperation).IsSuccess);
    }

    [Fact]
    public async Task ProductionCandidateAuthorizationRequiresTheBindingsCurrentStage()
    {
        var now = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(true, [AcceptanceWriteLeaseOptions.Switch]),
            new MemoryCapabilityStore(),
            new MutableTimeProvider(now));
        var template = Request();
        var prepare = template.Authorization with { Action = AdapterWriteAction.PrepareShare };
        Assert.True(coordinator.TryCreateLease(prepare, new(true, true, true)));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(prepare));
        var selection = From(prepare, binding) with { Action = BaiduAutomationAction.SelectExactItems };
        var submission = From(prepare, binding) with { Action = BaiduAutomationAction.ClickShare };
        var transferOnly = From(prepare, binding) with { Action = BaiduAutomationAction.ClickSave };

        Assert.True(await coordinator.IsAuthorizedAsync(selection));
        Assert.False(await coordinator.IsAuthorizedAsync(submission));
        Assert.True(await coordinator.TryValidateBindingAsync(
            prepare with { Action = AdapterWriteAction.SubmitShare }, binding));
        Assert.True(await coordinator.IsAuthorizedAsync(selection));
        Assert.True(await coordinator.IsAuthorizedAsync(submission));
        Assert.False(await coordinator.IsAuthorizedAsync(transferOnly));
    }

    [Fact]
    public async Task TransferSubmitFlowAuthorizesItsMinimalNewSessionCommandSequence()
    {
        var now = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(true, [AcceptanceWriteLeaseOptions.Switch]),
            new MemoryCapabilityStore(),
            new MutableTimeProvider(now));
        var ensure = Request().Authorization with
        {
            Capability = new("BaiduTransfer", "candidate-p1", "candidate-s1", "build-1"),
            Action = AdapterWriteAction.EnsureDirectory,
        };
        Assert.True(coordinator.TryCreateLease(ensure, new(true, true, true)));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(ensure));
        Assert.True(await coordinator.TryValidateBindingAsync(
            ensure with { Action = AdapterWriteAction.PrepareTransfer }, binding));
        Assert.True(await coordinator.TryValidateBindingAsync(
            ensure with { Action = AdapterWriteAction.SubmitTransfer }, binding));
        var session = new FlowCommandAuthorizationSession(
            coordinator,
            ensure with { Action = AdapterWriteAction.SubmitTransfer },
            binding,
            new RemoteItemIdentitySet([new("source-1", "source")]),
            new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Fixed, "a1b2"));
        var request = new TransferAutomationRequest(
            new ShareReference("share", "https://pan.baidu.com/s/share", "a1b2"),
            new RemoteDirectory("target", new RemotePath("/target")),
            DuplicatePolicy.Skip);

        var result = await new TransferAutomationFlow().SubmitAsync(
            session, request, WebAutomationBaseline.Create(["existing"]));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [BaiduAutomationAction.SelectExactItems, BaiduAutomationAction.SetExtractionCode,
             BaiduAutomationAction.ChooseDestination, BaiduAutomationAction.ClickSave],
            session.Actions);
        Assert.False(await session.CanAuthorizeAsync(BaiduAutomationAction.ChoosePermanent));
    }

    [Fact]
    public async Task ShareSubmitAndExportFlowAuthorizesItsMinimalNewSessionCommandSequence()
    {
        var now = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(true, [AcceptanceWriteLeaseOptions.Switch]),
            new MemoryCapabilityStore(),
            new MutableTimeProvider(now));
        var prepare = Request().Authorization with { Action = AdapterWriteAction.PrepareShare };
        Assert.True(coordinator.TryCreateLease(prepare, new(true, true, true)));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(prepare));
        Assert.True(await coordinator.TryValidateBindingAsync(
            prepare with { Action = AdapterWriteAction.SubmitShare }, binding));
        var items = new RemoteItemIdentitySet([new("remote-1", "item")]);
        var settings = new EffectiveShareSettings(
            true, true, ShareExtractionCodeBehavior.Fixed, "a1b2");
        var session = new FlowCommandAuthorizationSession(
            coordinator,
            prepare with { Action = AdapterWriteAction.SubmitShare },
            binding,
            items,
            settings);

        var result = await new ShareAutomationFlow().SubmitAndExportAsync(
            session,
            new PermanentShareRequest(items, "a1b2"),
            WebAutomationBaseline.Create([]));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [BaiduAutomationAction.SelectExactItems, BaiduAutomationAction.ChoosePermanent,
             BaiduAutomationAction.SetExtractionCode, BaiduAutomationAction.ClickShare,
             BaiduAutomationAction.CreateLink, BaiduAutomationAction.ExportResult],
            session.Actions);
        Assert.False(await session.CanAuthorizeAsync(BaiduAutomationAction.ClickSave));
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

    private sealed class BlockingMutationScriptHost : IBaiduCandidateDomScriptHost
    {
        private readonly TaskCompletionSource mutationEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource allowMutation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task MutationEntered => mutationEntered.Task;
        public int DomMutationCount { get; private set; }
        public void AllowMutationToComplete() => allowMutation.TrySetResult();

        public async Task<string> ExecuteAsync(
            BaiduCandidateBuiltInScript script,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (script == BaiduCandidateBuiltInScript.Observe) return Observed();
            mutationEntered.TrySetResult();
            await allowMutation.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            DomMutationCount++;
            return Mutated();
        }
    }

    private sealed class FlowCommandAuthorizationSession(
        IBaiduCandidateActionAuthorizationSource source,
        AdapterWriteAuthorizationRequest authorization,
        AdapterWriteAuthorizationBinding binding,
        RemoteItemIdentitySet exactItems,
        EffectiveShareSettings settings) : ITransferAutomationSession, IShareResultExportSession
    {
        public List<BaiduAutomationAction> Actions { get; } = [];

        public ValueTask<bool> CanAuthorizeAsync(
            BaiduAutomationAction action,
            CancellationToken cancellationToken = default)
        {
            var request = new BaiduCandidateDomRequest(
                authorization,
                binding,
                action,
                new Uri("https://pan.baidu.com/disk/main"),
                BaiduCandidatePageState.OwnDrive,
                exactItems.Items.Select(item => item.ExactId));
            return source.IsAuthorizedAsync(request, cancellationToken);
        }

        private async Task AuthorizeAsync(BaiduAutomationAction action, CancellationToken cancellationToken)
        {
            if (!await CanAuthorizeAsync(action, cancellationToken))
                throw new BaiduAutomationNotValidatedException(
                    BaiduAutomationDenialReason.AcceptanceMissing,
                    "The flow command was not authorized for its active binding stage.");
            Actions.Add(action);
        }

        public async Task<TransferSourceListing> OpenExternalShareAsync(
            ShareReference share, CancellationToken cancellationToken)
        {
            await AuthorizeAsync(BaiduAutomationAction.SelectExactItems, cancellationToken);
            return new(exactItems.Items.Length, Token);
        }

        public Task ReturnToExternalShareAsync(ShareReference share, CancellationToken cancellationToken) =>
            AuthorizeAsync(BaiduAutomationAction.SelectExactItems, cancellationToken);

        public Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken) =>
            AuthorizeAsync(BaiduAutomationAction.SetExtractionCode, cancellationToken);

        public Task<TransferDirectoryListing> OpenDestinationAndReadStableListingAsync(
            RemoteDirectory destination, CancellationToken cancellationToken) =>
            Task.FromResult(new TransferDirectoryListing(
                true, true, [new("existing", "existing")], Token));

        public Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(
            RemoteDirectory destination, CancellationToken cancellationToken) =>
            Task.FromResult(new TransferDirectoryListing(
                true, true, [new("existing", "existing"), .. exactItems.Items], Token));

        public async Task<TransferOfficialAcceptance> SaveWholeShareAsync(
            RemoteDirectory destination,
            DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken)
        {
            await AuthorizeAsync(BaiduAutomationAction.ChooseDestination, cancellationToken);
            await AuthorizeAsync(BaiduAutomationAction.ClickSave, cancellationToken);
            return new(TransferOfficialAcceptanceStatus.Accepted, "request", exactItems, Token);
        }

        public Task SelectExactRemoteItemsAsSingleGroupAsync(
            RemoteItemIdentitySet remoteItems, CancellationToken cancellationToken) =>
            AuthorizeAsync(BaiduAutomationAction.SelectExactItems, cancellationToken);

        public async Task ConfigurePermanentShareAsync(
            PermanentShareRequest request, CancellationToken cancellationToken)
        {
            await AuthorizeAsync(BaiduAutomationAction.ChoosePermanent, cancellationToken);
            await AuthorizeAsync(BaiduAutomationAction.SetExtractionCode, cancellationToken);
        }

        public async Task<ShareOfficialAcceptance> CreateShareOnceAsync(
            CancellationToken cancellationToken)
        {
            await AuthorizeAsync(BaiduAutomationAction.ClickShare, cancellationToken);
            await AuthorizeAsync(BaiduAutomationAction.CreateLink, cancellationToken);
            return ShareOfficialAcceptance.Accepted(
                "request", "share-id", "https://pan.baidu.com/s/share-id",
                exactItems, settings, Token);
        }

        public Task<ShareOfficialListing> ReadCompleteStableOfficialShareListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new ShareOfficialListing(
                true,
                true,
                [new ShareOfficialListEntry(
                    "share-id", "https://pan.baidu.com/s/share-id", exactItems, settings)],
                Token));

        public Task ExportOfficialResultAsync(CancellationToken cancellationToken) =>
            AuthorizeAsync(BaiduAutomationAction.ExportResult, cancellationToken);

        public Task MarkSubmittingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PersistBaselineAsync(
            WebAutomationBaseline baseline, string observationFingerprint,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PersistOfficialAcceptanceAsync(
            TransferOfficialAcceptance acceptance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PersistOfficialAcceptanceAsync(
            ShareOfficialAcceptance acceptance, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PermitTransferAdapter : IOperationBoundBaiduNetdiskAdapter
    {
        public TransferOperationAutomationContext? PreparedContext { get; private set; }

        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryForOperationAsync(
            TransferOperationAutomationContext context, AccountSession session, RemotePath path,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<RemoteDirectory>.Success(
                new RemoteDirectory("target", path)));

        public Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(
            TransferOperationAutomationContext context, AccountSession session, ShareReference share,
            RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken)
        {
            PreparedContext = context;
            return Task.FromResult(BaiduAdapterResult<TransferPreparationReceipt>.Success(
                TransferPreparationReceipt.Ready));
        }

        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
            TransferOperationAutomationContext context, AccountSession session, ShareReference share,
            RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
            TransferOperationAutomationContext context, AccountSession session,
            TransferConfirmationQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(
            AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(
            AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<System.Collections.Immutable.ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(
            AccountSession session, RemotePath path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(
            AccountSession session, RemotePath path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(
            AccountSession session, ShareReference share, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(
            AccountSession session, ShareReference share, RemoteDirectory destination,
            DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(
            AccountSession session, TransferConfirmationQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();
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
            return ValueTask.FromResult(IsExact(request));
        }

        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireMutationPermitAsync(
            BaiduCandidateDomRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult<IBaiduCandidateMutationPermit?>(
                IsExact(request) ? NoopMutationPermit.Instance : null);
        }

        private bool IsExact(BaiduCandidateDomRequest request)
        {
            var now = DateTimeOffset.UtcNow;
            return
                request == expected &&
                request.Binding.ExpiresAtUtc > now &&
                request.Authorization.IsRecovery == request.Binding.IsRecovery &&
                request.Authorization.Capability == request.Binding.Capability &&
                request.Authorization.AccountId == request.Binding.AccountId &&
                request.Authorization.OperationId == request.Binding.OperationId &&
                request.Authorization.Generation == request.Binding.Generation &&
                request.Authorization.BatchId == request.Binding.BatchId;
        }
    }

    private sealed class NoopMutationPermit : IBaiduCandidateMutationPermit
    {
        public static NoopMutationPermit Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
