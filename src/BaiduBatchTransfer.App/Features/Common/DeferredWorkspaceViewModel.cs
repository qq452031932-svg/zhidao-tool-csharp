namespace BaiduBatchTransfer.App.Features.Common;

public sealed class DeferredWorkspaceViewModel
{
    public DeferredWorkspaceViewModel(string moduleName, string statusMessage)
    {
        ModuleName = string.IsNullOrWhiteSpace(moduleName)
            ? throw new ArgumentException("模块名称不能为空。", nameof(moduleName))
            : moduleName;
        StatusMessage = string.IsNullOrWhiteSpace(statusMessage)
            ? throw new ArgumentException("状态说明不能为空。", nameof(statusMessage))
            : statusMessage;
    }

    public string ModuleName { get; }
    public string StatusMessage { get; }
}
