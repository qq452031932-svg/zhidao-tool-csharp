using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class BaiduLoginSessionRegistrarTests
{
    [Fact]
    public async Task RefreshPurposeUsesOnlyTheSessionRefreshPath()
    {
        var accountId = Guid.NewGuid();
        var ownership = AccountProfileOwnership.NewLoginProfile(Guid.NewGuid());
        var session = Unbound();
        var addCalls = 0;
        var refreshCalls = 0;
        var registrar = new BaiduLoginSessionRegistrar(
            (_, _) => { addCalls++; throw new InvalidOperationException(); },
            (seenAccount, seenSession, _) =>
            {
                refreshCalls++;
                Assert.Equal(accountId, seenAccount);
                Assert.Same(session, seenSession);
                return Task.FromResult(new RefreshAccountSessionResult(RefreshAccountSessionStatus.Refreshed));
            });

        var result = await registrar.RegisterAsync(
            BaiduLoginWindowPurpose.RefreshAccountSession,
            accountId,
            ownership,
            session,
            session.CapturedAtUtc,
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(0, addCalls);
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public async Task AddPurposePreservesTheExistingAddAccountResult()
    {
        var accountId = Guid.NewGuid();
        var ownership = AccountProfileOwnership.NewLoginProfile(Guid.NewGuid());
        var session = Unbound();
        var expected = new AddAccountResult(AddAccountStatus.DuplicateIdentity, Guid.NewGuid(), "duplicate");
        var registrar = new BaiduLoginSessionRegistrar(
            (request, _) =>
            {
                Assert.Equal(accountId, request.AccountId);
                Assert.Same(ownership, request.ProfileOwnership);
                Assert.False(request.PersistBrowserProfile);
                return Task.FromResult(expected);
            },
            (_, _, _) => throw new InvalidOperationException());

        var result = await registrar.RegisterAsync(
            BaiduLoginWindowPurpose.AddAccount,
            accountId,
            ownership,
            session,
            session.CapturedAtUtc,
            default);

        Assert.False(result.Succeeded);
        Assert.Same(expected, result.AddResult);
    }

    private static AccountSession Unbound() => new(
        "capture",
        [new SessionCookie("BDUSS", "synthetic", ".baidu.com", "/", null, true, true)],
        DateTimeOffset.Parse("2026-09-01T01:00:00Z"));
}
