using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Tests.Accounts;

public sealed class AccountRemovalCoordinatorTests
{
    private static readonly Guid AccountA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid AccountB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset RemovedAt = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private const string ProfilePath = @"C:\safe-profiles\bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public async Task GetAccountsPageDelegatesToStore()
    {
        var expected = new AccountPage(
            [new AccountListItem(AccountB, "Visible Name", "Active", true)],
            3,
            25,
            51);
        var store = new FakeRemovalStore { PageResult = expected };
        var coordinator = CreateCoordinator(store);

        var actual = await coordinator.GetAccountsPageAsync(3, 25, CancellationToken.None);

        Assert.Same(expected, actual);
        Assert.Equal((3, 25), store.LastPageRequest);
    }

    [Fact]
    public async Task PreviewDelegatesToStoreAndPreservesSafeFields()
    {
        var expected = new AccountExitPreview(
            AccountExitStatus.Ready,
            "Visible Name",
            12,
            4,
            2,
            "confirmation-secret",
            "退出前请确认。");
        var store = new FakeRemovalStore { PreviewResult = expected };
        var coordinator = CreateCoordinator(store);

        var actual = await coordinator.PreviewAsync(
            AccountB,
            AccountExitHistoryMode.RemoveTerminalHistory,
            CancellationToken.None);

        Assert.Same(expected, actual);
        Assert.Equal((AccountB, AccountExitHistoryMode.RemoveTerminalHistory), store.LastPreviewRequest);
        Assert.Equal(12, actual.TerminalOperationCount);
        Assert.Equal(4, actual.TerminalBatchCount);
        Assert.Equal(2, actual.SharedBatchCount);
        Assert.Equal("退出前请确认。", actual.SafeMessage);
    }

    [Fact]
    public async Task EmptyAccountIdIsRejectedByPreparationWithoutProfileCleanup()
    {
        var store = new FakeRemovalStore
        {
            PreparationResult = Preparation(AccountRemovalPreparationStatus.NotFound, Guid.Empty, null, false)
        };
        var profiles = new FakeProfileStore();
        var coordinator = CreateCoordinator(store, profiles);

        var result = await coordinator.ApplyAsync(Request(Guid.Empty, "token"), CancellationToken.None);

        Assert.Equal(AccountExitStatus.NotFound, result.Status);
        Assert.Equal(Guid.Empty, store.LastPreparationRequest!.AccountId);
        Assert.Empty(profiles.Events);
        Assert.Equal(0, store.FinalizeCount);
    }

    [Theory]
    [InlineData(AccountRemovalPreparationStatus.NotFound, AccountExitStatus.NotFound)]
    [InlineData(AccountRemovalPreparationStatus.ActiveWorkBlocked, AccountExitStatus.ActiveWorkBlocked)]
    public async Task NonPreparedStatusNeverRunsProfileCleanupOrFinalization(
        AccountRemovalPreparationStatus preparationStatus,
        AccountExitStatus expectedStatus)
    {
        var store = new FakeRemovalStore
        {
            PreparationResult = Preparation(preparationStatus, AccountB, null, false)
        };
        var profiles = new FakeProfileStore();
        var coordinator = CreateCoordinator(store, profiles);

        var result = await coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Empty(profiles.Events);
        Assert.Equal(0, store.FinalizeCount);
    }

    [Fact]
    public async Task ConfirmationTokenForAccountACannotBeAppliedToAccountB()
    {
        var store = new FakeRemovalStore
        {
            PrepareHandler = request => request.AccountId == AccountB && request.ConfirmationToken == "token-for-a"
                ? Preparation(AccountRemovalPreparationStatus.StaleConfirmation, AccountB, null, false)
                : throw new InvalidOperationException("Unexpected request.")
        };
        var profiles = new FakeProfileStore();
        var coordinator = CreateCoordinator(store, profiles);

        var result = await coordinator.ApplyAsync(Request(AccountB, "token-for-a"), CancellationToken.None);

        Assert.Equal(AccountExitStatus.StaleConfirmation, result.Status);
        Assert.Empty(profiles.Events);
        Assert.Equal(0, store.FinalizeCount);
    }

