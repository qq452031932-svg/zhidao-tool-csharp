using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public static class OperationStatusChineseMapper
{
    public static string Map(OperationStatus status) => status switch
    {
        OperationStatus.Waiting => "等待",
        OperationStatus.Reserved or OperationStatus.Preparing or OperationStatus.Submitting => "处理中",
        OperationStatus.ResultPending => "结果待确认",
        OperationStatus.Succeeded => "成功",
        OperationStatus.Failed => "失败",
        OperationStatus.Paused => "暂停",
        OperationStatus.Cancelled => "已取消",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

public static class ShareOperationStatusChineseMapper
{
    public static string Map(ShareOperationStatus status) => status switch
    {
        ShareOperationStatus.WaitingTransfer => "等待转存",
        ShareOperationStatus.WaitingShare => "等待",
        ShareOperationStatus.Reserved or ShareOperationStatus.Preparing or ShareOperationStatus.Submitting => "处理中",
        ShareOperationStatus.ResultPending => "结果待确认",
        ShareOperationStatus.Succeeded => "成功",
        ShareOperationStatus.Failed => "失败",
        ShareOperationStatus.Paused => "暂停",
        ShareOperationStatus.ReusedExisting => "复用已有分享",
        ShareOperationStatus.Cancelled => "已取消",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
