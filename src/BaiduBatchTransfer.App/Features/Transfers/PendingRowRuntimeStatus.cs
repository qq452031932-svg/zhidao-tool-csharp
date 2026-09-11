using BaiduBatchTransfer.App.Features.Jobs;

namespace BaiduBatchTransfer.App.Features.Transfers;

public static class PendingRowRuntimeStatus
{
    public static string Evaluate(
        Guid stagedImportRowId,
        IReadOnlyList<JobResultRow> results,
        int expectedOperationsPerResource,
        bool batchIsTerminal)
    {
        var operations = results.Where(row => row.StagedImportRowId == stagedImportRowId)
            .GroupBy(row => row.TransferOperationId)
            .Select(group => group.OrderByDescending(row => row.TransferUpdatedAt)
                .ThenByDescending(row => row.ShareUpdatedAt).First()).ToArray();
        if (operations.Any(HasProblem)) return "有问题，请查看结果";
        var hasCompleteDetails = expectedOperationsPerResource > 0 && operations.Length > 0 &&
            operations.GroupBy(row => row.CandidateSequence)
                .All(group => group.Count() == expectedOperationsPerResource);
        if (batchIsTerminal && (!hasCompleteDetails || operations.Any(row => !IsComplete(row))))
            return "任务已结束，请查看结果";
        if (operations.Length == 0) return "已进入任务";
        if (operations.Any(row => !IsComplete(row))) return "处理中";
        return hasCompleteDetails ? "已完成" : "处理中";
    }

    private static bool IsComplete(JobResultRow row) => row.TransferStatus == "Succeeded" &&
        (!row.ShareEnabled || row.ShareStatus is "Succeeded" or "ReusedExisting");

    private static bool HasProblem(JobResultRow row) =>
        row.CanReconcileTransfer || row.CanReconcileShare ||
        row.TransferStatus is not ("Waiting" or "Reserved" or "Preparing" or "Submitting" or "Succeeded") ||
        (row.ShareEnabled && row.ShareStatus is not
            (null or "WaitingTransfer" or "WaitingShare" or "Reserved" or "Preparing" or "Submitting" or "Succeeded" or "ReusedExisting"));
}
