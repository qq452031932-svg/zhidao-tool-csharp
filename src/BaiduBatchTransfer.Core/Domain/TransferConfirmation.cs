namespace BaiduBatchTransfer.Core.Domain;

public enum TransferConfirmation
{
    ConfirmedPresent,
    ConfirmedAbsent,
    Ambiguous
}

public static class TransferConfirmationCompatibility
{
    public static TransferConfirmationResult ToExactResult(
        this TransferConfirmation confirmation,
        RemoteItemIdentitySet? exactRemoteItems = null) => confirmation switch
        {
            TransferConfirmation.ConfirmedAbsent => TransferConfirmationResult.Absent,
            TransferConfirmation.Ambiguous => TransferConfirmationResult.Ambiguous,
            TransferConfirmation.ConfirmedPresent when exactRemoteItems is not null =>
                TransferConfirmationResult.ExactPresent(exactRemoteItems),
            TransferConfirmation.ConfirmedPresent => TransferConfirmationResult.Ambiguous,
            _ => throw new ArgumentOutOfRangeException(nameof(confirmation), confirmation, "Unknown transfer confirmation."),
        };
}
