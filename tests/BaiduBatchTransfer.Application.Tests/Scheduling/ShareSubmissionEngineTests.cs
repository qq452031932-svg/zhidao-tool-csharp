using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class ShareSubmissionEngineTests
{
    [Fact]
    public async Task DiscoveryReadsAllPagesAndKeepsOnlyExactGroupedItems()
    {
        var exact = new RemoteItemIdentitySet([
            new RemoteItemIdentity("folder-a", "folder"),
            new RemoteItemIdentity("apk-b", "app.apk")]);
        var adapter = new Adapter([
            new BaiduSharePage([Share("old-unrelated", One("other"))], "next"),
            new BaiduSharePage([Share("old-exact", exact)], null)]);

        var snapshot = await new ShareSubmissionEngine().DiscoverAsync(
            adapter, Session(), new PermanentShareRequest(exact), CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(2, snapshot.Value!.FullListing.Items.Length);
        Assert.Equal("old-exact", Assert.Single(snapshot.Value.ExactMatches).ShareId);
    }

    [Fact]
    public void ConfirmationRequiresOneNewExactShareAndCompleteBaselineStillPresent()
    {
        var engine = new ShareSubmissionEngine();
        var exact = One("target");
        var baseline = WebAutomationBaseline.Create(["old"]);
        var old = Share("old", One("old-item"));
        var accepted = Share("new", exact);

        Assert.NotNull(engine.ConfirmUniqueExact(
            new PermanentShareRequest(exact), baseline, [old, accepted]));
        Assert.Null(engine.ConfirmUniqueExact(
            new PermanentShareRequest(exact), baseline, [accepted]));
        Assert.Null(engine.ConfirmUniqueExact(
            new PermanentShareRequest(exact), baseline, [old, accepted, Share("new-2", exact)]));
    }

    [Fact]
    public void ReceiptMustMatchExactRequestAndPermanentSettings()
    {
        var engine = new ShareSubmissionEngine();
        var request = new PermanentShareRequest(One("target"), "A1b2");
        var receipt = new ShareCreationReceipt(
            DateTimeOffset.UtcNow, "request", request, "share", "https://pan.baidu.com/s/share",
            new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Fixed, "A1b2"));

        Assert.True(engine.IsExactAcceptedReceipt(request, receipt));
        Assert.False(engine.IsExactAcceptedReceipt(
            new PermanentShareRequest(One("different"), "A1b2"), receipt));
    }

    [Fact]
    public void ExistingShareDecisionRequiresOneCompatibleExactShareForReuse()
    {
        var engine = new ShareSubmissionEngine();
        var request = new PermanentShareRequest(One("target"), "A1b2");
        var compatible = Share("compatible", request.RemoteItems);
        var incompatible = Share("incompatible", request.RemoteItems, "Z9y8");

        var reuse = engine.ResolveExistingShare(
            ExistingShareDecision.Reuse, request, [compatible]);
        var ambiguous = engine.ResolveExistingShare(
            ExistingShareDecision.Reuse, request, [compatible, Share("second", request.RemoteItems)]);
        var mismatched = engine.ResolveExistingShare(
            ExistingShareDecision.Reuse, request, [incompatible]);

        Assert.Equal(ExistingShareAction.Reuse, reuse.Action);
        Assert.Equal("compatible", reuse.ReusableShare!.ShareId);
        Assert.Equal(ExistingShareAction.RequiresDecision, ambiguous.Action);
        Assert.Equal(ExistingShareAction.Incompatible, mismatched.Action);
    }

    [Theory]
    [InlineData(ExistingShareDecision.Regenerate, ExistingShareAction.SubmitNew)]
    [InlineData(ExistingShareDecision.Cancel, ExistingShareAction.Cancel)]
    public void ExplicitExistingShareDecisionIsAppliedWithoutGuessing(
        ExistingShareDecision decision,
        ExistingShareAction expected)
    {
        var request = new PermanentShareRequest(One("target"), "A1b2");

        var resolution = new ShareSubmissionEngine().ResolveExistingShare(
            decision, request, [Share("existing", request.RemoteItems)]);

        Assert.Equal(expected, resolution.Action);
        Assert.Null(resolution.ReusableShare);
    }

    [Fact]
    public async Task SubmissionStateMachineRetriesDefinitelyNotAcceptedThenAcceptsExactReceipt()
    {
        var request = new PermanentShareRequest(One("target"), "A1b2");
        var events = new List<string>();
        var submissions = new Queue<BaiduAdapterResult<ShareCreationReceipt>>([
            Failure(BaiduErrorKind.TransientService, SubmissionAcceptance.DefinitelyNotAccepted),
            BaiduAdapterResult<ShareCreationReceipt>.Success(Receipt(request))]);
        var ports = Ports(events, () => submissions.Dequeue());

        var result = await new ShareSubmissionEngine().ExecuteSubmissionAsync(
            request, ports, new RetryPolicy([TimeSpan.Zero]), new ImmediateDelay(), CancellationToken.None);

        Assert.Equal(ShareSubmissionDisposition.Accepted, result.Disposition);
        Assert.Equal(1, result.CompletedRetries);
        Assert.Equal(
            ["prepare", "boundary", "submit", "dnn-1", "begin-1", "submit"],
            events);
    }

    [Fact]
    public async Task SubmissionStateMachineExhaustsTheSameBoundedDefinitelyNotAcceptedPolicy()
    {
        var request = new PermanentShareRequest(One("target"), "A1b2");
        var events = new List<string>();
        var ports = Ports(events, () => Failure(
            BaiduErrorKind.TransientService, SubmissionAcceptance.DefinitelyNotAccepted));

        var result = await new ShareSubmissionEngine().ExecuteSubmissionAsync(
            request, ports, new RetryPolicy([TimeSpan.Zero]), new ImmediateDelay(), CancellationToken.None);

        Assert.Equal(ShareSubmissionDisposition.DefinitelyNotAccepted, result.Disposition);
        Assert.Equal(1, result.CompletedRetries);
        Assert.Equal(
            ["prepare", "boundary", "submit", "dnn-1", "begin-1", "submit", "dnn-2"],
            events);
    }

    [Fact]
    public async Task SubmissionStateMachinePersistsResultPendingAfterUnknownPostBoundaryResult()
    {
        var request = new PermanentShareRequest(One("target"), "A1b2");
        var events = new List<string>();
        var ports = Ports(events, () => Failure(
            BaiduErrorKind.UnknownResponse, SubmissionAcceptance.Unknown));

        var result = await new ShareSubmissionEngine().ExecuteSubmissionAsync(
            request, ports, RetryPolicy.Default, new ImmediateDelay(), CancellationToken.None);

        Assert.Equal(ShareSubmissionDisposition.ResultPending, result.Disposition);
        Assert.Equal(["prepare", "boundary", "submit", "pending"], events);
    }

    private static RemoteItemIdentitySet One(string id) =>
        new([new RemoteItemIdentity(id, id)]);
    private static BaiduShareRecord Share(
        string id,
        RemoteItemIdentitySet items,
        string extractionCode = "A1b2") =>
        new(id, items, $"https://pan.baidu.com/s/{id}", extractionCode, true, true);
    private static ShareCreationReceipt Receipt(PermanentShareRequest request) => new(
        DateTimeOffset.UtcNow, "adapter", request, "share", "https://pan.baidu.com/s/share",
        new EffectiveShareSettings(true, true,
            request.UsesRandomExtractionCode ? ShareExtractionCodeBehavior.Random : ShareExtractionCodeBehavior.Fixed,
            request.UsesRandomExtractionCode ? "R4nD" : request.FixedExtractionCode));
    private static BaiduAdapterResult<ShareCreationReceipt> Failure(
        BaiduErrorKind kind,
        SubmissionAcceptance acceptance) =>
        BaiduAdapterResult<ShareCreationReceipt>.Failure(
            kind, adapterDiagnostic: null, submissionAcceptance: acceptance);
    private static ShareSubmissionPorts Ports(
        List<string> events,
        Func<BaiduAdapterResult<ShareCreationReceipt>> submit) => new(
        _ => { events.Add("prepare"); return Task.FromResult(BaiduAdapterResult<SharePreparationReceipt>.Success(SharePreparationReceipt.Ready)); },
        _ => { events.Add("boundary"); return Task.FromResult(true); },
        (retry, _) => { events.Add($"begin-{retry}"); return Task.FromResult(true); },
        _ => { events.Add("submit"); return Task.FromResult(submit()); },
        (attempt, _) => { events.Add($"dnn-{attempt}"); return Task.FromResult(true); },
        _ => { events.Add("pending"); return Task.FromResult(true); });
    private static AccountSession Session() => new(
        Guid.NewGuid(), "user", [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)],
        DateTimeOffset.UtcNow);

    private sealed class Adapter(IReadOnlyList<BaiduSharePage> pages) : IBaiduShareAdapter
    {
        private int index;
        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Success(pages[index++]));
    }

    private sealed class ImmediateDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
