using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class AcceptanceWriteLeaseCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LeaseRequiresDebugSwitchAndEveryDisposableResourceConfirmation()
    {
        var request = Request();

        Assert.False(Coordinator(false, ["--webview-acceptance"]).TryCreateLease(request, Confirmed()));
        Assert.False(Coordinator(true, []).TryCreateLease(request, Confirmed()));
        Assert.False(Coordinator(true, ["--webview-acceptance"]).TryCreateLease(
            request, new(true, false, true)));
        Assert.True(Coordinator(true, ["--webview-acceptance"]).TryCreateLease(request, Confirmed()));
    }

    [Fact]
    public async Task LeaseIsExactSingleUseAndCannotAuthorizeAnotherIdentity()
    {
        var coordinator = Coordinator(true, ["--webview-acceptance"]);
        var request = Request();
        Assert.True(coordinator.TryCreateLease(request, Confirmed()));

        Assert.False(await coordinator.TryAuthorizeAsync(request with { Generation = 2 }));
        Assert.True(await coordinator.TryAuthorizeAsync(request));
        Assert.False(await coordinator.TryAuthorizeAsync(request));
    }

    [Fact]
    public async Task ConsumedRequestCanNeverReceiveAReplacementLease()
    {
        var coordinator = Coordinator(true, ["--webview-acceptance"]);
        var request = Request();
        Assert.True(coordinator.TryCreateLease(request, Confirmed()));
        Assert.True(await coordinator.TryAuthorizeAsync(request));

        Assert.False(coordinator.TryCreateLease(request, Confirmed()));
    }

    [Fact]
    public async Task ExpiredLeaseFailsClosed()
    {
        var clock = new MutableTimeProvider(Now);
        var coordinator = Coordinator(true, ["--webview-acceptance"], clock);
        var request = Request();
        Assert.True(coordinator.TryCreateLease(request, Confirmed()));

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.False(await coordinator.TryAuthorizeAsync(request));
    }

    [Fact]
    public void ExpiredUnconsumedLeaseCannotBeReissuedForTheSameExactIdentity()
    {
        var clock = new MutableTimeProvider(Now);
        var coordinator = Coordinator(true, ["--webview-acceptance"], clock);
        var request = Request();
        Assert.True(coordinator.TryCreateLease(request, Confirmed()));

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.False(coordinator.TryCreateLease(request, Confirmed()));
        Assert.True(coordinator.TryCreateLease(
            request with { OperationId = Guid.NewGuid() }, Confirmed()));
        Assert.True(coordinator.TryCreateLease(
            request with { Generation = request.Generation + 1 }, Confirmed()));
    }

    [Fact]
    public async Task ChainedTransferStagesCannotExtendTheOriginalOperationAuthorization()
    {
        var clock = new MutableTimeProvider(Now);
        var coordinator = Coordinator(true, ["--webview-acceptance"], clock);
        var ensure = Request() with { Action = AdapterWriteAction.EnsureDirectory };
        Assert.True(coordinator.TryCreateLease(ensure, Confirmed()));
        Assert.True(await coordinator.TryAuthorizeAsync(ensure));

        clock.Advance(TimeSpan.FromMinutes(6));
        var submit = ensure with { Action = AdapterWriteAction.SubmitTransfer };

        Assert.False(await coordinator.TryAuthorizeAsync(submit));
        Assert.False(coordinator.IsAuthorized(Navigation(submit)));
    }

    [Fact]
    public async Task FormalAcceptanceRequiresAllEvidenceAndSecondConfirmation()
    {
        var store = new MemoryCapabilityStore();
        var presenter = new RecordingPresenter();
        var coordinator = Coordinator(true, ["--webview-acceptance"], store: store, presenter: presenter);
        var request = Request();
        Assert.True(coordinator.TryCreateLease(request, Confirmed()));
        Assert.True(await coordinator.TryAuthorizeAsync(request));

        Assert.False(await coordinator.AcceptVersionAsync(request, userConfirmed: true));
        await coordinator.RecordRemoteResultAsync(request, "remote-A");
        await coordinator.RecordRecoveryAsync(request with { IsRecovery = true }, "remote-A");
        await coordinator.OnLocalResultExportCompletedAsync(ExportEvidence(request));
        Assert.Equal(1, presenter.EnableCount);
        Assert.False(await coordinator.AcceptVersionAsync(request, userConfirmed: false));
        Assert.True(await coordinator.AcceptVersionAsync(request, userConfirmed: true));
        Assert.False(coordinator.TryCreateLease(request, Confirmed()));

        var saved = Assert.Single(store.Records);
        Assert.Equal(request.Capability.SelectorVersion, saved.SelectorVersion);
    }

    [Fact]
    public async Task MismatchedOrNonRecoveryEvidenceCannotEnableFormalAcceptance()
    {
        var presenter = new RecordingPresenter();
        var coordinator = Coordinator(true, ["--webview-acceptance"], presenter: presenter);
        var request = Request();
        Assert.True(coordinator.TryCreateLease(request, Confirmed()));
        Assert.True(await coordinator.TryAuthorizeAsync(request));

        await coordinator.RecordRemoteResultAsync(request, "remote-A");
        await coordinator.RecordRecoveryAsync(request, "remote-A");
        await coordinator.RecordRemoteResultAsync(request with { IsRecovery = true }, "remote-B");
        await coordinator.RecordRecoveryAsync(request with { IsRecovery = true }, "remote-B");
        await coordinator.OnLocalResultExportCompletedAsync(ExportEvidence(request));

        Assert.Equal(0, presenter.EnableCount);
        Assert.False(await coordinator.AcceptVersionAsync(request, userConfirmed: true));
    }

    [Fact]
    public async Task SameBatchExportForAnotherOperationCannotCompleteCandidateEvidence()
    {
        var presenter = new RecordingPresenter();
        var coordinator = Coordinator(true, ["--webview-acceptance"], presenter: presenter);
        var request = Request();
        Assert.True(coordinator.TryCreateLease(request, Confirmed()));
        Assert.True(await coordinator.TryAuthorizeAsync(request));
        await coordinator.RecordRemoteResultAsync(request, "remote-A");
        await coordinator.RecordRecoveryAsync(request with { IsRecovery = true }, "remote-A");

        await coordinator.OnLocalResultExportCompletedAsync(
            ExportEvidence(request, operationId: Guid.NewGuid()));

        Assert.Equal(0, presenter.EnableCount);
        Assert.False(await coordinator.AcceptVersionAsync(request, userConfirmed: true));
    }

    [Fact]
    public async Task ThrowingFormalAcceptancePresenterCannotEraseCapturedEvidence()
    {
        var store = new MemoryCapabilityStore();
        var coordinator = Coordinator(
            true, ["--webview-acceptance"], store: store, presenter: new ThrowingPresenter());
        var request = Request();
        Assert.True(coordinator.TryCreateLease(request, Confirmed()));
        Assert.True(await coordinator.TryAuthorizeAsync(request));
        await coordinator.RecordRemoteResultAsync(request, "remote-A");
        await coordinator.RecordRecoveryAsync(request with { IsRecovery = true }, "remote-A");

        await coordinator.OnLocalResultExportCompletedAsync(ExportEvidence(request));

        Assert.True(await coordinator.AcceptVersionAsync(request, userConfirmed: true));
        Assert.Single(store.Records);
    }

    [Fact]
    public async Task StaleOrDifferentVersionFormalAcceptanceFailsClosed()
    {
        var store = new MemoryCapabilityStore();
        store.Records.Add(new(
            "BaiduTransfer", "protocol-v1", "selector-v1", "build-1",
            Now.AddDays(-31), Hash));
        var coordinator = Coordinator(false, ["--webview-acceptance"], store: store);

        Assert.False(await coordinator.TryAuthorizeAsync(Request()));
        Assert.False(await coordinator.TryAuthorizeAsync(Request() with
        {
            Capability = new("BaiduTransfer", "protocol-v1", "selector-v2", "build-1"),
        }));
    }

    [Fact]
    public async Task ExactConsumedDebugLeaseAuthorizesOnlyItsOfficialCandidateNavigation()
    {
        var coordinator = Coordinator(true, ["--webview-acceptance"]);
        var request = Request();
        Assert.True(coordinator.TryCreateLease(request, Confirmed()));
        Assert.True(await coordinator.TryAuthorizeAsync(request));
        var authorizer = new ProductionWebAutomationNavigationAuthorizer(coordinator);

        var exact = Navigation(request);

        Assert.True(authorizer.Authorize(exact).IsAllowed);
        Assert.False(authorizer.Authorize(exact with
        {
            AcceptanceScope = exact.AcceptanceScope! with { OperationId = Guid.NewGuid() },
        }).IsAllowed);
        Assert.False(authorizer.Authorize(exact with
        {
            OfficialUri = new Uri("https://example.com/"),
        }).IsAllowed);
    }

    [Fact]
    public async Task FreshFormalAcceptanceAlsoCreatesExactOrdinaryNavigationScope()
    {
        var store = new MemoryCapabilityStore();
        var request = Request();
        store.Records.Add(new(
            request.Capability.AdapterKind,
            request.Capability.ProtocolVersion,
            request.Capability.SelectorVersion,
            request.Capability.BuildVersion,
            Now,
            Hash));
        var coordinator = Coordinator(false, [], store: store);

        Assert.True(await coordinator.TryAuthorizeAsync(request));

        var authorizer = new ProductionWebAutomationNavigationAuthorizer(coordinator);
        Assert.True(authorizer.Authorize(Navigation(request)).IsAllowed);
        Assert.False(authorizer.Authorize(Navigation(request with
        {
            Capability = new AdapterCapabilityVersion(
                request.Capability.AdapterKind,
                request.Capability.ProtocolVersion,
                "different",
                request.Capability.BuildVersion),
        })).IsAllowed);
    }

    private static BaiduNavigationAuthorizationRequest Navigation(
        AdapterWriteAuthorizationRequest request) => new(
        BaiduAutomationAction.OpenOfficialUri,
        new Uri("https://pan.baidu.com/disk/main"),
        request.Capability.ProtocolVersion,
        request.Capability.SelectorVersion,
        request.Capability.BuildVersion,
        new BaiduAutomationAcceptance(
            false,
            request.Capability.ProtocolVersion,
            request.Capability.SelectorVersion,
            request.Capability.BuildVersion),
        BaiduProtocolValidationStatus.NotValidated,
        new BaiduAcceptanceOperationScope(
            request.Capability.AdapterKind,
            request.AccountId,
            request.OperationId,
            request.Generation,
            request.BatchId));

    private static AcceptanceWriteLeaseCoordinator Coordinator(
        bool debug,
        IReadOnlyList<string> args,
        TimeProvider? clock = null,
        MemoryCapabilityStore? store = null,
        IAcceptanceWriteLeasePresenter? presenter = null) => new(
            new AcceptanceWriteLeaseOptions(debug, args),
            store ?? new MemoryCapabilityStore(),
            clock ?? new MutableTimeProvider(Now),
            presenter);

    private static AdapterWriteAuthorizationRequest Request() => new(
        new AdapterCapabilityVersion("BaiduTransfer", "protocol-v1", "selector-v1", "build-1"),
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        1,
        Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private static AcceptanceWriteLeaseConfirmation Confirmed() => new(true, true, true);
    private static LocalResultExportCompletedEvidence ExportEvidence(
        AdapterWriteAuthorizationRequest request,
        Guid? operationId = null) => new(
        new LocalResultExportKey(
            LocalResultOwnerKind.TransferBatch,
            request.BatchId,
            LocalResultExportKind.ReplacementWorkbook,
            1),
        [new LocalResultOperationEvidence(
            request.Capability.AdapterKind,
            request.AccountId,
            operationId ?? request.OperationId,
            request.Generation,
            Hash)],
        Hash,
        Hash);
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private sealed class MemoryCapabilityStore : IAdapterCapabilityStore
    {
        public List<AdapterCapabilityAcceptanceRecord> Records { get; } = [];

        public Task AcceptAsync(AdapterCapabilityAcceptanceRecord acceptance, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(value => value.AdapterKind == acceptance.AdapterKind);
            Records.Add(acceptance);
            return Task.CompletedTask;
        }

        public Task<AdapterCapabilityAcceptanceRecord?> GetAsync(
            string adapterKind,
            string protocolVersion,
            string selectorVersion,
            string adapterBuildVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Records.SingleOrDefault(value =>
                value.AdapterKind == adapterKind &&
                value.ProtocolVersion == protocolVersion &&
                value.SelectorVersion == selectorVersion &&
                value.AdapterBuildVersion == adapterBuildVersion));
    }

    private sealed class RecordingPresenter : IAcceptanceWriteLeasePresenter
    {
        public int EnableCount { get; private set; }
        public Task<bool> PresentAsync(AdapterWriteAuthorizationRequest request, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task EnableFormalAcceptanceAsync(AdapterWriteAuthorizationRequest request, string evidenceHash, CancellationToken cancellationToken = default)
        {
            EnableCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPresenter : IAcceptanceWriteLeasePresenter
    {
        public Task<bool> PresentAsync(AdapterWriteAuthorizationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task EnableFormalAcceptanceAsync(AdapterWriteAuthorizationRequest request, string evidenceHash, CancellationToken cancellationToken = default) =>
            Task.FromException(new OperationCanceledException("simulated presenter cancellation"));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
