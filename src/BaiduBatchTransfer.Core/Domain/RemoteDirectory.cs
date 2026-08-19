namespace BaiduBatchTransfer.Core.Domain;

public sealed record RemoteDirectory
{
    public RemoteDirectory(string remoteId, RemotePath path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        ArgumentNullException.ThrowIfNull(path);

        RemoteId = remoteId.Trim();
        Path = path;
    }

    public string RemoteId { get; }

    public RemotePath Path { get; }
}
