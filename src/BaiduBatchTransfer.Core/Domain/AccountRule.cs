namespace BaiduBatchTransfer.Core.Domain;

public sealed record AccountRule
{
    public AccountRule(
        Guid id,
        string directoryPrefix,
        Guid accountId,
        int priority,
        bool isEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPrefix);
        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        Id = id;
        DirectoryPrefix = directoryPrefix;
        AccountId = accountId;
        Priority = priority;
        IsEnabled = isEnabled;
    }

    public Guid Id { get; }

    public string DirectoryPrefix { get; }

    public Guid AccountId { get; }

    public int Priority { get; }

    public bool IsEnabled { get; }
}
