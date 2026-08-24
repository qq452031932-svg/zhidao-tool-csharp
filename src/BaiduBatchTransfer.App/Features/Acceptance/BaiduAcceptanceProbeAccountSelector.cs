using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed record BaiduAcceptanceProbeAccountSelection(
    bool CanOpen,
    Guid? AccountId,
    string? ProfilePath,
    string SafeMessage)
{
    public override string ToString() =>
        $"BaiduAcceptanceProbeAccountSelection {{ CanOpen = {CanOpen}, PrivateData = [REDACTED] }}";
}

public interface IBaiduAcceptanceProbeAccountSelector
{
    BaiduAcceptanceProbeAccountSelection Select(IEnumerable<AccountRecord> accounts);
}

public sealed class BaiduAcceptanceProbeAccountSelector : IBaiduAcceptanceProbeAccountSelector
{
    public BaiduAcceptanceProbeAccountSelection Select(IEnumerable<AccountRecord> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var eligible = accounts
            .Where(account => account.Status == "Active" && account.IsEnabled && !account.IsDeleted &&
                !string.IsNullOrWhiteSpace(account.WebView2ProfileDirectory))
            .Take(2)
            .ToArray();
        return eligible.Length == 1
            ? new(true, eligible[0].Id, eligible[0].WebView2ProfileDirectory, "已找到唯一可用的现场验收账号。")
            : new(false, null, null, "现场验收模式要求本机恰好有一个启用且已登录的账号。请先调整账号状态。 ");
    }
}
