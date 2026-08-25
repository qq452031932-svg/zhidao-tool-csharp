namespace BaiduBatchTransfer.App.Services;

public sealed class LoginWindowCloseGate
{
    private readonly Task profilePreparationStopped;
    private readonly Func<CancellationToken, Task> closeProfileAsync;

    public LoginWindowCloseGate(
        Task profilePreparationStopped,
        Func<CancellationToken, Task> closeProfileAsync)
    {
        this.profilePreparationStopped = profilePreparationStopped
            ?? throw new ArgumentNullException(nameof(profilePreparationStopped));
        this.closeProfileAsync = closeProfileAsync
            ?? throw new ArgumentNullException(nameof(closeProfileAsync));
    }

    public async Task WaitForSafeCloseAsync(CancellationToken cancellationToken)
    {
        await profilePreparationStopped.WaitAsync(cancellationToken).ConfigureAwait(false);
        await closeProfileAsync(cancellationToken).ConfigureAwait(false);
    }
}
