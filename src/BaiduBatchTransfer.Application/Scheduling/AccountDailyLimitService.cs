namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record SubmissionBoundaryCommand(
    Guid OperationId,
    Guid AccountId,
    string ConfirmationDescriptorJson,
    DateTimeOffset LeaseExpiresAt,
    int? ExpectedAttemptCount = null)
{
    public override string ToString() =>
        "SubmissionBoundaryCommand { EntityIds = [REDACTED], ConfirmationDescriptorJson = [REDACTED] }";
}

public sealed record SubmissionBoundaryPersistence(
    SubmissionBoundaryCommand Command,
    string LocalDateKey,
    DateTimeOffset PersistedAt)
{
    public override string ToString() =>
        $"SubmissionBoundaryPersistence {{ Command = [REDACTED], LocalDateKey = {LocalDateKey}, PersistedAt = {PersistedAt:O} }}";
}

public sealed record SubmissionBoundaryResult(
    bool Accepted,
    bool WasNewlyCounted,
    int DailyExecutedCount);

public interface ISubmissionBoundaryStore
{
    Task<SubmissionBoundaryResult> PersistAsync(
        SubmissionBoundaryPersistence persistence,
        CancellationToken cancellationToken = default);
}

public sealed class AccountDailyLimitService
{
    private readonly ISubmissionBoundaryStore store;
    private readonly TimeProvider timeProvider;

    public AccountDailyLimitService(ISubmissionBoundaryStore store, TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<SubmissionBoundaryResult> PersistFirstSubmissionAsync(
        SubmissionBoundaryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.OperationId == Guid.Empty || command.AccountId == Guid.Empty)
            throw new ArgumentException("Operation and account IDs are required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.ConfirmationDescriptorJson))
            throw new ArgumentException("A confirmation descriptor is required.", nameof(command));

        var now = timeProvider.GetLocalNow();
        var dateKey = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return store.PersistAsync(
            new SubmissionBoundaryPersistence(command, dateKey, now), cancellationToken);
    }
}
