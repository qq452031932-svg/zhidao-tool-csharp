using System.Text;
using System.Collections.Immutable;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferSourceReaderTests
{
    [Theory]
    [InlineData("Mixed")]
    [InlineData("Folder")]
    [InlineData("File")]
    public async Task ProjectsExactKindsAndNamesWithoutFlattening(string selection)
    {
        var source = Snapshot();
        var items = selection == "Mixed" ? source.Items : source.Items.Where(item => item.Kind.ToString() == selection).ToImmutableArray();
        source = Signed(source.BaiduUserId, source.GenerationToken, items);
        var reader = new ManagedTransferSourceReader((request, _) => Task.FromResult(ManagedBrowserResponse.For(request,
            new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.Observed, source))));
        var result = await Read(reader);
        Assert.Equal(ManagedBrowserSourceListingResultKind.Observed, result.Kind);
        var observed = Assert.IsType<BaiduTransferCandidateObservation>(result.Observation);
        Assert.Equal(BaiduCandidatePageState.ExternalShare, observed.PageState);
        Assert.Null(observed.ParentExactId);
        Assert.Null(observed.CanonicalPath);
        Assert.Empty(observed.AncestorExactIds);
        Assert.True(observed.ReachedEnd && observed.IsComplete && observed.IsStable);
        Assert.Equal(items.Length, observed.TotalCount);
        Assert.Equal(items.Select(item => item.ExactId), observed.Items.Select(item => item.ExactId));
        Assert.Equal(items.Select(item => item.ServerName), observed.Items.Select(item => item.ServerName));
        Assert.Equal(items.Select(item => item.Kind.ToString()), observed.Items.Select(item => item.Kind.ToString()));
        Assert.DoesNotContain(source.BaiduUserId, result.ToString());
    }

    [Fact]
    public void CrossLanguageFingerprintIncludesTypeNameGenerationAndSource()
    {
        var snapshot = Snapshot();
        Assert.Equal("8dacdb734e7cb4bbc8dac00252f7ccf6d39edf9f978b633970cb526acd719dcc", snapshot.ComputeFingerprint(Payload().Binding.SourceUri));
        Assert.NotEqual(snapshot.ObservationFingerprint, snapshot.ComputeFingerprint(Payload().Binding.SourceUri.Split('?')[0]));
        var changed = new ManagedBrowserSourceSnapshot(snapshot.BaiduUserId, "generation-2", 2, true, true, snapshot.Items, snapshot.ObservationFingerprint);
        Assert.NotEqual(snapshot.ObservationFingerprint, changed.ComputeFingerprint(Payload().Binding.SourceUri));
        foreach (var item in new[] { new ManagedBrowserSourceItem("101", ManagedBrowserSourceItemKind.File, "测试文件夹"),
                     new ManagedBrowserSourceItem("101", ManagedBrowserSourceItemKind.Folder, "renamed") })
        {
            changed = new ManagedBrowserSourceSnapshot(snapshot.BaiduUserId, snapshot.GenerationToken, 2, true, true,
                new[] { item, snapshot.Items[1] }, snapshot.ObservationFingerprint);
            Assert.NotEqual(snapshot.ObservationFingerprint, changed.ComputeFingerprint(Payload().Binding.SourceUri));
        }
    }

    [Theory]
    [InlineData("fingerprint", ManagedBrowserSourceListingResultKind.PageNotValidated)]
    [InlineData("uid", ManagedBrowserSourceListingResultKind.AccountIdentityMismatch)]
    [InlineData("envelope", ManagedBrowserSourceListingResultKind.PageNotValidated)]
    [InlineData("protocol", ManagedBrowserSourceListingResultKind.PageNotValidated)]
    [InlineData("transport", ManagedBrowserSourceListingResultKind.BrowserUnavailable)]
    [InlineData("timeout", ManagedBrowserSourceListingResultKind.TimedOut)]
    [InlineData("cancel", ManagedBrowserSourceListingResultKind.Cancelled)]
    public async Task MismatchAndFailuresNeverExposeObservationOrReplay(string failure, ManagedBrowserSourceListingResultKind expected)
    {
        var calls = 0;
        var reader = new ManagedTransferSourceReader((request, _) =>
        {
            calls++;
            if (failure == "protocol") throw new ManagedBrowserHelperClientException(ManagedBrowserHelperClientError.ProtocolViolation);
            if (failure == "transport") throw new IOException("private transport");
            if (failure == "timeout") throw new TimeoutException();
            if (failure == "cancel") throw new OperationCanceledException();
            var source = Snapshot();
            source = new ManagedBrowserSourceSnapshot(failure == "uid" ? "different" : source.BaiduUserId, source.GenerationToken,
                2, true, true, source.Items, failure == "fingerprint" ? new string('0', 64) : source.ObservationFingerprint);
            var response = new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.Observed, source);
            return Task.FromResult(failure == "envelope"
                ? new ManagedBrowserResponse(Guid.NewGuid(), request.OperationId, request.AccountId, request.Generation, request.Action, response)
                : ManagedBrowserResponse.For(request, response));
        });
        var result = await Read(reader);
        Assert.Equal(expected, result.Kind);
        Assert.Null(result.Observation);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EveryDenialRemainsKindOnly()
    {
        foreach (var kind in Enum.GetValues<ManagedBrowserSourceListingResultKind>().Where(kind => kind != ManagedBrowserSourceListingResultKind.Observed))
        {
            var reader = new ManagedTransferSourceReader((request, _) => Task.FromResult(ManagedBrowserResponse.For(request,
                new ManagedBrowserSourceListingResult(kind))));
            var result = await Read(reader);
            Assert.Equal(kind, result.Kind);
            Assert.Null(result.Observation);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeSendOrAfterReplyCannotPublishObservation(bool before)
    {
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        var reader = new ManagedTransferSourceReader((request, _) =>
        {
            calls++;
            cancel.Cancel();
            return Task.FromResult(ManagedBrowserResponse.For(request,
                new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.Observed, Snapshot())));
        });
        if (before) cancel.Cancel();
        var result = await Read(reader, cancel.Token);
        Assert.Equal(ManagedBrowserSourceListingResultKind.Cancelled, result.Kind);
        Assert.Null(result.Observation);
        Assert.Equal(before ? 0 : 1, calls);
    }

    [Theory]
    [InlineData(" name.apk ")]
    [InlineData(" ")]
    public async Task SameNamesKeepDistinctIdsAndOrdinalOrder(string name)
    {
        var source = Snapshot();
        var items = new[] { new ManagedBrowserSourceItem("b", ManagedBrowserSourceItemKind.File, name),
            new ManagedBrowserSourceItem("a", ManagedBrowserSourceItemKind.File, name) };
        var reader = new ManagedTransferSourceReader((request, _) => Task.FromResult(ManagedBrowserResponse.For(request,
            new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.Observed, Signed(source.BaiduUserId, source.GenerationToken, items)))));
        var result = await Read(reader);
        Assert.Equal(ManagedBrowserSourceListingResultKind.Observed, result.Kind);
        Assert.Equal(new[] { "a", "b" }, result.Observation!.Items.Select(item => item.ExactId));
        Assert.All(result.Observation.Items, item => Assert.Equal(name, item.ServerName));
    }

    [Theory]
    [InlineData(" ", "name")]
    [InlineData("id", "")]
    [InlineData("id", "\n")]
    public void CandidateProjectionStillRejectsBlankIdsAndEmptyOrControlNames(string id, string name) =>
        Assert.ThrowsAny<ArgumentException>(() => new BaiduTransferCandidateItem(id, RemoteResourceKind.File, name));

    private static ManagedBrowserSourceSnapshot Signed(string uid, string generation, IEnumerable<ManagedBrowserSourceItem> items)
    {
        var copy = items.ToArray();
        var source = new ManagedBrowserSourceSnapshot(uid, generation, copy.Length, true, true, copy, new string('0', 64));
        return new ManagedBrowserSourceSnapshot(uid, generation, copy.Length, true, true, copy, source.ComputeFingerprint(Payload().Binding.SourceUri));
    }
    private static ManagedBrowserSourceSnapshot Snapshot() => ((ManagedBrowserSourceListingResult)ManagedBrowserProtocol.DecodeResponse(
        Encoding.UTF8.GetBytes(ManagedBrowserSourceListingProtocolTests.ResponseJson())).Result).Snapshot!;
    private static ManagedBrowserReadTransferSourceRequest Payload() => (ManagedBrowserReadTransferSourceRequest)ManagedBrowserProtocol.DecodeRequest(
        Encoding.UTF8.GetBytes(ManagedBrowserSourceListingProtocolTests.Fixture()["request"]!.ToJsonString())).Payload;
    private static Task<ManagedTransferSourceReadResult> Read(ManagedTransferSourceReader reader, CancellationToken cancellation = default) =>
        reader.ReadAsync(Guid.NewGuid(), Guid.NewGuid(), 7, Payload(), cancellation);
}
