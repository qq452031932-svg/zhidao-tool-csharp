using System.Runtime.CompilerServices;
using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedChromiumTransferAdapterTests
{
    [Fact]
    public async Task FourStagesPreserveExactOperationSessionSourceDestinationAndPolicy()
    {
        var fixture = new Fixture();
        var adapter = fixture.Adapter();

        var directory = await adapter.EnsureDirectoryForOperationAsync(
            fixture.Context, fixture.Session, fixture.Path, CancellationToken.None);
        var prepared = await adapter.PrepareWholeShareForOperationAsync(
            fixture.Context, fixture.Session, fixture.Share, directory.Value!.Directory!,
            DuplicatePolicy.Skip, CancellationToken.None);
        var saved = await adapter.SaveWholeShareForOperationAsync(
            fixture.Context, fixture.Session, fixture.Share, directory.Value.Directory!,
            DuplicatePolicy.Skip, CancellationToken.None);

        Assert.True(prepared.IsSuccess);
        Assert.True(saved.IsSuccess);
        Assert.Equal("destination-exact-apk", saved.Value!.ExactRemoteItems!.Items.Single().ExactId);
        Assert.Equal("item.apk", saved.Value.ExactRemoteItems.Items.Single().DisplayedTopLevelName);
        Assert.Same(fixture.Context, fixture.Runtime.Context);
        Assert.Same(fixture.Session, fixture.Runtime.Session);
        Assert.Same(fixture.Share, fixture.Runtime.Share);
        Assert.Equal(fixture.Path, fixture.Runtime.Path);
        Assert.Equal(DuplicatePolicy.Skip, fixture.Runtime.Policy);
        Assert.Equal(["Ensure", "Prepare", "Save", "Dispose"], fixture.Runtime.Events);
    }

    [Fact]
    public async Task FinalSubmissionIsInvokedAtMostOnce()
    {
        var fixture = new Fixture();
        var adapter = fixture.Adapter();
        var directory = (await adapter.EnsureDirectoryForOperationAsync(
            fixture.Context, fixture.Session, fixture.Path, CancellationToken.None)).Value!.Directory!;
        await adapter.PrepareWholeShareForOperationAsync(
            fixture.Context, fixture.Session, fixture.Share, directory, DuplicatePolicy.Skip,
            CancellationToken.None);

        var first = await adapter.SaveWholeShareForOperationAsync(
            fixture.Context, fixture.Session, fixture.Share, directory, DuplicatePolicy.Skip,
            CancellationToken.None);
        var second = await adapter.SaveWholeShareForOperationAsync(
            fixture.Context, fixture.Session, fixture.Share, directory, DuplicatePolicy.Skip,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal(1, fixture.Runtime.SaveCalls);
    }

    [Fact]
    public async Task PendingIsRegisteredBeforeMappingAndRetainedConfirmationDoesNotReadAgain()
    {
        var fixture = new Fixture();
        var quarantine = new FakeQuarantine(new ManagedTransferQuarantineKey(
            ManagedTransferQuarantineStage.DirectoryPreparation, fixture.Context.OperationId,
            fixture.Context.AccountId, fixture.Context.Generation, fixture.Session.SessionId, 0));
        fixture.Runtime.EnsureResult = new(
            BaiduAdapterResult<TransferDirectoryPreparationOutcome>.Success(
                TransferDirectoryPreparationOutcome.NeedsAttention(
                    fixture.Path, BaiduErrorKind.UnknownResponse), SubmissionAcceptance.Unknown),
            new(quarantine.Key, registry => registry.TryRegister(quarantine)));
        var adapter = fixture.Adapter();

        var pending = await adapter.EnsureDirectoryForOperationAsync(
            fixture.Context, fixture.Session, fixture.Path, CancellationToken.None);
        var confirmed = await adapter.ConfirmExactTransferForOperationAsync(
            fixture.Context with { IsRecovery = true }, fixture.Session, fixture.Query,
            CancellationToken.None);

        Assert.True(pending.IsSuccess);
        Assert.True(fixture.Registry.IsAccountQuarantined(fixture.Context.AccountId));
        Assert.True(confirmed.IsSuccess);
        Assert.Equal(TransferConfirmationState.Ambiguous, confirmed.Value!.State);
        Assert.Equal(0, fixture.Runtime.ConfirmCalls);
        Assert.Equal(1, quarantine.Calls);
    }

    [Fact]
    public void ShareHandoffCanBeReadAcrossPhasesAndRemovedOnlyByExactOwner()
    {
        var accountId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var ownershipId = Guid.NewGuid();
        var session = new ManagedBrowserSessionReference(Guid.NewGuid(), installationId, accountId,
            "uid-1", BrowserFamily.Chrome, profileId.ToString("N"), ownershipId.ToString("N"));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var profile = ManagedBrowserProfileDescriptor.FromStoredSession(session, root);
        var installationDirectory = Path.Combine(root, "browser");
        var installation = new BrowserInstallationIdentity(installationId, BrowserFamily.Chrome,
            installationDirectory, Path.Combine(installationDirectory, "chrome.exe"), "Google LLC",
            "Google Chrome", "file-id");
        var browserId = Guid.NewGuid();
        var port = new DevToolsActivePort(9222, browserId, new Uri("http://127.0.0.1:9222/"),
            new Uri($"ws://127.0.0.1:9222/devtools/browser/{browserId:D}"));
        var handle = new ManagedChromiumSessionHandle(123, installation, profile, port, "target-1");
        var lifecycle = new ManagedBrowserLifecycleSession(session, 1, 1, profile.CanonicalSourcePath);
        var destination = new RemoteDirectory("directory-1", new RemotePath("/target"));
        var exactItems = new RemoteItemIdentitySet([
            new("exact-apk", "item.apk"),
            new("exact-assets", "assets")]);
        var key = new ManagedTransferShareHandoffKey(Guid.NewGuid(), accountId,
            exactItems.Fingerprint);
        var handoff = new ManagedTransferShareHandoff(key, Guid.NewGuid(), 1, session, lifecycle,
            handle, destination, exactItems);
        var equalButNotOwned = handoff with { };
        var registry = new ManagedTransferShareHandoffRegistry();

        Assert.True(registry.TryRegister(handoff));
        Assert.True(registry.TryGet(key, out var discovered));
        Assert.Same(handoff, discovered);
        Assert.True(registry.TryGet(key, out var prepared));
        Assert.Same(handoff, prepared);
        Assert.False(registry.TryRemove(key, equalButNotOwned));
        Assert.True(registry.TryGet(key, out _));
        Assert.True(registry.TryRemove(key, handoff));
        Assert.False(registry.TryGet(key, out _));
        Assert.False(registry.TryRemove(key, handoff));
    }

    [Fact]
    public async Task ExactIdentityDriftRejectsBeforeStartingHelperOrRequestingWritePermit()
    {
        var fixture = new Fixture();
        var helperStarts = 0;
        var helpers = new ManagedBrowserHelperClientFactory(_ =>
        {
            helperStarts++;
            throw new InvalidOperationException("A drifted identity must not start a helper.");
        });
        var writeGate = Uninitialized<AcceptanceWriteLeaseCoordinator>();
        var factory = new ManagedChromiumTransferRuntimeFactory(
            Uninitialized<ManagedBrowserLifecycleCoordinator>(),
            Uninitialized<ManagedChromiumProcessManager>(),
            helpers,
            (IAdapterWriteAuthorizationGate)writeGate,
            (IBaiduCandidateActionAuthorizationSource)writeGate,
            Uninitialized<SqliteWebAutomationAttemptStore>(),
            Uninitialized<TransferRecoveryAuthorizationSource>(),
            new ManagedTransferShareHandoffRegistry(),
            "1.0.0",
            exactBindingResolver: (_, _, _) =>
                Task.FromResult<ManagedChromiumExactRuntimeBinding?>(null));

        await using var runtime = await factory.BeginOperationAsync(
            fixture.Context, fixture.Session, CancellationToken.None);
        var result = await runtime.EnsureAsync(fixture.Path, CancellationToken.None);

        Assert.False(result.RequireResult().IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.RequireResult().ErrorKind);
        Assert.Equal(0, helperStarts);
    }

    [Fact]
    public async Task TerminalDirectoryFailureReleasesOperationRuntime()
    {
        var fixture = new Fixture();
        fixture.Runtime.EnsureResult = new(
            BaiduAdapterResult<TransferDirectoryPreparationOutcome>.Success(
                TransferDirectoryPreparationOutcome.NeedsAttention(
                    fixture.Path, BaiduErrorKind.NotValidated)));
        var adapter = fixture.Adapter();

        await adapter.EnsureDirectoryForOperationAsync(
            fixture.Context, fixture.Session, fixture.Path, CancellationToken.None);

        Assert.Equal(["Ensure", "Dispose"], fixture.Runtime.Events);
    }

    private static T Uninitialized<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private sealed class Fixture
    {
        internal ShareReference Share { get; } = new("share-1", "https://pan.baidu.com/s/source", "a1b2");
        internal RemotePath Path { get; } = new("/target");
        internal ManagedBrowserSessionReference Session { get; }
        internal TransferOperationAutomationContext Context { get; }
        internal FakeRuntime Runtime { get; }
        internal ManagedTransferQuarantineRegistry Registry { get; } = new();
        internal TransferConfirmationQuery Query { get; }

        internal Fixture()
        {
            var account = Guid.NewGuid();
            Session = new(Guid.NewGuid(), Guid.NewGuid(), account, "uid-1", BrowserFamily.Chrome,
                "profile-1", "ownership-1");
            Context = new(Guid.NewGuid(), Guid.NewGuid(), account, 1, Share,
                PersistedExecutionProfile: ExecutionProfileSnapshot.Managed(
                    Session.BrowserFamily, Session.ProfileId, Session.InstallationId, Session.SessionId));
            Runtime = new FakeRuntime();
            Query = new TransferConfirmationQuery("share-1", Path, "outer", "operation",
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        }

        internal ManagedChromiumTransferAdapter Adapter() =>
            new((context, session, _) =>
            {
                Runtime.Context = context;
                Runtime.Session = session;
                return Task.FromResult<IManagedChromiumTransferOperation>(Runtime);
            }, Registry);
    }

    private sealed class FakeRuntime : IManagedChromiumTransferOperation
    {
        internal TransferOperationAutomationContext? Context { get; set; }
        internal ManagedBrowserSessionReference? Session { get; set; }
        internal ShareReference? Share { get; private set; }
        internal RemotePath? Path { get; private set; }
        internal DuplicatePolicy? Policy { get; private set; }
        internal int SaveCalls { get; private set; }
        internal int ConfirmCalls { get; private set; }
        internal List<string> Events { get; } = [];
        internal ManagedChromiumTransferPhaseResult<TransferDirectoryPreparationOutcome>? EnsureResult { get; set; }

        public Task<ManagedChromiumTransferPhaseResult<TransferDirectoryPreparationOutcome>> EnsureAsync(
            RemotePath path, CancellationToken cancellationToken)
        {
            Events.Add("Ensure"); Path = path;
            var directory = new RemoteDirectory("directory-exact", path);
            return Task.FromResult(EnsureResult ?? new(
                BaiduAdapterResult<TransferDirectoryPreparationOutcome>.Success(
                    TransferDirectoryPreparationOutcome.Prepared(directory))));
        }

        public Task<ManagedChromiumTransferPhaseResult<TransferPreparationReceipt>> PrepareAsync(
            ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken)
        {
            Events.Add("Prepare"); Share = share; Policy = duplicatePolicy;
            return Task.FromResult(new ManagedChromiumTransferPhaseResult<TransferPreparationReceipt>(
                BaiduAdapterResult<TransferPreparationReceipt>.Success(TransferPreparationReceipt.Ready)));
        }

        public Task<ManagedChromiumTransferPhaseResult<TransferReceipt>> SaveAsync(
            ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken)
        {
            Events.Add("Save"); SaveCalls++; Share = share; Policy = duplicatePolicy;
            var exact = new RemoteItemIdentitySet([
                new RemoteItemIdentity("destination-exact-apk", "item.apk"),
            ]);
            return Task.FromResult(new ManagedChromiumTransferPhaseResult<TransferReceipt>(
                BaiduAdapterResult<TransferReceipt>.Success(
                    TransferReceipt.WithExactEvidence(DateTimeOffset.UtcNow, null, exact))));
        }

        public Task<ManagedChromiumTransferPhaseResult<TransferConfirmationResult>> ConfirmAsync(
            TransferConfirmationQuery query, CancellationToken cancellationToken)
        {
            ConfirmCalls++;
            return Task.FromResult(new ManagedChromiumTransferPhaseResult<TransferConfirmationResult>(
                BaiduAdapterResult<TransferConfirmationResult>.Success(TransferConfirmationResult.Ambiguous)));
        }

        public ValueTask DisposeAsync()
        {
            Events.Add("Dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeQuarantine(ManagedTransferQuarantineKey key) : IManagedTransferQuarantine
    {
        public ManagedTransferQuarantineKey Key { get; } = key;
        internal int Calls { get; private set; }
        public Task<ManagedTransferQuarantineReconciliation> ReconcileAsync(
            int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ManagedTransferQuarantineReconciliation.Retained);
        }
    }
}
