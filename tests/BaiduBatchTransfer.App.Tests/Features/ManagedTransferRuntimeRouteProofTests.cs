using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferRuntimeRouteProofTests
{
    [Fact]
    public async Task ExactOwnedHandleAndOriginalSourceRouteAreProvedReadOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Targets.Page = new(Fixture.SourceUri);
        var proof = new ManagedTransferRuntimeRouteProof(fixture.Manager, fixture.SendAsync);

        var exact = await proof.VerifyAsync(fixture.Request(
            ManagedTransferRouteExpectation.OriginalSource), CancellationToken.None);

        Assert.True(exact);
        Assert.Equal(1, fixture.ProofReads);
        Assert.Equal(1, fixture.HelperCalls);
        Assert.Equal(0, fixture.PermitRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactOwnedHandleAcceptsOnlyCurrentOfficialTargetRoute(
        bool exactDirectory)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Targets.Page = new("https://pan.baidu.com/disk/main#/index?category=all");
        var proof = new ManagedTransferRuntimeRouteProof(fixture.Manager, fixture.SendAsync);
        var route = exactDirectory
            ? ManagedTransferRouteExpectation.ExactTargetDirectory(
                "directory-1", "/target", ["root-1", "directory-1"])
            : ManagedTransferRouteExpectation.OfficialTargetRoot;

        Assert.True(await proof.VerifyAsync(fixture.Request(route), CancellationToken.None));
        Assert.Equal(1, fixture.ProofReads);
        Assert.Equal(1, fixture.HelperCalls);
    }

    [Fact]
    public async Task DifferentHandleInstanceIsRejectedBeforeProbeHelperOrPermit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var forged = new ManagedChromiumSessionHandle(
            fixture.Handle.RootProcessId,
            fixture.Handle.Installation,
            fixture.Handle.Profile,
            new DevToolsActivePort(
                fixture.Handle.Endpoint.Port,
                fixture.Handle.BrowserId,
                fixture.Handle.Endpoint,
                new Uri($"ws://127.0.0.1:{fixture.Handle.Endpoint.Port}/devtools/browser/{fixture.Handle.BrowserId:D}")),
            fixture.Handle.TargetId);
        var proof = new ManagedTransferRuntimeRouteProof(fixture.Manager, fixture.SendAsync);

        var exact = await proof.VerifyAsync(fixture.Request(
            ManagedTransferRouteExpectation.OriginalSource, forged), CancellationToken.None);

        Assert.False(exact);
        Assert.Equal(0, fixture.ProofReads);
        Assert.Equal(0, fixture.HelperCalls);
        Assert.Equal(0, fixture.PermitRequests);
    }

    [Fact]
    public async Task ProcessOwnershipOrTargetIdentityDriftFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proof = new ManagedTransferRuntimeRouteProof(fixture.Manager, fixture.SendAsync);
        fixture.Process.Owned = false;

        Assert.False(await proof.VerifyAsync(fixture.Request(
            ManagedTransferRouteExpectation.OriginalSource), CancellationToken.None));
        Assert.Equal(0, fixture.HelperCalls);
        Assert.Equal(0, fixture.PermitRequests);
    }

    [Fact]
    public async Task TargetObservationUidDriftFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Targets.Page = new("https://pan.baidu.com/disk/main#/index?category=all");
        fixture.TargetBaiduUserId = "uid-other";
        var proof = new ManagedTransferRuntimeRouteProof(fixture.Manager, fixture.SendAsync);

        Assert.False(await proof.VerifyAsync(fixture.Request(
            ManagedTransferRouteExpectation.OfficialTargetRoot), CancellationToken.None));
        Assert.Equal(1, fixture.HelperCalls);
        Assert.Equal(0, fixture.PermitRequests);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture() { }

        internal const string SourceUri = "https://pan.baidu.com/s/source_1?pwd=A1b2";
        internal ManagedChromiumProcessManager Manager { get; private set; } = null!;
        internal ManagedChromiumSessionHandle Handle { get; private set; } = null!;
        internal ManagedBrowserSessionReference Session { get; private set; } = null!;
        internal WebAutomationRequestIdentity Identity { get; private set; } = null!;
        internal ManagedBrowserTargetActionBinding Binding { get; private set; } = null!;
        internal ManagedChromiumProcessManagerTests.FakeTargets Targets { get; private set; } = null!;
        internal ManagedChromiumProcessManagerTests.FakeProcess Process { get; private set; } = null!;
        internal int ProofReads { get; set; }
        internal int HelperCalls { get; set; }
        internal int PermitRequests { get; set; }
        internal string TargetBaiduUserId { get; set; } = "uid-1";

        internal static async Task<Fixture> CreateAsync()
        {
            var accountId = Guid.NewGuid();
            var installation = new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Chrome,
                @"C:\SyntheticBrowser", @"C:\SyntheticBrowser\chrome.exe",
                "Google LLC", "Google Chrome", "synthetic-file");
            var session = new ManagedBrowserSessionReference(Guid.NewGuid(), installation.InstallationId,
                accountId, "uid-1", BrowserFamily.Chrome, Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"));
            var profile = ManagedBrowserProfileDescriptor.FromStoredSession(
                session, @"C:\SyntheticProfileRoot");
            var executable = new ManagedChromiumProcessManagerTests.FakeExecutable(installation);
            var profileLease = new ManagedChromiumProcessManagerTests.FakeProfile(profile);
            var processes = new ManagedChromiumProcessManagerTests.FakeProcesses();
            var listeners = new ManagedChromiumProcessManagerTests.FakeListeners();
            var targets = new ManagedChromiumProcessManagerTests.FakeTargets
            {
                Page = new("https://pan.baidu.com/disk/main#/index?category=all"),
            };
            var fixture = new Fixture { Session = session, Targets = targets, Process = processes.Process };
            targets.OnRead = () => fixture.ProofReads++;
            var manager = new ManagedChromiumProcessManager(
                (_, _) => Task.FromResult<IBrowserExecutableLaunchLease>(executable),
                (_, _) => Task.FromResult<IManagedBrowserProfileLease>(profileLease),
                processes, listeners, targets, TimeSpan.FromMilliseconds(100));
            var handle = (await manager.LaunchAsync(installation, profile)).Session!;
            fixture.ProofReads = 0;
            var source = SourceSnapshot(SourceUri, session.StableBaiduUserId);
            fixture.Manager = manager;
            fixture.Handle = handle;
            fixture.Identity = new WebAutomationRequestIdentity(Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
                    WebAutomationOwnerKind.Transfer, ExecutionProfileSnapshot.Managed(
                        BrowserFamily.Chrome, session.ProfileId, session.InstallationId, session.SessionId));
            fixture.Binding = new ManagedBrowserTargetActionBinding(handle.BrowserId, handle.TargetId,
                handle.Endpoint, 30_000, SourceUri, session.StableBaiduUserId, source);
            return fixture;
        }

        internal ManagedTransferRouteProofRequest Request(
            ManagedTransferRouteExpectation route,
            ManagedChromiumSessionHandle? handle = null) =>
            new(handle ?? Handle, Session, Identity, Binding, route);

        internal Task<ManagedBrowserResponse> SendAsync(
            ManagedBrowserRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HelperCalls++;
            if (request.Payload is ManagedBrowserReadTransferSourceRequest)
                return Task.FromResult(ManagedBrowserResponse.For(request,
                    new ManagedBrowserSourceListingResult(
                        ManagedBrowserSourceListingResultKind.Observed,
                        Binding.ExpectedSource)));
            if (request.Payload is ManagedBrowserReadTransferTargetDirectoryRequest target)
            {
                var directoryId = target.ExpectedDirectoryExactId ?? "root-1";
                var path = target.ExpectedCanonicalPath ?? "/";
                var ancestors = target.ExpectedAncestorExactIds ?? [directoryId];
                var snapshot = new ManagedBrowserTargetInventorySnapshot(
                    Binding.BrowserId, Binding.TargetId, Binding.DevToolsEndpoint,
                    TargetBaiduUserId, "target-generation", directoryId, path,
                    ancestors, 0, true, true, 1, []);
                return Task.FromResult(ManagedBrowserResponse.For(request,
                    new ManagedBrowserReadTransferTargetDirectoryResult(
                        ManagedBrowserReadTransferTargetDirectoryResultKind.Observed,
                        snapshot)));
            }
            throw new InvalidOperationException("Unexpected fixed proof action.");
        }

        public async ValueTask DisposeAsync() => await Manager.DisposeAsync();

        private static ManagedBrowserSourceSnapshot SourceSnapshot(string sourceUri, string uid)
        {
            var unsigned = new ManagedBrowserSourceSnapshot(uid, "generation-1", 1, true, true,
                [new ManagedBrowserSourceItem("source-1", ManagedBrowserSourceItemKind.File, "source.txt")],
                new string('0', 64));
            return new ManagedBrowserSourceSnapshot(uid, unsigned.GenerationToken, unsigned.TotalCount,
                true, true, unsigned.Items, unsigned.ComputeFingerprint(sourceUri));
        }
    }
}
