using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteRemoteSelectionSessionStore(
    SqliteConnectionFactory factory) : IRemoteSelectionSessionStore
{
    public async Task<RemoteSelectionSession> CreateAsync(
        Guid accountId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        var session = new RemoteSelectionSession(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            RemoteSelectionSessionState.Active, 0, now, now);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO RemoteSelectionSessions(
                SessionId,WorkspaceDraftId,AccountId,Generation,State,Revision,CreatedAt,UpdatedAt)
            SELECT $session,$draft,$account,$generation,$state,0,$now,$now
            WHERE EXISTS(SELECT 1 FROM Accounts
              WHERE Id=$account AND Status='Active' AND IsDeleted=0 AND IsEnabled=1);
            """;
        AddIdentity(command, session.SessionId, session.WorkspaceDraftId, accountId, 1);
        command.Parameters.AddWithValue("$state", RemoteSelectionSessionState.Active.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The account cannot own a resource selection session.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<RemoteSelectionSession?> GetAsync(
        Guid sessionId,
        Guid workspaceDraftId,
        Guid accountId,
        int generation,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, workspaceDraftId, accountId, generation);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT State,Revision,CreatedAt,UpdatedAt FROM RemoteSelectionSessions
            WHERE SessionId=$session AND WorkspaceDraftId=$draft AND AccountId=$account
              AND Generation=$generation;
            """;
        AddIdentity(command, sessionId, workspaceDraftId, accountId, generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new RemoteSelectionSession(sessionId, workspaceDraftId, accountId, generation,
            Enum.Parse<RemoteSelectionSessionState>(reader.GetString(0)), reader.GetInt64(1),
            DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public async Task<bool> TryCompleteAsync(
        Guid sessionId,
        Guid workspaceDraftId,
        Guid accountId,
        int generation,
        long expectedRevision,
        RemoteSelectionSessionState terminalState,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sessionId, workspaceDraftId, accountId, generation);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (terminalState == RemoteSelectionSessionState.Active || !Enum.IsDefined(terminalState))
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE RemoteSelectionSessions SET State=$state,Revision=Revision+1,UpdatedAt=$now
                WHERE SessionId=$session AND WorkspaceDraftId=$draft AND AccountId=$account
                  AND Generation=$generation AND State='Active' AND Revision=$revision
                  AND ($state<>'Confirmed' OR EXISTS(
                    SELECT 1 FROM Accounts account
                    WHERE account.Id=$account AND account.Status='Active'
                      AND account.IsEnabled=1 AND account.IsDeleted=0));
                """;
            AddIdentity(command, sessionId, workspaceDraftId, accountId, generation);
            command.Parameters.AddWithValue("$state", terminalState.ToString());
            command.Parameters.AddWithValue("$revision", expectedRevision);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            await using var attempt = connection.CreateCommand();
            attempt.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            attempt.CommandText = """
                UPDATE WebAutomationAttempts
                SET State=$attemptState,ResumeStage=NULL,IsWaitingForUser=0,
                    Revision=Revision+1,UpdatedAt=$now
                WHERE OperationId=$session AND AccountId=$account AND Generation=$generation
                  AND OwnerKind='RemoteSelection'
                  AND State IN ('Preparing','BaselinePersisted','Submitting','ResultPending','UserHandoff');
                """;
            attempt.Parameters.AddWithValue("$attemptState",
                terminalState == RemoteSelectionSessionState.NeedsDecision
                    ? WebAutomationAttemptState.NeedsDecision.ToString()
                    : WebAutomationAttemptState.DefinitelyNotSubmitted.ToString());
            attempt.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            attempt.Parameters.AddWithValue("$account", accountId.ToString("D"));
            attempt.Parameters.AddWithValue("$generation", generation);
            attempt.Parameters.AddWithValue("$now", now.ToString("O"));
            await attempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> RecoverIncompleteAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var updated = now.ToString("O");
        try
        {
            await using (var attempts = connection.CreateCommand())
            {
                attempts.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                attempts.CommandText = """
                    UPDATE WebAutomationAttempts AS attempt
                    SET State='DefinitelyNotSubmitted',ResumeStage=NULL,IsWaitingForUser=0,
                        Revision=Revision+1,UpdatedAt=$now
                    WHERE OwnerKind='RemoteSelection'
                      AND State IN ('Preparing','BaselinePersisted','Submitting','ResultPending','UserHandoff')
                      AND EXISTS(SELECT 1 FROM RemoteSelectionSessions owner
                        WHERE owner.SessionId=attempt.OperationId
                          AND owner.AccountId=attempt.AccountId
                          AND owner.Generation=attempt.Generation
                          AND owner.State='Active');
                    """;
                attempts.Parameters.AddWithValue("$now", updated);
                await attempts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var sessions = connection.CreateCommand();
            sessions.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            sessions.CommandText = """
                UPDATE RemoteSelectionSessions SET State='Cancelled',Revision=Revision+1,UpdatedAt=$now
                WHERE State='Active';
                """;
            sessions.Parameters.AddWithValue("$now", updated);
            var count = await sessions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void AddIdentity(Microsoft.Data.Sqlite.SqliteCommand command,
        Guid sessionId, Guid workspaceDraftId, Guid accountId, int generation)
    {
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$draft", workspaceDraftId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
    }

    private static void ValidateIdentity(Guid sessionId, Guid workspaceDraftId, Guid accountId, int generation)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session is required.", nameof(sessionId));
        if (workspaceDraftId == Guid.Empty) throw new ArgumentException("A workspace draft is required.", nameof(workspaceDraftId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
    }
}
