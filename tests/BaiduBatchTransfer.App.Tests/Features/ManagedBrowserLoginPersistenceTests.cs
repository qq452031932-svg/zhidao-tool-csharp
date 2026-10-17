using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Application.Accounts;
using Microsoft.Data.Sqlite;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserLoginPersistenceTests
{
    [Fact]
    public async Task DirectorySelectionPersistsAndRejectsStaleIntentAcrossInstances()
    {
        var factory = await CreateDatabaseAsync();
        IBrowserDirectoryState Create() => new SqliteBrowserDirectoryState(factory);
        var first = Create();
        var empty = await first.LoadAsync(default);
        Assert.Null(empty.Directory);
        var claim = new BrowserDirectoryIntentClaim(BrowserDirectorySelectionIntentKind.EnsurePrompt, Guid.NewGuid(), Guid.NewGuid());
        var begin = await first.TryBeginEnsurePromptAsync(empty.Revision, claim, default);
        Assert.Equal(BrowserDirectoryIntentBeginStatus.Acquired, begin.Status);
        var other = Create();
        Assert.Equal(BrowserDirectoryIntentBeginStatus.Conflict,
            (await other.TryBeginEnsurePromptAsync(empty.Revision, claim, default)).Status);
        Assert.Equal(BrowserDirectoryStateSaveStatus.Committed,
            (await first.TrySaveAsync(@"C:\Browser", begin.Intent!.Revision, begin.Intent, default)).Status);
        Assert.Equal(@"C:\Browser", (await other.LoadAsync(default)).Directory);
        Assert.False(await other.TryAbandonSelectionAsync(begin.Intent, default));
    }

    [Theory]
    [InlineData(true, "123", "Bound")]
    [InlineData(false, "123", "Bound")]
    [InlineData(false, "456", "AccountIdentityMismatch")]
    public async Task BindingCreatesReservedAccountOrPreservesSelectedLegacyAccount(bool addNew, string observedUid, string expected)
    {
        var factory = await CreateDatabaseAsync();
        var accountId = Guid.NewGuid();
        var units = new SqliteUnitOfWorkFactory(factory);
        if (!addNew)
        {
            await using var unit = await units.BeginAsync();
            await unit.Accounts.UpsertAsync(AccountRecord.Create(accountId, "123", "旧账号") with
            { EncryptedSession = [1, 2, 3], WebView2ProfileDirectory = @"C:\legacy" });
            await unit.CommitAsync();
        }
        var installation = Installation();
        var session = Session(accountId, installation, observedUid);
        var result = await new SqliteManagedBrowserStateStore(factory).BindLoginAsync(addNew, installation, session, null, default);
        Assert.Equal(expected, result.ToString());
        await using var read = await units.BeginAsync();
        var account = await read.Accounts.GetByIdAsync(accountId);
        Assert.NotNull(account);
        Assert.Equal(addNew ? "ManagedChromium" : "WebView2", account.LoginMethod);
        Assert.Equal("123", account.BaiduUserId);
        Assert.Equal(addNew ? null : new byte[] { 1, 2, 3 }, account.EncryptedSession);
        Assert.Equal(addNew ? null : @"C:\legacy", account.WebView2ProfileDirectory);
        await read.RollbackAsync();
        var saved = await new SqliteManagedBrowserStateStore(factory).LoadActiveSessionAsync(accountId, BrowserFamily.Chrome, default);
        Assert.Equal(expected == "Bound", saved is not null);
    }

    [Fact]
    public async Task ReloginAfterExitReleasesTombstoneIdentityInSameTransaction()
    {
        var factory = await CreateDatabaseAsync();
        var units = new SqliteUnitOfWorkFactory(factory);
        var formerId = Guid.NewGuid();
        await using (var unit = await units.BeginAsync())
        {
            await unit.Accounts.UpsertAsync(AccountRecord.Create(formerId, "123", "已退出账号") with
            {
                Status = "Deleted", IsEnabled = false, IsDeleted = true, DeletedAt = DateTimeOffset.UtcNow
            });
            await unit.CommitAsync();
        }
        var reserved = Guid.NewGuid();
        var installation = Installation();
        var result = await new SqliteManagedBrowserStateStore(factory).BindLoginAsync(
            true, installation, Session(reserved, installation, "123"), null, default);
        Assert.Equal("Bound", result.ToString());
        await using var read = await units.BeginAsync();
        Assert.Equal(reserved, (await read.Accounts.GetByBaiduUserIdAsync("123"))!.Id);
        var former = (await read.Accounts.GetByIdAsync(formerId))!;
        Assert.Equal($"removed:{formerId:N}", former.BaiduUserId);
        Assert.True(former.IsRemovedTombstone);
        await read.RollbackAsync();
    }

    [Fact]
    public async Task DuplicateIdentityCannotLeaveAnAccountOrSessionBehind()
    {
        var factory = await CreateDatabaseAsync();
        var units = new SqliteUnitOfWorkFactory(factory);
        await using (var unit = await units.BeginAsync())
        {
            await unit.Accounts.UpsertAsync(AccountRecord.Create(Guid.NewGuid(), "123", "已有账号"));
            await unit.CommitAsync();
        }
        var reserved = Guid.NewGuid();
        var installation = Installation();
        var result = await new SqliteManagedBrowserStateStore(factory).BindLoginAsync(
            true, installation, Session(reserved, installation, "123"), null, default);
        Assert.Equal("DuplicateIdentity", result.ToString());
        await using var read = await units.BeginAsync();
        Assert.Null(await read.Accounts.GetByIdAsync(reserved));
        await read.RollbackAsync();
        Assert.Null(await new SqliteManagedBrowserStateStore(factory).LoadActiveSessionAsync(reserved, BrowserFamily.Chrome, default));
        Assert.Null(await new SqliteManagedBrowserStateStore(factory).LoadActiveInstallationAsync(default));
    }

    private static async Task<SqliteConnectionFactory> CreateDatabaseAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "task11-binding-" + Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(Path.Combine(root, "state.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        return factory;
    }
    private static BrowserInstallationIdentity Installation() => new(Guid.NewGuid(), BrowserFamily.Chrome,
        @"C:\Browser", @"C:\Browser\chrome.exe", "Google LLC", "Google Chrome", Guid.NewGuid().ToString("N"));
    private static ManagedBrowserSessionReference Session(Guid accountId, BrowserInstallationIdentity installation, string uid) =>
        new(Guid.NewGuid(), installation.InstallationId, accountId, uid, installation.BrowserFamily,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
    [Fact]
    public async Task ExplicitReselectAbandonPreservesSavedDirectoryAndNewerIntent()
    {
        var factory = await CreateDatabaseAsync();
        var state = new SqliteBrowserDirectoryState(factory);
        var first = await state.TryBeginEnsurePromptAsync(0,
            new(BrowserDirectorySelectionIntentKind.EnsurePrompt, Guid.NewGuid(), Guid.NewGuid()), default);
        await state.TrySaveAsync(@"C:\Browser", first.Intent!.Revision, first.Intent, default);
        var reselect = await state.TryBeginReselectAsync(
            new(BrowserDirectorySelectionIntentKind.ExplicitReselect, Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.Equal(BrowserDirectoryIntentBeginStatus.Acquired, reselect.Status);
        Assert.Equal(@"C:\Browser", (await state.LoadAsync(default)).Directory);
        Assert.True(await state.TryAbandonSelectionAsync(reselect.Intent!, default));
        var newer = await state.TryBeginReselectAsync(
            new(BrowserDirectorySelectionIntentKind.ExplicitReselect, Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.False(await state.TryAbandonSelectionAsync(reselect.Intent!, default));
        Assert.Equal(newer.Intent!.IntentId, (await state.LoadAsync(default)).PendingIntent!.IntentId);
    }

    [Fact]
    public async Task SessionInsertFailureRollsBackNewAccountAndInstallation()
    {
        var factory = await CreateDatabaseAsync();
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER TestRejectSession BEFORE INSERT ON ManagedBrowserSessions BEGIN SELECT RAISE(ABORT,'test failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        var reserved = Guid.NewGuid();
        var installation = Installation();
        var store = new SqliteManagedBrowserStateStore(factory);
        Assert.Equal(ManagedBrowserBindingStatus.Conflict,
            await store.BindLoginAsync(true, installation, Session(reserved, installation, "123"), null, default));
        await using var unit = await new SqliteUnitOfWorkFactory(factory).BeginAsync();
        Assert.Null(await unit.Accounts.GetByIdAsync(reserved));
        await unit.RollbackAsync();
        Assert.Null(await store.LoadInstallationAsync(installation.InstallationId, default));
        Assert.Null(await store.LoadActiveSessionAsync(reserved, BrowserFamily.Chrome, default));
    }

    [Theory]
    [InlineData("Invalid", true, false)]
    [InlineData("Disabled", false, false)]
    [InlineData("Active", true, true)]
    public async Task ReloginRefusesInactiveOrDeletedSelectedAccount(string status, bool enabled, bool deleted)
    {
        var factory = await CreateDatabaseAsync();
        var account = AccountRecord.Create(Guid.NewGuid(), "123", "已有账号") with
        { Status = status, IsEnabled = enabled, IsDeleted = deleted };
        await using (var unit = await new SqliteUnitOfWorkFactory(factory).BeginAsync())
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.CommitAsync();
        }
        var installation = Installation();
        var store = new SqliteManagedBrowserStateStore(factory);
        Assert.Equal(ManagedBrowserBindingStatus.AccountUnavailable,
            await store.BindLoginAsync(false, installation, Session(account.Id, installation, "123"), null, default));
        Assert.Null(await store.LoadActiveSessionAsync(account.Id, BrowserFamily.Chrome, default));
    }

    [Fact]
    public async Task ExistingSessionAndLegacyFieldsSurviveMismatchAndCancellation()
    {
        var factory = await CreateDatabaseAsync();
        var account = AccountRecord.Create(Guid.NewGuid(), "123", "旧账号") with
        { EncryptedSession = [7, 8, 9], WebView2ProfileDirectory = @"C:\legacy", LastValidatedAt = DateTimeOffset.UtcNow };
        var units = new SqliteUnitOfWorkFactory(factory);
        await using (var unit = await units.BeginAsync())
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.CommitAsync();
        }
        var installation = Installation();
        var store = new SqliteManagedBrowserStateStore(factory);
        var original = Session(account.Id, installation, "123");
        Assert.Equal(ManagedBrowserBindingStatus.Bound, await store.BindLoginAsync(false, installation, original, null, default));
        var expected = await store.LoadActiveSessionAsync(account.Id, BrowserFamily.Chrome, default);
        var mismatched = new ManagedBrowserSessionReference(original.SessionId, original.InstallationId, original.AccountId,
            "456", original.BrowserFamily, original.ProfileId, original.ProfileOwnershipId);
        Assert.Equal(ManagedBrowserBindingStatus.AccountIdentityMismatch,
            await store.BindLoginAsync(false, installation, mismatched, expected, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.BindLoginAsync(false, installation, original, expected, cancellation.Token));
        var after = await store.LoadActiveSessionAsync(account.Id, BrowserFamily.Chrome, default);
        Assert.Equal(expected!.Revision, after!.Revision);
        Assert.Equal(original.SessionId, after.Identity.SessionId);
        Assert.Equal("123", after.Identity.StableBaiduUserId);
        await using var read = await units.BeginAsync();
        var unchanged = await read.Accounts.GetByIdAsync(account.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(account.EncryptedSession, unchanged.EncryptedSession);
        Assert.Equal(account with { EncryptedSession = null }, unchanged with { EncryptedSession = null });
    }

    [Fact]
    public void TypedApplicationRequestRequiresReservedIdAndDoesNotGenerateAnotherIdOnRetry()
    {
        var reserved = Guid.NewGuid();
        var request = ManagedBrowserLoginRequest.AddNew(reserved);
        Assert.Equal(reserved, request.AccountId);
        Assert.Equal(ManagedBrowserLoginRequestKind.AddNew, request.Kind);
        Assert.DoesNotContain(reserved.ToString(), request.ToString());
        var existing = ManagedBrowserLoginRequest.ReloginExisting(reserved);
        Assert.Equal(reserved, existing.AccountId);
        Assert.Equal(ManagedBrowserLoginRequestKind.ReloginExisting, existing.Kind);
        Assert.Throws<ArgumentException>(() => ManagedBrowserLoginRequest.AddNew(Guid.Empty));
        Assert.Throws<ArgumentException>(() => ManagedBrowserLoginRequest.ReloginExisting(Guid.Empty));
    }

    [Fact]
    public async Task ApplicationBoundaryRejectsDifferentSelectedAccountBeforeCallingStore()
    {
        var recorder = new RecordingLoginStore();
        var service = new ManagedBrowserAccountService(recorder);
        var reserved = Guid.NewGuid();
        var request = ManagedBrowserLoginRequest.AddNew(reserved);
        var installation = Installation();
        var rejected = service.BindAsync(request, installation, Session(Guid.NewGuid(), installation, "123"), null, default);
        Assert.Equal(ManagedBrowserBindingStatus.Conflict, await rejected);
        Assert.Empty(recorder.Accounts);
        var observed = Session(reserved, installation, "123");
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(ManagedBrowserBindingStatus.Bound, await service.BindAsync(request, installation, observed, null, default));
        Assert.Equal(new[] { reserved, reserved }, recorder.Accounts);
    }

    [Fact]
    public async Task DirectorySchemaRefusesPartialIntentIdentity()
    {
        var factory = await CreateDatabaseAsync();
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE BrowserDirectorySelection SET IntentKind=1,OwnerEpoch=NULL,IntentId=$id WHERE Singleton=1;";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ForgedOwnerAndCancellationCannotChangeSavedDirectoryOrExactIntent()
    {
        var factory = await CreateDatabaseAsync();
        var state = new SqliteBrowserDirectoryState(factory);
        var begin = await state.TryBeginEnsurePromptAsync(0,
            new(BrowserDirectorySelectionIntentKind.EnsurePrompt, Guid.NewGuid(), Guid.NewGuid()), default);
        var intent = begin.Intent!;
        var forged = new BrowserDirectorySelectionIntent(intent.Revision, intent.Kind, Guid.NewGuid(), intent.IntentId);
        Assert.Equal(BrowserDirectoryStateSaveStatus.Conflict,
            (await state.TrySaveAsync(@"C:\Different", intent.Revision, forged, default)).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            state.TrySaveAsync(@"C:\Different", intent.Revision, intent, cancellation.Token));
        var current = await state.LoadAsync(default);
        Assert.Null(current.Directory);
        Assert.Equal(intent.OwnerEpoch, current.PendingIntent!.OwnerEpoch);
        Assert.Equal(intent.IntentId, current.PendingIntent.IntentId);
        Assert.Equal(intent.Revision, current.Revision);
    }

    [Fact]
    public async Task ReloginMustSupplyTheExactCurrentSessionAndNeverRewritesItsIdentity()
    {
        var factory = await CreateDatabaseAsync();
        var store = new SqliteManagedBrowserStateStore(factory);
        var accountId = Guid.NewGuid();
        var installation = Installation();
        var original = Session(accountId, installation, "123");
        Assert.Equal(ManagedBrowserBindingStatus.Bound, await store.BindLoginAsync(true, installation, original, null, default));
        var expected = await store.LoadActiveSessionAsync(accountId, BrowserFamily.Chrome, default);
        Assert.Equal(ManagedBrowserBindingStatus.Conflict, await store.BindLoginAsync(false, installation, original, null, default));
        Assert.Equal(ManagedBrowserBindingStatus.Conflict, await store.BindLoginAsync(false, installation,
            Session(accountId, installation, "123"), expected, default));
        Assert.Equal(ManagedBrowserBindingStatus.Bound, await store.BindLoginAsync(false, installation, original, expected, default));
        var after = await store.LoadActiveSessionAsync(accountId, BrowserFamily.Chrome, default);
        Assert.Equal(expected!.Revision, after!.Revision);
        Assert.Equal(original.SessionId, after.Identity.SessionId);
    }

    [Fact]
    public async Task ReloginCannotValidateAStoredSessionWhoseInstallationWasRetired()
    {
        var factory = await CreateDatabaseAsync();
        var store = new SqliteManagedBrowserStateStore(factory);
        var installation = Installation();
        var original = Session(Guid.NewGuid(), installation, "123");
        Assert.Equal(ManagedBrowserBindingStatus.Bound, await store.BindLoginAsync(true, installation, original, null, default));
        var session = await store.LoadActiveSessionAsync(original.AccountId, BrowserFamily.Chrome, default);
        var old = await store.LoadActiveInstallationAsync(default);
        var replacement = await store.CreateInstallationAsync(Installation(), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await store.TryReplaceActiveInstallationAsync(new(old, replacement), default)).Status);
        Assert.Equal(ManagedBrowserBindingStatus.Conflict,
            await store.BindLoginAsync(false, installation, original, session, default));
        Assert.Equal(session!.Revision, (await store.LoadSessionAsync(original.SessionId, default))!.Revision);
    }

    private sealed class RecordingLoginStore : IManagedBrowserLoginStore
    {
        public List<Guid> Accounts { get; } = [];
        public Task<ManagedBrowserBindingStatus> BindLoginAsync(bool addNew, BrowserInstallationIdentity installation,
            ManagedBrowserSessionReference session, StoredManagedBrowserSession? expectedCurrent, CancellationToken cancellationToken)
        {
            Accounts.Add(session.AccountId);
            return Task.FromResult(ManagedBrowserBindingStatus.Bound);
        }
    }
}
