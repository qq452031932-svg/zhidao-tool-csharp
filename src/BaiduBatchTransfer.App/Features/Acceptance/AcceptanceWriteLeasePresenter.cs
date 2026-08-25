using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed class AcceptanceWriteLeasePresenter(
    IUiDispatcher dispatcher,
    Func<AdapterWriteAuthorizationRequest, AcceptanceConfirmationWindow> windowFactory)
    : IAcceptanceWriteLeasePresenter
{
    private readonly object sync = new();
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, AcceptanceConfirmationWindow> windows = [];

    public async Task<bool> PresentAsync(
        AdapterWriteAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        var window = await dispatcher.InvokeAsync(() =>
        {
            var created = windowFactory(request);
            created.Owner = System.Windows.Application.Current?.MainWindow;
            var key = AcceptanceWriteLeaseIdentity.From(request);
            lock (sync) windows[key] = created;
            created.Closed += (_, _) =>
            {
                lock (sync) windows.Remove(key);
            };
            created.Show();
            return created;
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            return await window.WaitForLeaseDecisionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (window.IsVisible) window.Close();
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task EnableFormalAcceptanceAsync(
        AdapterWriteAuthorizationRequest request,
        string evidenceHash,
        CancellationToken cancellationToken = default)
    {
        await dispatcher.InvokeAsync(() =>
        {
            AcceptanceConfirmationWindow? window;
            lock (sync) windows.TryGetValue(AcceptanceWriteLeaseIdentity.From(request), out window);
            return window?.EnableVersionAcceptance(evidenceHash) == true;
        }, cancellationToken).ConfigureAwait(false);
    }
}
