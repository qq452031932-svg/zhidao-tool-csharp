using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class TransferRecoveryAuthorizationSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly AdapterCapabilityVersion Capability = new(
        "BaiduTransfer", "p2", "s2", "build-1");

    [Fact]
    public async Task ExactOwnedUnresolvedMutationReceivesShortLivedReadOnlyAuthorization()
    {
        var graph = Graph();
        var source = new TransferRecoveryAuthorizationSource(
            graph.Store, new Ownership(true), new FixedTimeProvider(Now));

        var authorization = await source.TryAuthorizeDirectoryMutationAsync(
            new(graph.BatchId, 2, graph.Mutation));

        Assert.NotNull(authorization);
        Assert.Equal(graph.Mutation.Generation, authorization.OriginGeneration);
        Assert.Equal(graph.Mutation.OwningAttemptRevision, authorization.OwningAttemptRevision);
        Assert.Equal(new RemotePath("/父目录"), authorization.ParentPath);
        Assert.Equal(Now.AddMinutes(2), authorization.ExpiresAtUtc);
        Assert.DoesNotContain("父目录", authorization.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongOwnershipOrForgedMutationCannotAuthorizeRecovery()
    {
        var graph = Graph();
        var denied = new TransferRecoveryAuthorizationSource(
            graph.Store, new Ownership(false), new FixedTimeProvider(Now));
        var forged = new TransferDirectoryMutationRecord(
            graph.Mutation.OperationId, graph.Mutation.AccountId, graph.Mutation.Generation,
            graph.Mutation.SegmentOrdinal, "forged-parent", null,
            graph.Mutation.CanonicalChildPath, graph.Mutation.ListingGenerationToken,
            graph.Mutation.BaiduUserId, graph.Mutation.Capability,
            graph.Mutation.State, graph.Mutation.OwningAttemptRevision);
        var source = new TransferRecoveryAuthorizationSource(
            graph.Store, new Ownership(true), new FixedTimeProvider(Now));

        Assert.Null(await denied.TryAuthorizeDirectoryMutationAsync(
            new(graph.BatchId, 2, graph.Mutation)));
        Assert.Null(await source.TryAuthorizeDirectoryMutationAsync(
            new(graph.BatchId, 2, forged)));
    }

    [Fact]
    public async Task ResultPendingSubmissionReceivesTargetOnlyReadBinding()
    {
        var operationId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var sourceEvidence = new TransferSourceEvidence(
            new RemoteItemIdentitySet([new("source", "来源")]), 1, true, true,
            "source-generation", "uid-1", Capability, new string('a', 64));
        var baseline = WebAutomationBaseline.Create(["old"]);
        var targetFingerprint = TransferTargetPreparationEvidence.ComputeObservationFingerprint(
            "target-id", new RemotePath("/目标"), 1, true, true,
            "target-generation", "uid-1", TransferTargetInventoryScope.AllItems, baseline);
        var target = new TransferTargetPreparationEvidence(
            "target-id", new RemotePath("/目标"), 1, true, true,
            "target-generation", "uid-1", TransferTargetInventoryScope.AllItems,
            baseline, targetFingerprint);
        var submission = new TransferSubmissionEvidence(
            sourceEvidence.Items.Fingerprint, target.TargetExactId, baseline.Fingerprint,
            "uid-1", Capability, Now.AddMinutes(-1));
        var attempt = new WebAutomationAttemptRecord(
            operationId, accountId, 2, WebAutomationOwnerKind.Transfer,
            WebAutomationAttemptState.ResultPending, null, baseline, targetFingerprint,
            false, 5, Now.AddMinutes(-2), Now.AddMinutes(-1),
            sourceEvidence: sourceEvidence, targetPreparationEvidence: target,
            submissionEvidence: submission);
        var source = new TransferRecoveryAuthorizationSource(
            new AttemptStore(attempt, Graph().Mutation), new Ownership(true),
            new FixedTimeProvider(Now));

        var binding = await source.TryAuthorizeSubmissionRecoveryAsync(new(
            operationId, batchId, accountId, 2,
            new RemoteDirectory("target-id", new RemotePath("/目标")),
            "uid-1", Capability));

        Assert.NotNull(binding);
        Assert.Equal(sourceEvidence.Items.Fingerprint, binding.SourceFingerprint);
        Assert.Equal(baseline.Fingerprint, binding.BaselineFingerprint);
        Assert.Equal("https://pan.baidu.com/disk/main#/all?path=%2F%E7%9B%AE%E6%A0%87",
            binding.OfficialTargetUri.AbsoluteUri);
        Assert.DoesNotContain("目标", binding.ToString(), StringComparison.Ordinal);

        Assert.Null(await source.TryAuthorizeSubmissionRecoveryAsync(new(
            operationId, batchId, accountId, 2,
            new RemoteDirectory("forged-target", new RemotePath("/目标")),
            "uid-1", Capability)));
        Assert.Null(await source.TryAuthorizeSubmissionRecoveryAsync(new(
            operationId, batchId, accountId, 2,
            new RemoteDirectory("target-id", new RemotePath("/目标")),
            "wrong-uid", Capability)));
    }

    private static GraphData Graph()
    {
        var operationId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var evidence = new TransferSourceEvidence(
            new RemoteItemIdentitySet([new("source", "来源")]), 1, true, true,
            "source-generation", "uid-1", Capability, new string('a', 64));
        var attempt = new WebAutomationAttemptRecord(
            operationId, accountId, 1, WebAutomationOwnerKind.Transfer,
            WebAutomationAttemptState.Preparing, null, null, evidence.ObservationFingerprint,
            false, 3, Now.AddMinutes(-2), Now.AddMinutes(-1), sourceEvidence: evidence);
        var mutation = new TransferDirectoryMutationRecord(
            operationId, accountId, 1, 1, "parent-id", null,
            new RemotePath("/父目录/新目录"), "listing-generation", "uid-1", Capability,
            TransferDirectoryMutationState.OutcomeUnknown, 3);
        return new(batchId, mutation, new AttemptStore(attempt, mutation));
    }

    private sealed record GraphData(
        Guid BatchId,
        TransferDirectoryMutationRecord Mutation,
        AttemptStore Store);

    private sealed class Ownership(bool allowed) : IWebAutomationRequestOwnershipStore
    {
        public Task<bool> IsBoundAsync(
            Guid operationId, Guid batchId, Guid accountId, int generation,
            WebAutomationOwnerKind ownerKind, CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed);
    }

    private sealed class AttemptStore(
        WebAutomationAttemptRecord attempt,
        TransferDirectoryMutationRecord mutation) : IWebAutomationAttemptStore
    {
        public Task<WebAutomationAttemptRecord?> GetAsync(
            Guid operationId, Guid accountId, int generation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WebAutomationAttemptRecord?>(
                operationId == attempt.OperationId && accountId == attempt.AccountId &&
                generation == attempt.Generation ? attempt : null);

        public Task<TransferDirectoryMutationRecord?> GetUnresolvedDirectoryMutationAsync(
            Guid operationId, Guid accountId, string parentExactId, string canonicalSegment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TransferDirectoryMutationRecord?>(
                operationId == mutation.OperationId && accountId == mutation.AccountId &&
                parentExactId == mutation.ParentExactId && canonicalSegment == "新目录"
                    ? mutation : null);

        public Task<bool> BeginAsync(WebAutomationAttemptRecord value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(PageQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryPersistBaselineAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationBaseline baseline, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryAdvanceAsync(Guid operationId, Guid accountId, int generation, WebAutomationAttemptState expectedState, long expectedRevision, WebAutomationAttemptState nextState, string? observationFingerprint, bool isWaitingForUser, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryResumeFromUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, WebAutomationAttemptState terminalState, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
