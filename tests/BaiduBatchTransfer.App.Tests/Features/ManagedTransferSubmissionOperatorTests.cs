using System.IO;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferSubmissionOperatorTests
{
    [Fact]
    public async Task ReadinessSubmitAndOutcomeUseOnlyTheirTypedFixedActions()
    {
        var calls = new List<ManagedBrowserRequest>();
        var context = Context();
        var readiness = new ManagedBrowserTransferSubmissionReadiness(context, 1);
        var official = ManagedBrowserTransferSubmissionOutcomeResult.CreateAcceptedBound(
            "request-1", context.SelectedTargetExactId, context.SourceExactId, "destination-1");
        var sut = new ManagedTransferSubmissionOperator((request, _) =>
        {
            calls.Add(request);
            ManagedBrowserResult result = request.Action switch
            {
                ManagedBrowserAction.ObserveTransferSubmissionReady =>
                    new ManagedBrowserTransferSubmissionReadinessResult(
                        ManagedBrowserTransferSubmissionReadinessResultKind.Ready, readiness),
                ManagedBrowserAction.SubmitTransferOnce =>
                    new ManagedBrowserSubmitTransferOnceResult(
                        ManagedBrowserTransferSubmitResultKind.ClickAttempted,
                        ManagedBrowserTransferSubmitDispatchState.ClickDispatched),
                ManagedBrowserAction.ObserveTransferSubmissionOutcome => official,
                _ => throw new InvalidOperationException("unexpected fixed action"),
            };
            return Task.FromResult(ManagedBrowserResponse.For(request, result));
        });

        var ready = await sut.ObserveReadyAsync(OperationId, AccountId, 7, context);
        var submitted = await sut.SubmitOnceAsync(OperationId, AccountId, 7, readiness);
        var outcome = await sut.ObserveOutcomeAsync(OperationId, AccountId, 7, readiness);

        Assert.Equal(ManagedBrowserTransferSubmissionReadinessResultKind.Ready, ready.Kind);
        Assert.Same(readiness, ready.Readiness);
        Assert.Equal(ManagedBrowserTransferSubmitResultKind.ClickAttempted, submitted.Kind);
        Assert.Equal(ManagedBrowserTransferSubmitDispatchState.ClickDispatched, submitted.DispatchState);
        Assert.False(submitted.IsSuccess);
        Assert.Equal(ManagedBrowserTransferOutcomeKind.AcceptedBound, outcome.Kind);
        Assert.Same(official, outcome.OfficialOutcome);
        Assert.Equal(new[]
        {
            ManagedBrowserAction.ObserveTransferSubmissionReady,
            ManagedBrowserAction.SubmitTransferOnce,
            ManagedBrowserAction.ObserveTransferSubmissionOutcome,
        }, calls.Select(request => request.Action));
        Assert.All(calls, request =>
        {
            Assert.Equal(OperationId, request.OperationId);
            Assert.Equal(AccountId, request.AccountId);
            Assert.Equal(7, request.Generation);
        });
        Assert.IsType<ManagedBrowserObserveTransferSubmissionReadyRequest>(calls[0].Payload);
        Assert.IsType<ManagedBrowserSubmitTransferOnceRequest>(calls[1].Payload);
        Assert.IsType<ManagedBrowserObserveTransferSubmissionOutcomeRequest>(calls[2].Payload);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("operation")]
    [InlineData("account")]
    [InlineData("generation")]
    [InlineData("action")]
    public async Task ResponseIdentityDriftFailsClosedWithoutRetry(string drift)
    {
        var calls = 0;
        var context = Context();
        var sut = new ManagedTransferSubmissionOperator((request, _) =>
        {
            calls++;
            return Task.FromResult(new ManagedBrowserResponse(
                drift == "request" ? Guid.NewGuid() : request.CorrelationId,
                drift == "operation" ? Guid.NewGuid() : request.OperationId,
                drift == "account" ? Guid.NewGuid() : request.AccountId,
                drift == "generation" ? request.Generation + 1 : request.Generation,
                drift == "action" ? ManagedBrowserAction.Health : request.Action,
                new ManagedBrowserTransferSubmissionReadinessResult(
                    ManagedBrowserTransferSubmissionReadinessResultKind.Ready,
                    new ManagedBrowserTransferSubmissionReadiness(context, 1))));
        });

        var result = await sut.ObserveReadyAsync(OperationId, AccountId, 7, context);

        Assert.Equal(ManagedBrowserTransferSubmissionReadinessResultKind.PageNotValidated, result.Kind);
        Assert.Null(result.Readiness);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReadinessResponseTimeoutDriftFailsClosedWithoutRetry()
    {
        var calls = 0;
        var requested = Context(timeout: 1_000);
        var changed = Context(timeout: 999);
        var changedReadiness = new ManagedBrowserTransferSubmissionReadiness(changed, 1);
        var sut = new ManagedTransferSubmissionOperator((request, _) =>
        {
            calls++;
            return Task.FromResult(ManagedBrowserResponse.For(request,
                new ManagedBrowserTransferSubmissionReadinessResult(
                    ManagedBrowserTransferSubmissionReadinessResultKind.Ready, changedReadiness)));
        });

        var result = await sut.ObserveReadyAsync(OperationId, AccountId, 7, requested);

        Assert.Equal(ManagedBrowserTransferSubmissionReadinessResultKind.PageNotValidated, result.Kind);
        Assert.Null(result.Readiness);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SubmitTransportFailureAfterSingleSendIsOutcomeUnknownAndNeverSuccess()
    {
        var calls = 0;
        var readiness = new ManagedBrowserTransferSubmissionReadiness(Context(), 1);
        var sut = new ManagedTransferSubmissionOperator((_, _) =>
        {
            calls++;
            throw new IOException("private transport context");
        });

        var result = await sut.SubmitOnceAsync(OperationId, AccountId, 7, readiness);

        Assert.Equal(ManagedBrowserTransferSubmitResultKind.BrowserUnavailable, result.Kind);
        Assert.Equal(ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown, result.DispatchState);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("private", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrecancelledSubmitIsKnownNotDispatchedAndSendsNothing()
    {
        var calls = 0;
        var readiness = new ManagedBrowserTransferSubmissionReadiness(Context(), 1);
        var sut = new ManagedTransferSubmissionOperator((_, _) =>
        {
            calls++;
            throw new InvalidOperationException();
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await sut.SubmitOnceAsync(OperationId, AccountId, 7, readiness, cancelled.Token);

        Assert.Equal(ManagedBrowserTransferSubmitResultKind.Cancelled, result.Kind);
        Assert.Equal(ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched, result.DispatchState);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ReadOnlyFailuresRemainTypedRedactedAndSingleSend()
    {
        var calls = 0;
        var context = Context();
        var readiness = new ManagedBrowserTransferSubmissionReadiness(context, 1);
        var sut = new ManagedTransferSubmissionOperator((_, _) =>
        {
            calls++;
            throw new IOException("private source url and exact id");
        });

        var ready = await sut.ObserveReadyAsync(OperationId, AccountId, 7, context);
        var outcome = await sut.ObserveOutcomeAsync(OperationId, AccountId, 7, readiness);

        Assert.Equal(ManagedBrowserTransferSubmissionReadinessResultKind.BrowserUnavailable, ready.Kind);
        Assert.Equal(ManagedBrowserTransferOutcomeKind.BrowserUnavailable, outcome.Kind);
        Assert.Null(outcome.OfficialOutcome);
        Assert.Equal(2, calls);
        Assert.DoesNotContain(context.SourceUri, ready.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(context.SourceExactId, outcome.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedPrivateHelperFailureIsContainedAndRedacted()
    {
        var readiness = new ManagedBrowserTransferSubmissionReadiness(Context(), 1);
        var sut = new ManagedTransferSubmissionOperator((_, _) =>
            throw new Exception("private unexpected helper content"));

        var ready = await sut.ObserveReadyAsync(OperationId, AccountId, 7, readiness.Context);
        var submit = await sut.SubmitOnceAsync(OperationId, AccountId, 7, readiness);
        var outcome = await sut.ObserveOutcomeAsync(OperationId, AccountId, 7, readiness);

        Assert.Equal(ManagedBrowserTransferSubmissionReadinessResultKind.BrowserUnavailable, ready.Kind);
        Assert.Equal(ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown, submit.DispatchState);
        Assert.Equal(ManagedBrowserTransferOutcomeKind.BrowserUnavailable, outcome.Kind);
        Assert.DoesNotContain("private", ready.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", submit.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", outcome.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static ManagedBrowserTransferSubmissionContext Context(int timeout = 1_000) => new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"), "target-1",
        new Uri("http://127.0.0.1:43127/"), timeout,
        "https://pan.baidu.com/s/source_1?pwd=A1b2", "uid-1", "source-generation-1",
        new string('a', 64), new string('b', 64), "source-1", ManagedBrowserSourceItemKind.File,
        "selected-target-1", "/prepared", new string('c', 64), DuplicatePolicy.Skip);

    private static readonly Guid OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
}
