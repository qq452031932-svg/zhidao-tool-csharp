using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class SqliteShareAutomationAcceptanceEvidenceStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-share-evidence-{Guid.NewGuid():N}");

    [Fact]
    public async Task PreexistingExactEvidenceCanAtomicallyFinishACrashInterruptedSubmittingAttempt()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var accountId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var items = new RemoteItemIdentitySet([new RemoteItemIdentity("remote-id", "item")]);
        var context = new ShareOperationAutomationContext(
            operationId, Guid.NewGuid(), accountId, 1, WebAutomationOwnerKind.StandaloneShare, items).Validate();
        await SeedSubmittingAttemptAsync(factory, context, baselineItemCount: 0, revision: 1);
        var acceptance = ShareOfficialAcceptance.Accepted(
            "adapter", "share", "https://pan.baidu.com/s/share", items,
            new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Fixed, "A1b2"),
            new string('b', 64));
        var store = new SqliteShareAutomationAcceptanceEvidenceStore(factory);

        await store.PersistAsync(context, acceptance);
        var restored = await store.GetAsync(context);
        var reconstructedFromListing = ShareOfficialAcceptance.Accepted(
            "share", "share", "https://pan.baidu.com/s/share", items,
            new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Fixed, "A1b2"),
            new string('c', 64));

        Assert.Equal(acceptance, restored);
        Assert.True(await store.TryPersistRecoveredAsync(context, reconstructedFromListing));
        Assert.Equal(acceptance, await store.GetAsync(context));
        Assert.False(await store.TryPersistRecoveredAsync(context, reconstructedFromListing));
        await using (var verify = await factory.OpenConnectionAsync())
        await using (var command = verify.CreateCommand())
        {
            command.CommandText = "SELECT State,AcceptedItemCount FROM WebAutomationAttempts WHERE OperationId=$operation;";
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("ResultPending", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistAsync(
            context with { Generation = 2 }, acceptance));
    }

    [Fact]
    public async Task InvalidPermanentOrUnlimitedEvidenceIsRejectedBeforeAnyRowIsWritten()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "invalid.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups-invalid")), "test").MigrateAsync();
        var accountId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var items = new RemoteItemIdentitySet([new RemoteItemIdentity("remote-id", "item")]);
        var context = new ShareOperationAutomationContext(
            operationId, Guid.NewGuid(), accountId, 1, WebAutomationOwnerKind.StandaloneShare, items).Validate();
        await SeedSubmittingAttemptAsync(factory, context, baselineItemCount: 0, revision: 1);
        var invalid = ShareOfficialAcceptance.Accepted(
            "adapter", "share", "https://pan.baidu.com/s/share", items,
            new EffectiveShareSettings(false, true, ShareExtractionCodeBehavior.Random, "A1b2"),
            new string('b', 64));
        var store = new SqliteShareAutomationAcceptanceEvidenceStore(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistAsync(context, invalid));

        Assert.Null(await store.GetAsync(context));
    }

    [Fact]
    public async Task RecoveredEvidenceAndAttemptStageArePersistedInOneExactTransaction()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "recovered.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups-recovered")), "test").MigrateAsync();
        var accountId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var items = new RemoteItemIdentitySet([new RemoteItemIdentity("remote-id", "item")]);
        var context = new ShareOperationAutomationContext(
            operationId, Guid.NewGuid(), accountId, 1, WebAutomationOwnerKind.StandaloneShare, items).Validate();
        await SeedSubmittingAttemptAsync(factory, context, baselineItemCount: 1, revision: 4);
        var acceptance = ShareOfficialAcceptance.Accepted(
            "share-recovered", "share-recovered", "https://pan.baidu.com/s/share-recovered", items,
            new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Random, "A1b2"),
            new string('b', 64));
        var store = new SqliteShareAutomationAcceptanceEvidenceStore(factory);

        Assert.True(await store.TryPersistRecoveredAsync(context, acceptance));
        Assert.False(await store.TryPersistRecoveredAsync(context, acceptance));

        await using var verify = await factory.OpenConnectionAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = """
            SELECT State,AcceptedItemCount,Revision,
              (SELECT COUNT(*) FROM WebAutomationShareAcceptances WHERE OperationId=$operation),
              (SELECT COUNT(*) FROM WebAutomationAttemptAcceptedRemoteItems WHERE OperationId=$operation)
            FROM WebAutomationAttempts WHERE OperationId=$operation;
            """;
        verifyCommand.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ResultPending", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(5, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(1, reader.GetInt32(4));
    }

    private static async Task SeedSubmittingAttemptAsync(
        SqliteConnectionFactory factory,
        ShareOperationAutomationContext context,
        int baselineItemCount,
        long revision)
    {
        var now = DateTimeOffset.UtcNow;
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(AccountRecord.Create(context.AccountId, "user", "account"));
            await unit.CommitAsync();
        }
        var batch = StandaloneShareBatchRecord.Create(
            context.BatchId, Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, "results"), 1, now);
        var operation = StandaloneShareOperationRecord.Create(
            context.OperationId, context.BatchId,
            new OwnedRemoteSelection(context.AccountId, context.ExactRemoteItems,
                "item", RemoteResourceKind.File, new RemotePath("/")),
            1, now);
        var receipt = await new SqliteStandaloneShareStore(factory).CreateOrGetBatchAsync(
            StandaloneSharePendingBatchEnvelope.Create(batch, [operation]));
        Assert.Equal(context.OperationId, Assert.Single(receipt.Operations).Id);

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WebAutomationAttempts(
              OperationId,AccountId,Generation,OwnerKind,State,ResumeStage,ObservationFingerprint,
              BaselineFingerprint,BaselineItemCount,BaselinePersistedAt,IsWaitingForUser,Revision,CreatedAt,UpdatedAt,
              AcceptanceFingerprint,AcceptedItemCount,AcceptancePersistedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            SELECT Id,AccountId,$generation,'StandaloneShare','Submitting',NULL,NULL,
              $baseline,$baselineCount,$now,0,$revision,$now,$now,NULL,0,NULL,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId
            FROM StandaloneShareOperations WHERE Id=$operation AND AccountId=$account;
            """;
        command.Parameters.AddWithValue("$account", context.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$operation", context.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$generation", context.Generation);
        command.Parameters.AddWithValue("$baseline", new string('a', 64));
        command.Parameters.AddWithValue("$baselineCount", baselineItemCount);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
