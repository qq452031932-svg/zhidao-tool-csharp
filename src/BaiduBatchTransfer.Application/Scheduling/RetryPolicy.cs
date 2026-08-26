using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record RetryDecision(bool ShouldRetry, TimeSpan Delay)
{
    public static RetryDecision Stop { get; } = new(false, TimeSpan.Zero);
}

public readonly record struct ExecutionProfileReselectionEvidence(
    WebAutomationAttemptState PreviousAttemptState,
    bool HasSourceEvidence,
    bool HasDirectoryMutation,
    bool HasShareMutation,
    bool HasSubmittingEvidence,
    bool HasResultPendingEvidence,
    bool HasRemoteSideEffectEvidence);

public sealed class RetryPolicy
{
    public static TimeSpan MaximumSupportedDelay { get; } =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    private readonly TimeSpan[] backoffs;

    public RetryPolicy(IEnumerable<TimeSpan> backoffs)
    {
        ArgumentNullException.ThrowIfNull(backoffs);
        this.backoffs = backoffs.ToArray();
        if (this.backoffs.Any(value => value < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(backoffs), "Retry delays cannot be negative.");
    }

    public static RetryPolicy Default { get; } = new(
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)]);

    public RetryDecision Decide(
        BaiduErrorKind errorKind,
        int completedRetries,
        TimeSpan? serverWait = null)
    {
        if (completedRetries < 0) throw new ArgumentOutOfRangeException(nameof(completedRetries));
        if (serverWait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(serverWait));
        if (errorKind != BaiduErrorKind.TransientService || completedRetries >= backoffs.Length)
            return RetryDecision.Stop;

        var delay = backoffs[completedRetries];
        if (serverWait is { } requested && requested > delay) delay = requested;
        if (delay > MaximumSupportedDelay) delay = MaximumSupportedDelay;
        return new RetryDecision(true, delay);
    }

    public static ExecutionProfileSnapshot SelectExecutionProfileForNextGeneration(
        ExecutionProfileSnapshot original,
        ExecutionProfileSnapshot currentDefault,
        ExecutionProfileReselectionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(currentDefault);
        var canReselect = evidence.PreviousAttemptState == WebAutomationAttemptState.DefinitelyNotSubmitted &&
            !evidence.HasSourceEvidence &&
            !evidence.HasDirectoryMutation &&
            !evidence.HasShareMutation &&
            !evidence.HasSubmittingEvidence &&
            !evidence.HasResultPendingEvidence &&
            !evidence.HasRemoteSideEffectEvidence;
        return canReselect ? currentDefault : original;
    }
}
