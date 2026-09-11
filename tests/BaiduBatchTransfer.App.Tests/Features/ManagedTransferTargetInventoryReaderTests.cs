using System.Reflection;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferTargetInventoryReaderTests
{
    private static readonly Guid BrowserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Theory]
    [InlineData("/", new[] { "root-1" })]
    [InlineData("/parent/child", new[] { "root-1", "parent-1", "child-1" })]
    public async Task CompleteStableAllItemsRetainWireDetailsAndProjectOnlyDurableFields(
        string path, string[] rootInclusiveAncestors)
    {
        var snapshot = Snapshot(directoryId: rootInclusiveAncestors[^1], path: path,
            ancestors: rootInclusiveAncestors);
        var result = await ReadAsync(Reader(snapshot), Payload(path, rootInclusiveAncestors));

        Assert.Equal(ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, result.Kind);
        var projected = Assert.IsType<ManagedTransferTargetInventoryEvidence>(result.Evidence);
        Assert.Same(snapshot, projected.WireSnapshot);
        Assert.Equal(rootInclusiveAncestors, projected.WireSnapshot.AncestorExactIds);
        Assert.Equal(snapshot.ComputeFingerprint(), snapshot.ObservationFingerprint);
        Assert.Collection(projected.WireSnapshot.Items,
            item =>
            {
                Assert.Equal("file-1", item.ExactId);
                Assert.Equal(ManagedBrowserTargetInventoryItemKind.File, item.Kind);
                Assert.Equal("one.txt", item.ServerName);
            },
            item =>
            {
                Assert.Equal("folder-1", item.ExactId);
                Assert.Equal(ManagedBrowserTargetInventoryItemKind.Folder, item.Kind);
                Assert.Equal("folder", item.ServerName);
            });

        Assert.Equal(TransferDirectoryListingScope.AllItems, projected.AllItems.Scope);
        Assert.Equal(new[] { "file-1", "folder-1" },
            projected.AllItems.TopLevelItems.Select(item => item.ExactId));
        Assert.Equal(new[] { TransferRemoteItemKind.File, TransferRemoteItemKind.Folder },
            projected.AllItems.DetailedItems.Select(item => item.Kind));

        var durable = projected.DurableEvidence;
        Assert.Equal(snapshot.DirectoryExactId, durable.TargetExactId);
        Assert.Equal(new RemotePath(path), durable.TargetPath);
        Assert.Equal(snapshot.TotalCount, durable.TotalCount);
        Assert.True(durable.ReachedEnd && durable.IsStable);
        Assert.Equal(snapshot.GenerationToken, durable.TargetGenerationToken);
        Assert.Equal(snapshot.BaiduUserId, durable.BaiduUserId);
        Assert.Equal(TransferTargetInventoryScope.AllItems, durable.Scope);
        Assert.Equal(new[] { "file-1", "folder-1" },
            durable.Baseline.Items.Select(item => item.ExactId));
        Assert.NotEqual(snapshot.ObservationFingerprint, durable.ObservationFingerprint);
        Assert.Equal(TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                durable.TargetExactId, durable.TargetPath, durable.TotalCount, durable.ReachedEnd,
                durable.IsStable, durable.TargetGenerationToken, durable.BaiduUserId,
                TransferTargetInventoryScope.AllItems, durable.Baseline),
            durable.ObservationFingerprint);

        Assert.DoesNotContain("one.txt", durable.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.BaiduUserId, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(path, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("browser")]
    [InlineData("target")]
    [InlineData("endpoint")]
    [InlineData("uid")]
    [InlineData("directory")]
    [InlineData("path")]
    [InlineData("ancestry")]
    [InlineData("generation")]
    [InlineData("fingerprint")]
    [InlineData("envelope")]
    public async Task BindingRouteSessionAndWireFingerprintDriftFailClosed(string drift)
    {
        var source = Snapshot();
        var changed = Snapshot(
            browserId: drift == "browser" ? Guid.Parse("55555555-5555-5555-5555-555555555555") : BrowserId,
            targetId: drift == "target" ? "target-2" : "target-1",
            endpoint: drift == "endpoint" ? "http://127.0.0.1:43128/" : "http://127.0.0.1:43127/",
            uid: drift == "uid" ? "uid-2" : "uid-1",
            directoryId: drift == "directory" ? "other-parent" : "parent-1",
            path: drift == "path" ? "/other" : "/parent",
            ancestors: drift switch
            {
                "directory" => ["root-1", "other-parent"],
                "ancestry" => ["other-root", "parent-1"],
                _ => ["root-1", "parent-1"],
            });
        if (drift == "generation")
            SetBackingField(changed, nameof(ManagedBrowserTargetInventorySnapshot.GenerationToken), "generation-2");
        if (drift == "fingerprint")
            SetBackingField(changed, nameof(ManagedBrowserTargetInventorySnapshot.ObservationFingerprint), new string('0', 64));

        var reader = new ManagedTransferTargetInventoryReader((request, _) =>
        {
            var response = ManagedBrowserResponse.For(request,
                new ManagedBrowserReadTransferTargetDirectoryResult(
                    ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, changed));
            if (drift == "envelope")
                response = new ManagedBrowserResponse(Guid.NewGuid(), response.OperationId, response.AccountId,
                    response.Generation, response.Action, response.Result);
            return Task.FromResult(response);
        });

        var result = await ReadAsync(reader, Payload());

        Assert.Equal(ManagedBrowserReadTransferTargetDirectoryResultKind.PageNotValidated, result.Kind);
        Assert.Null(result.Evidence);
        Assert.DoesNotContain(source.BaiduUserId, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ManagedBrowserReadTransferTargetDirectoryResultKind.PageNotValidated)]
    [InlineData(ManagedBrowserReadTransferTargetDirectoryResultKind.AccountIdentityMismatch)]
    [InlineData(ManagedBrowserReadTransferTargetDirectoryResultKind.LoginRequired)]
    [InlineData(ManagedBrowserReadTransferTargetDirectoryResultKind.VerificationRequired)]
    [InlineData(ManagedBrowserReadTransferTargetDirectoryResultKind.BrowserUnavailable)]
    [InlineData(ManagedBrowserReadTransferTargetDirectoryResultKind.TimedOut)]
    [InlineData(ManagedBrowserReadTransferTargetDirectoryResultKind.Cancelled)]
    public async Task DenialsNeverProjectEvidence(ManagedBrowserReadTransferTargetDirectoryResultKind kind)
    {
        var reader = new ManagedTransferTargetInventoryReader((request, _) => Task.FromResult(
            ManagedBrowserResponse.For(request, new ManagedBrowserReadTransferTargetDirectoryResult(kind))));

        var result = await ReadAsync(reader, Payload());

        Assert.Equal(kind, result.Kind);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task CancellationAndTransportFailuresFailClosedWithoutReplay()
    {
        var calls = 0;
        var reader = new ManagedTransferTargetInventoryReader((_, _) =>
        {
            calls++;
            throw new IOException("private-target-transport");
        });

        var unavailable = await ReadAsync(reader, Payload());
        Assert.Equal(ManagedBrowserReadTransferTargetDirectoryResultKind.BrowserUnavailable, unavailable.Kind);
        Assert.Equal(1, calls);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var before = await ReadAsync(reader, Payload(), cancelled.Token);
        Assert.Equal(ManagedBrowserReadTransferTargetDirectoryResultKind.Cancelled, before.Kind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FolderCandidateAndMalformedOrLossySnapshotsCannotBecomeDurableEvidence()
    {
        Assert.DoesNotContain(typeof(ManagedTransferTargetInventoryReader).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(TransferDirectoryListing)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManagedTransferTargetInventoryReader.MapScope((TransferTargetInventoryScope)0));

        var variants = new (string Property, object Value)[]
        {
            (nameof(ManagedBrowserTargetInventorySnapshot.Scope), (ManagedBrowserTargetInventoryScope)0),
            (nameof(ManagedBrowserTargetInventorySnapshot.TotalCount), 1),
            (nameof(ManagedBrowserTargetInventorySnapshot.ReachedEnd), false),
            (nameof(ManagedBrowserTargetInventorySnapshot.IsStable), false),
            (nameof(ManagedBrowserTargetInventorySnapshot.Items),
                new[] { new ManagedBrowserTargetInventoryItem("file-1", ManagedBrowserTargetInventoryItemKind.File, "one.txt"),
                        new ManagedBrowserTargetInventoryItem("file-1", ManagedBrowserTargetInventoryItemKind.Folder, "folder") }),
        };
        foreach (var (property, value) in variants)
        {
            var forged = Snapshot();
            SetBackingField(forged, property, value);
            Assert.ThrowsAny<ArgumentException>(() => ManagedTransferTargetInventoryReader.Project(forged));
        }

        var longSegments = Enumerable.Range(0, 35).Select(index =>
            $"{index:D2}" + new string('a', 58)).ToArray();
        var longAncestors = Enumerable.Range(0, longSegments.Length + 1)
            .Select(index => $"ancestor-{index:D2}").ToArray();
        var wireValidButNotDurable = Snapshot(directoryId: longAncestors[^1],
            path: "/" + string.Join('/', longSegments), ancestors: longAncestors);
        Assert.Throws<ArgumentException>(() =>
            ManagedTransferTargetInventoryReader.Project(wireValidButNotDurable));
    }

    [Fact]
    public async Task ReaderDispatchesOnlyTheFixedReadAction()
    {
        ManagedBrowserRequest? dispatched = null;
        var reader = new ManagedTransferTargetInventoryReader((request, _) =>
        {
            dispatched = request;
            return Task.FromResult(ManagedBrowserResponse.For(request,
                new ManagedBrowserReadTransferTargetDirectoryResult(
                    ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, Snapshot())));
        });

        var result = await ReadAsync(reader, Payload());

        Assert.Equal(ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, result.Kind);
        Assert.NotNull(dispatched);
        Assert.Equal(ManagedBrowserAction.ReadTransferTargetDirectory, dispatched!.Action);
    }

    [Fact]
    public async Task RouteProofRequestBindsExactOwnedSessionAndOnlyThreeFixedRouteStates()
    {
        var binding = Binding();
        var fixture = RouteFixture(binding);
        var exactDirectory = ManagedTransferRouteExpectation.ExactTargetDirectory(
            "parent-1", "/parent", ["root-1", "parent-1"]);
        var expectations = new[]
        {
            ManagedTransferRouteExpectation.OriginalSource,
            ManagedTransferRouteExpectation.OfficialTargetRoot,
            exactDirectory,
        };
        var proof = new RecordingRouteProof();

        foreach (var expected in expectations)
            Assert.True(await proof.VerifyAsync(Request(fixture, binding, expected), default));

        Assert.Equal(new[]
        {
            ManagedTransferRouteState.OriginalSource,
            ManagedTransferRouteState.OfficialTargetRoot,
            ManagedTransferRouteState.ExactTargetDirectory,
        }, proof.Requests.Select(request => request.ExpectedRoute.State));
        Assert.Equal("parent-1", proof.Requests[^1].ExpectedRoute.DirectoryExactId);
        Assert.Equal("/parent", proof.Requests[^1].ExpectedRoute.CanonicalPath);
        Assert.Equal(new[] { "root-1", "parent-1" }, proof.Requests[^1].ExpectedRoute.AncestorExactIds);

        foreach (var drift in new[] { "browser", "target", "endpoint" })
        {
            var changedBinding = Binding(
                browserId: drift == "browser" ? Guid.NewGuid() : binding.BrowserId,
                targetId: drift == "target" ? "target-2" : binding.TargetId,
                endpoint: drift == "endpoint" ? "http://127.0.0.1:43128/" : binding.DevToolsEndpoint.AbsoluteUri);
            var error = Assert.Throws<ArgumentException>(() => Request(
                fixture, changedBinding, ManagedTransferRouteExpectation.OriginalSource));
            Assert.DoesNotContain("target-2", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("43128", error.ToString(), StringComparison.Ordinal);
        }

        Assert.Throws<ArgumentException>(() => ManagedTransferRouteExpectation.ExactTargetDirectory(
            "parent-1", "/parent", ["root-1", "other-parent"]));
        Assert.DoesNotContain("parent-1", exactDirectory.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/parent", exactDirectory.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("target-1", proof.Requests[0].ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("installation")]
    [InlineData("profile")]
    [InlineData("family")]
    [InlineData("ownership")]
    [InlineData("uid")]
    [InlineData("source")]
    [InlineData("request-account")]
    [InlineData("request-owner")]
    [InlineData("request-session")]
    public void RouteProofRejectsOwnedSessionRequestIdentityAndSourceDrift(string drift)
    {
        var binding = Binding();
        var fixture = RouteFixture(binding);
        var session = fixture.Session;
        var identity = fixture.Identity;
        if (drift == "source")
        {
            SetBackingField(binding.ExpectedSource,
                nameof(ManagedBrowserSourceSnapshot.ObservationFingerprint), new string('0', 64));
        }
        else if (drift is "account" or "installation" or "profile" or "family" or "ownership" or "uid")
        {
            session = new ManagedBrowserSessionReference(
                session.SessionId,
                drift == "installation" ? Guid.NewGuid() : session.InstallationId,
                drift == "account" ? Guid.NewGuid() : session.AccountId,
                drift == "uid" ? "uid-2" : session.StableBaiduUserId,
                drift == "family" ? BrowserFamily.Edge : session.BrowserFamily,
                drift == "profile" ? Guid.NewGuid().ToString("N") : session.ProfileId,
                drift == "ownership" ? Guid.NewGuid().ToString("N") : session.ProfileOwnershipId);
        }
        else
        {
            identity = new WebAutomationRequestIdentity(
                identity.OperationId, identity.BatchId,
                drift == "request-account" ? Guid.NewGuid() : identity.AccountId,
                identity.Generation,
                drift == "request-owner" ? WebAutomationOwnerKind.TransferShare : identity.OwnerKind,
                drift == "request-session"
                    ? ExecutionProfileSnapshot.Managed(BrowserFamily.Chrome, session.ProfileId,
                        session.InstallationId, Guid.NewGuid())
                    : identity.ExecutionProfile);
        }

        var error = Assert.Throws<ArgumentException>(() => new ManagedTransferRouteProofRequest(
            fixture.Handle, session, identity, binding, ManagedTransferRouteExpectation.OriginalSource));

        Assert.DoesNotContain("uid-2", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(session.ProfileId, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RouteProofDefensivelySnapshotsExactSessionAndRequestIdentity()
    {
        var binding = Binding();
        var fixture = RouteFixture(binding);

        var request = Request(fixture, binding, ManagedTransferRouteExpectation.OriginalSource);

        Assert.NotSame(fixture.Session, request.ExactManagedSession);
        Assert.NotSame(fixture.Identity, request.RequestIdentity);
        Assert.Equal(fixture.Session.SessionId, request.ExactManagedSession.SessionId);
        Assert.Equal(fixture.Session.ProfileOwnershipId, request.ExactManagedSession.ProfileOwnershipId);
        Assert.Equal(fixture.Identity, request.RequestIdentity);
    }

    [Fact]
    public void SnapshotHeaderAndRequestCollectionsCannotBeMutatedThroughExposedInterfaces()
    {
        var inputAncestors = new[] { "root-1", "parent-1" };
        var inputItems = new[]
        {
            new ManagedBrowserTargetInventoryItem(
                "file-1", ManagedBrowserTargetInventoryItemKind.File, "one.txt"),
            new ManagedBrowserTargetInventoryItem(
                "folder-1", ManagedBrowserTargetInventoryItemKind.Folder, "folder"),
        };
        var snapshot = new ManagedBrowserTargetInventorySnapshot(
            BrowserId, "target-1", new Uri("http://127.0.0.1:43127/"), "uid-1",
            "generation-1", "parent-1", "/parent", inputAncestors, 2, true, true, 3,
            inputItems);
        var header = snapshot.ToHeader();
        var request = new ManagedBrowserReadTransferTargetDirectoryRequest(
            Binding(), "parent-1", "/parent", inputAncestors);

        inputAncestors[0] = "changed-input";
        inputItems[0] = new ManagedBrowserTargetInventoryItem(
            "changed-input", ManagedBrowserTargetInventoryItemKind.File, "changed.txt");
        Assert.Equal("root-1", snapshot.AncestorExactIds[0]);
        Assert.Equal("file-1", snapshot.Items[0].ExactId);
        Assert.Equal("root-1", header.AncestorExactIds[0]);
        Assert.Equal("root-1", request.ExpectedAncestorExactIds![0]);

        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<string>>(snapshot.AncestorExactIds)[0] = "forged-root");
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<ManagedBrowserTargetInventoryItem>>(snapshot.Items)[0] =
                new ManagedBrowserTargetInventoryItem(
                    "forged-item", ManagedBrowserTargetInventoryItemKind.File, "forged.txt"));
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<string>>(header.AncestorExactIds)[0] = "forged-header");
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<string>>(request.ExpectedAncestorExactIds!)[0] = "forged-request");
    }

    [Fact]
    public void FrozenWireCollectionsKeepEvidenceViewsConsistent()
    {
        var projected = ManagedTransferTargetInventoryReader.Project(Snapshot());
        var wireItems = Assert.IsAssignableFrom<IList<ManagedBrowserTargetInventoryItem>>(
            projected.WireSnapshot.Items);

        Assert.Throws<NotSupportedException>(() => wireItems[0] =
            new ManagedBrowserTargetInventoryItem(
                "forged-item", ManagedBrowserTargetInventoryItemKind.File, "forged.txt"));

        var wireIds = projected.WireSnapshot.Items.Select(item => item.ExactId).ToArray();
        Assert.Equal(wireIds, projected.AllItems.TopLevelItems.Select(item => item.ExactId));
        Assert.Equal(wireIds, projected.DurableEvidence.Baseline.Items.Select(item => item.ExactId));
        Assert.Equal(projected.WireSnapshot.ComputeFingerprint(),
            projected.WireSnapshot.ObservationFingerprint);
    }

    [Theory]
    [InlineData(ManagedBrowserTargetInventoryItemKind.File, TransferRemoteItemKind.File)]
    [InlineData(ManagedBrowserTargetInventoryItemKind.Folder, TransferRemoteItemKind.Folder)]
    public void ItemKindMappingIsExact(
        ManagedBrowserTargetInventoryItemKind wireKind, TransferRemoteItemKind adapterKind) =>
        Assert.Equal(adapterKind, ManagedTransferTargetInventoryReader.MapItemKind(wireKind));

    [Fact]
    public void UnknownItemKindCannotFallThroughToFolder() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManagedTransferTargetInventoryReader.MapItemKind((ManagedBrowserTargetInventoryItemKind)0));

    [Fact]
    public void ReaderAndRouteProofHaveNoWritePermitMutationOrNavigationDependencies()
    {
        var inspected = new[]
        {
            typeof(ManagedTransferTargetInventoryReader),
            typeof(IManagedTransferRouteProof),
            typeof(ManagedTransferRouteProofRequest),
            typeof(ManagedTransferRouteExpectation),
        };
        var dependencies = inspected.SelectMany(type => type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(field => field.FieldType)
            .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Assert.DoesNotContain(dependencies, name => name.Contains("MutationPermit", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies, name => name.Contains(nameof(TransferDirectoryMutationRecord), StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies, name => name.Contains("Navigation", StringComparison.Ordinal));
    }

    private static ManagedTransferTargetInventoryReader Reader(ManagedBrowserTargetInventorySnapshot snapshot) =>
        new((request, _) => Task.FromResult(ManagedBrowserResponse.For(request,
            new ManagedBrowserReadTransferTargetDirectoryResult(
                ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, snapshot))));

    private static Task<ManagedTransferTargetInventoryReadResult> ReadAsync(
        ManagedTransferTargetInventoryReader reader, ManagedBrowserReadTransferTargetDirectoryRequest payload,
        CancellationToken cancellationToken = default) => reader.ReadAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 7, payload, cancellationToken);

    private static ManagedBrowserReadTransferTargetDirectoryRequest Payload(
        string path = "/parent", IReadOnlyList<string>? ancestors = null)
    {
        var actualAncestors = ancestors ?? ["root-1", "parent-1"];
        return new ManagedBrowserReadTransferTargetDirectoryRequest(Binding(), actualAncestors[^1], path, actualAncestors);
    }

    private static ManagedBrowserTargetActionBinding Binding(
        Guid? browserId = null, string targetId = "target-1",
        string endpoint = "http://127.0.0.1:43127/")
    {
        const string sourceUri = "https://pan.baidu.com/s/source_1?pwd=A1b2";
        var unsigned = new ManagedBrowserSourceSnapshot("uid-1", "source-generation", 1, true, true,
            [new ManagedBrowserSourceItem("source-1", ManagedBrowserSourceItemKind.File, "source.txt")],
            new string('0', 64));
        var source = new ManagedBrowserSourceSnapshot(unsigned.BaiduUserId, unsigned.GenerationToken,
            unsigned.TotalCount, true, true, unsigned.Items,
            unsigned.ComputeFingerprint(sourceUri));
        return new ManagedBrowserTargetActionBinding(browserId ?? BrowserId, targetId, new Uri(endpoint),
            30_000, sourceUri, source.BaiduUserId, source);
    }

    private static ManagedBrowserTargetInventorySnapshot Snapshot(
        Guid? browserId = null, string targetId = "target-1",
        string endpoint = "http://127.0.0.1:43127/", string uid = "uid-1",
        string generation = "generation-1", string directoryId = "parent-1",
        string path = "/parent", IReadOnlyList<string>? ancestors = null) =>
        new(browserId ?? BrowserId, targetId, new Uri(endpoint), uid, generation,
            directoryId, path, ancestors ?? ["root-1", "parent-1"], 2, true, true, 3,
            [new ManagedBrowserTargetInventoryItem("folder-1", ManagedBrowserTargetInventoryItemKind.Folder, "folder"),
             new ManagedBrowserTargetInventoryItem("file-1", ManagedBrowserTargetInventoryItemKind.File, "one.txt")]);

    private static ManagedTransferRouteProofRequest Request(RouteProofFixture fixture,
        ManagedBrowserTargetActionBinding binding, ManagedTransferRouteExpectation expectedRoute) =>
        new(fixture.Handle, fixture.Session, fixture.Identity, binding, expectedRoute);

    private static RouteProofFixture RouteFixture(ManagedBrowserTargetActionBinding binding)
    {
        var accountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var installation = new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Chrome,
            @"C:\SyntheticBrowser", @"C:\SyntheticBrowser\chrome.exe", "Google LLC", "Google Chrome", "synthetic-file");
        var session = new ManagedBrowserSessionReference(Guid.NewGuid(), installation.InstallationId,
            accountId, "uid-1", BrowserFamily.Chrome, Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        var profile = ManagedBrowserProfileDescriptor.FromStoredSession(session, @"C:\SyntheticProfileRoot");
        var handle = new ManagedChromiumSessionHandle(1234, installation, profile,
            new DevToolsActivePort(binding.DevToolsEndpoint.Port, binding.BrowserId,
                binding.DevToolsEndpoint, new Uri($"ws://127.0.0.1:{binding.DevToolsEndpoint.Port}/devtools/browser/{binding.BrowserId:D}")),
            binding.TargetId);
        var identity = new WebAutomationRequestIdentity(Guid.NewGuid(), Guid.NewGuid(), accountId, 7,
            WebAutomationOwnerKind.Transfer, ExecutionProfileSnapshot.Managed(BrowserFamily.Chrome,
                session.ProfileId, session.InstallationId, session.SessionId));
        return new(handle, session, identity);
    }

    private static void SetBackingField<T>(object target, string property, T value)
    {
        var field = target.GetType().GetField($"<{property}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private sealed class RecordingRouteProof : IManagedTransferRouteProof
    {
        internal List<ManagedTransferRouteProofRequest> Requests { get; } = [];
        public Task<bool> VerifyAsync(ManagedTransferRouteProofRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(true);
        }
    }

    private sealed record RouteProofFixture(
        ManagedChromiumSessionHandle Handle,
        ManagedBrowserSessionReference Session,
        WebAutomationRequestIdentity Identity);
}
