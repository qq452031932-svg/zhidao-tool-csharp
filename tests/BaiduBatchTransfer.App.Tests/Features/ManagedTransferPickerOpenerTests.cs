using System.Collections.Immutable;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferPickerOpenerTests
{
    [Fact]
    public async Task OpensOnceUnderPermitThenPublishesVerifiedPicker()
    {
        var f = new Fixture();
        var result = await f.Run();
        Assert.Equal(ManagedBrowserTransferPickerResultKind.PickerOpened, result.Kind);
        Assert.Equal(BaiduCandidatePageState.DestinationPicker, result.Observation!.PageState);
        Assert.Equal(1, f.Authorization.Acquired);
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.Equal(new[] { ManagedBrowserAction.OpenTransferPicker, ManagedBrowserAction.ReadTransferDirectory },
            f.Requests.Select(r => r.Action));
        Assert.All(f.Requests, r => Assert.Equal(f.Request.Authorization.OperationId, r.OperationId));
        Assert.DoesNotContain(f.Source.BaiduUserId, result.ToString());
    }

    [Fact]
    public async Task SubmissionStageExactSourceProofOpensPickerUnderItsOwnPermit()
    {
        var f = new Fixture();
        f.SetAuthorizationStage(AdapterWriteAction.SubmitTransfer);

        var result = await f.RunSubmission();

        Assert.Equal(ManagedBrowserTransferPickerResultKind.PickerOpened, result.Kind);
        Assert.Equal(1, f.Authorization.Acquired);
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.Equal(1, f.Authorization.MaximumActive);
        Assert.Equal(new[] { ManagedBrowserAction.OpenTransferPicker, ManagedBrowserAction.ReadTransferDirectory },
            f.Requests.Select(request => request.Action));
    }

    [Fact]
    public async Task SubmitTransferBindingCannotSubstituteForPreparationPickerProof()
    {
        var f = new Fixture();
        f.SetAuthorizationStage(AdapterWriteAction.SubmitTransfer);

        var result = await f.Run();

        Assert.Equal(ManagedBrowserTransferPickerResultKind.PageNotValidated, result.Kind);
        Assert.Equal(0, f.Authorization.Acquired);
        Assert.Equal(0, f.Authorization.Disposed);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task EnsureDirectoryBindingCannotSubstituteForSubmissionPickerProof()
    {
        var f = new Fixture();

        var result = await f.RunSubmission();

        Assert.Equal(ManagedBrowserTransferPickerResultKind.PageNotValidated, result.Kind);
        Assert.Equal(0, f.Authorization.Acquired);
        Assert.Equal(0, f.Authorization.Disposed);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("source")]
    [InlineData("ids")]
    public async Task SubmissionPickerRejectsRecoveryAndStaleSourceProof(string change)
    {
        var f = new Fixture();
        f.SetAuthorizationStage(AdapterWriteAction.SubmitTransfer);
        var auth = f.Request.Authorization with { IsRecovery = change == "recovery" };
        var read = f.Request.ReadBinding;
        if (change == "source")
            read = new(new Uri("https://pan.baidu.com/s/other"), read.ExpectedPageState,
                read.ExpectedBaiduUserId, read.ExpectedExactIds, null, null);
        if (change == "ids")
            read = new(read.OfficialUri, read.ExpectedPageState,
                read.ExpectedBaiduUserId, ["other"], null, null);
        f.Request = new(auth, f.Request.Binding, f.Request.Action, read, null);

        var result = await f.RunSubmission();

        Assert.Equal(ManagedBrowserTransferPickerResultKind.PageNotValidated, result.Kind);
        Assert.Equal(0, f.Authorization.Acquired);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future-issued")]
    public async Task SubmissionStageStaleBindingCannotAcquirePickerPermit(string change)
    {
        var f = new Fixture();
        f.SetAuthorizationStage(AdapterWriteAction.SubmitTransfer);
        var now = DateTimeOffset.UtcNow;
        f.SetBindingWindow(change == "future-issued" ? now.AddMinutes(1) : now.AddMinutes(-2),
            change == "expired" ? now.AddMinutes(-1) : now.AddMinutes(2));

        var result = await f.RunSubmission();

        Assert.Equal(ManagedBrowserTransferPickerResultKind.PageNotValidated, result.Kind);
        Assert.Equal(0, f.Authorization.Acquired);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task MissingPermitCannotSendAnyAction()
    {
        var f = new Fixture(); f.Authorization.Allow = false;
        Assert.Equal(ManagedBrowserTransferPickerResultKind.PageNotValidated, (await f.Run()).Kind);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task FailedPostPermitRevalidationDisposesPermitWithoutDispatch()
    {
        var f = new Fixture
        {
            PreDispatchValidation = _ => Task.FromResult(false),
        };

        var result = await f.Run();

        Assert.Equal(ManagedBrowserTransferPickerResultKind.PageNotValidated, result.Kind);
        Assert.Null(result.Observation);
        Assert.Empty(f.Requests);
        Assert.Equal(1, f.Authorization.Acquired);
        Assert.Equal(1, f.Authorization.Disposed);
    }

    [Fact]
    public async Task CancellationDuringPostPermitRevalidationDisposesPermitWithoutDispatch()
    {
        var f = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.PreDispatchValidation = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        };
        using var cancellation = new CancellationTokenSource();
        var run = f.Run(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var result = await run;

        Assert.Equal(ManagedBrowserTransferPickerResultKind.Cancelled, result.Kind);
        Assert.Null(result.Observation);
        Assert.Empty(f.Requests);
        Assert.Equal(1, f.Authorization.Acquired);
        Assert.Equal(1, f.Authorization.Disposed);
    }

    [Theory]
    [InlineData("uid")] [InlineData("fingerprint")] [InlineData("name")] [InlineData("generation")]
    [InlineData("incomplete")] [InlineData("parent")] [InlineData("state")] [InlineData("ids")]
    [InlineData("action")] [InlineData("recovery")] [InlineData("stage")] [InlineData("source")]
    public async Task InvalidEvidenceOrAuthorizationNeverAcquiresPermit(string change)
    {
        var f = new Fixture();
        f.Observation = change switch
        {
            "uid" => f.Observation with { BaiduUserId = "other" },
            "fingerprint" => f.Observation with { ObservationToken = new string('0', 64) },
            "name" => f.Observation with { Items = [new(f.Source.Items[0].ExactId, RemoteResourceKind.File, "changed")] },
            "generation" => f.Observation with { GenerationToken = "changed" },
            "incomplete" => f.Observation with { IsComplete = false },
            "parent" => f.Observation with { ParentExactId = "parent" },
            "state" => f.Observation with { PageState = BaiduCandidatePageState.OwnDrive },
            _ => f.Observation,
        };
        if (change is "action" or "recovery" or "stage" or "source" or "ids")
        {
            var read = f.Request.ReadBinding;
            if (change == "source") read = new(new("https://pan.baidu.com/s/other"), read.ExpectedPageState,
                read.ExpectedBaiduUserId, read.ExpectedExactIds, null, null);
            if (change == "ids") read = new(read.OfficialUri, read.ExpectedPageState,
                read.ExpectedBaiduUserId, ["other"], null, null);
            var auth = f.Request.Authorization with
            {
                IsRecovery = change == "recovery",
                Action = change == "stage" ? AdapterWriteAction.SubmitShare : AdapterWriteAction.EnsureDirectory,
            };
            f.Request = new(auth, f.Request.Binding,
                change == "action" ? BaiduAutomationAction.SubmitExtraction : BaiduAutomationAction.OpenDestinationPicker,
                read, null);
        }
        Assert.Equal(ManagedBrowserTransferPickerResultKind.PageNotValidated, (await f.Run()).Kind);
        Assert.Equal(0, f.Authorization.Acquired);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AfterDispatchUncertainResultNeverReadsOrRetries(bool corrupt)
    {
        var f = new Fixture();
        f.OnSend = request => Task.FromResult(corrupt
            ? new ManagedBrowserResponse(Guid.NewGuid(), request.OperationId, request.AccountId, request.Generation,
                request.Action, new ManagedBrowserTransferPickerResult(ManagedBrowserTransferPickerResultKind.PickerOpened))
            : ManagedBrowserResponse.For(request, new ManagedBrowserTransferPickerResult(ManagedBrowserTransferPickerResultKind.PageNotValidated)));
        Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown, (await f.Run()).Kind);
        Assert.Single(f.Requests);
        Assert.Equal(1, f.Authorization.Disposed);
    }

    [Fact]
    public async Task DirectoryReadFailureAfterClickIsUncertainNotOpened()
    {
        var f = new Fixture(); f.FailRead = true;
        var result = await f.Run();
        Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown, result.Kind);
        Assert.Null(result.Observation);
        Assert.Equal(2, f.Requests.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationDoesNotReleasePermitOrCallerAccountFenceWhileSendPending(bool fault)
    {
        var f = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OnSend = _ => { started.SetResult(); return release.Task; };
        using var cancellation = new CancellationTokenSource();
        using var accountFence = new SemaphoreSlim(1, 1);
        async Task<ManagedTransferPickerOpenResult> Caller()
        {
            await accountFence.WaitAsync();
            try { return await f.Run(cancellation.Token); }
            finally { accountFence.Release(); }
        }
        var pending = Caller();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Task.Delay(30); // Let any incorrectly detached cancellation continuation run while send stays blocked.
        Assert.False(pending.IsCompleted);
        Assert.False(accountFence.Wait(0));
        Assert.Equal(0, f.Authorization.Disposed);
        if (fault) release.SetException(new IOException("private content must not escape"));
        else release.SetResult(ManagedBrowserResponse.For(f.Requests[0],
            new ManagedBrowserTransferPickerResult(ManagedBrowserTransferPickerResultKind.PickerOpened)));
        Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown, (await pending).Kind);
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.Single(f.Requests);
        // Test fixture releases only; production caller must quarantine on OutcomeUnknown.
        Assert.True(accountFence.Wait(0));
    }

    [Fact]
    public async Task DeadlineDoesNotReleasePendingMutationOrPublishLateSuccess()
    {
        var f = new Fixture(timeout: 250);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OnSend = _ => { started.SetResult(); return release.Task; };
        var pending = f.Run();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(400);
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, f.Authorization.Disposed);
            release.SetResult(ManagedBrowserResponse.For(f.Requests[0],
                new ManagedBrowserTransferPickerResult(ManagedBrowserTransferPickerResultKind.PickerOpened)));
            var result = await pending;
            Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown, result.Kind);
            Assert.Null(result.Observation);
            Assert.Single(f.Requests);
        }
        finally { release.TrySetException(new IOException("synthetic settlement")); }
    }

    [Fact]
    public async Task CancellationBeforeDispatchDoesNotAcquirePermit()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal(ManagedBrowserTransferPickerResultKind.Cancelled, (await f.Run(cancellation.Token)).Kind);
        Assert.Equal(0, f.Authorization.Acquired);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task PermitReleaseFailureIsRedactedAndCannotPublishOpened()
    {
        var f = new Fixture(); f.Authorization.ThrowOnDispose = true;
        var result = await f.Run();
        Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown, result.Kind);
        Assert.Null(result.Observation);
        Assert.DoesNotContain("private", result.ToString());
    }

    [Fact]
    public async Task CancellationDuringPermitReleaseCannotPublishLateSuccess()
    {
        var f = new Fixture();
        f.Authorization.ReleaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var pending = f.Run(cancellation.Token);
        await f.Authorization.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        Assert.False(pending.IsCompleted);
        f.Authorization.ReleaseGate.SetResult();
        var result = await pending;
        Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown, result.Kind);
        Assert.Null(result.Observation);
    }

    private sealed class Fixture
    {
        internal readonly ManagedBrowserOpenTransferSourceRequest Binding;
        internal readonly ManagedBrowserSourceSnapshot Source;
        internal readonly Authorization Authorization = new();
        internal BaiduCandidateDomMutationRequest Request;
        internal BaiduTransferCandidateObservation Observation;
        internal readonly List<ManagedBrowserRequest> Requests = [];
        internal Func<ManagedBrowserRequest, Task<ManagedBrowserResponse>>? OnSend;
        internal Func<CancellationToken, Task<bool>>? PreDispatchValidation;
        internal bool FailRead;
        internal Fixture(int timeout = 5000)
        {
            var json = ManagedBrowserTransferPickerTests.Request();
            json["payload"]!["timeoutMilliseconds"] = timeout;
            var payload = (ManagedBrowserOpenTransferPickerRequest)ManagedBrowserProtocol.DecodeRequest(
                ManagedBrowserTransferPickerTests.Bytes(json)).Payload;
            Binding = payload.Binding; Source = payload.ExpectedSource;
            var capability = new AdapterCapabilityVersion("BaiduTransfer", "protocol", "selectors", "synthetic-build");
            var auth = new AdapterWriteAuthorizationRequest(capability, Guid.NewGuid(), Guid.NewGuid(), 7,
                Guid.NewGuid(), AdapterWriteAction.EnsureDirectory);
            Request = new(auth, new(AdapterWriteAuthorizationSource.ConsumedLease, capability, auth.AccountId,
                auth.OperationId, auth.Generation, auth.BatchId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), false, null),
                BaiduAutomationAction.OpenDestinationPicker,
                new(new(Binding.SourceUri), BaiduCandidatePageState.ExternalShare, Source.BaiduUserId,
                    Source.Items.Select(i => i.ExactId), null, null), null);
            Observation = new(BaiduCandidatePageState.ExternalShare, Source.BaiduUserId, null, null, [],
                Source.Items.Select(i => new BaiduTransferCandidateItem(i.ExactId,
                    i.Kind == ManagedBrowserSourceItemKind.File ? RemoteResourceKind.File : RemoteResourceKind.Folder,
                    i.ServerName)).ToImmutableArray(), Source.TotalCount, true, true, true, Source.GenerationToken, Source.ObservationFingerprint);
        }
        internal Task<ManagedTransferPickerOpenResult> Run(CancellationToken token = default) =>
            new ManagedTransferPickerOpener(Send, Authorization, PreDispatchValidation).OpenAsync(Request, Binding, Observation, token);
        internal Task<ManagedTransferPickerOpenResult> RunSubmission(CancellationToken token = default) =>
            new ManagedTransferPickerOpener(Send, Authorization, PreDispatchValidation)
                .OpenForSubmissionAsync(Request, Binding, Observation, token);
        internal void SetAuthorizationStage(AdapterWriteAction action)
        {
            Request = new(Request.Authorization with { Action = action }, Request.Binding, Request.Action,
                Request.ReadBinding, Request.TypedValue, Request.TargetExactId);
        }
        internal void SetBindingWindow(DateTimeOffset issuedAtUtc, DateTimeOffset expiresAtUtc)
        {
            var current = Request.Binding;
            var replacement = new AdapterWriteAuthorizationBinding(current.Source, current.Capability,
                current.AccountId, current.OperationId, current.Generation, current.BatchId,
                issuedAtUtc, expiresAtUtc, current.IsRecovery, current.FormalEvidenceFingerprint);
            Request = new(Request.Authorization, replacement, Request.Action, Request.ReadBinding,
                Request.TypedValue, Request.TargetExactId);
        }
        private async Task<ManagedBrowserResponse> Send(ManagedBrowserRequest request, CancellationToken token)
        {
            Requests.Add(request);
            Assert.Equal(0, Authorization.Disposed);
            if (OnSend is not null) return await OnSend(request);
            if (request.Action == ManagedBrowserAction.OpenTransferPicker) return ManagedBrowserResponse.For(request,
                new ManagedBrowserTransferPickerResult(ManagedBrowserTransferPickerResultKind.PickerOpened));
            if (FailRead) return ManagedBrowserResponse.For(request,
                new ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind.PageNotValidated));
            var directory = new ManagedBrowserDirectorySnapshot(Source.BaiduUserId, "directory-gen", "root", "/",
                ["root"], 0, true, true, [], new string('0', 64));
            directory = new(Source.BaiduUserId, "directory-gen", "root", "/", ["root"], 0, true, true, [],
                directory.ComputeFingerprint(Binding.SourceUri));
            return ManagedBrowserResponse.For(request, new ManagedBrowserDirectoryListingResult(
                ManagedBrowserDirectoryListingResultKind.Observed, directory));
        }
    }

    private sealed class Authorization : IBaiduCandidateActionAuthorizationSource
    {
        internal bool Allow = true;
        internal bool ThrowOnDispose;
        internal TaskCompletionSource? ReleaseGate;
        internal readonly TaskCompletionSource ReleaseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Acquired, Disposed, Active, MaximumActive;
        public ValueTask<bool> IsAuthorizedAsync(BaiduCandidateDomRequest request, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireMutationPermitAsync(BaiduCandidateDomRequest request, CancellationToken token) =>
            ValueTask.FromResult<IBaiduCandidateMutationPermit?>(null);
        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireTransferMutationPermitAsync(BaiduCandidateDomMutationRequest request, CancellationToken token)
        {
            Acquired++;
            if (!Allow) return ValueTask.FromResult<IBaiduCandidateMutationPermit?>(null);
            Active++;
            MaximumActive = Math.Max(MaximumActive, Active);
            return ValueTask.FromResult<IBaiduCandidateMutationPermit?>(new Permit(this));
        }
        private sealed class Permit(Authorization owner) : IBaiduCandidateMutationPermit
        {
            public async ValueTask DisposeAsync()
            {
                owner.Disposed++;
                owner.Active--;
                owner.ReleaseStarted.TrySetResult();
                if (owner.ReleaseGate is not null) await owner.ReleaseGate.Task;
                if (owner.ThrowOnDispose) throw new IOException("private permission context");
            }
        }
    }
}
