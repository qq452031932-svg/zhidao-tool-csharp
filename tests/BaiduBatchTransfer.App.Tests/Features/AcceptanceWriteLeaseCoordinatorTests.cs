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
    public async Task TemporaryShareBindingIsCreatedOnceAndSubmitOnlyRevalidatesExactIdentity()
    {
        var coordinator = Coordinator(true, ["--webview-acceptance"]);
        var prepare = Request() with
        {
            Capability = new("BaiduShare", "protocol-v1", "selector-v1", "build-1"),
            Action = AdapterWriteAction.PrepareShare,
        };
        Assert.True(coordinator.TryCreateLease(prepare, Confirmed()));

        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(prepare));

        Assert.Equal(AdapterWriteAuthorizationSource.ConsumedLease, binding.Source);
        Assert.Equal(prepare.OperationId, binding.OperationId);
        var submit = prepare with { Action = AdapterWriteAction.SubmitShare };
        Assert.False(await coordinator.TryValidateBindingAsync(
            submit with { OperationId = Guid.NewGuid() }, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(
            submit with { AccountId = Guid.NewGuid() }, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(
            submit with { Generation = submit.Generation + 1 }, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(
            submit with { BatchId = Guid.NewGuid() }, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(
            submit with
            {
                Capability = new AdapterCapabilityVersion(
                    submit.Capability.AdapterKind,
                    submit.Capability.ProtocolVersion,
                    "other-selector",
                    submit.Capability.BuildVersion),
            }, binding));
        Assert.True(await coordinator.TryValidateBindingAsync(submit, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(submit, binding));
        Assert.Null(await coordinator.TryCreateBindingAsync(prepare));
    }

    [Fact]
    public async Task TemporaryTransferBindingRequiresExactEnsurePrepareSubmitSequence()
    {
        var coordinator = Coordinator(true, ["--webview-acceptance"]);
        var ensure = Request() with { Action = AdapterWriteAction.EnsureDirectory };
        Assert.True(coordinator.TryCreateLease(ensure, Confirmed()));

        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(ensure));
        var prepare = ensure with { Action = AdapterWriteAction.PrepareTransfer };
        var submit = ensure with { Action = AdapterWriteAction.SubmitTransfer };

        Assert.False(await coordinator.TryValidateBindingAsync(submit, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(
            prepare with { OperationId = Guid.NewGuid() }, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(
            prepare with { AccountId = Guid.NewGuid() }, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(
            prepare with { Generation = prepare.Generation + 1 }, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(
            prepare with { BatchId = Guid.NewGuid() }, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(
            prepare with
            {
                Capability = new AdapterCapabilityVersion(
                    prepare.Capability.AdapterKind,
                    prepare.Capability.ProtocolVersion,
                    prepare.Capability.SelectorVersion,
                    "other-build"),
            }, binding));
        Assert.True(await coordinator.TryValidateBindingAsync(prepare, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(prepare, binding));
        Assert.True(await coordinator.TryValidateBindingAsync(submit, binding));
        Assert.False(await coordinator.TryValidateBindingAsync(submit, binding));
        Assert.Null(await coordinator.TryCreateBindingAsync(ensure));
    }

    [Fact]
    public async Task TransferCandidateMutationPermitsFollowExactStageMatrixAndRecoveryIsReadOnly()
    {
        var coordinator = Coordinator(true, ["--webview-acceptance"]);
        var ensure = Request() with { Action = AdapterWriteAction.EnsureDirectory };
        Assert.True(coordinator.TryCreateLease(ensure, Confirmed()));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(ensure));
        var read = new BaiduCandidateDomReadRequest(
            new Uri("https://pan.baidu.com/disk/main"), BaiduCandidatePageState.DestinationPicker,
            "uid-1", ["item-1"], "parent-1", new RemotePath("/目标"));

        foreach (var action in new[]
        {
            BaiduAutomationAction.SetExtractionCode,
            BaiduAutomationAction.SubmitExtraction,
            BaiduAutomationAction.OpenDestinationPicker,
            BaiduAutomationAction.EnterDirectory,
            BaiduAutomationAction.CreateDirectory,
            BaiduAutomationAction.ConfirmDirectoryCreation,
            BaiduAutomationAction.CancelDestinationPicker,
        })
        {
            var permit = await coordinator.TryAcquireTransferMutationPermitAsync(
                Mutation(ensure, binding, read, action));
            Assert.NotNull(permit);
            await permit.DisposeAsync();
        }
        Assert.Null(await coordinator.TryAcquireTransferMutationPermitAsync(
            Mutation(ensure, binding, read, BaiduAutomationAction.ClickConfirmTransfer)));

        var prepare = ensure with { Action = AdapterWriteAction.PrepareTransfer };
        Assert.True(await coordinator.TryValidateBindingAsync(prepare, binding));
        foreach (var action in Enum.GetValues<BaiduAutomationAction>().Where(value =>
                     value is not BaiduAutomationAction.Unspecified and not BaiduAutomationAction.OpenOfficialUri))
            Assert.Null(await coordinator.TryAcquireTransferMutationPermitAsync(
                Mutation(prepare, binding, read, action)));

        var submit = ensure with { Action = AdapterWriteAction.SubmitTransfer };
        Assert.True(await coordinator.TryValidateBindingAsync(submit, binding));
        foreach (var action in new[]
        {
            BaiduAutomationAction.SetExtractionCode,
            BaiduAutomationAction.SubmitExtraction,
            BaiduAutomationAction.SelectExactItems,
            BaiduAutomationAction.OpenDestinationPicker,
            BaiduAutomationAction.EnterDirectory,
            BaiduAutomationAction.ClickConfirmTransfer,
        })
        {
            var permit = await coordinator.TryAcquireTransferMutationPermitAsync(
                Mutation(submit, binding, read, action));
            Assert.NotNull(permit);
            await permit.DisposeAsync();
        }
        Assert.Null(await coordinator.TryAcquireTransferMutationPermitAsync(
            Mutation(submit, binding, read, BaiduAutomationAction.CreateDirectory)));

        var store = new MemoryCapabilityStore();
        store.Records.Add(new(
            ensure.Capability.AdapterKind, ensure.Capability.ProtocolVersion,
            ensure.Capability.SelectorVersion, ensure.Capability.BuildVersion, Now, Hash));
        var formal = Coordinator(false, [], store: store);
        var recovery = ensure with { OperationId = Guid.NewGuid(), IsRecovery = true };
        var recoveryBinding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await formal.TryCreateBindingAsync(recovery));
        Assert.Null(await formal.TryAcquireTransferMutationPermitAsync(
            Mutation(recovery, recoveryBinding, read, BaiduAutomationAction.CreateDirectory)));
    }

    [Fact]
    public async Task TransferBindingRejectsExpiredTemporaryRecoveryAndAllowsExactFormalRecovery()
    {
        var clock = new MutableTimeProvider(Now);
        var temporary = Coordinator(true, ["--webview-acceptance"], clock);
        var ensure = Request() with { Action = AdapterWriteAction.EnsureDirectory };
        Assert.True(temporary.TryCreateLease(ensure, Confirmed()));
        var expired = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await temporary.TryCreateBindingAsync(ensure));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(await temporary.TryValidateBindingAsync(
            ensure with { Action = AdapterWriteAction.PrepareTransfer }, expired));
        Assert.Null(await temporary.TryCreateBindingAsync(
            ensure with { OperationId = Guid.NewGuid(), IsRecovery = true }));

        var store = new MemoryCapabilityStore();
        store.Records.Add(new(
            ensure.Capability.AdapterKind,
            ensure.Capability.ProtocolVersion,
            ensure.Capability.SelectorVersion,
            ensure.Capability.BuildVersion,
            Now,
            Hash));
        var formal = Coordinator(false, [], store: store);
        foreach (var isRecovery in new[] { false, true })
        {
            var operation = ensure with { OperationId = Guid.NewGuid(), IsRecovery = isRecovery };
            var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
                await formal.TryCreateBindingAsync(operation));
            Assert.Equal(AdapterWriteAuthorizationSource.ExactFormalRecord, binding.Source);
            Assert.True(await formal.TryValidateBindingAsync(
                operation with { Action = AdapterWriteAction.PrepareTransfer }, binding));
            Assert.True(await formal.TryValidateBindingAsync(
                operation with { Action = AdapterWriteAction.SubmitTransfer }, binding));
        }
    }

    [Fact]
    public async Task ReleasedFormalTransferBindingCannotBeReissuedForTheSameExactIdentity()
    {
        var store = new MemoryCapabilityStore();
        var ensure = Request() with { Action = AdapterWriteAction.EnsureDirectory };
        store.Records.Add(new(
            ensure.Capability.AdapterKind,
            ensure.Capability.ProtocolVersion,
            ensure.Capability.SelectorVersion,
            ensure.Capability.BuildVersion,
            Now,
            Hash));
        var coordinator = Coordinator(false, [], store: store);
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(ensure));

        await coordinator.ReleaseBindingAsync(binding);

        Assert.Null(await coordinator.TryCreateBindingAsync(ensure));
        Assert.NotNull(await coordinator.TryCreateBindingAsync(
            ensure with { OperationId = Guid.NewGuid() }));
        Assert.NotNull(await coordinator.TryCreateBindingAsync(
            ensure with { Generation = ensure.Generation + 1 }));
    }

    [Fact]
    public async Task TemporaryShareBindingCannotSwitchToFormalRecordAfterOriginalExpiry()
    {
        var clock = new MutableTimeProvider(Now);
        var store = new MemoryCapabilityStore();
        var coordinator = Coordinator(true, ["--webview-acceptance"], clock, store);
        var prepare = Request() with
        {
            Capability = new("BaiduShare", "protocol-v1", "selector-v1", "build-1"),
            Action = AdapterWriteAction.PrepareShare,
        };
        Assert.True(coordinator.TryCreateLease(prepare, Confirmed()));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(prepare));

        clock.Advance(TimeSpan.FromMinutes(6));
        store.Records.Add(new(
            prepare.Capability.AdapterKind,
            prepare.Capability.ProtocolVersion,
            prepare.Capability.SelectorVersion,
            prepare.Capability.BuildVersion,
            clock.GetUtcNow(),
            Hash));

        Assert.False(await coordinator.TryValidateBindingAsync(
            prepare with { Action = AdapterWriteAction.SubmitShare }, binding));
    }

    [Fact]
    public async Task ExactFormalShareBindingAllowsOrdinaryAndRecoveryWithoutLeaseConsumption()
    {
        var store = new MemoryCapabilityStore();
        var prepare = Request() with
        {
            Capability = new("BaiduShare", "protocol-v1", "selector-v1", "build-1"),
            Action = AdapterWriteAction.PrepareShare,
        };
        store.Records.Add(new(
            prepare.Capability.AdapterKind,
            prepare.Capability.ProtocolVersion,
            prepare.Capability.SelectorVersion,
            prepare.Capability.BuildVersion,
            Now,
            Hash));
        var coordinator = Coordinator(false, [], store: store);

        foreach (var isRecovery in new[] { false, true })
        {
            var request = prepare with
            {
                OperationId = Guid.NewGuid(),
                IsRecovery = isRecovery,
            };
            var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
                await coordinator.TryCreateBindingAsync(request));
            Assert.Equal(AdapterWriteAuthorizationSource.ExactFormalRecord, binding.Source);
            Assert.Equal(Now + AcceptanceWriteLeaseCoordinator.FormalEvidenceLifetime, binding.ExpiresAtUtc);
            Assert.True(await coordinator.TryValidateBindingAsync(
                request with { Action = AdapterWriteAction.SubmitShare }, binding));
        }
    }

    [Fact]
    public async Task TemporaryShareBindingRejectsRecoveryAndFormalBindingRejectsChangedRecord()
    {
        var temporary = Coordinator(true, ["--webview-acceptance"]);
        var recoveryPrepare = Request() with
        {
            Capability = new("BaiduShare", "protocol-v1", "selector-v1", "build-1"),
            Action = AdapterWriteAction.PrepareShare,
            IsRecovery = true,
        };
        Assert.True(temporary.TryCreateLease(recoveryPrepare, Confirmed()));
        Assert.Null(await temporary.TryCreateBindingAsync(recoveryPrepare));

        var store = new MemoryCapabilityStore();
        var ordinary = recoveryPrepare with { IsRecovery = false };
        store.Records.Add(new(
            ordinary.Capability.AdapterKind,
            ordinary.Capability.ProtocolVersion,
            ordinary.Capability.SelectorVersion,
            ordinary.Capability.BuildVersion,
            Now,
            Hash));
        var formal = Coordinator(false, [], store: store);
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await formal.TryCreateBindingAsync(ordinary));
        store.Records.Clear();
        store.Records.Add(new(
            ordinary.Capability.AdapterKind,
            ordinary.Capability.ProtocolVersion,
            ordinary.Capability.SelectorVersion,
            ordinary.Capability.BuildVersion,
            Now.AddMinutes(1),
            HashB));

        Assert.False(await formal.TryValidateBindingAsync(
            ordinary with { Action = AdapterWriteAction.SubmitShare }, binding));
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
        await coordinator.RecordRemoteResultAsync(request, Hash);
        await coordinator.RecordRecoveryAsync(request with { IsRecovery = true }, Hash);
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

        await coordinator.RecordRemoteResultAsync(request, Hash);
        await coordinator.RecordRecoveryAsync(request, Hash);
        await coordinator.RecordRemoteResultAsync(request with { IsRecovery = true }, HashB);
        await coordinator.RecordRecoveryAsync(request with { IsRecovery = true }, HashB);
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
        await coordinator.RecordRemoteResultAsync(request, Hash);
        await coordinator.RecordRecoveryAsync(request with { IsRecovery = true }, Hash);

        await coordinator.OnLocalResultExportCompletedAsync(
            ExportEvidence(request, operationId: Guid.NewGuid()));

        Assert.Equal(0, presenter.EnableCount);
        Assert.False(await coordinator.AcceptVersionAsync(request, userConfirmed: true));
    }

    [Fact]
    public async Task WorkbookContentFingerprintMustEqualTheCapturedRemoteFingerprint()
    {
        var presenter = new RecordingPresenter();
        var coordinator = Coordinator(true, ["--webview-acceptance"], presenter: presenter);
        var request = Request();
        Assert.True(coordinator.TryCreateLease(request, Confirmed()));
        Assert.True(await coordinator.TryAuthorizeAsync(request));
        await coordinator.RecordRemoteResultAsync(request, Hash);
        await coordinator.RecordRecoveryAsync(request with { IsRecovery = true }, Hash);

        await coordinator.OnLocalResultExportCompletedAsync(
            ExportEvidence(request, contentFingerprint: HashB));

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
        await coordinator.RecordRemoteResultAsync(request, Hash);
        await coordinator.RecordRecoveryAsync(request with { IsRecovery = true }, Hash);

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

    private static BaiduCandidateDomMutationRequest Mutation(
        AdapterWriteAuthorizationRequest authorization,
        AdapterWriteAuthorizationBinding binding,
        BaiduCandidateDomReadRequest read,
        BaiduAutomationAction action) =>
        new(authorization, binding, action, read,
            action switch
            {
                BaiduAutomationAction.SetExtractionCode => "1111",
                BaiduAutomationAction.CreateDirectory => "目录",
                _ => null,
            });
    private static LocalResultExportCompletedEvidence ExportEvidence(
        AdapterWriteAuthorizationRequest request,
        Guid? operationId = null,
        string? contentFingerprint = null) => new(
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
            contentFingerprint ?? Hash)],
        Hash,
        Hash);
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

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
