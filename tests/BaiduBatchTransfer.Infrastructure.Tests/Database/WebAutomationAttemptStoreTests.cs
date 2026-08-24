using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class WebAutomationAttemptStoreTests : IDisposable
{
    // Ownership note: browser attempts duplicate no TransferOperations/ShareOperations result facts.
    // They persist only the UI phase, sanitized observation fingerprint, and exact pre-submit
    // remote directory set needed to recover one account-bound generation.
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-web-attempt-{Guid.NewGuid():N}");

    [Fact]
    public async Task TransferBaseline_RoundTripsExactPreSubmitDirectorySet()
    {
        var graph = await CreateAsync();
        var attempt = WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now);

        Assert.True(await graph.Store.BeginAsync(attempt));
        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 2, 0,
            ExactBaseline("before-2", "before-1"), Fingerprint('a'), graph.Now.AddSeconds(1)));

        var restored = await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 2);

        Assert.NotNull(restored);
        Assert.Equal(WebAutomationAttemptState.BaselinePersisted, restored.State);
        Assert.Equal(
            new[] { "before-1", "before-2" },
            restored.BaselineRemoteItems.Select(item => item.ExactId).Order());
    }

    [Fact]
    public async Task Advance_RejectsOldGenerationWrongAccountStaleRevisionAndInvalidTransition()
    {
        var graph = await CreateAsync();
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now));

        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 1, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.DefinitelyNotSubmitted, Fingerprint('b'), false, graph.Now));
        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, Guid.NewGuid(), 2, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.DefinitelyNotSubmitted, Fingerprint('b'), false, graph.Now));
        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 2, WebAutomationAttemptState.Preparing, 1,
            WebAutomationAttemptState.DefinitelyNotSubmitted, Fingerprint('b'), false, graph.Now));
        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 2, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.Succeeded, Fingerprint('b'), false, graph.Now));
    }

    [Fact]
    public async Task ResultCannotSucceedFromFingerprintWithoutExactPersistedBaseline()
    {
        var graph = await CreateAsync();
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now));

        var changed = await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 1, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.Succeeded, Fingerprint('c'), false, graph.Now.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(WebAutomationAttemptState.Preparing,
            (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!.State);
    }

    [Fact]
    public async Task RecoveryReadsAreBoundedAndModelsRedactEvidence()
    {
        var graph = await CreateAsync();
        for (var index = 0; index < 3; index++)
        {
            await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
                Guid.NewGuid(), graph.AccountId, 1, WebAutomationOwnerKind.StandaloneShare,
                graph.Now.AddSeconds(index)));
        }

        var page = await graph.Store.GetRecoveryPageAsync(new PageQuery(1, 2));

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.TotalCount);
        var text = page.Items[0].ToString();
        Assert.Contains("[REDACTED]", text);
        Assert.DoesNotContain(Fingerprint('a'), text, StringComparison.Ordinal);
    }

    private static WebAutomationBaseline ExactBaseline(params string[] ids) =>
        WebAutomationBaseline.Create(ids.Select(id => new RemoteItemIdentity(id, $"name-{id}")));

    private static string Fingerprint(char value) => new(value, 64);

    private async Task<Graph> CreateAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        return new Graph(
            new SqliteWebAutomationAttemptStore(factory),
            Guid.NewGuid(), Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Graph(
        SqliteWebAutomationAttemptStore Store,
        Guid OperationId,
        Guid AccountId,
        DateTimeOffset Now);
}
