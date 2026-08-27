using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Accounts;

public sealed class ManagedBrowserAccountRemovalTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InstallationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ProfileId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OwnershipId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ManifestId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTimeOffset RemovedAt = new(2026, 8, 27, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreparationRefusesRemovalWhenExactManagedSessionCannotBeStopped()
    {
        var store = new RecordingRemovalStore();
        var lifecycle = new RecordingLifecycle(Snapshot(), ManagedBrowserStopStatus.Blocked);
        var profiles = new RecordingProfileStore(Plan());
        var coordinator = Create(store, lifecycle, profiles);

        var result = await coordinator.ApplyAsync(Request(), default);

        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, result.Status);
        Assert.Equal(SessionId, Assert.Single(lifecycle.StopRequests).Session.SessionId);
        Assert.Equal(InstallationId, lifecycle.StopRequests[0].Session.InstallationId);
        Assert.Equal(BrowserFamily.Chrome, lifecycle.StopRequests[0].Session.BrowserFamily);
        Assert.Equal(ProfileId.ToString("N"), lifecycle.StopRequests[0].Session.ProfileId);
        Assert.Equal(OwnershipId.ToString("N"), lifecycle.StopRequests[0].Session.ProfileOwnershipId);
        Assert.Empty(store.ManagedPrepareRequests);
        Assert.Empty(profiles.PrepareRequests);
    }

    [Fact]
    public async Task InFlightWorkRefusalKeepsPreparedManagedSnapshotOutOfDatabase()
    {
        var store = new RecordingRemovalStore
        {
            ManagedPrepareResult = new(
                AccountRemovalPreparationStatus.ActiveWorkBlocked,
                AccountId,
                false,
                "blocked"),
        };
        var lifecycle = new RecordingLifecycle(Snapshot(), ManagedBrowserStopStatus.Stopped);
        var profiles = new RecordingProfileStore(Plan());
        var coordinator = Create(store, lifecycle, profiles);

        var result = await coordinator.ApplyAsync(Request(), default);

        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, result.Status);
        Assert.Single(store.ManagedPrepareRequests);
        Assert.Equal(0, store.CompletionCalls);
    }

    [Fact]
    public async Task PreparationSnapshotsExactManagedIdentityAndDeterministicQuarantineBeforeTombstoning()
    {
        var store = new RecordingRemovalStore
        {
            ManagedPrepareResult = new(
                AccountRemovalPreparationStatus.Prepared,
                AccountId,
                true,
                "prepared",
                Plan()),
        };
        var lifecycle = new RecordingLifecycle(Snapshot(), ManagedBrowserStopStatus.Stopped);
        var profiles = new RecordingProfileStore(Plan());
        var coordinator = Create(store, lifecycle, profiles);

        _ = await coordinator.ApplyAsync(Request(), default);

        var captured = Assert.Single(store.ManagedPrepareRequests).Cleanup;
        Assert.NotNull(captured);
        Assert.Equal(SessionId, captured.Session.SessionId);
        Assert.Equal(InstallationId, captured.Session.InstallationId);
        Assert.Equal(AccountId, captured.Session.AccountId);
        Assert.Equal(BrowserFamily.Chrome, captured.Session.BrowserFamily);
        Assert.Equal(ProfileId.ToString("N"), captured.Session.ProfileId);
        Assert.Equal(OwnershipId.ToString("N"), captured.Session.ProfileOwnershipId);
        Assert.Equal(7, captured.InstallationRevision);
        Assert.Equal(11, captured.SessionRevision);
        Assert.Equal(@"C:\safe\managed-browser-profiles\chrome\11111111111111111111111111111111", captured.SourceProfilePath);
        Assert.Equal(@"C:\safe\managed-browser-quarantine\66666666666666666666666666666666", captured.QuarantinePath);
        Assert.Equal(ManifestId, captured.ManifestId);
        Assert.Equal(0, captured.ManifestRevision);
        Assert.True(profiles.PreparedBeforeDatabaseCall);
    }

    [Fact]
    public async Task CommittedMoveIntentIsMovedAndCasCompletedBeforeReturningCleanupPending()
    {
        var store = new RecordingRemovalStore
        {
            ManagedPrepareResult = new(
                AccountRemovalPreparationStatus.Prepared,
                AccountId,
                true,
                "prepared",
                Plan()),
        };
        var lifecycle = new RecordingLifecycle(Snapshot(), ManagedBrowserStopStatus.Stopped);
        var profiles = new RecordingProfileStore(Plan());
        var state = new RecordingManagedStateStore(
            Snapshot(), Plan(), ManagedProfileCleanupState.QuarantineMoveIntent, 1);
        var coordinator = new AccountRemovalCoordinator(
            store,
            CreateManagedCleanupService(profiles, state),
            lifecycle,
            profiles);

        var result = await coordinator.ApplyAsync(Request(), default);

        Assert.Equal(AccountExitStatus.CleanupPending, result.Status);
        Assert.Equal(1, profiles.MoveCalls);
        Assert.Equal(0, store.CompletionCalls);
        Assert.Equal(ManagedProfileCleanupState.Quarantined, Assert.Single(state.Transitions).TargetState);
    }

    [Theory]
    [InlineData(ManagedBrowserProfileLocationStatus.SourceOnly, true)]
    [InlineData(ManagedBrowserProfileLocationStatus.QuarantineOnly, false)]
    public async Task RestartRecoveryMovesOrFinishesCasFromDurableIntent(
        ManagedBrowserProfileLocationStatus location,
        bool expectsMove)
    {
        var profiles = new RecordingProfileStore(Plan()) { Location = location };
        var state = new RecordingManagedStateStore(Snapshot(), Plan(), ManagedProfileCleanupState.QuarantineMoveIntent, 1);
        var service = CreateManagedCleanupService(profiles, state);

        var result = await service.RecoverManagedProfileAsync(ManifestId, default);

        Assert.Equal(ManagedBrowserProfileCleanupStatus.Quarantined, result.Status);
        Assert.Equal(expectsMove ? 1 : 0, profiles.MoveCalls);
        var transition = Assert.Single(state.Transitions);
        Assert.Equal(ManagedProfileCleanupState.QuarantineMoveIntent, transition.ExpectedState);
        Assert.Equal(ManagedProfileCleanupState.Quarantined, transition.TargetState);
        Assert.Equal(1, transition.ExpectedRevision);
    }

    [Theory]
    [InlineData(ManagedBrowserProfileLocationStatus.BothPresent)]
    [InlineData(ManagedBrowserProfileLocationStatus.NeitherPresent)]
    [InlineData(ManagedBrowserProfileLocationStatus.IsolationRequired)]
    public async Task AmbiguousRestartStateBecomesCleanupPending(
        ManagedBrowserProfileLocationStatus location)
    {
        var profiles = new RecordingProfileStore(Plan()) { Location = location };
        var state = new RecordingManagedStateStore(Snapshot(), Plan(), ManagedProfileCleanupState.QuarantineMoveIntent, 1);
        var service = CreateManagedCleanupService(profiles, state);

        var result = await service.RecoverManagedProfileAsync(ManifestId, default);

        Assert.Equal(ManagedBrowserProfileCleanupStatus.CleanupPending, result.Status);
        Assert.Equal(ManagedProfileCleanupState.CleanupPending, Assert.Single(state.Transitions).TargetState);
        Assert.Equal(0, profiles.MoveCalls);
    }

    [Fact]
    public async Task FailedMoveStaysCleanupPendingWithoutCompletingCas()
    {
        var profiles = new RecordingProfileStore(Plan())
        {
            Location = ManagedBrowserProfileLocationStatus.SourceOnly,
            MoveStatus = ManagedBrowserProfileMoveStatus.Conflict,
        };
        var state = new RecordingManagedStateStore(Snapshot(), Plan(), ManagedProfileCleanupState.QuarantineMoveIntent, 1);
        var service = CreateManagedCleanupService(profiles, state);

        var result = await service.RecoverManagedProfileAsync(ManifestId, default);

        Assert.Equal(ManagedBrowserProfileCleanupStatus.CleanupPending, result.Status);
        Assert.Equal(ManagedProfileCleanupState.CleanupPending, Assert.Single(state.Transitions).TargetState);
    }

    [Fact]
    public async Task OnlyExactConfirmationCanEnterDeleteIntentAndRepeatedConfirmationDoesNotDeleteTwice()
    {
        var profiles = new RecordingProfileStore(Plan()) { Location = ManagedBrowserProfileLocationStatus.QuarantineOnly };
        var state = new RecordingManagedStateStore(Snapshot(), Plan(), ManagedProfileCleanupState.Quarantined, 2);
        var service = CreateManagedCleanupService(profiles, state);
        var confirmation = await service.CreateManagedProfileDeletionConfirmationAsync(ManifestId, default);
        var wrong = new ManagedBrowserProfileDeletionConfirmation(
            confirmation!.ManifestId,
            confirmation.ExpectedRevision,
            confirmation.ConfirmationToken + "wrong");

        var rejected = await service.DeleteConfirmedManagedProfileAsync(wrong, default);
        var deleted = await service.DeleteConfirmedManagedProfileAsync(confirmation, default);
        var repeated = await service.DeleteConfirmedManagedProfileAsync(confirmation, default);

        Assert.Equal(ManagedBrowserProfileCleanupStatus.Conflict, rejected.Status);
        Assert.Equal(ManagedBrowserProfileCleanupStatus.Deleted, deleted.Status);
        Assert.Equal(ManagedBrowserProfileCleanupStatus.Conflict, repeated.Status);
        Assert.Equal(1, profiles.DeleteCalls);
        Assert.Collection(state.Transitions,
            request => Assert.Equal(ManagedProfileCleanupState.DeleteIntent, request.TargetState),
            request => Assert.Equal(ManagedProfileCleanupState.Deleted, request.TargetState));
    }

    [Fact]
    public async Task FailedConfirmedDeleteRemainsCleanupPendingAndAccountFinalizationIsNotReversed()
    {
        var profiles = new RecordingProfileStore(Plan())
        {
            Location = ManagedBrowserProfileLocationStatus.QuarantineOnly,
            DeleteStatus = ManagedBrowserProfileDeleteStatus.IsolationRequired,
        };
        var state = new RecordingManagedStateStore(Snapshot(), Plan(), ManagedProfileCleanupState.Quarantined, 2);
        var service = CreateManagedCleanupService(profiles, state);
        var confirmation = await service.CreateManagedProfileDeletionConfirmationAsync(ManifestId, default);

        var result = await service.DeleteConfirmedManagedProfileAsync(confirmation!, default);

        Assert.Equal(ManagedBrowserProfileCleanupStatus.CleanupPending, result.Status);
        Assert.Equal(ManagedProfileCleanupState.CleanupPending, state.Transitions[^1].TargetState);
    }

    [Fact]
    public async Task ConfirmationForAnotherAccountCannotDeleteTheQuarantinedProfile()
    {
        var profiles = new RecordingProfileStore(Plan())
        {
            Location = ManagedBrowserProfileLocationStatus.QuarantineOnly,
        };
        var state = new RecordingManagedStateStore(
            Snapshot(), Plan(), ManagedProfileCleanupState.Quarantined, 2);
        var service = CreateManagedCleanupService(profiles, state);
        var confirmation = await service.CreateManagedProfileDeletionConfirmationAsync(ManifestId, default);

        var result = await service.DeleteConfirmedManagedProfileAsync(
            Guid.NewGuid(), confirmation!, default);

        Assert.Equal(ManagedBrowserProfileCleanupStatus.Conflict, result.Status);
        Assert.Equal(0, profiles.DeleteCalls);
        Assert.Empty(state.Transitions);
    }

    [Fact]
    public async Task FamilySwitchRejectsStartedOrRecoveringWorkBeforeCreatingIdentity()
    {
        var state = new RecordingFamilySwitchStateStore(Snapshot());
        var coordinator = new ManagedBrowserFamilySwitchCoordinator(
            state,
            new FixedFamilySwitchFence(false));

        var result = await coordinator.SwitchAsync(SwitchObservation(), default);

        Assert.Equal(ManagedBrowserFamilySwitchStatus.ActiveWorkBlocked, result.Status);
        Assert.Empty(state.Calls);
    }

    [Fact]
    public async Task FamilySwitchRequiresFreshMatchingStableUidBeforeAnyStateWrite()
    {
        var state = new RecordingFamilySwitchStateStore(Snapshot());
        var coordinator = new ManagedBrowserFamilySwitchCoordinator(
            state,
            new FixedFamilySwitchFence(true));
        var observation = SwitchObservation() with { ObservedStableBaiduUserId = "different-user" };

        var result = await coordinator.SwitchAsync(observation, default);

        Assert.Equal(ManagedBrowserFamilySwitchStatus.IdentityMismatch, result.Status);
        Assert.Empty(state.Calls);
    }

    [Fact]
    public async Task FamilySwitchRejectsStaleExpectedProfileTupleEvenWhenRevisionMatches()
    {
        var stored = Snapshot();
        var staleSession = new ManagedBrowserSessionReference(
            stored.SessionId,
            stored.InstallationId,
            stored.AccountId,
            stored.Session.StableBaiduUserId,
            stored.BrowserFamily,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            stored.ProfileOwnershipId);
        var stale = new ManagedBrowserLifecycleSession(
            staleSession,
            stored.InstallationRevision,
            stored.SessionRevision,
            stored.CanonicalProfilePath);
        var observation = SwitchObservation() with { ExpectedCurrent = stale };
        var state = new RecordingFamilySwitchStateStore(stored);
        var coordinator = new ManagedBrowserFamilySwitchCoordinator(
            state,
            new FixedFamilySwitchFence(true));

        var result = await coordinator.SwitchAsync(observation, default);

        Assert.Equal(ManagedBrowserFamilySwitchStatus.Conflict, result.Status);
        Assert.Equal(new[] { "load-installation", "load-session" }, state.Calls);
    }

    [Fact]
    public async Task FamilySwitchCreatesFreshFamilyProfileAndRetiresOldReferenceWithoutDeletingIt()
    {
        var original = Snapshot();
        var state = new RecordingFamilySwitchStateStore(original);
        var coordinator = new ManagedBrowserFamilySwitchCoordinator(
            state,
            new FixedFamilySwitchFence(true));
        var observation = SwitchObservation();

        var result = await coordinator.SwitchAsync(observation, default);

        Assert.Equal(ManagedBrowserFamilySwitchStatus.Switched, result.Status);
        Assert.Equal(new[] { "load-installation", "load-session", "switch-family" }, state.Calls);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Retired, state.OldSessionState);
        Assert.Equal(original.ProfileId, state.OldProfileId);
        Assert.Equal(BrowserFamily.Edge, state.ActiveSession!.Identity.BrowserFamily);
        Assert.NotEqual(original.ProfileId, state.ActiveSession.Identity.ProfileId);
        Assert.NotEqual(original.ProfileOwnershipId, state.ActiveSession.Identity.ProfileOwnershipId);
        Assert.Equal(BrowserFamily.Chrome, original.BrowserFamily);
        Assert.Equal(ProfileId.ToString("N"), original.ProfileId);
    }

    [Fact]
    public async Task FamilySwitchMapsAtomicStoreFenceWithoutPerformingPartialWrites()
    {
        var state = new RecordingFamilySwitchStateStore(Snapshot())
        {
            SwitchStatus = ManagedBrowserFamilySwitchTransitionStatus.ActiveWorkBlocked,
        };
        var coordinator = new ManagedBrowserFamilySwitchCoordinator(
            state,
            new FixedFamilySwitchFence(true));

        var result = await coordinator.SwitchAsync(SwitchObservation(), default);

        Assert.Equal(ManagedBrowserFamilySwitchStatus.ActiveWorkBlocked, result.Status);
        Assert.Equal(new[] { "load-installation", "load-session", "switch-family" }, state.Calls);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Active, state.OldSessionState);
        Assert.Null(state.ActiveSession);
    }

    private static RemoveAccountService CreateManagedCleanupService(
        RecordingProfileStore profiles,
        RecordingManagedStateStore state) =>
        new(new NoOpWebViewProfileStore(), new NoOpProfilePathPolicy(), state, profiles);

    private static AccountRemovalCoordinator Create(
        RecordingRemovalStore store,
        RecordingLifecycle lifecycle,
        RecordingProfileStore profiles) =>
        new(
            store,
            new RemoveAccountService(new NoOpWebViewProfileStore(), new NoOpProfilePathPolicy()),
            lifecycle,
            profiles);

    private static AccountExitRequest Request() =>
        new(AccountId, AccountExitHistoryMode.KeepHistory, "confirmation", RemovedAt);

    private static ManagedBrowserLifecycleSession Snapshot() =>
        new(
            new ManagedBrowserSessionReference(
                SessionId,
                InstallationId,
                AccountId,
                "stable-baidu-uid",
                BrowserFamily.Chrome,
                ProfileId.ToString("N"),
                OwnershipId.ToString("N")),
            installationRevision: 7,
            sessionRevision: 11,
            @"C:\safe\managed-browser-profiles\chrome\11111111111111111111111111111111");

    private static ManagedBrowserProfileCleanupPreparation Plan() =>
        new(
            Snapshot(),
            ManifestId,
            manifestRevision: 0,
            @"C:\safe\managed-browser-quarantine\66666666666666666666666666666666");

    private static ManagedBrowserFamilySwitchObservation SwitchObservation()
    {
        var installationId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var directory = @"C:\safe\edge";
        return new ManagedBrowserFamilySwitchObservation(
            Snapshot(),
            new BrowserInstallationIdentity(
                installationId,
                BrowserFamily.Edge,
                directory,
                Path.Combine(directory, "msedge.exe"),
                "Microsoft Corporation",
                "Microsoft Edge",
                "edge-file-id"),
            new ManagedBrowserSessionReference(
                Guid.Parse("88888888-8888-8888-8888-888888888888"),
                installationId,
                AccountId,
                "stable-baidu-uid",
                BrowserFamily.Edge,
                "99999999999999999999999999999999",
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            "stable-baidu-uid");
    }

    private sealed class RecordingLifecycle(
        ManagedBrowserLifecycleSession? session,
        ManagedBrowserStopStatus stopStatus) : IManagedBrowserLifecycle
    {
        public List<ManagedBrowserLifecycleSession> StopRequests { get; } = [];

        public Task<ManagedBrowserLifecycleSession?> LoadActiveSessionAsync(
            Guid accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult(accountId == AccountId ? session : null);

        public Task<ManagedBrowserStopStatus> StopAsync(
            ManagedBrowserLifecycleSession exactSession,
            CancellationToken cancellationToken)
        {
            StopRequests.Add(exactSession);
            return Task.FromResult(stopStatus);
        }
    }

    private sealed class RecordingProfileStore(ManagedBrowserProfileCleanupPreparation plan) :
        IManagedBrowserProfileStore
    {
        public List<ManagedBrowserLifecycleSession> PrepareRequests { get; } = [];
        public bool PreparedBeforeDatabaseCall { get; set; }
        public ManagedBrowserProfileLocationStatus Location { get; set; } = ManagedBrowserProfileLocationStatus.SourceOnly;
        public ManagedBrowserProfileDeleteStatus DeleteStatus { get; set; } = ManagedBrowserProfileDeleteStatus.Deleted;
        public ManagedBrowserProfileMoveStatus MoveStatus { get; set; } = ManagedBrowserProfileMoveStatus.Moved;
        public int MoveCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public ManagedBrowserProfileCleanupPreparation PrepareRemoval(
            ManagedBrowserLifecycleSession session)
        {
            PrepareRequests.Add(session);
            PreparedBeforeDatabaseCall = true;
            return plan;
        }

        public Task<ManagedBrowserProfileLocationStatus> ObserveAsync(ManagedBrowserProfileCleanupPreparation preparation, CancellationToken cancellationToken) =>
            Task.FromResult(Location);

        public Task<ManagedBrowserProfileMoveStatus> MoveToQuarantineAsync(ManagedBrowserProfileCleanupPreparation preparation, CancellationToken cancellationToken)
        {
            MoveCalls++;
            if (MoveStatus == ManagedBrowserProfileMoveStatus.Moved)
                Location = ManagedBrowserProfileLocationStatus.QuarantineOnly;
            return Task.FromResult(MoveStatus);
        }

        public Task<ManagedBrowserProfileDeleteStatus> DeleteQuarantinedAsync(ManagedBrowserProfileCleanupPreparation preparation, string confirmationTokenHash, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return Task.FromResult(DeleteStatus);
        }
    }

    private sealed class RecordingManagedStateStore : IManagedBrowserStateStore
    {
        private readonly ManagedBrowserLifecycleSession session;
        private StoredManagedProfileCleanupManifest? manifest;

        public RecordingManagedStateStore(
            ManagedBrowserLifecycleSession session,
            ManagedBrowserProfileCleanupPreparation preparation,
            ManagedProfileCleanupState state,
            long revision)
        {
            this.session = session;
            manifest = new StoredManagedProfileCleanupManifest(
                new ManagedProfileCleanupManifest(
                    preparation.ManifestId,
                    session.AccountId,
                    session.SessionId,
                    preparation.SourceProfilePath,
                    preparation.QuarantinePath,
                    session.ProfileOwnershipId,
                    null),
                state,
                revision);
        }

        public List<ManagedProfileCleanupTransitionRequest> Transitions { get; } = [];

        public Task<StoredManagedProfileCleanupManifest?> LoadCleanupManifestAsync(Guid manifestId, CancellationToken cancellationToken) =>
            Task.FromResult(manifest?.Manifest.ManifestId == manifestId ? manifest : null);

        public Task<StoredManagedProfileCleanupManifest?> LoadLatestCleanupManifestAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult(manifest?.Manifest.AccountId == accountId ? manifest : null);

        public Task<StoredManagedBrowserSession?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<StoredManagedBrowserSession?>(session.SessionId == sessionId
                ? new StoredManagedBrowserSession(session.Session, ManagedBrowserSessionLifecycleState.QuarantinePending, session.SessionRevision)
                : null);

        public Task<StoredBrowserInstallation?> LoadInstallationAsync(Guid installationId, CancellationToken cancellationToken) =>
            Task.FromResult<StoredBrowserInstallation?>(session.InstallationId == installationId
                ? new StoredBrowserInstallation(
                    new BrowserInstallationIdentity(
                        session.InstallationId,
                        session.BrowserFamily,
                        @"C:\safe",
                        @"C:\safe\chrome.exe",
                        "Google LLC",
                        "Google Chrome",
                        "sha256"),
                    BrowserInstallationLifecycleState.Retired,
                    session.InstallationRevision)
                : null);

        public Task<ManagedBrowserTransitionResult> TryTransitionCleanupManifestAsync(ManagedProfileCleanupTransitionRequest request, CancellationToken cancellationToken)
        {
            Transitions.Add(request);
            if (manifest is null || manifest.State != request.ExpectedState || manifest.Revision != request.ExpectedRevision)
                return Task.FromResult(ManagedBrowserTransitionResult.Conflict(manifest?.Revision ?? 0));
            manifest = new StoredManagedProfileCleanupManifest(
                new ManagedProfileCleanupManifest(
                    manifest.Manifest.ManifestId,
                    manifest.Manifest.AccountId,
                    manifest.Manifest.SessionId,
                    manifest.Manifest.SourceProfilePath,
                    manifest.Manifest.QuarantinePath,
                    manifest.Manifest.ExpectedOwnershipId,
                    request.TargetState == ManagedProfileCleanupState.DeleteIntent
                        ? request.ConfirmationTokenHash
                        : manifest.Manifest.ConfirmationTokenHash),
                request.TargetState,
                request.ExpectedRevision + 1);
            return Task.FromResult(ManagedBrowserTransitionResult.Applied(request.ExpectedRevision + 1));
        }

        public Task<StoredBrowserInstallation> CreateInstallationAsync(BrowserInstallationIdentity installation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredBrowserInstallation?> LoadActiveInstallationAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>> TryReplaceActiveInstallationAsync(ManagedBrowserInstallationActivationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedBrowserTransitionResult> TryRetireInstallationAsync(ManagedBrowserTransitionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredManagedBrowserSession> CreateSessionAsync(ManagedBrowserSessionReference session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredManagedBrowserSession?> LoadActiveSessionAsync(Guid accountId, BrowserFamily browserFamily, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>> TryReplaceActiveSessionAsync(ManagedBrowserSessionActivationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedBrowserTransitionResult> TryRetireSessionAsync(ManagedBrowserTransitionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedBrowserTransitionResult> TryClaimRetiredIdentityForCleanupAsync(ManagedBrowserCleanupClaimRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredManagedProfileCleanupManifest> CreateCleanupManifestAsync(ManagedProfileCleanupManifest manifest, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddRetentionAsync(ManagedBrowserRetentionReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseRetentionAsync(ManagedBrowserRetentionReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsRetirementEligibleAsync(Guid installationId, Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedFamilySwitchFence(bool allowed) : IManagedBrowserFamilySwitchFence
    {
        public Task<bool> CanSwitchAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult(allowed);
    }

    private sealed class RecordingFamilySwitchStateStore : IManagedBrowserStateStore
    {
        private readonly ManagedBrowserLifecycleSession original;
        private StoredBrowserInstallation? createdInstallation;
        private StoredManagedBrowserSession? createdSession;

        public RecordingFamilySwitchStateStore(ManagedBrowserLifecycleSession original) => this.original = original;
        public List<string> Calls { get; } = [];
        public ManagedBrowserSessionLifecycleState OldSessionState { get; private set; } = ManagedBrowserSessionLifecycleState.Active;
        public string OldProfileId => original.ProfileId;
        public StoredManagedBrowserSession? ActiveSession { get; private set; }
        public ManagedBrowserFamilySwitchTransitionStatus SwitchStatus { get; set; } =
            ManagedBrowserFamilySwitchTransitionStatus.Applied;

        public Task<StoredBrowserInstallation?> LoadInstallationAsync(Guid installationId, CancellationToken cancellationToken)
        {
            Calls.Add("load-installation");
            return Task.FromResult<StoredBrowserInstallation?>(new StoredBrowserInstallation(
                ChromeInstallation(original.InstallationId), BrowserInstallationLifecycleState.Active,
                original.InstallationRevision));
        }

        public Task<StoredManagedBrowserSession?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            Calls.Add("load-session");
            return Task.FromResult<StoredManagedBrowserSession?>(new StoredManagedBrowserSession(
                original.Session, OldSessionState, original.SessionRevision));
        }

        public Task<StoredBrowserInstallation> CreateInstallationAsync(BrowserInstallationIdentity installation, CancellationToken cancellationToken)
        {
            Calls.Add("create-installation");
            createdInstallation = new(installation, BrowserInstallationLifecycleState.Retired, 0);
            return Task.FromResult(createdInstallation);
        }

        public Task<StoredManagedBrowserSession> CreateSessionAsync(ManagedBrowserSessionReference session, CancellationToken cancellationToken)
        {
            Calls.Add("create-session");
            createdSession = new(session, ManagedBrowserSessionLifecycleState.Retired, 0);
            return Task.FromResult(createdSession);
        }

        public Task<ManagedBrowserTransitionResult> TryRetireSessionAsync(ManagedBrowserTransitionRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("retire-session");
            OldSessionState = ManagedBrowserSessionLifecycleState.Retired;
            return Task.FromResult(ManagedBrowserTransitionResult.Applied(original.SessionRevision + 1));
        }

        public Task<ManagedBrowserFamilySwitchTransitionResult> TrySwitchFamilyAsync(
            ManagedBrowserFamilySwitchTransitionRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add("switch-family");
            if (SwitchStatus == ManagedBrowserFamilySwitchTransitionStatus.Applied)
            {
                OldSessionState = ManagedBrowserSessionLifecycleState.Retired;
                ActiveSession = new StoredManagedBrowserSession(
                    request.TargetSession, ManagedBrowserSessionLifecycleState.Active, 1);
            }
            return Task.FromResult(new ManagedBrowserFamilySwitchTransitionResult(SwitchStatus));
        }

        public Task<ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>> TryReplaceActiveInstallationAsync(ManagedBrowserInstallationActivationRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("activate-installation");
            var active = new StoredBrowserInstallation(createdInstallation!.Identity, BrowserInstallationLifecycleState.Active, 1);
            return Task.FromResult(ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.Applied(active));
        }

        public Task<ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>> TryReplaceActiveSessionAsync(ManagedBrowserSessionActivationRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("activate-session");
            ActiveSession = new StoredManagedBrowserSession(createdSession!.Identity, ManagedBrowserSessionLifecycleState.Active, 1);
            return Task.FromResult(ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>.Applied(ActiveSession));
        }

        private static BrowserInstallationIdentity ChromeInstallation(Guid id) => new(
            id, BrowserFamily.Chrome, @"C:\safe\chrome", @"C:\safe\chrome\chrome.exe",
            "Google LLC", "Google Chrome", "chrome-file-id");

        public Task<StoredBrowserInstallation?> LoadActiveInstallationAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedBrowserTransitionResult> TryRetireInstallationAsync(ManagedBrowserTransitionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredManagedBrowserSession?> LoadActiveSessionAsync(Guid accountId, BrowserFamily browserFamily, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedBrowserTransitionResult> TryClaimRetiredIdentityForCleanupAsync(ManagedBrowserCleanupClaimRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredManagedProfileCleanupManifest> CreateCleanupManifestAsync(ManagedProfileCleanupManifest manifest, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredManagedProfileCleanupManifest?> LoadCleanupManifestAsync(Guid manifestId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedBrowserTransitionResult> TryTransitionCleanupManifestAsync(ManagedProfileCleanupTransitionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddRetentionAsync(ManagedBrowserRetentionReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseRetentionAsync(ManagedBrowserRetentionReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsRetirementEligibleAsync(Guid installationId, Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingRemovalStore : IAccountRemovalStore
    {
        public List<(AccountExitRequest Request, ManagedBrowserProfileCleanupPreparation? Cleanup)> ManagedPrepareRequests { get; } = [];
        public int CompletionCalls { get; private set; }
        public AccountRemovalPreparation ManagedPrepareResult { get; set; } =
            new(AccountRemovalPreparationStatus.Prepared, AccountId, true, "prepared", Plan());

        public Task<AccountPage> GetAccountsPageAsync(int pageNumber, int pageSize, CancellationToken token) =>
            Task.FromResult(new AccountPage([], pageNumber, pageSize, 0));

        public Task<AccountExitPreview> PreviewAsync(
            Guid accountId,
            AccountExitHistoryMode mode,
            CancellationToken token) =>
            Task.FromResult(new AccountExitPreview(
                AccountExitStatus.Ready, "test", 0, 0, 0, "confirmation", "ready"));

        public Task<AccountRemovalPreparation> PrepareRemovalAsync(
            AccountExitRequest request,
            CancellationToken token) =>
            PrepareRemovalAsync(request, null, token);

        public Task<AccountRemovalPreparation> PrepareRemovalAsync(
            AccountExitRequest request,
            ManagedBrowserProfileCleanupPreparation? cleanup,
            CancellationToken token)
        {
            ManagedPrepareRequests.Add((request, cleanup));
            return Task.FromResult(ManagedPrepareResult);
        }

        public async Task CompletePreparedRemovalAsync(
            Guid accountId,
            DateTimeOffset removedAt,
            Func<Guid, string?, CancellationToken, Task> externalCleanup,
            CancellationToken token)
        {
            CompletionCalls++;
            await externalCleanup(accountId, null, token);
        }
    }

    private sealed class NoOpWebViewProfileStore : IWebViewProfileStore
    {
        public Task CloseAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(Guid accountId, string profilePath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RegisterOwnershipAsync(Guid accountId, string exactProfilePath, Guid ownershipToken, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<bool> CommitOwnedAsync(Guid accountId, string exactProfilePath, Guid ownershipToken, Func<CancellationToken, Task> commitAction, CancellationToken cancellationToken)
        {
            await commitAction(cancellationToken);
            return true;
        }
        public async Task<OwnedProfileDeletionStatus> DeleteOwnedAsync(Guid accountId, string exactProfilePath, Guid ownershipToken, Func<CancellationToken, Task<bool>> confirmNoActiveAccount, CancellationToken cancellationToken)
        {
            _ = await confirmNoActiveAccount(cancellationToken);
            return OwnedProfileDeletionStatus.Completed;
        }
    }

    private sealed class NoOpProfilePathPolicy : IAccountProfilePathPolicy
    {
        public string GetAccountProfilePath(Guid accountId) => @"C:\safe\legacy";
        public void EnsureSafeAccountPath(Guid accountId, string profilePath) { }
    }
}
