using BaiduBatchTransfer.App.Features.Transfers;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class CurrentImportCleanupInteractionTests
{
    [Fact]
    public void ConfirmationCarriesExactRowCountAndLocalOnlyScope()
    {
        var message = WpfCurrentImportCleanupInteraction.BuildMessage(1_234);

        Assert.Contains("1234", message, StringComparison.Ordinal);
        Assert.Contains("只删除本地待处理导入", message, StringComparison.Ordinal);
        Assert.Contains("不影响百度网盘文件和分享链接", message, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfConfirmationUsesYesNoWarningAndExplicitNoDefault()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "BaiduBatchTransfer.App", "Features", "Transfers", "CurrentImportCleanupInteraction.cs"));

        Assert.Contains("MessageBoxButton.YesNo", source, StringComparison.Ordinal);
        Assert.Contains("MessageBoxImage.Warning", source, StringComparison.Ordinal);
        Assert.Contains("MessageBoxResult.No", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BaiduBatchTransfer.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