    [Fact]
    public async Task StaleConfirmationNeverRunsProfileCleanupOrFinalization()
    {
        var store = new FakeRemovalStore
        {
            PreparationResult = Preparation(AccountRemovalPreparationStatus.StaleConfirmation, AccountB, null, false)
        };
        var profiles = new FakeProfileStore();
        var coordinator = CreateCoordinator(store, profiles);

        var result = await coordinator.ApplyAsync(Request(AccountB, "stale-token"), CancellationToken.None);

        Assert.Equal(AccountExitStatus.StaleConfirmation, result.Status);
        Assert.Empty(profiles.Events);
        Assert.Equal(0, store.FinalizeCount);
    }

    [Fact]
    public async Task StoreFailurePreventsProfileCleanup()
    {
        var store = new FakeRemovalStore { PreparationFailure = new IOException("database-file-and-account-id") };
        var profiles = new FakeProfileStore();
        var coordinator = CreateCoordinator(store, profiles);

        var result = await coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None);

        Assert.Equal(AccountExitStatus.Failed, result.Status);
        Assert.Empty(profiles.Events);
        Assert.Equal(0, store.FinalizeCount);
        Assert.Equal("账号退出准备失败。", result.SafeMessage);
        Assert.DoesNotContain("database-file-and-account-id", result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringPreparationPropagatesWithoutProfileCleanup()
    {
        var events = new List<string>();
        var cancellation = new OperationCanceledException("prepare-cancelled");
        var store = new FakeRemovalStore(events) { PreparationFailure = cancellation };
        var profiles = new FakeProfileStore(events);
        var coordinator = new AccountRemovalCoordinator(store, profiles, new FakePathPolicy(events));

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None));

