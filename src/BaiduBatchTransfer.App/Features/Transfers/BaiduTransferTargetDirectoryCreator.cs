using System.Buffers;
using System.Text;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.Transfers;

public sealed class BaiduTransferTargetDirectoryCreator(
    IBaiduSessionStore sessionStore,
    IBaiduNetdiskAdapter adapter) : ITransferTargetDirectoryCreator
{
    private const string InvalidNameMessage = "文件夹名称无效，请更换名称。";
    private const string SessionUnavailableMessage = "登录会话不可用，请重新登录。";
    private const string SessionMismatchMessage = "登录会话与所选账号不匹配，请重新登录。";
    private const string LoginExpiredMessage = "登录已失效，请重新登录。";
    private const string InvalidTargetMessage = "无法创建文件夹，请更换名称。";
    private const string RemoteFailureMessage = "文件夹创建失败，请稍后重试。";

    public async Task<TransferTargetDirectoryCreateResult> CreateAsync(
        Guid accountId,
        string parentPath,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            return TransferTargetDirectoryCreateResult.Failure(SessionMismatchMessage);

        if (!TryBuildDirectChild(parentPath, folderName, out var childPath))
            return TransferTargetDirectoryCreateResult.Failure(InvalidNameMessage);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accountKey = accountId.ToString("D");
            var session = await sessionStore.LoadAsync(accountKey, cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
                return TransferTargetDirectoryCreateResult.Failure(SessionUnavailableMessage);
            if (!string.Equals(session.AccountId, accountKey, StringComparison.Ordinal))
                return TransferTargetDirectoryCreateResult.Failure(SessionMismatchMessage);

            cancellationToken.ThrowIfCancellationRequested();
            var adapterResult = await adapter.EnsureDirectoryAsync(
                    session, childPath!, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (adapterResult.IsSuccess)
                return TransferTargetDirectoryCreateResult.Success(childPath!.Value);

            return TransferTargetDirectoryCreateResult.Failure(adapterResult.ErrorKind switch
            {
                BaiduErrorKind.InvalidTargetPath => InvalidTargetMessage,
                BaiduErrorKind.ExpiredLogin or BaiduErrorKind.VerificationRequired => LoginExpiredMessage,
                _ => RemoteFailureMessage,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return TransferTargetDirectoryCreateResult.Failure(RemoteFailureMessage);
        }
    }

    private static bool TryBuildDirectChild(
        string parentPath,
        string folderName,
        out RemotePath? childPath)
    {
        childPath = null;
        if (folderName is null) return false;
        var segment = folderName.Trim();
        if (!IsValidSegment(segment)) return false;

        try
        {
            var parent = new RemotePath(parentPath);
            var joined = parent.Value == "/"
                ? $"/{segment}"
                : $"{parent.Value}/{segment}";
            var child = new RemotePath(joined);
            var expectedPrefix = parent.Value == "/" ? "/" : $"{parent.Value}/";
            if (!string.Equals(child.Value, expectedPrefix + segment, StringComparison.Ordinal))
                return false;
            if (!child.Value.StartsWith(expectedPrefix, StringComparison.Ordinal))
                return false;

            childPath = child;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." ||
            segment.Contains('/') || segment.Contains('\\'))
            return false;

        var scalarCount = 0;
        var remaining = segment.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done || Rune.IsControl(rune)) return false;
            scalarCount++;
            if (scalarCount > 255) return false;
            remaining = remaining[consumed..];
        }

        return true;
    }
}
