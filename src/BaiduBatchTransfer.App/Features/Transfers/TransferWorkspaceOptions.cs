using System.IO;

namespace BaiduBatchTransfer.App.Features.Transfers;

public enum TransferAccountMode
{
    SingleAccount,
    MultiAccountReplication,
    MultiAccountDistribution,
}

public sealed record TransferWorkspaceOptions(
    string DefaultTargetDirectory = "/我的资源",
    string JobNamePrefix = "批量转存",
    int MaximumRecentBatches = 20,
    int PostCommitTimeoutSeconds = 10,
    string? DefaultResultDirectory = null,
    bool SimplifiedDirectMode = false,
    int DefaultMaxConcurrency = 2)
{
    public TransferWorkspaceOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultTargetDirectory))
            throw new ArgumentException("A default target directory is required.", nameof(DefaultTargetDirectory));
        if (string.IsNullOrWhiteSpace(JobNamePrefix))
            throw new ArgumentException("A job-name prefix is required.", nameof(JobNamePrefix));
        if (MaximumRecentBatches is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MaximumRecentBatches));
        if (PostCommitTimeoutSeconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(PostCommitTimeoutSeconds));
        if (DefaultMaxConcurrency is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(DefaultMaxConcurrency));
        if (!string.IsNullOrWhiteSpace(DefaultResultDirectory) &&
            !Path.IsPathFullyQualified(DefaultResultDirectory))
            throw new ArgumentException("A fully qualified default result directory is required.", nameof(DefaultResultDirectory));
        return this;
    }
}

public sealed record TransferResultDestination(Guid BatchJobId, string ResultDirectory)
{
    public TransferResultDestination Validate()
    {
        if (BatchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(BatchJobId));
        if (string.IsNullOrWhiteSpace(ResultDirectory) || !Path.IsPathFullyQualified(ResultDirectory))
            throw new ArgumentException("A fully qualified local result directory is required.", nameof(ResultDirectory));
        return this;
    }

    public override string ToString() =>
        $"TransferResultDestination {{ BatchJobId = {BatchJobId}, ResultDirectory = [REDACTED] }}";
}

public interface ITransferResultDestinationSink
{
    Task SetAsync(
        TransferResultDestination destination,
        CancellationToken cancellationToken = default);
}

public interface ILocalResultDirectoryPreparer
{
    Task EnsureExistsAsync(
        string directoryPath,
        CancellationToken cancellationToken = default);
}

public sealed class LocalResultDirectoryPreparer : ILocalResultDirectoryPreparer
{
    public Task EnsureExistsAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(directoryPath) || !Path.IsPathFullyQualified(directoryPath))
            throw new ArgumentException("A fully qualified local result directory is required.", nameof(directoryPath));
        Directory.CreateDirectory(Path.GetFullPath(directoryPath));
        return Task.CompletedTask;
    }
}
