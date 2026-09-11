namespace BaiduBatchTransfer.App.Features.Transfers;

public sealed record TransferTargetDirectoryEntry(string DisplayName, string Path);

public sealed record TransferTargetDirectoryLoadResult
{
    private TransferTargetDirectoryLoadResult(
        IReadOnlyList<TransferTargetDirectoryEntry> directories,
        string? errorMessage)
    {
        Directories = directories;
        ErrorMessage = errorMessage;
    }

    public IReadOnlyList<TransferTargetDirectoryEntry> Directories { get; }
    public string? ErrorMessage { get; }
    public bool IsSuccess => ErrorMessage is null;

    public static TransferTargetDirectoryLoadResult Success(
        IReadOnlyList<TransferTargetDirectoryEntry> directories) =>
        new(directories ?? throw new ArgumentNullException(nameof(directories)), null);

    public static TransferTargetDirectoryLoadResult Failure(string errorMessage) =>
        new([], string.IsNullOrWhiteSpace(errorMessage)
            ? throw new ArgumentException("错误消息不能为空。", nameof(errorMessage))
            : errorMessage);
}

public interface ITransferTargetDirectorySource
{
    Task<TransferTargetDirectoryLoadResult> LoadAsync(
        Guid accountId,
        string path,
        CancellationToken cancellationToken = default);
}

public sealed record TransferTargetDirectoryCreateResult
{
    private TransferTargetDirectoryCreateResult(string? createdPath, string? errorMessage)
    {
        CreatedPath = createdPath;
        ErrorMessage = errorMessage;
    }

    public string? CreatedPath { get; }
    public string? ErrorMessage { get; }
    public bool IsSuccess => CreatedPath is not null && ErrorMessage is null;

    public static TransferTargetDirectoryCreateResult Success(string createdPath) =>
        new(string.IsNullOrWhiteSpace(createdPath)
            ? throw new ArgumentException("创建后的目录不能为空。", nameof(createdPath))
            : createdPath, null);

    public static TransferTargetDirectoryCreateResult Failure(string errorMessage) =>
        new(null, string.IsNullOrWhiteSpace(errorMessage)
            ? throw new ArgumentException("错误消息不能为空。", nameof(errorMessage))
            : errorMessage);
}

public interface ITransferTargetDirectoryCreator
{
    Task<TransferTargetDirectoryCreateResult> CreateAsync(
        Guid accountId,
        string parentPath,
        string folderName,
        CancellationToken cancellationToken = default);
}

public interface ITransferTargetDirectoryPicker
{
    Task<string?> PickAsync(
        Guid accountId,
        string initialPath,
        CancellationToken cancellationToken = default);
}
