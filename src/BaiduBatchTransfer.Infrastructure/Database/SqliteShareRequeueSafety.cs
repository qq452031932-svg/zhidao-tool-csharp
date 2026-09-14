namespace BaiduBatchTransfer.Infrastructure.Database;

internal static class SqliteShareRequeueSafety
{
    // The caller supplies the Failed/Paused status and, for mutation, snapshot fences.
    public static string Predicate(string operation) => $"""
        ({operation}.Status<>'Paused' OR COALESCE({operation}.LastErrorCode,'') NOT IN (
          'AwaitingExistingShareDecision','CancelledByUser','IncompatibleExistingShare'))
        AND ({operation}.SubmitStartedAt IS NULL OR {operation}.SubmissionAcceptance='DefinitelyNotAccepted')
        AND EXISTS(SELECT 1 FROM TransferOperations transfer
          WHERE transfer.Id={operation}.TransferOperationId
            AND transfer.Status='Succeeded' AND transfer.SubmitState='ResultReceived'
            AND transfer.RemoteItemIdsJson IS NOT NULL AND length(trim(transfer.RemoteItemIdsJson))>2)
        AND EXISTS(SELECT 1 FROM ShareOperationGenerations lineage
          WHERE lineage.ShareOperationId={operation}.Id AND lineage.Generation={operation}.Generation
            AND lineage.ExactItemCount>0
            AND lineage.ExactItemCount=(SELECT COUNT(*) FROM ShareOperationRemoteItems remote
              WHERE remote.ShareOperationId={operation}.Id))
        AND EXISTS(SELECT 1 FROM BatchJobs batch
          WHERE batch.Id={operation}.BatchJobId
            AND batch.Status IN ('Active','CompletedWithShortfall') AND batch.AcceptanceOnly=0)
        AND EXISTS(SELECT 1 FROM Accounts account
          WHERE account.Id={operation}.AccountId AND account.Status='Active'
            AND account.IsEnabled=1 AND account.IsDeleted=0)
        AND EXISTS(SELECT 1 FROM BatchOutputAccounts output
          WHERE output.BatchJobId={operation}.BatchJobId
            AND output.AccountId={operation}.AccountId
            AND output.OutputOrdinal={operation}.OutputOrdinal AND output.ShareEnabled=1)
        AND EXISTS(SELECT 1 FROM BatchShareSettings settings
          WHERE settings.BatchJobId={operation}.BatchJobId
            AND settings.SettingsFingerprint={operation}.SettingsFingerprint)
        AND NOT EXISTS(SELECT 1 FROM ShareOperations active
          WHERE active.Id<>{operation}.Id AND active.AccountId={operation}.AccountId
            AND active.ExactItemSetFingerprint={operation}.ExactItemSetFingerprint
            AND active.SettingsFingerprint={operation}.SettingsFingerprint
            AND active.Generation={operation}.Generation+1
            AND active.Status IN ('WaitingShare','Reserved','Preparing','Submitting','ResultPending','Paused'))
        """;
}
