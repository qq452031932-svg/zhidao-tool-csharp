using System.Collections.Immutable;

namespace BaiduBatchTransfer.Core.Domain;

public enum AccountShareMetric
{
    Transfer,
    View,
    Download,
}

public enum AccountShareRemoteStatus
{
    Valid,
    Invalid,
}

public sealed record AccountShareMetricObservation
{
    public AccountShareMetricObservation(
        AccountShareMetric metric,
        long? historicalTotal,
        IReadOnlyDictionary<DateOnly, long>? directDailyValues = null)
    {
        ValidateMetric(metric);
        ValidateCount(historicalTotal, nameof(historicalTotal));
        Metric = metric;
        HistoricalTotal = historicalTotal;
        DirectDailyValues = directDailyValues is null
            ? null
            : directDailyValues.ToImmutableDictionary(
                pair => pair.Key,
                pair => ValidateCount(pair.Value, nameof(directDailyValues))!.Value);
    }

    public AccountShareMetric Metric { get; }
    public long? HistoricalTotal { get; }
    public IReadOnlyDictionary<DateOnly, long>? DirectDailyValues { get; }

    public override string ToString() =>
        $"AccountShareMetricObservation {{ Metric = {Metric}, Values = [REDACTED] }}";

    internal static void ValidateMetric(AccountShareMetric metric)
    {
        if (!Enum.IsDefined(metric))
            throw new ArgumentOutOfRangeException(nameof(metric));
    }

    internal static long? ValidateCount(long? value, string parameterName)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }
}

public sealed record AccountShareObservation
{
    public AccountShareObservation(
        string shareId,
        string title,
        Uri shareUri,
        DateTimeOffset sharedAtUtc,
        AccountShareRemoteStatus status,
        IReadOnlyList<AccountShareMetricObservation> metrics)
    {
        ShareId = ValidateText(shareId, nameof(shareId), 256);
        Title = ValidateText(title, nameof(title), 4096);
        ShareUri = ValidateShareUri(shareUri);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        SharedAtUtc = sharedAtUtc.ToUniversalTime();
        Metrics = ValidateAndCopyMetrics(metrics);
    }

    public string ShareId { get; }
    public string Title { get; }
    public Uri ShareUri { get; }
    public DateTimeOffset SharedAtUtc { get; }
    public AccountShareRemoteStatus Status { get; }
    public IReadOnlyList<AccountShareMetricObservation> Metrics { get; }

    public override string ToString() =>
        $"AccountShareObservation {{ Status = {Status}, Metrics = {Metrics.Count}, Values = [REDACTED] }}";

    internal static string ValidateText(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("A bounded non-blank value is required.", parameterName);
        return value;
    }

    internal static Uri ValidateShareUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.Equals(uri.Host, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("A safe Baidu share URI is required.", nameof(uri));
        return uri;
    }

    internal static IReadOnlyList<AccountShareMetricObservation> ValidateAndCopyMetrics(
        IReadOnlyList<AccountShareMetricObservation> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var copy = metrics.ToImmutableArray();
        if (copy.Length != 3 || copy.Any(item => item is null))
            throw new ArgumentException("Exactly three metric observations are required.", nameof(metrics));
        foreach (var metric in Enum.GetValues<AccountShareMetric>())
        {
            if (copy.Count(item => item.Metric == metric) != 1)
                throw new ArgumentException("Each known metric must occur exactly once.", nameof(metrics));
        }
        return copy;
    }
}

public sealed record AccountSharePage
{
    public AccountSharePage(
        IReadOnlyList<AccountShareObservation> items,
        string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(items);
        var copy = items.ToImmutableArray();
        if (copy.Any(item => item is null))
            throw new ArgumentException("Page items cannot contain null.", nameof(items));
        if (copy.Select(item => item.ShareId).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Page share identities must be unique.", nameof(items));
        if (nextCursor is not null &&
            (string.IsNullOrWhiteSpace(nextCursor) || nextCursor.Length > 2048 || nextCursor.Any(char.IsControl)))
            throw new ArgumentException("The continuation is invalid.", nameof(nextCursor));
        Items = copy;
        NextCursor = nextCursor;
    }

    public IReadOnlyList<AccountShareObservation> Items { get; }
    public string? NextCursor { get; }

    public override string ToString() =>
        $"AccountSharePage {{ ItemCount = {Items.Count}, HasNextPage = {NextCursor is not null}, Values = [REDACTED] }}";
}

public sealed record AccountShareMetricHistory
{
    public AccountShareMetricHistory(
        AccountShareMetric metric,
        long? historicalTotal,
        IReadOnlyDictionary<DateOnly, long?>? cumulativeTotals = null,
        IReadOnlyDictionary<DateOnly, long?>? directDailyValues = null)
    {
        AccountShareMetricObservation.ValidateMetric(metric);
        Metric = metric;
        HistoricalTotal = AccountShareMetricObservation.ValidateCount(historicalTotal, nameof(historicalTotal));
        CumulativeTotals = CopyCounts(cumulativeTotals, nameof(cumulativeTotals));
        DirectDailyValues = CopyCounts(directDailyValues, nameof(directDailyValues));
    }

