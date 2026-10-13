using System.Collections.ObjectModel;

namespace BaiduBatchTransfer.App.Features.Home;

public sealed record HomeFeature(string Title, string Description);

/// <summary>
/// 首页。回答"这是什么软件、能做什么、这版改了什么"。
///
/// 刻意不显示授权信息：页头常驻授权徽标，设置中心有完整的授权卡片（还带激活按钮
/// 和机器绑定状态）。同一份信息摆三个地方，用户不会看得更明白，只会在三处不一致
/// 时更困惑。
///
/// 也不做"最新公告"和"问题反馈"：两者都需要服务端接口（拉公告、收反馈），而授权
/// 服务目前只提供激活与校验。写死一段文字冒充公告，会变成永远不更新的假公告；
/// 反馈框点了提交却什么都没发出去，等于骗用户。两者都比没有更糟。
/// </summary>
public sealed class HomeWorkspaceViewModel
{
    public HomeWorkspaceViewModel(IEnumerable<ChangelogEntry>? changelog = null) =>
        Changelog = new ObservableCollection<ChangelogEntry>(changelog ?? ApplicationChangelog.Entries);

    public string ProductName => "知道综合工具";
    public string VersionDisplay => $"v{Updates.ApplicationBuildInfo.Current.ProductVersion}";
    public string Tagline => "百度知道资源采集与网盘批量管理工作台。";
    public string ContactHint => "技术支持与卡密购买请联系微信：swatfanhua";

    public IReadOnlyList<HomeFeature> Features { get; } =
    [
        new("批量转存", "导入分享链接或 Excel，按目标目录批量转存，可多账号复制或分流。"),
        new("知道采集", "按关键词精准或模糊采集网盘资源，支持表格导入、结果导出及送入批量转存。"),
        new("监控反馈", "跟踪分享链接状态，失效时能第一时间发现。"),
        new("广告插入", "在转存结果中批量插入推广内容。"),
        new("文件排除", "按规则跳过不需要的文件，避免占用容量。"),
        new("数据统计", "汇总转存与分享的执行情况。"),
        new("设置中心", "管理软件授权、检查更新和常用设置。"),
        new("账号管理", "登录并管理多个百度网盘账号，查看各账号剩余容量。"),
    ];

    public ObservableCollection<ChangelogEntry> Changelog { get; }
}
