using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferDirectoryReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectsExactDestinationFoldersIncludingExplicitEmpty(bool empty)
    {
        var original = Snapshot();
        var snapshot = Signed(original.BaiduUserId, empty ? [] : original.Items);
        var reader = Reader(snapshot);
        var result = await Read(reader);
        Assert.Equal(ManagedBrowserDirectoryListingResultKind.Observed, result.Kind);
        var observed = Assert.IsType<BaiduTransferCandidateObservation>(result.Observation);
        Assert.Equal(BaiduCandidatePageState.DestinationPicker, observed.PageState);
        Assert.Equal("parent-1", observed.ParentExactId);
        Assert.Equal("/目标目录", observed.CanonicalPath!.Value);
        Assert.Equal(new[] { "parent-1" }, observed.AncestorExactIds);
        Assert.True(observed.ReachedEnd && observed.IsStable && observed.IsComplete);
        Assert.Equal(snapshot.Items.Select(i => i.ServerName), observed.Items.Select(i => i.ServerName));
        Assert.DoesNotContain(snapshot.BaiduUserId, result.ToString());
        Assert.DoesNotContain("目标目录", snapshot.ToString());
        Assert.DoesNotContain("目标目录", Payload("parent-1", "/目标目录").ToString());
    }

    [Fact]
    public void FingerprintPinsSourceParentPathOrderedAncestorsAndExactNames()
    {
        var source = Snapshot();
        Assert.Equal("53d8c07848a9a877545fc8c73521ee559d7041bf16e77ba397a9c5f0b1b06fa4", source.ComputeFingerprint(Payload().Binding.SourceUri));
        Assert.NotEqual(source.ObservationFingerprint, source.ComputeFingerprint(Payload().Binding.SourceUri.Split('?')[0]));
    }

    [Theory]
    [InlineData("uid", ManagedBrowserDirectoryListingResultKind.AccountIdentityMismatch)]
    [InlineData("fingerprint", ManagedBrowserDirectoryListingResultKind.PageNotValidated)]
    [InlineData("parent", ManagedBrowserDirectoryListingResultKind.PageNotValidated)]
    [InlineData("path", ManagedBrowserDirectoryListingResultKind.PageNotValidated)]
    [InlineData("envelope", ManagedBrowserDirectoryListingResultKind.PageNotValidated)]
    [InlineData("timeout", ManagedBrowserDirectoryListingResultKind.TimedOut)]
    [InlineData("cancel", ManagedBrowserDirectoryListingResultKind.Cancelled)]
    [InlineData("transport", ManagedBrowserDirectoryListingResultKind.BrowserUnavailable)]
    [InlineData("protocol", ManagedBrowserDirectoryListingResultKind.PageNotValidated)]
    public async Task MismatchesAndFailuresNeverPublishOrReplay(string fault, ManagedBrowserDirectoryListingResultKind expected)
    {
        var calls = 0;
        var reader = new ManagedTransferDirectoryReader((request, _) =>
        {
            calls++;
            if (fault == "timeout") throw new TimeoutException();
            if (fault == "cancel") throw new OperationCanceledException();
            if (fault == "transport") throw new IOException("private");
            if (fault == "protocol") throw new ManagedBrowserHelperClientException(ManagedBrowserHelperClientError.ProtocolViolation);
            var source = Snapshot();
            source = new ManagedBrowserDirectorySnapshot(fault == "uid" ? "wrong-uid" : source.BaiduUserId,
                source.GenerationToken, source.ParentExactId, source.CanonicalPath, source.AncestorExactIds,
                source.TotalCount, true, true, source.Items, fault == "fingerprint" ? new string('0', 64) : source.ObservationFingerprint);
            var result = new ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind.Observed, source);
            return Task.FromResult(fault == "envelope"
                ? new ManagedBrowserResponse(Guid.NewGuid(), request.OperationId, request.AccountId, request.Generation, request.Action, result)
                : ManagedBrowserResponse.For(request, result));
        });
        var payload = fault is "parent" or "path" ? Payload(fault == "parent" ? "wrong" : "parent-1", fault == "path" ? "/other" : "/目标目录") : Payload();
        var result = await reader.ReadAsync(Guid.NewGuid(), Guid.NewGuid(), 7, payload, default);
        Assert.Equal(expected, result.Kind); Assert.Null(result.Observation); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrAfterSendCannotPublish(bool before)
    {
        using var cancel = new CancellationTokenSource(); var calls = 0;
        var reader = new ManagedTransferDirectoryReader((request, _) =>
        {
            calls++; cancel.Cancel();
            return Task.FromResult(ManagedBrowserResponse.For(request, new ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind.Observed, Snapshot())));
        });
        if (before) cancel.Cancel();
        var result = await Read(reader, cancel.Token);
        Assert.Equal(ManagedBrowserDirectoryListingResultKind.Cancelled, result.Kind);
        Assert.Null(result.Observation); Assert.Equal(before ? 0 : 1, calls);
    }

    [Fact]
    public async Task AllDenialsAreKindOnlyAndWhitespaceOnlyFolderNamesAreRejected()
    {
        foreach (var kind in Enum.GetValues<ManagedBrowserDirectoryListingResultKind>().Where(k => k != ManagedBrowserDirectoryListingResultKind.Observed))
        {
            var reader = new ManagedTransferDirectoryReader((request, _) => Task.FromResult(ManagedBrowserResponse.For(request, new ManagedBrowserDirectoryListingResult(kind))));
            var result = await Read(reader); Assert.Equal(kind, result.Kind); Assert.Null(result.Observation);
        }
        var observed = await Read(Reader(Signed(Snapshot().BaiduUserId, [new("child", ManagedBrowserSourceItemKind.Folder, " ")])));
        Assert.Equal(ManagedBrowserDirectoryListingResultKind.PageNotValidated, observed.Kind);
        Assert.Null(observed.Observation);
        var exact = await new ManagedTransferDirectoryReader((request, _) => Task.FromResult(ManagedBrowserResponse.For(request,
            new ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind.Observed, Snapshot()))))
            .ReadAsync(Guid.NewGuid(), Guid.NewGuid(), 7, Payload("parent-1", "/目标目录"), default);
        Assert.Equal(ManagedBrowserDirectoryListingResultKind.Observed, exact.Kind);
    }

    [Fact]
    public async Task RetainsWireValidatedRootInclusiveEvidenceAndProjectsOnlyTheProvenRootAway()
    {
        var cases = new[]
        {
            ("/", new[] { "root-id" }, Array.Empty<string>()),
            ("/甲", new[] { "root-id", "jia-id" }, new[] { "jia-id" }),
            ("/甲/乙", new[] { "root-id", "jia-id", "yi-id" }, new[] { "jia-id", "yi-id" }),
        };

        foreach (var (path, rootInclusive, projected) in cases)
        {
            var result = await Read(Reader(SignedDirectory(path, rootInclusive)));

            Assert.Equal(ManagedBrowserDirectoryListingResultKind.Observed, result.Kind);
            Assert.NotNull(result.Evidence);
            Assert.Equal("root-id", result.Evidence!.RootExactId);
            Assert.Equal(rootInclusive, result.Evidence.RootInclusiveAncestorExactIds);
            Assert.Equal(rootInclusive, result.Evidence.Snapshot.AncestorExactIds);
            var candidates = Assert.IsType<TransferDirectoryListing>(result.FolderCandidates);
            Assert.Equal(TransferDirectoryListingScope.FolderCandidates, candidates.Scope);
            Assert.Equal(projected, candidates.AncestorExactIds);
            Assert.Equal(projected, result.Observation!.AncestorExactIds);
        }
    }

    [Fact]
    public async Task FolderCandidatesIsAnExplicitAdapterListingAndEvidenceCannotBePubliclyForged()
    {
        var result = await Read(Reader(Snapshot()));

        Assert.Equal(TransferDirectoryListingScope.FolderCandidates, result.Evidence!.FolderCandidates.Scope);
        Assert.Empty(typeof(ManagedTransferDirectoryEvidence).GetConstructors());
    }

    [Fact]
    public void WireSnapshotRejectsMalformedOrIncompleteFolderEvidenceBeforeProjection()
    {
        var source = Snapshot();
        void Reject(string path, IEnumerable<string> ancestors, int totalCount, bool reachedEnd,
            bool stable, IEnumerable<ManagedBrowserSourceItem> items) =>
            Assert.Throws<ArgumentException>(() => new ManagedBrowserDirectorySnapshot(
                source.BaiduUserId, source.GenerationToken, source.ParentExactId, path, ancestors,
                totalCount, reachedEnd, stable, items, source.ObservationFingerprint));

        Reject("/甲/乙", ["root-1", "parent-1"], 0, true, true, []);
        Reject("/目标目录", ["root-1", "wrong-id"], 0, true, true, []);
        Reject("/目标目录", ["root-1", "root-1"], 0, true, true, []);
        Reject("/目标目录/", ["root-1", "parent-1"], 0, true, true, []);
        Reject("/目标目录", ["root-1", "parent-1"], 1, true, true, []);
        Reject("/目标目录", ["root-1", "parent-1"], 0, false, true, []);
        Reject("/目标目录", ["root-1", "parent-1"], 0, true, false, []);
        Reject("/目标目录", ["root-1", "parent-1"], 1, true, true,
            [new ManagedBrowserSourceItem("file-id", ManagedBrowserSourceItemKind.File, "file")]);
    }

    private static ManagedTransferDirectoryReader Reader(ManagedBrowserDirectorySnapshot source) => new((request, _) => Task.FromResult(ManagedBrowserResponse.For(request, new ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind.Observed, source))));
    private static ManagedBrowserDirectorySnapshot Signed(string uid, IEnumerable<ManagedBrowserSourceItem> items)
    {
        var copy = items.ToArray();
        var unsigned = new ManagedBrowserDirectorySnapshot(uid, "directory-gen-1", "parent-1", "/目标目录", ["root-1", "parent-1"], copy.Length, true, true, copy, new string('0', 64));
        return new(uid, unsigned.GenerationToken, unsigned.ParentExactId, unsigned.CanonicalPath, unsigned.AncestorExactIds, copy.Length, true, true, copy, unsigned.ComputeFingerprint(Payload().Binding.SourceUri));
    }
    private static ManagedBrowserDirectorySnapshot SignedDirectory(string path, IReadOnlyList<string> ancestors)
    {
        var current = ancestors[^1];
        var unsigned = new ManagedBrowserDirectorySnapshot(Snapshot().BaiduUserId, "directory-gen-1", current, path,
            ancestors, 0, true, true, [], new string('0', 64));
        return new ManagedBrowserDirectorySnapshot(unsigned.BaiduUserId, unsigned.GenerationToken,
            unsigned.ParentExactId, unsigned.CanonicalPath, unsigned.AncestorExactIds, unsigned.TotalCount,
            true, true, unsigned.Items, unsigned.ComputeFingerprint(Payload().Binding.SourceUri));
    }
    private static ManagedBrowserDirectorySnapshot Snapshot() => ((ManagedBrowserDirectoryListingResult)ManagedBrowserProtocol.DecodeResponse(ManagedBrowserDirectoryListingProtocolTests.Bytes(ManagedBrowserDirectoryListingProtocolTests.Response())).Result).Snapshot!;
    private static ManagedBrowserReadTransferDirectoryRequest Payload(string? parent = null, string? path = null)
    {
        var parsed = (ManagedBrowserReadTransferDirectoryRequest)ManagedBrowserProtocol.DecodeRequest(ManagedBrowserDirectoryListingProtocolTests.Bytes(ManagedBrowserDirectoryListingProtocolTests.Fixture()["request"]!)).Payload;
        return new(parsed.Binding, parent, path);
    }
    private static Task<ManagedTransferDirectoryReadResult> Read(ManagedTransferDirectoryReader reader, CancellationToken token = default) => reader.ReadAsync(Guid.NewGuid(), Guid.NewGuid(), 7, Payload(), token);
}
