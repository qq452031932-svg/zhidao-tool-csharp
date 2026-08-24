namespace BaiduBatchTransfer.Infrastructure.Database;

internal static class SqliteRetryFailedTransferSafety
{
    public static string Predicate(
        string operation,
        string batch,
        string resource,
        string allocation,
        string account,
        string output,
        string settings) => $"""
        {operation}.Status='Failed'
        AND {operation}.OutcomeKind='Failed'
        AND {operation}.SubmitState IN ('NotPrepared','PreparingToSubmit')
        AND {operation}.DailyCountedAt IS NULL
        AND {operation}.ConfirmationDescriptorJson IS NULL
        AND {operation}.SubmitStartedAt IS NULL
        AND {operation}.AdapterRequestId IS NULL
        AND {operation}.RemoteItemIdsJson IS NULL
        AND {operation}.TargetSlotState='Released'
        AND {operation}.LeaseExpiresAt IS NULL
        AND {batch}.Status IN ('Active','Queued','Paused','CompletedWithShortfall')
        AND {resource}.CandidateStatus='Expanded'
        AND {resource}.GlobalValidityStatus<>'Invalid'
        AND {resource}.ReservedAccountId IS NULL
        AND {allocation}.FailedItemCount>0
        AND {account}.Status='Active'
        AND {account}.IsEnabled=1
        AND {account}.IsDeleted=0
        AND {operation}.AttemptCount<2147483647
        AND NOT EXISTS(
          SELECT 1 FROM ShareOperations existingShare
          WHERE existingShare.TransferOperationId={operation}.Id)
        AND (
          {output}.BatchJobId IS NULL
          OR {output}.ShareEnabled=0
          OR (
            {settings}.SettingsFingerprint IS NOT NULL
            AND trim({settings}.SettingsFingerprint)<>''
            AND {output}.PlannedShareCount>0
            AND {output}.CreatedShareOperationCount<{output}.PlannedShareCount
            AND {batch}.ShareOperationCount=(
              SELECT COUNT(*) FROM ShareOperations batchShare
              WHERE batchShare.BatchJobId={batch}.Id)
          )
        )
        AND (
          {batch}.QuotaBasis<>'SuccessTarget'
          OR (
            CAST({allocation}.EffectiveTargetSuccessCount AS INTEGER)
              - {allocation}.FulfilledCount
              - {allocation}.ReservedSlotCount
              - (
                SELECT COUNT(*) FROM TransferOperations waiting
                JOIN ResourceItems waitingResource ON waitingResource.Id=waiting.ResourceItemId
                WHERE waiting.BatchJobId={operation}.BatchJobId
                  AND waiting.AssignedAccountId={operation}.AssignedAccountId
                  AND waiting.Status='Waiting'
                  AND waiting.TargetSlotState IN ('Unoccupied','Released')
                  AND waitingResource.CandidateStatus='Reserved'
                  AND waitingResource.ReservedAccountId={operation}.AssignedAccountId
              )>0
            OR {allocation}.EffectiveTargetSuccessCount<{allocation}.TargetSuccessCount
          )
        )
        """;
}
