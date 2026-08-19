namespace BaiduBatchTransfer.Infrastructure.Database;

public interface IDatabaseFileOperations
{
    void CreateDirectory(string path);
    bool Exists(string path);
    void Copy(string source, string destination, bool overwrite);
    void Move(string source, string destination);
    void Replace(string source, string destination, string? backupPath);
    void Delete(string path);
}

internal sealed class DatabaseFileOperations : IDatabaseFileOperations
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public bool Exists(string path) => File.Exists(path);
    public void Copy(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
    public void Move(string source, string destination) => File.Move(source, destination);
    public void Replace(string source, string destination, string? backupPath) =>
        File.Replace(source, destination, backupPath, ignoreMetadataErrors: true);
    public void Delete(string path) => File.Delete(path);
}
