using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferQuarantineRegistryTests
{
    [Fact]
    public void RegistrationApiCoversEveryManagedTransferQuarantineType()
    {
        var overloads = typeof(ManagedTransferQuarantineRegistry)
            .GetMethods(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            .Where(method => method.Name == "TryRegister" && method.GetParameters().Length == 2)
            .Select(method => method.GetParameters()[1].ParameterType)
            .ToArray();

        Assert.Contains(typeof(ManagedTransferDirectoryPreparationQuarantine), overloads);
        Assert.Contains(typeof(ManagedTransferPickerQuarantine), overloads);
        Assert.Contains(typeof(ManagedTransferTargetBaselineQuarantine), overloads);
        Assert.Contains(typeof(ManagedTransferFinalSubmissionQuarantine), overloads);
    }

    [Fact]
    public void PreSubmissionStageCanRegisterWithoutSubmissionId()
    {
        var registry = new ManagedTransferQuarantineRegistry();
        var key = Key(ManagedTransferQuarantineStage.Picker, submissionId: null);

        Assert.True(registry.TryRegister(new FakeQuarantine(key)));
        Assert.True(registry.IsAccountQuarantined(key.AccountId));
    }

    [Fact]
    public void SameAccountCannotOwnSecondStageAndExistingEntryIsNotOverwritten()
    {
        var registry = new ManagedTransferQuarantineRegistry();
        var first = new FakeQuarantine(Key(ManagedTransferQuarantineStage.DirectoryPreparation));
        var second = new FakeQuarantine(Key(
            ManagedTransferQuarantineStage.FinalSubmission,
            accountId: first.Key.AccountId,
            operationId: Guid.NewGuid()));

        Assert.True(registry.TryRegister(first));
        Assert.False(registry.TryRegister(second));

        Assert.Same(first, registry.Find(first.Key));
        Assert.Null(registry.Find(second.Key));
    }

    [Fact]
    public async Task ConcurrentReconciliationCallsMergeAndReleasedEntryIsCompareRemoved()
    {
        var registry = new ManagedTransferQuarantineRegistry();
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var quarantine = new FakeQuarantine(
            Key(ManagedTransferQuarantineStage.TargetBaseline),
            async _ =>
            {
                await gate.Task;
                return ManagedTransferQuarantineReconciliation.Released;
            });
        Assert.True(registry.TryRegister(quarantine));

        var first = registry.ReconcileAsync(quarantine.Key, 1_000, CancellationToken.None);
        var second = registry.ReconcileAsync(quarantine.Key, 1_000, CancellationToken.None);
        await WaitUntilAsync(() => quarantine.Calls == 1);
        gate.SetResult();

        Assert.Equal(ManagedTransferQuarantineReconciliation.Released, await first);
        Assert.Equal(ManagedTransferQuarantineReconciliation.Released, await second);
        Assert.Equal(1, quarantine.Calls);
        Assert.False(registry.IsAccountQuarantined(quarantine.Key.AccountId));
    }

    [Fact]
    public async Task RetainedResultKeepsEntryAndDoesNotReleaseOwnedLease()
    {
        var lease = new TrackingDisposable();
        var key = Key(ManagedTransferQuarantineStage.TargetBaseline);
        var concrete = new ManagedTransferTargetBaselineQuarantine(
            Task.CompletedTask,
            (_, _) => Task.FromResult(false),
            [],
            lease);
        var registry = new ManagedTransferQuarantineRegistry();
        Assert.True(registry.TryRegister(key, concrete));

        var result = await registry.ReconcileAsync(key, 1_000, CancellationToken.None);

        Assert.Equal(ManagedTransferQuarantineReconciliation.Retained, result);
        Assert.False(lease.Disposed);
        Assert.True(registry.IsAccountQuarantined(key.AccountId));
    }

    private static ManagedTransferQuarantineKey Key(
        ManagedTransferQuarantineStage stage,
        Guid? accountId = null,
        Guid? operationId = null,
        Guid? submissionId = null) =>
        new(stage, operationId ?? Guid.NewGuid(), accountId ?? Guid.NewGuid(),
            1, Guid.NewGuid(), 0, submissionId);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }

    private sealed class FakeQuarantine : IManagedTransferQuarantine
    {
        private readonly Func<CancellationToken, Task<ManagedTransferQuarantineReconciliation>> reconcile;

        internal FakeQuarantine(
            ManagedTransferQuarantineKey key,
            Func<CancellationToken, Task<ManagedTransferQuarantineReconciliation>>? reconcile = null)
        {
            Key = key;
            this.reconcile = reconcile ?? (_ => Task.FromResult(
                ManagedTransferQuarantineReconciliation.Retained));
        }

        public ManagedTransferQuarantineKey Key { get; }
        internal int Calls { get; private set; }

        public Task<ManagedTransferQuarantineReconciliation> ReconcileAsync(
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            Calls++;
            return reconcile(cancellationToken);
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
