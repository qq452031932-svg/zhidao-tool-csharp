using BaiduBatchTransfer.App.Features.Common;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WorkspaceLogBufferTests
{
    [Fact]
    public void SanitizesOnlyAtTheEntryConstructionBoundary()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Common", "WorkspaceLogEntry.cs"));

        Assert.Equal(1, CountOccurrences(source, "SanitizeForEntry(message.Trim())"));
        Assert.Contains("string.Equals(lastAppendedEntry.Message, entry.Message", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectEntryConstructionAndToStringCannotLeakRawMessageContent()
    {
        var entry = new WorkspaceLogEntry(
            new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero),
            WorkspaceLogLevel.Information,
            "token=direct-construction-secret");

        Assert.DoesNotContain("direct-construction-secret", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("direct-construction-secret", entry.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(entry.Message, entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExposesTheStoredUtcTimestampInLocalTimeForDisplay()
    {
        var timestamp = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
        var entry = new WorkspaceLogEntry(timestamp, WorkspaceLogLevel.Information, "正在处理");

        Assert.Equal(timestamp, entry.Timestamp);
        Assert.Equal(timestamp.ToLocalTime(), entry.LocalTimestamp);
    }

    [Theory]
    [InlineData(WorkspaceLogLevel.Information, "信息")]
    [InlineData(WorkspaceLogLevel.Success, "成功")]
    [InlineData(WorkspaceLogLevel.Warning, "警告")]
    [InlineData(WorkspaceLogLevel.Error, "错误")]
    public void ExposesChineseDisplayTextForEveryLevel(WorkspaceLogLevel level, string expected)
    {
        var entry = new WorkspaceLogEntry(
            new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero),
            level,
            "正在处理");

        Assert.Equal(expected, entry.LevelText);
    }

    [Fact]
    public void KeepsOnlyTheLatestOneHundredEntriesInAscendingTimeOrder()
    {
        var buffer = new WorkspaceLogBuffer();
        var origin = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);

        for (var index = 104; index >= 0; index--)
            buffer.AppendSafe($"消息 {index}", WorkspaceLogLevel.Information, origin.AddMinutes(index));

        Assert.Equal(100, buffer.Entries.Count);
        Assert.Equal(Enumerable.Range(5, 100).Select(index => $"消息 {index}"),
            buffer.Entries.Select(entry => entry.Message));
        Assert.True(buffer.Entries.Zip(buffer.Entries.Skip(1),
            (left, right) => left.Timestamp <= right.Timestamp).All(value => value));
    }

    [Fact]
    public void ConsecutiveDuplicateSafeMessagesReplaceThePreviousEntry()
    {
        var buffer = new WorkspaceLogBuffer();
        var first = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
        var second = first.AddSeconds(3);

        buffer.AppendSafe("正在处理", WorkspaceLogLevel.Information, first);
        buffer.AppendSafe("正在处理", WorkspaceLogLevel.Warning, second);

        var entry = Assert.Single(buffer.Entries);
        Assert.Equal(second, entry.Timestamp);
        Assert.Equal(WorkspaceLogLevel.Warning, entry.Level);
    }

    [Fact]
    public void ClearRemovesEntriesAndResetsConsecutiveDuplicateTracking()
    {
        var buffer = new WorkspaceLogBuffer();
        var first = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
        var second = first.AddSeconds(1);

        buffer.AppendSafe("正在处理", timestamp: first);
        buffer.Clear();
        buffer.AppendSafe("正在处理", timestamp: second);

        var entry = Assert.Single(buffer.Entries);
        Assert.Equal(second, entry.Timestamp);
    }

    [Fact]
    public void SupportsEveryExplicitLevel()
    {
        var buffer = new WorkspaceLogBuffer();

        foreach (var level in Enum.GetValues<WorkspaceLogLevel>())
            buffer.AppendSafe(level.ToString(), level);

        Assert.Equal(Enum.GetValues<WorkspaceLogLevel>(), buffer.Entries.Select(entry => entry.Level));
    }

    [Theory]
    [InlineData("提取码：a1B2", "a1B2")]
    [InlineData("token=top-secret", "top-secret")]
    [InlineData("Authorization: Bearer bearer-secret", "bearer-secret")]
    [InlineData("Cookie: BDUSS=cookie-secret; STOKEN=second-secret", "cookie-secret")]
    [InlineData("profile=private-profile", "private-profile")]
    [InlineData("profile=\"Default Private Profile\"", "Default Private Profile")]
    [InlineData("draftFingerprint: internal-fingerprint", "internal-fingerprint")]
    [InlineData("lease-id=private-lease-id", "private-lease-id")]
    [InlineData("lease: private-lease-token", "private-lease-token")]
    [InlineData("lease-id=private lease value，正在处理。", "private lease value")]
    [InlineData(@"已保存到 C:\Users\alice\Documents\private.xlsx", @"C:\Users\alice\Documents\private.xlsx")]
    [InlineData("已保存到 C:/Users/alice/Documents/private.xlsx", "C:/Users/alice/Documents/private.xlsx")]
    [InlineData(@"已保存到 “C:\Users\Alice Smith\Secret Folder\private result.xlsx”。", "Secret Folder")]
    [InlineData(@"已保存到 C:\Users\Alice Smith\Secret Folder\private result.xlsx，请查看结果。", "Secret Folder")]
    [InlineData("已保存到 /home/alice/private/result.xlsx", "/home/alice/private/result.xlsx")]
    public void RedactsSensitiveValues(string message, string secret)
    {
        var buffer = new WorkspaceLogBuffer();

        buffer.AppendSafe(message);

        var safe = Assert.Single(buffer.Entries).Message;
        Assert.DoesNotContain(secret, safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("已隐藏", safe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pan.baidu.com")]
    [InlineData("yun.baidu.com")]
    public void MasksFullBaiduShareUrlsWithoutRetainingAnyVerbatimPart(string host)
    {
        var url = $"https://{host}/s/1privateShare?pwd=a1b2";
        var buffer = new WorkspaceLogBuffer();

        buffer.AppendSafe($"分享已创建：{url}");

        var safe = Assert.Single(buffer.Entries).Message;
        Assert.DoesNotContain(url, safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1privateShare", safe, StringComparison.Ordinal);
        Assert.Equal("分享已创建：[百度网盘链接已隐藏]", safe);
    }

    [Theory]
    [InlineData("转存和分享已完成，本机结果文件尚待保存。")]
    [InlineData("正在等待账号确认。")]
    [InlineData("未取得这一条任务的独占执行权，未进行远端操作。")]
    public void ClassifiesIncompleteProductionStatusesAsWarnings(string message)
    {
        var buffer = new WorkspaceLogBuffer();

        buffer.AppendSafe(message);

        Assert.Equal(WorkspaceLogLevel.Warning, Assert.Single(buffer.Entries).Level);
    }

    [Theory]
    [InlineData("进度 2/25：成功 2，失败 0，剩余 23。")]
    [InlineData("进度 1,234/2,500：成功 1,234，失败 0，剩余 1,266。")]
    public void ClassifiesProgressWithoutFailuresAsInformation(string message)
    {
        var buffer = new WorkspaceLogBuffer();

        buffer.AppendSafe(message);

        Assert.Equal(WorkspaceLogLevel.Information, Assert.Single(buffer.Entries).Level);
    }

    [Fact]
    public void ClassifiesProgressWithFailuresAsError()
    {
        var buffer = new WorkspaceLogBuffer();

        buffer.AppendSafe("进度 25/25：成功 24，失败 1，剩余 0。");

        Assert.Equal(WorkspaceLogLevel.Error, Assert.Single(buffer.Entries).Level);
    }

    [Fact]
    public void PreservesOrdinaryStatusAfterAPunctuationDelimitedSpacedSecret()
    {
        var buffer = new WorkspaceLogBuffer();

        buffer.AppendSafe("lease-id=private lease value，正在处理。");

        Assert.Equal("lease-id：[敏感信息已隐藏]，正在处理。", Assert.Single(buffer.Entries).Message);
    }

    [Fact]
    public void PreservesOrdinaryChineseUserFacingStatus()
    {
        const string message = "导入完成，请选择参与账号和保存目录。";
        var buffer = new WorkspaceLogBuffer();

        buffer.AppendSafe(message);

        Assert.Equal(message, Assert.Single(buffer.Entries).Message);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
