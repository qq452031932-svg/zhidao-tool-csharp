using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Common;

/// <summary>Coordinates local preview, confirmation and deletion without any remote capability.</summary>
public sealed class LocalHistoryCleanupViewModel : ObservableObject, IDisposable
{
    private readonly LocalTerminalHistoryService? service;
    private readonly ILocalHistoryCleanupInteraction? interaction;
    private readonly LocalHistoryKind kind;
    private readonly Func<bool> ownerAvailable;
    private readonly Func<CancellationToken, Task> reload;
    private readonly Action<string> report;
    private readonly CancellationTokenSource lifetime = new();
    private long previewGeneration;
    private bool closed;
    private bool busy;
    private LocalHistoryCleanupPreview? preview;

    public LocalHistoryCleanupViewModel(LocalTerminalHistoryService? service,
        ILocalHistoryCleanupInteraction? interaction, LocalHistoryKind kind, Func<bool> ownerAvailable,
        Func<CancellationToken, Task> reload, Action<string> report)
    {
        this.service = service;
        this.interaction = interaction;
        this.kind = kind;
        this.ownerAvailable = ownerAvailable;
        this.reload = reload;
        this.report = report;
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => CanClear);
    }

    public LocalHistoryCleanupPreview? Preview => preview;
    public bool IsBusy => busy;
    public bool CanClear => !closed && !busy && ownerAvailable() && interaction is not null &&
        preview is { TerminalBatchCount: > 0, HasConflicts: false };
    public IAsyncRelayCommand ClearCommand { get; }

    public void NotifyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanClear));
        ClearCommand.NotifyCanExecuteChanged();
    }

    public async Task RefreshPreviewAsync(CancellationToken cancellationToken = default)
    {
        if (closed || service is null) return;
        var generation = ++previewGeneration;
        preview = null;
        NotifyAvailabilityChanged();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try
        {
            var value = await service.PreviewAsync(kind, operation.Token).ConfigureAwait(true);
            if (closed || operation.IsCancellationRequested || generation != previewGeneration) return;
            preview = value;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!closed && generation == previewGeneration)
                report("暂时无法读取可清空的历史，请稍后重试。");
        }
        finally
        {
            if (!closed && generation == previewGeneration)
            {
                OnPropertyChanged(nameof(Preview));
                NotifyAvailabilityChanged();
            }
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (closed || busy || !ownerAvailable() || service is null || interaction is null) return;
        busy = true;
        OnPropertyChanged(nameof(IsBusy));
        NotifyAvailabilityChanged();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var deletedCount = 0;
        try
        {
            await RefreshPreviewAsync(operation.Token).ConfigureAwait(true);
            if (closed || operation.IsCancellationRequested || preview is not { } confirmedPreview) return;
            if (confirmedPreview.HasConflicts)
            {
                report("仍有任务在处理，暂时无法清空历史，请稍后重试。");
                return;
            }
            if (confirmedPreview.TerminalBatchCount == 0)
            {
                report("没有可清空的已结束历史。");
                return;
            }
            var confirmed = await interaction.ConfirmAsync(confirmedPreview, operation.Token).ConfigureAwait(true);
            if (closed || operation.IsCancellationRequested) return;
            if (!confirmed)
            {
                report("已取消清空历史。");
                return;
            }
            if (!ownerAvailable())
            {
                report("仍有任务在处理，暂时无法清空历史，请稍后重试。");
                return;
            }
            // This exact object is what the user confirmed. Never replace its token after confirmation.
            var result = await service.DeleteAsync(confirmedPreview, operation.Token).ConfigureAwait(true);
            if (closed || operation.IsCancellationRequested) return;
            if (result.Disposition == LocalHistoryCleanupDisposition.Conflict)
                report("历史记录已变化或仍有任务在处理，未清空任何历史，请稍后重试。");
            else if (result.Disposition == LocalHistoryCleanupDisposition.NothingToDelete)
                report("没有可清空的已结束历史。");
            else
            {
                deletedCount = result.DeletedBatchCount;
                await reload(operation.Token).ConfigureAwait(true);
                if (closed || operation.IsCancellationRequested) return;
                report($"已清空 {result.DeletedBatchCount} 个已结束的{(kind == LocalHistoryKind.Transfer ? "转存" : "分享")}批次历史。仅删除本地记录，不影响百度网盘文件和分享链接。");
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!closed) report(deletedCount > 0
                ? $"已清空 {deletedCount} 个批次历史，但暂时无法刷新列表，请稍后重试。"
                : "清空历史未能完成，请稍后重试。");
        }
        finally
        {
            if (!closed)
            {
                await RefreshPreviewAsync(operation.Token).ConfigureAwait(true);
                busy = false;
                OnPropertyChanged(nameof(IsBusy));
                NotifyAvailabilityChanged();
            }
        }
    }

    public void Close()
    {
        if (closed) return;
        closed = true;
        ++previewGeneration;
        preview = null;
        lifetime.Cancel();
        NotifyAvailabilityChanged();
    }

    public void Dispose()
    {
        Close();
        lifetime.Dispose();
    }
}