    public AccountShareMetric Metric { get; }
    public long? HistoricalTotal { get; }
    public IReadOnlyDictionary<DateOnly, long?> CumulativeTotals { get; }
    public IReadOnlyDictionary<DateOnly, long?> DirectDailyValues { get; }

    public override string ToString() =>
        $"AccountShareMetricHistory {{ Metric = {Metric}, Values = [REDACTED] }}";

    private static IReadOnlyDictionary<DateOnly, long?> CopyCounts(
        IReadOnlyDictionary<DateOnly, long?>? source,
        string parameterName)
    {
        if (source is null) return ImmutableDictionary<DateOnly, long?>.Empty;
        return source.ToImmutableDictionary(
            pair => pair.Key,
            pair => AccountShareMetricObservation.ValidateCount(pair.Value, parameterName));
    }
}

public sealed record AccountShareCachedRecord
{
    public AccountShareCachedRecord(
        string shareId,
        string title,
        Uri shareUri,
        DateTimeOffset sharedAtUtc,
        AccountShareRemoteStatus status,
        IReadOnlyList<AccountShareMetricHistory> metrics)
    {
        ShareId = AccountShareObservation.ValidateText(shareId, nameof(shareId), 256);
        Title = AccountShareObservation.ValidateText(title, nameof(title), 4096);
        ShareUri = AccountShareObservation.ValidateShareUri(shareUri);
        SharedAtUtc = sharedAtUtc.ToUniversalTime();
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        ArgumentNullException.ThrowIfNull(metrics);
        var copy = metrics.ToImmutableArray();
        if (copy.Length != 3 || copy.Any(item => item is null))
            throw new ArgumentException("Exactly three metric histories are required.", nameof(metrics));
        foreach (var metric in Enum.GetValues<AccountShareMetric>())
        {
            if (copy.Count(item => item.Metric == metric) != 1)
                throw new ArgumentException("Each known metric must occur exactly once.", nameof(metrics));
        }
        Metrics = copy;
    }

    public string ShareId { get; }
    public string Title { get; }
    public Uri ShareUri { get; }
    public DateTimeOffset SharedAtUtc { get; }
    public AccountShareRemoteStatus Status { get; }
    public IReadOnlyList<AccountShareMetricHistory> Metrics { get; }

    public override string ToString() =>
        $"AccountShareCachedRecord {{ Status = {Status}, Metrics = {Metrics.Count}, Values = [REDACTED] }}";
}

public sealed record AccountShareCachedSnapshot
{
    public AccountShareCachedSnapshot(
        Guid accountId,
        DateTimeOffset? lastSuccessfulSyncAtUtc,
        IReadOnlyList<AccountShareCachedRecord> items)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("Account ID is required.", nameof(accountId));
        ArgumentNullException.ThrowIfNull(items);
        AccountId = accountId;
        LastSuccessfulSyncAtUtc = lastSuccessfulSyncAtUtc?.ToUniversalTime();
        Items = items.ToImmutableArray();
    }

    public Guid AccountId { get; }
    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; }
    public IReadOnlyList<AccountShareCachedRecord> Items { get; }
}

public sealed record AccountShareSyncCommit
{
    public AccountShareSyncCommit(
        Guid accountId,
        long generation,
        DateTimeOffset syncedAtUtc,
        IReadOnlyList<AccountShareObservation> items)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("Account ID is required.", nameof(accountId));
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        ArgumentNullException.ThrowIfNull(items);
        var copy = items.ToImmutableArray();
        if (copy.Select(item => item.ShareId).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Complete sync identities must be unique.", nameof(items));
        AccountId = accountId;
        Generation = generation;
        SyncedAtUtc = syncedAtUtc.ToUniversalTime();
        Items = copy;
    }

    public Guid AccountId { get; }
    public long Generation { get; }
    public DateTimeOffset SyncedAtUtc { get; }
    public IReadOnlyList<AccountShareObservation> Items { get; }
}
