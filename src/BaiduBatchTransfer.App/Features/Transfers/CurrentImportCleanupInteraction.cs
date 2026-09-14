using System.Windows;

namespace BaiduBatchTransfer.App.Features.Transfers;

public interface ICurrentImportCleanupInteraction
{
    Task<bool> ConfirmAsync(long currentRowCount, CancellationToken cancellationToken = default);
}

public sealed class WpfCurrentImportCleanupInteraction : ICurrentImportCleanupInteraction
{
    public static string BuildMessage(long currentRowCount)
    {
        if (currentRowCount < 0) throw new ArgumentOutOfRangeException(nameof(currentRowCount));
        return $"确认清空当前导入的 {currentRowCount} 条记录？\n\n只删除本地待处理导入，不影响百度网盘文件和分享链接。此操作无法撤销。";
    }

    public Task<bool> ConfirmAsync(long currentRowCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var confirmed = MessageBox.Show(BuildMessage(currentRowCount), "清空当前导入",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(confirmed);
    }
}

internal sealed class RefusingCurrentImportCleanupInteraction : ICurrentImportCleanupInteraction
{
    public static RefusingCurrentImportCleanupInteraction Instance { get; } = new();
    public Task<bool> ConfirmAsync(long currentRowCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

internal sealed class CurrentImportActivityGate
{
    private readonly object sync = new();
    private int activeActivities;
    private bool cleanupActive;

    public event EventHandler? StateChanged;

    public bool HasActiveActivities
    {
        get { lock (sync) return activeActivities > 0; }
    }

    public bool IsCleanupActive
    {
        get { lock (sync) return cleanupActive; }
    }

    public IDisposable? TryEnterActivity()
    {
        lock (sync)
        {
            if (cleanupActive) return null;
            activeActivities++;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        return new Lease(ExitActivity);
    }

    public IDisposable? TryEnterCleanup(Func<bool> canEnter)
    {
        ArgumentNullException.ThrowIfNull(canEnter);
        lock (sync)
        {
            if (cleanupActive || activeActivities != 0 || !canEnter()) return null;
            cleanupActive = true;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        return new Lease(ExitCleanup);
    }

    private void ExitActivity()
    {
        lock (sync)
        {
            if (activeActivities <= 0) throw new InvalidOperationException("No current-import activity is active.");
            activeActivities--;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExitCleanup()
    {
        lock (sync)
        {
            if (!cleanupActive) return;
            cleanupActive = false;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? ownedRelease = release;
        public void Dispose() => Interlocked.Exchange(ref ownedRelease, null)?.Invoke();
    }
}
