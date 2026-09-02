using System.Collections.Concurrent;
using System.Net;
using System.Globalization;
using System.Text.Json;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class ReadOnlyProductionContractProbeTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyDictionary<string, Uri> Routes =
        new Dictionary<string, Uri>(StringComparer.Ordinal)
        {
            ["identity"] = new(
                "https://pan.baidu.com/api/gettemplatevariable?fields=%5B%22uk%22%2C%22username%22%2C%22avatar_url%22%2C%22photo_url%22%5D"),
            ["web-context"] = new(
                "https://pan.baidu.com/api/gettemplatevariable?fields=%5B%22bdstoken%22%5D"),
            ["quota"] = new("https://pan.baidu.com/api/quota"),
        };

    [Fact]
    public async Task ApprovedProbeReadsOnlyFixedRoutesAndPrintsOnlySanitizedShape()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("BAIDU_BATCH_READ_ONLY_PROBE"),
                "approved",
                StringComparison.Ordinal))
            return;

        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BaiduBatchTransfer");
        using var host = AppBootstrap.CreateHost(dataRoot);
        var unitFactory = host.Services.GetRequiredService<IUnitOfWorkFactory>();
        Guid accountId;
        await using (var unit = await unitFactory.BeginAsync(default))
        {
            var page = await unit.Accounts.GetPageAsync(new PageQuery(1, 100), default);
            var account = Assert.Single(page.Items, item => item.IsEnabled && !item.IsDeleted);
            accountId = account.Id;
            await unit.RollbackAsync(default);
        }

        var session = await host.Services.GetRequiredService<IBaiduSessionStore>()
            .LoadAsync(accountId.ToString("D"), default);
        Assert.NotNull(session);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
        };
        var characterizer = new BaiduReadOnlyContractCharacterizer(Routes, handler);
        foreach (var route in Routes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route.Value);
            var observation = await characterizer.ObserveAsync(
                route.Key, request, session!, default);
            Assert.Equal("GET", observation.Method);
            Assert.Equal(200, observation.StatusCode);
            Assert.NotEmpty(observation.JsonShape);
            output.WriteLine(observation.ToString());
        }
    }

    [Fact]
    public async Task ShareAnalyticsApprovedProbeWalksEveryReadOnlyPage()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("BAIDU_BATCH_SHARE_ANALYTICS_PROBE"),
                "approved",
                StringComparison.Ordinal))
            return;

        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BaiduBatchTransfer");
        using var host = AppBootstrap.CreateHost(dataRoot);
        var unitFactory = host.Services.GetRequiredService<IUnitOfWorkFactory>();
        Guid accountId;
        await using (var unit = await unitFactory.BeginAsync(default))
        {
            var page = await unit.Accounts.GetPageAsync(new PageQuery(1, 100), default);
            var active = page.Items.Where(item => item.IsEnabled && !item.IsDeleted).ToArray();
            Assert.NotEmpty(active);

            if (active.Length == 1)
            {
                accountId = active[0].Id;
            }
            else
            {
                var selected = Environment.GetEnvironmentVariable("BAIDU_BATCH_SHARE_ANALYTICS_ACCOUNT_ID");
                Assert.True(Guid.TryParse(selected, out accountId),
                    "Multiple active accounts require an explicit local account selection.");
                Assert.Contains(active, account => account.Id == accountId);
            }
            await unit.RollbackAsync(default);
        }

        var session = await host.Services.GetRequiredService<IBaiduSessionStore>()
            .LoadAsync(accountId.ToString("D"), default);
        Assert.NotNull(session);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
        };
        var characterizer = new BaiduReadOnlyContractCharacterizer(
            new Dictionary<string, Uri> { ["quota"] = new("https://pan.baidu.com/api/quota") },
            handler);
        var pagePolicy = new BaiduReadOnlyPagedRoutePolicy(
            "share-records",
            HttpMethod.Get,
            "/share/record",
            new Dictionary<string, BaiduReadOnlyQueryRule>(StringComparer.Ordinal)
            {
                ["page"] = BaiduReadOnlyQueryRule.PositiveInteger(),
                ["desc"] = BaiduReadOnlyQueryRule.Fixed("1"),
                ["order"] = BaiduReadOnlyQueryRule.Fixed("time"),
            },
            MaximumPages: 1);
        var pageCache = new ConcurrentDictionary<int, Lazy<Task<LiveSharePageProbe>>>();

        Task<LiveSharePageProbe> ProbePageAsync(int pageNumber) =>
            pageCache.GetOrAdd(pageNumber, number => new(() => ProbeUncachedPageAsync(number))).Value;

        async Task<LiveSharePageProbe> ProbeUncachedPageAsync(int pageNumber)
        {
            LiveSharePageContents? contents = null;
            var observations = await characterizer.ObservePagesAsync(
                pagePolicy,
                _ => new Uri(
                    $"https://pan.baidu.com/share/record?page={pageNumber.ToString(CultureInfo.InvariantCulture)}&desc=1&order=time"),
                root =>
                {
                    contents = ReadVerifiedSharePage(root);
                    return new(contents.ItemCount, null);
                },
                session!,
                default);
            var observation = Assert.Single(observations);
            Assert.NotNull(contents);
            Assert.Equal("GET", observation.Method);
            Assert.Equal(200, observation.StatusCode);
            Assert.NotEmpty(observation.JsonShape);
            return new(pageNumber, contents, observation);
        }

        var upper = 1;
        var lowerFull = 0;
        while ((await ProbePageAsync(upper)).Contents.ItemCount == 100)
        {
            lowerFull = upper;
            Assert.True(upper <= 5_000, "The share page bound was exceeded.");
            upper = checked(upper * 2);
        }

        var low = lowerFull + 1;
        var high = upper;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if ((await ProbePageAsync(middle)).Contents.ItemCount < 100)
                high = middle;
            else
                low = middle + 1;
        }
        var terminalPage = low;

        await Parallel.ForEachAsync(
            Enumerable.Range(1, terminalPage),
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (pageNumber, _) => await ProbePageAsync(pageNumber));
        var pages = await Task.WhenAll(
            Enumerable.Range(1, terminalPage).Select(ProbePageAsync));
        Assert.All(pages[..^1], page => Assert.Equal(100, page.Contents.ItemCount));
        Assert.InRange(pages[^1].Contents.ItemCount, 0, 99);
        Assert.All(pages[..^1], page => Assert.True(page.Contents.NextMarker > 0));
        var shareIds = pages.SelectMany(page => page.Contents.ShareIds).ToArray();
        Assert.Equal(shareIds.Length, shareIds.Distinct().Count());

        output.WriteLine(
            $"share-records GET status=200 pages={terminalPage} items={shareIds.Length} shape={string.Join(", ", pages[0].Observation.JsonShape)} values=[REDACTED]");
    }

    private static LiveSharePageContents ReadVerifiedSharePage(JsonElement root)
    {
        Assert.Equal(0, root.GetProperty("errno").GetInt32());
        var list = root.GetProperty("list");
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        foreach (var item in list.EnumerateArray())
        {
            Assert.True(item.GetProperty("shareId").GetInt64() > 0);
            Assert.True(
                HasNonBlankString(item, "typicalPath") || HasNonBlankString(item, "name"),
                "Each share requires a stable title or path.");
            Assert.True(
                HasNonBlankString(item, "shortlink") || HasNonBlankString(item, "shorturl"),
                "Each share requires a stable URL.");
            Assert.True(item.GetProperty("ctime").GetInt64() >= 0);
            Assert.Equal(JsonValueKind.Number, item.GetProperty("status").ValueKind);
            Assert.True(item.GetProperty("tCnt").GetInt64() >= 0);
            Assert.True(item.GetProperty("vCnt").GetInt64() >= 0);
            Assert.True(item.GetProperty("dCnt").GetInt64() >= 0);
        }

        var nextMarker = root.TryGetProperty("nextpage", out var nextPage) &&
            nextPage.ValueKind == JsonValueKind.Number &&
            nextPage.TryGetInt32(out var marker)
                ? marker
                : 0;
        return new(
            list.GetArrayLength(),
            nextMarker,
            list.EnumerateArray()
                .Select(item => item.GetProperty("shareId").GetInt64())
                .ToArray());
    }

    private static bool HasNonBlankString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString());

    private sealed record LiveSharePageContents(
        int ItemCount,
        int NextMarker,
        long[] ShareIds);

    private sealed record LiveSharePageProbe(
        int PageNumber,
        LiveSharePageContents Contents,
        BaiduReadOnlyPageObservation Observation);
}
