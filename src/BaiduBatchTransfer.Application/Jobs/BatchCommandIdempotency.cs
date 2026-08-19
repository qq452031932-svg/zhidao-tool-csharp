using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.Application.Jobs;

internal static class BatchCommandIdempotency
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Guid, GateEntry> Gates = [];

    public static Guid AuditId(string commandName, string confirmationToken, params Guid[] requestIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "batch-command-v1");
        Append(hash, commandName);
        Append(hash, confirmationToken);
        foreach (var requestId in requestIds) Append(hash, requestId.ToString("N"));
        return new Guid(hash.GetHashAndReset().AsSpan(0, 16));
    }

    public static async ValueTask<IAsyncDisposable> AcquireAsync(
        Guid auditId,
        CancellationToken cancellationToken)
    {
        GateEntry entry;
        lock (Sync)
        {
            if (!Gates.TryGetValue(auditId, out entry!))
            {
                entry = new GateEntry();
                Gates.Add(auditId, entry);
            }
            entry.Users++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GateLease(auditId, entry);
        }
        catch
        {
            ReleaseUser(auditId, entry, releaseSemaphore: false);
            throw;
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static void ReleaseUser(Guid auditId, GateEntry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore) entry.Semaphore.Release();
        lock (Sync)
        {
            entry.Users--;
            if (entry.Users == 0 && Gates.TryGetValue(auditId, out var current) && ReferenceEquals(current, entry))
            {
                Gates.Remove(auditId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class GateLease(Guid auditId, GateEntry entry) : IAsyncDisposable
    {
        private GateEntry? entry = entry;

        public ValueTask DisposeAsync()
        {
            var value = Interlocked.Exchange(ref entry, null);
            if (value is not null) ReleaseUser(auditId, value, releaseSemaphore: true);
            return ValueTask.CompletedTask;
        }
    }
}

public sealed record AppendCandidatesResult(
    Guid BatchJobId,
    Guid ImportSessionId,
    DateTimeOffset AppliedAt,
    string EventCode);

public sealed record ReopenShortfallResult(
    Guid BatchJobId,
    DateTimeOffset AppliedAt,
    string EventCode);
