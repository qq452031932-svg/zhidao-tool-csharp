using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.Transfers;

public sealed class BaiduTransferTargetDirectorySource(
    IBaiduSessionStore sessionStore,
    IBaiduNetdiskAdapter adapter) : ITransferTargetDirectorySource
{
    public async Task<TransferTargetDirectoryLoadResult> LoadAsync(
        Guid accountId,
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var accountKey = accountId.ToString("D");
            var session = await sessionStore.LoadAsync(accountKey, cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
                return TransferTargetDirectoryLoadResult.Failure("登录会话不可用，请重新登录。");
            if (!string.Equals(session.AccountId, accountKey, StringComparison.Ordinal))
                return TransferTargetDirectoryLoadResult.Failure("登录会话与所选账号不匹配，请重新登录。");

            var result = await adapter.ListDirectoriesAsync(
                    session,
                    new RemotePath(path),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
                return TransferTargetDirectoryLoadResult.Failure("目录读取失败，请稍后重试。");

            var directories = result.Value
                .Select(directory => new TransferTargetDirectoryEntry(
                    GetDisplayName(directory.Path.Value),
                    directory.Path.Value))
                .OrderBy(entry => entry.DisplayName, NaturalOrdinalStringComparer.Instance)
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ToArray();
            return TransferTargetDirectoryLoadResult.Success(directories);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return TransferTargetDirectoryLoadResult.Failure("目录读取失败，请稍后重试。");
        }
    }

    private static string GetDisplayName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private sealed class NaturalOrdinalStringComparer : IComparer<string>
    {
        public static NaturalOrdinalStringComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
                {
                    var leftEnd = ReadDigits(left, leftIndex);
                    var rightEnd = ReadDigits(right, rightIndex);
                    var leftDigits = left.AsSpan(leftIndex, leftEnd - leftIndex);
                    var rightDigits = right.AsSpan(rightIndex, rightEnd - rightIndex);
                    var leftTrimmed = TrimLeadingZeros(leftDigits);
                    var rightTrimmed = TrimLeadingZeros(rightDigits);
                    var lengthOrder = leftTrimmed.Length.CompareTo(rightTrimmed.Length);
                    if (lengthOrder != 0) return lengthOrder;
                    var digitOrder = leftTrimmed.SequenceCompareTo(rightTrimmed);
                    if (digitOrder != 0) return digitOrder;
                    var zeroOrder = leftDigits.Length.CompareTo(rightDigits.Length);
                    if (zeroOrder != 0) return zeroOrder;
                    leftIndex = leftEnd;
                    rightIndex = rightEnd;
                    continue;
                }

                var characterOrder = left[leftIndex].CompareTo(right[rightIndex]);
                if (characterOrder != 0) return characterOrder;
                leftIndex++;
                rightIndex++;
            }

            return left.Length.CompareTo(right.Length);
        }

        private static int ReadDigits(string value, int start)
        {
            var index = start;
            while (index < value.Length && char.IsDigit(value[index])) index++;
            return index;
        }

        private static ReadOnlySpan<char> TrimLeadingZeros(ReadOnlySpan<char> value)
        {
            var index = 0;
            while (index < value.Length - 1 && value[index] == '0') index++;
            return value[index..];
        }
    }
}
