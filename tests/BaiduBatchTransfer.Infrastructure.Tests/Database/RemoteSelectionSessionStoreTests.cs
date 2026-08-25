using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class RemoteSelectionSessionStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-picker-session-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreatePersistsAnActiveExactIdentityThatCanOwnOneWebAttempt()
    {
        var graph = await CreateAsync();

        var session = await graph.Sessions.CreateAsync(graph.AccountId, graph.Now);

        Assert.Equal(graph.AccountId, session.AccountId);
        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.NotEqual(Guid.Empty, session.WorkspaceDraftId);
        Assert.Equal(1, session.Generation);
        Assert.Equal(RemoteSelectionSessionState.Active, session.State);
        Assert.Equal(0, session.Revision);
        Assert.True(await new SqliteWebAutomationRequestOwnershipStore(graph.Factory).IsBoundAsync(
            session.SessionId, session.WorkspaceDraftId, graph.AccountId, session.Generation,
            WebAutomationOwnerKind.RemoteSelection));
        Assert.True(await new SqliteWebAutomationAttemptStore(graph.Factory).BeginAsync(
            WebAutomationAttemptRecord.Create(session.SessionId, graph.AccountId, session.Generation,
                WebAutomationOwnerKind.RemoteSelection, graph.Now)));
    }

    [Fact]
    public async Task DeletedOrDisabledAccountCannotCreateASelectionSession()
    {
        var graph = await CreateAsync();
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Accounts SET Status='Deleted',IsDeleted=1,IsEnabled=0 WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", graph.AccountId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            graph.Sessions.CreateAsync(graph.AccountId, graph.Now));
    }

    [Fact]
    public async Task AccountDeletedAfterSessionCreationInvalidatesQueuedOwnershipAndAttemptStart()
    {
        var graph = await CreateAsync();
        var session = await graph.Sessions.CreateAsync(graph.AccountId, graph.Now);
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Accounts SET Status='Deleted',IsDeleted=1,IsEnabled=0 WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", graph.AccountId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        Assert.False(await new SqliteWebAutomationRequestOwnershipStore(graph.Factory).IsBoundAsync(
            session.SessionId, session.WorkspaceDraftId, graph.AccountId, 1,
            WebAutomationOwnerKind.RemoteSelection));
        Assert.False(await new SqliteWebAutomationAttemptStore(graph.Factory).BeginAsync(
            WebAutomationAttemptRecord.Create(session.SessionId, graph.AccountId, 1,
                WebAutomationOwnerKind.RemoteSelection, graph.Now.AddSeconds(1))));
    }

    [Fact]
    public async Task OwnershipRejectsWrongSessionDraftAccountGenerationAndTerminalSession()
    {
        var graph = await CreateAsync();
        var session = await graph.Sessions.CreateAsync(graph.AccountId, graph.Now);
        var ownership = new SqliteWebAutomationRequestOwnershipStore(graph.Factory);

        Assert.False(await ownership.IsBoundAsync(Guid.NewGuid(), session.WorkspaceDraftId,
            graph.AccountId, 1, WebAutomationOwnerKind.RemoteSelection));
        Assert.False(await ownership.IsBoundAsync(session.SessionId, Guid.NewGuid(),
            graph.AccountId, 1, WebAutomationOwnerKind.RemoteSelection));
        Assert.False(await ownership.IsBoundAsync(session.SessionId, session.WorkspaceDraftId,
            graph.OtherAccountId, 1, WebAutomationOwnerKind.RemoteSelection));
        Assert.False(await ownership.IsBoundAsync(session.SessionId, session.WorkspaceDraftId,
            graph.AccountId, 2, WebAutomationOwnerKind.RemoteSelection));

        Assert.True(await graph.Sessions.TryCompleteAsync(session.SessionId, session.WorkspaceDraftId,
            graph.AccountId, 1, 0, RemoteSelectionSessionState.Cancelled, graph.Now.AddSeconds(1)));
        Assert.False(await ownership.IsBoundAsync(session.SessionId, session.WorkspaceDraftId,
            graph.AccountId, 1, WebAutomationOwnerKind.RemoteSelection));
        Assert.False(await new SqliteWebAutomationAttemptStore(graph.Factory).BeginAsync(
            WebAutomationAttemptRecord.Create(session.SessionId, graph.AccountId, 1,
                WebAutomationOwnerKind.RemoteSelection, graph.Now.AddSeconds(2))));
    }

    [Theory]
    [InlineData(RemoteSelectionSessionState.Confirmed)]
    [InlineData(RemoteSelectionSessionState.Cancelled)]
    [InlineData(RemoteSelectionSessionState.NeedsDecision)]
    public async Task CompletionIsAnExactTerminalCasAndCannotBeReopened(RemoteSelectionSessionState terminal)
    {
        var graph = await CreateAsync();
        var session = await graph.Sessions.CreateAsync(graph.AccountId, graph.Now);
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            session.SessionId, graph.AccountId, 1, WebAutomationOwnerKind.RemoteSelection, graph.Now)));

        Assert.True(await graph.Sessions.TryCompleteAsync(session.SessionId, session.WorkspaceDraftId,
            graph.AccountId, 1, 0, terminal, graph.Now.AddSeconds(1)));
        Assert.False(await graph.Sessions.TryCompleteAsync(session.SessionId, session.WorkspaceDraftId,
            graph.AccountId, 1, 0, RemoteSelectionSessionState.Cancelled, graph.Now.AddSeconds(2)));
        Assert.False(await graph.Sessions.TryCompleteAsync(session.SessionId, Guid.NewGuid(),
            graph.AccountId, 1, 1, RemoteSelectionSessionState.Cancelled, graph.Now.AddSeconds(2)));

        var restored = await graph.Sessions.GetAsync(session.SessionId, session.WorkspaceDraftId,
            graph.AccountId, 1);
        Assert.Equal(terminal, restored!.State);
        Assert.Equal(1, restored.Revision);
        Assert.Equal(
            terminal == RemoteSelectionSessionState.NeedsDecision
                ? WebAutomationAttemptState.NeedsDecision
                : WebAutomationAttemptState.DefinitelyNotSubmitted,
            (await attempts.GetAsync(session.SessionId, graph.AccountId, 1))!.State);
    }

    [Fact]
    public async Task RestartRecoveryConservativelyCancelsOnlyActiveSessionsAndIsIdempotent()
    {
        var graph = await CreateAsync();
        var active = await graph.Sessions.CreateAsync(graph.AccountId, graph.Now);
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            active.SessionId, graph.AccountId, 1, WebAutomationOwnerKind.RemoteSelection, graph.Now)));
        var confirmed = await graph.Sessions.CreateAsync(graph.AccountId, graph.Now);
        Assert.True(await graph.Sessions.TryCompleteAsync(confirmed.SessionId, confirmed.WorkspaceDraftId,
            graph.AccountId, 1, 0, RemoteSelectionSessionState.Confirmed, graph.Now.AddSeconds(1)));

        var reopened = new SqliteRemoteSelectionSessionStore(graph.Factory);
        Assert.Equal(1, await reopened.RecoverIncompleteAsync(graph.Now.AddMinutes(1)));
        Assert.Equal(0, await reopened.RecoverIncompleteAsync(graph.Now.AddMinutes(2)));

        Assert.Equal(RemoteSelectionSessionState.Cancelled,
            (await reopened.GetAsync(active.SessionId, active.WorkspaceDraftId, graph.AccountId, 1))!.State);
        Assert.Equal(RemoteSelectionSessionState.Confirmed,
            (await reopened.GetAsync(confirmed.SessionId, confirmed.WorkspaceDraftId, graph.AccountId, 1))!.State);
        Assert.Equal(WebAutomationAttemptState.DefinitelyNotSubmitted,
            (await attempts.GetAsync(active.SessionId, graph.AccountId, 1))!.State);
    }

    [Fact]
    public async Task HandoffCancelDurablyCancelsOnlyTheSelectionSessionAndResolvesReadOnlyAttempt()
    {
        var graph = await CreateAsync();
        var session = await graph.Sessions.CreateAsync(graph.AccountId, graph.Now);
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            session.SessionId, graph.AccountId, 1, WebAutomationOwnerKind.RemoteSelection, graph.Now)));
        Assert.True(await attempts.TryAdvanceAsync(session.SessionId, graph.AccountId, 1,
            WebAutomationAttemptState.Preparing, 0, WebAutomationAttemptState.UserHandoff,
            new string('a', 64), true, graph.Now.AddSeconds(1)));
        var handoff = await attempts.GetAsync(session.SessionId, graph.AccountId, 1);
        var cancellation = new SqliteWebAutomationBatchCancellationStore(graph.Factory);

        var disposition = await cancellation.CancelAsync(
            session.SessionId, session.WorkspaceDraftId, graph.AccountId, 1,
            WebAutomationOwnerKind.RemoteSelection, handoff!.Revision,
            WebAutomationAttemptState.Preparing, graph.Now.AddSeconds(2));

        Assert.Equal(WebAutomationBatchCancellationDisposition.Cancelled, disposition);
        Assert.Equal(WebAutomationBatchCancellationDisposition.AlreadyCancelled,
            await cancellation.CancelAsync(
                session.SessionId, session.WorkspaceDraftId, graph.AccountId, 1,
                WebAutomationOwnerKind.RemoteSelection, handoff.Revision,
                WebAutomationAttemptState.Preparing, graph.Now.AddSeconds(3)));
        Assert.True(await cancellation.IsCancelledAsync(
            session.WorkspaceDraftId, WebAutomationOwnerKind.RemoteSelection));
        Assert.Equal(RemoteSelectionSessionState.Cancelled,
            (await graph.Sessions.GetAsync(session.SessionId, session.WorkspaceDraftId,
                graph.AccountId, 1))!.State);
        Assert.Equal(WebAutomationAttemptState.DefinitelyNotSubmitted,
            (await attempts.GetAsync(session.SessionId, graph.AccountId, 1))!.State);
    }

    private async Task<Graph> CreateAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, $"picker-{Guid.NewGuid():N}.db"));
        await new DatabaseMigrator(factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        await using var connection = await factory.OpenConnectionAsync();
        foreach (var id in new[] { accountId, otherAccountId })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,
                  DailyExecutedCount,SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
                VALUES($id,$user,'account','WebView2','Active',0,0,0,1,'2026-08-25',0);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$user", id.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }
        return new(factory, new SqliteRemoteSelectionSessionStore(factory), accountId,
            otherAccountId, DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed record Graph(SqliteConnectionFactory Factory,
        SqliteRemoteSelectionSessionStore Sessions, Guid AccountId, Guid OtherAccountId,
        DateTimeOffset Now);
}
