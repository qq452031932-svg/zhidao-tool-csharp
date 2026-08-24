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
        Task close;
        lock (gate)
        {
            if (!registrations.TryGetValue(accountId, out registration)) return;
            close = registration.BeginClose(cancellationToken);
        }
        try { await close.ConfigureAwait(false); }
        catch
        {
            MarkCloseFailed(registration, close);
            throw;
        }
        CompleteClose(registration, close);
    }

    private void Unregister(Registration registration)
    {
        lock (gate)
            if (registration.CanUnregister &&
                registrations.GetValueOrDefault(registration.AccountId) == registration)
                registrations.Remove(registration.AccountId);
    }

    private void CompleteClose(Registration registration, Task close)
    {
        lock (gate)
        {
            if (registrations.GetValueOrDefault(registration.AccountId) == registration &&
                registration.TryComplete(close))
                registrations.Remove(registration.AccountId);
            registration.Detach();
        }
    }

    private void MarkCloseFailed(Registration registration, Task close)
    {
        lock (gate)
        {
            if (registrations.GetValueOrDefault(registration.AccountId) == registration)
                registration.TryFail(close);
        }
    }

    private sealed class Registration(
        WebView2ProfileCloser owner,
        Guid accountId,
        Func<CancellationToken, Task> closeAsync) : IDisposable
    {
        private enum RegistrationState { Open, Closing, CloseFailed, Closed }
        private WebView2ProfileCloser? owner = owner;
        private Task? closeTask;
        private RegistrationState state;
        public Guid AccountId { get; } = accountId;
        public bool CanUnregister => state == RegistrationState.Open;

        public Task BeginClose(CancellationToken cancellationToken)
        {
            if (state == RegistrationState.Closing) return closeTask!;
            if (state == RegistrationState.Closed) return Task.CompletedTask;
            state = RegistrationState.Closing;
            return closeTask = InvokeCloseAsync(cancellationToken);
        }

        public bool TryComplete(Task close)
        {
            if (state != RegistrationState.Closing || closeTask != close) return false;
            state = RegistrationState.Closed;
            closeTask = null;
            return true;
        }

        public void TryFail(Task close)
        {
            if (state != RegistrationState.Closing || closeTask != close) return;
            state = RegistrationState.CloseFailed;
            closeTask = null;
        }

        private async Task InvokeCloseAsync(CancellationToken cancellationToken) =>
            await closeAsync(cancellationToken).ConfigureAwait(false);

        public void Detach() => Interlocked.Exchange(ref owner, null);
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Unregister(this);
    }
}
