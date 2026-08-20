namespace BaiduBatchTransfer.Core.Domain;

public enum TransferConfirmationState
{
    Absent,
    ExactPresent,
    Ambiguous,
}

public sealed class TransferConfirmationResult : IEquatable<TransferConfirmationResult>
{
    private TransferConfirmationResult(
        TransferConfirmationState state,
        RemoteItemIdentitySet? remoteItems)
    {
        if (state == TransferConfirmationState.ExactPresent && remoteItems is null)
            throw new ArgumentNullException(nameof(remoteItems));
        if (state != TransferConfirmationState.ExactPresent && remoteItems is not null)
            throw new ArgumentException("Only exact-present confirmation can carry remote items.", nameof(remoteItems));
        State = state;
        RemoteItems = remoteItems;
    }

    public static TransferConfirmationResult Absent { get; } =
        new(TransferConfirmationState.Absent, null);

    public static TransferConfirmationResult Ambiguous { get; } =
        new(TransferConfirmationState.Ambiguous, null);

    public static TransferConfirmationResult ExactPresent(RemoteItemIdentitySet remoteItems)
    {
        ArgumentNullException.ThrowIfNull(remoteItems);
        return new TransferConfirmationResult(TransferConfirmationState.ExactPresent, remoteItems);
    }

    public TransferConfirmationState State { get; }

    public RemoteItemIdentitySet? RemoteItems { get; }

    public bool Equals(TransferConfirmationResult? other) =>
        other is not null && State == other.State && Equals(RemoteItems, other.RemoteItems);

    public override bool Equals(object? obj) => Equals(obj as TransferConfirmationResult);

    public override int GetHashCode() => HashCode.Combine(State, RemoteItems);

    public override string ToString() =>
        $"TransferConfirmationResult {{ State = {State}, RemoteItems = [REDACTED] }}";
}
