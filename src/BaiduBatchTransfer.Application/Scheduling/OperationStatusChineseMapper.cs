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