        Assert.Same(cancellation, actual);
        Assert.Equal(new[] { "prepare" }, events);
        Assert.Equal(0, store.FinalizeCount);
    }

    [Theory]
    [InlineData(CancellationBoundary.Close)]
    [InlineData(CancellationBoundary.Delete)]
    [InlineData(CancellationBoundary.Finalize)]
    public async Task CancellationAfterCommittedPreparationReturnsCleanupPendingAndStopsAtBoundary(
        CancellationBoundary boundary)
    {
        var events = new List<string>();
        var cancellation = new OperationCanceledException($"{boundary}-cancelled");
        var store = new FakeRemovalStore(events)
        {
            PreparationResult = Preparation(AccountRemovalPreparationStatus.Prepared, AccountB, ProfilePath, true),
            FinalizeFailure = boundary == CancellationBoundary.Finalize ? cancellation : null
        };
        var profiles = new FakeProfileStore(events)
        {
            CloseFailure = boundary == CancellationBoundary.Close ? cancellation : null,
            DeleteFailure = boundary == CancellationBoundary.Delete ? cancellation : null
        };
        var coordinator = new AccountRemovalCoordinator(store, profiles, new FakePathPolicy(events));

        var result = await coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None);

        Assert.Equal(AccountExitStatus.CleanupPending, result.Status);
        Assert.Equal("账号已禁用且凭据已清除；本机登录目录清理或墓碑终结仍待完成。", result.SafeMessage);
        Assert.Equal(ExpectedEvents(boundary), events);
        Assert.Equal(boundary == CancellationBoundary.Finalize ? 1 : 0, store.FinalizeCount);
    }

    [Fact]
    public async Task ProfileCleanupFailureAfterCommittedPreparationReturnsCleanupPending()
    {
        var store = new FakeRemovalStore
        {
            PreparationResult = Preparation(AccountRemovalPreparationStatus.Prepared, AccountB, ProfilePath, true)
        };
        var profiles = new FakeProfileStore { DeleteFailure = new IOException("profile-locked") };
        var coordinator = CreateCoordinator(store, profiles);

        var result = await coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None);

        Assert.True(store.PreparationResult.DatabasePrepared);
        Assert.Equal(AccountExitStatus.CleanupPending, result.Status);
        Assert.Equal(new[] { "close", "delete" }, profiles.Events);
        Assert.Equal(0, store.FinalizeCount);
        Assert.Equal("账号已禁用且凭据已清除；本机登录目录清理或墓碑终结仍待完成。", result.SafeMessage);
        Assert.DoesNotContain("profile-locked", result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparedRemovalClosesDeletesAndFinalizesInOrder()
    {
        var events = new List<string>();
        var unitOfWorkFactory = new RejectingUnitOfWorkFactory();
        var store = new FakeRemovalStore(events)
        {
            PreparationResult = Preparation(AccountRemovalPreparationStatus.Prepared, AccountB, ProfilePath, true)
        };
        var profiles = new FakeProfileStore(events);
        var paths = new FakePathPolicy(events);
        var removalService = new RemoveAccountService(unitOfWorkFactory, profiles, paths);
        var coordinator = new AccountRemovalCoordinator(store, removalService);

        var result = await coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None);

        Assert.Equal(AccountExitStatus.Removed, result.Status);
        Assert.Equal("账号已退出。", result.SafeMessage);
        Assert.Equal(new[] { "prepare", "close", "validate-path", "delete", "finalize" }, events);
        Assert.Equal(0, unitOfWorkFactory.BeginCount);
        Assert.Equal((AccountB, RemovedAt), store.LastFinalizeRequest);
        Assert.Equal((AccountB, ProfilePath), paths.LastValidation);
    }

    [Fact]
    public async Task PreparedRemovalWithoutProfileSnapshotClosesThenFinalizesWithoutDelete()
    {
        var events = new List<string>();
        var store = new FakeRemovalStore(events)
        {
            PreparationResult = Preparation(AccountRemovalPreparationStatus.Prepared, AccountB, null, true)
        };
        var profiles = new FakeProfileStore(events);
        var coordinator = new AccountRemovalCoordinator(store, profiles, new FakePathPolicy(events));

        var result = await coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None);

        Assert.Equal(AccountExitStatus.Removed, result.Status);
        Assert.Equal(new[] { "prepare", "close", "finalize" }, events);
        Assert.Equal(1, store.FinalizeCount);
    }

    [Fact]
    public async Task UnsafePreparedProfileSnapshotNeverReachesDeleteOrFinalization()
    {
        var events = new List<string>();
        var store = new FakeRemovalStore(events)
        {
            PreparationResult = Preparation(AccountRemovalPreparationStatus.Prepared, AccountB, ProfilePath, true)
        };
        var profiles = new FakeProfileStore(events);
        var paths = new FakePathPolicy(events) { ValidationFailure = new UnauthorizedAccessException("secret-path") };
        var coordinator = new AccountRemovalCoordinator(store, profiles, paths);

        var result = await coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None);

        Assert.Equal(AccountExitStatus.CleanupPending, result.Status);
        Assert.Equal(new[] { "prepare", "close", "validate-path" }, events);
        Assert.Equal(0, store.FinalizeCount);
        Assert.DoesNotContain("secret-path", result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparedCleanupRetryClosesDeletesAndConditionallyFinalizesWithoutLegacyDisable()
    {
        var events = new List<string>();
        var unitOfWorkFactory = new RejectingUnitOfWorkFactory();
        var store = new FakeRemovalStore(events)
        {
            PreparationResult = Preparation(AccountRemovalPreparationStatus.Prepared, AccountB, ProfilePath, true)
        };
        var profiles = new FakeProfileStore(events) { DeleteFailure = new IOException("locked-profile") };
        var removalService = new RemoveAccountService(
            unitOfWorkFactory,
            profiles,
            new FakePathPolicy(events));
        var coordinator = new AccountRemovalCoordinator(store, removalService);

        var first = await coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None);
        profiles.DeleteFailure = null;
        var retry = await coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None);

        Assert.Equal(AccountExitStatus.CleanupPending, first.Status);
        Assert.Equal(AccountExitStatus.Removed, retry.Status);
        Assert.Equal(
            new[]
            {
                "prepare", "close", "validate-path", "delete",
                "prepare", "close", "validate-path", "delete", "finalize"
            },
            events);
        Assert.Equal(1, store.FinalizeCount);
        Assert.Equal(0, unitOfWorkFactory.BeginCount);
    }

    [Fact]
    public async Task PreparedResponseForAnotherAccountIsRejectedBeforeProfileCleanup()
    {
        var store = new FakeRemovalStore
        {
            PreparationResult = Preparation(AccountRemovalPreparationStatus.Prepared, AccountA, ProfilePath, true)
        };
        var profiles = new FakeProfileStore();
        var coordinator = CreateCoordinator(store, profiles);

        var result = await coordinator.ApplyAsync(Request(AccountB, "token"), CancellationToken.None);

        Assert.Equal(AccountExitStatus.Failed, result.Status);
        Assert.Empty(profiles.Events);
        Assert.Equal(0, store.FinalizeCount);
    }

    [Fact]
    public void ContractToStringValuesRedactAccountScopedSecrets()
    {
        const string displayName = "Sensitive Display Name";
        const string token = "sensitive-confirmation-token";
        const string path = @"C:\secret\profile";
        var request = new AccountExitRequest(AccountB, AccountExitHistoryMode.KeepHistory, token, RemovedAt);
        var preview = new AccountExitPreview(AccountExitStatus.Ready, displayName, 1, 2, 3, token, "safe");
        var item = new AccountListItem(AccountB, displayName, "Active", true);
        var page = new AccountPage([item], 1, 25, 1);
        var preparation = new AccountRemovalPreparation(
            AccountRemovalPreparationStatus.Prepared,
            AccountB,
            path,
            true,
            "safe");

        foreach (var text in new[]
                 {
                     request.ToString(), preview.ToString(), item.ToString(), page.ToString(), preparation.ToString()
                 })
        {
            Assert.DoesNotContain(AccountB.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(displayName, text, StringComparison.Ordinal);
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(path, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static AccountRemovalCoordinator CreateCoordinator(
        FakeRemovalStore store,
        FakeProfileStore? profiles = null) =>
        new(store, profiles ?? new FakeProfileStore(), new FakePathPolicy());

    private static AccountExitRequest Request(Guid accountId, string token) =>
        new(accountId, AccountExitHistoryMode.RemoveTerminalHistory, token, RemovedAt);

    private static AccountRemovalPreparation Preparation(
        AccountRemovalPreparationStatus status,
        Guid accountId,
        string? profilePath,
        bool databasePrepared) =>
        new(status, accountId, profilePath, databasePrepared, $"safe-{status}");

    private static string[] ExpectedEvents(CancellationBoundary boundary) => boundary switch
    {
        CancellationBoundary.Close => ["prepare", "close"],
        CancellationBoundary.Delete => ["prepare", "close", "validate-path", "delete"],
        CancellationBoundary.Finalize => ["prepare", "close", "validate-path", "delete", "finalize"],
        _ => throw new ArgumentOutOfRangeException(nameof(boundary))
    };

    public enum CancellationBoundary
    {
        Close,
        Delete,
        Finalize
    }

    private sealed class FakeRemovalStore(List<string>? events = null) : IAccountRemovalStore
    {
        public AccountPage PageResult { get; set; } = new([], 1, 25, 0);
        public AccountExitPreview PreviewResult { get; set; } =
            new(AccountExitStatus.NotFound, string.Empty, 0, 0, 0, string.Empty, "safe-not-found");
        public AccountRemovalPreparation PreparationResult { get; set; } =
            Preparation(AccountRemovalPreparationStatus.NotFound, AccountB, null, false);
        public Func<AccountExitRequest, AccountRemovalPreparation>? PrepareHandler { get; set; }
        public Exception? PreparationFailure { get; set; }
        public Exception? FinalizeFailure { get; set; }
        public (int PageNumber, int PageSize)? LastPageRequest { get; private set; }
        public (Guid AccountId, AccountExitHistoryMode Mode)? LastPreviewRequest { get; private set; }
        public AccountExitRequest? LastPreparationRequest { get; private set; }
        public (Guid AccountId, DateTimeOffset RemovedAt)? LastFinalizeRequest { get; private set; }
        public int FinalizeCount { get; private set; }

        public Task<AccountPage> GetAccountsPageAsync(int pageNumber, int pageSize, CancellationToken token)
        {
            LastPageRequest = (pageNumber, pageSize);
            return Task.FromResult(PageResult);
        }

        public Task<AccountExitPreview> PreviewAsync(
            Guid accountId,
            AccountExitHistoryMode mode,
            CancellationToken token)
        {
            LastPreviewRequest = (accountId, mode);
            return Task.FromResult(PreviewResult);
        }

        public Task<AccountRemovalPreparation> PrepareRemovalAsync(
            AccountExitRequest request,
            CancellationToken token)
        {
            events?.Add("prepare");
            LastPreparationRequest = request;
            if (PreparationFailure is not null)
                return Task.FromException<AccountRemovalPreparation>(PreparationFailure);
            return Task.FromResult(PrepareHandler?.Invoke(request) ?? PreparationResult);
        }

        public Task FinalizeTombstoneAsync(Guid accountId, DateTimeOffset removedAt, CancellationToken token)
        {
            events?.Add("finalize");
            FinalizeCount++;
            LastFinalizeRequest = (accountId, removedAt);
            return FinalizeFailure is null ? Task.CompletedTask : Task.FromException(FinalizeFailure);
        }
    }

    private sealed class FakeProfileStore(List<string>? sharedEvents = null) : IWebViewProfileStore
    {
        public List<string> Events { get; } = sharedEvents ?? [];
        public Exception? CloseFailure { get; set; }
        public Exception? DeleteFailure { get; set; }

        public Task CloseAsync(Guid accountId, CancellationToken cancellationToken)
        {
            Events.Add("close");
            return CloseFailure is null ? Task.CompletedTask : Task.FromException(CloseFailure);
        }

        public Task DeleteAsync(Guid accountId, string exactProfilePath, CancellationToken cancellationToken)
        {
            Events.Add("delete");
            return DeleteFailure is null ? Task.CompletedTask : Task.FromException(DeleteFailure);
        }

        public Task RegisterOwnershipAsync(
            Guid accountId,
            string exactProfilePath,
            Guid ownershipToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CommitOwnedAsync(
            Guid accountId,
            string exactProfilePath,
            Guid ownershipToken,
            Func<CancellationToken, Task> commitAction,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OwnedProfileDeletionStatus> DeleteOwnedAsync(
            Guid accountId,
            string exactProfilePath,
            Guid ownershipToken,
            Func<CancellationToken, Task<bool>> confirmNoActiveAccount,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePathPolicy(List<string>? events = null) : IAccountProfilePathPolicy
    {
        public (Guid AccountId, string Path)? LastValidation { get; private set; }
        public Exception? ValidationFailure { get; set; }

        public string GetAccountProfilePath(Guid accountId) => throw new NotSupportedException();

        public void EnsureSafeAccountPath(Guid accountId, string candidatePath)
        {
            events?.Add("validate-path");
            LastValidation = (accountId, candidatePath);
            if (ValidationFailure is not null) throw ValidationFailure;
        }
    }

    private sealed class RejectingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        public int BeginCount { get; private set; }

        public Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default)
        {
            BeginCount++;
            throw new InvalidOperationException("The prepared cleanup path must not begin a unit of work.");
        }
    }
}
