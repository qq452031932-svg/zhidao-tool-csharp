using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Services;

public sealed class WebView2ProfileCloser : IWebViewProfileCloser
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Registration> registrations = [];

    public IDisposable Register(Guid accountId, Func<CancellationToken, Task> closeAsync)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account ID is required.", nameof(accountId));
        ArgumentNullException.ThrowIfNull(closeAsync);
        var registration = new Registration(this, accountId, closeAsync);
        lock (gate)
        {
            if (!registrations.TryAdd(accountId, registration))
                throw new InvalidOperationException("This account profile is already open.");
        }
        return registration;
    }

    public async Task CloseAsync(Guid accountId, CancellationToken cancellationToken)
    {
        Registration? registration;
        lock (gate)
        {
            if (!registrations.Remove(accountId, out registration)) return;
            registration.Detach();
        }
        await registration.CloseAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Unregister(Registration registration)
    {
        lock (gate)
            if (registrations.GetValueOrDefault(registration.AccountId) == registration)
                registrations.Remove(registration.AccountId);
    }

    private sealed class Registration(
        WebView2ProfileCloser owner,
        Guid accountId,
        Func<CancellationToken, Task> closeAsync) : IDisposable
    {
        private WebView2ProfileCloser? owner = owner;
        public Guid AccountId { get; } = accountId;
        public Task CloseAsync(CancellationToken cancellationToken) => closeAsync(cancellationToken);
        public void Detach() => Interlocked.Exchange(ref owner, null);
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Unregister(this);
    }
}
