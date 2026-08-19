using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.Infrastructure.Tests.Security;

public sealed class DpapiSessionProtectorTests
{
    private static readonly Guid AccountId = Guid.Parse("63fb2582-cc52-456e-8ea0-4e2e83bf9d30");
    private const string BaiduUserId = "baidu-user-42";

    [Fact]
    public void CurrentUserRoundTripProducesDefensiveSessionSnapshot()
    {
        var protector = new DpapiSessionProtector();
        var session = CreateSession();

        var protectedResult = protector.Protect(session);
        Assert.True(protectedResult.IsSuccess);
        var ciphertext = Assert.IsType<byte[]>(protectedResult.ProtectedBytes);

        var unprotected = protector.Unprotect(ciphertext, AccountId, BaiduUserId);

        Assert.True(unprotected.IsSuccess);
        var restored = Assert.IsType<AccountSession>(unprotected.Session);
        Assert.Equal(AccountId, restored.LocalAccountId);
        Assert.Equal(BaiduUserId, restored.BaiduUserId);
        Assert.Equal("BDUSS", Assert.Single(restored.SessionCookies).Name);
        Assert.Equal("cookie-value", Assert.Single(restored.SessionCookies).Value);
        ciphertext[0] ^= 0xff;
        Assert.Equal("cookie-value", Assert.Single(restored.SessionCookies).Value);
    }

    [Fact]
    public void ProtectingSameSessionTwiceUsesNonDeterministicCiphertext()
    {
        var protector = new DpapiSessionProtector();

        var first = protector.Protect(CreateSession());
        var second = protector.Protect(CreateSession());

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.ProtectedBytes, second.ProtectedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptOrTruncatedCiphertextReturnsSafeTypedFailure(bool truncate)
    {
        var protector = new DpapiSessionProtector();
        var bytes = Assert.IsType<byte[]>(protector.Protect(CreateSession()).ProtectedBytes);
        bytes = truncate ? bytes[..Math.Min(8, bytes.Length)] : (byte[])bytes.Clone();
        if (!truncate) bytes[bytes.Length / 2] ^= 0x7f;

        var result = protector.Unprotect(bytes, AccountId, BaiduUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(SessionProtectionFailure.InvalidCiphertext, result.Failure);
        Assert.DoesNotContain("crypt", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("json", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongAccountBindingIsRejected()
    {
        var protector = new DpapiSessionProtector();
        var bytes = Assert.IsType<byte[]>(protector.Protect(CreateSession()).ProtectedBytes);

        var wrongLocal = protector.Unprotect(bytes, Guid.NewGuid(), BaiduUserId);
        var wrongBaidu = protector.Unprotect(bytes, AccountId, "another-user");

        Assert.Equal(SessionProtectionFailure.BindingMismatch, wrongLocal.Failure);
        Assert.Equal(SessionProtectionFailure.BindingMismatch, wrongBaidu.Failure);
    }

    [Fact]
    public void WrongEntropyIsRejectedWithoutRawDetails()
    {
        var bytes = Assert.IsType<byte[]>(new DpapiSessionProtector("purpose-a").Protect(CreateSession()).ProtectedBytes);

        var result = new DpapiSessionProtector("purpose-b").Unprotect(bytes, AccountId, BaiduUserId);

        Assert.Equal(SessionProtectionFailure.InvalidCiphertext, result.Failure);
        Assert.Equal("The saved session could not be read safely.", result.SafeMessage);
    }

    [Fact]
    public void UnsupportedEnvelopeVersionIsRejectedAfterUnprotect()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new DpapiSessionProtector();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = SessionEnvelope.CurrentVersion + 1,
            LocalAccountId = AccountId,
            BaiduUserId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Cookies = new[]
            {
                new { Name = "BDUSS", Value = "cookie-value", Domain = ".baidu.com", Path = "/", ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1), IsSecure = true, IsHttpOnly = true }
            }
        });
        var ciphertext = ProtectedData.Protect(
            json,
            DpapiSessionProtector.DeriveEntropy(DpapiSessionProtector.DefaultPurpose),
            DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(json);

        var result = protector.Unprotect(ciphertext, AccountId, BaiduUserId);

        Assert.Equal(SessionProtectionFailure.UnsupportedVersion, result.Failure);
    }

    [Fact]
    public void NullCookieElementInValidDpapiPayloadReturnsTypedSafeFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = SessionEnvelope.CurrentVersion,
            LocalAccountId = AccountId,
            BaiduUserId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Cookies = new object?[] { null }
        });
        var ciphertext = ProtectedData.Protect(
            json,
            DpapiSessionProtector.DeriveEntropy(DpapiSessionProtector.DefaultPurpose),
            DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(json);

        var result = new DpapiSessionProtector().Unprotect(ciphertext, AccountId, BaiduUserId);

        Assert.Equal(SessionProtectionFailure.InvalidCiphertext, result.Failure);
        Assert.Equal("The saved session could not be read safely.", result.SafeMessage);
    }

    [Fact]
    public void EnvelopeAndCookieEnforceLimitsAndDoNotExposeSecrets()
    {
        var mutable = new List<SessionCookie> { CreateCookie() };
        var envelope = new SessionEnvelope(
            SessionEnvelope.CurrentVersion,
            AccountId,
            BaiduUserId,
            DateTimeOffset.UtcNow,
            mutable);
        mutable.Clear();

        Assert.Single(envelope.Cookies);
        Assert.Throws<ArgumentException>(() => new SessionCookie(
            new string('n', SessionEnvelope.MaxCookieNameLength + 1),
            "value", ".baidu.com", "/", DateTimeOffset.UtcNow.AddHours(1), true, true));
        Assert.Throws<ArgumentException>(() => new SessionCookie(
            "BDUSS", "value", "https://baidu.com/path", "/", DateTimeOffset.UtcNow.AddHours(1), true, true));
        Assert.Throws<ArgumentException>(() => new SessionCookie(
            "bad;name", "value", ".baidu.com", "/", DateTimeOffset.UtcNow.AddHours(1), true, true));
        Assert.Throws<ArgumentException>(() => new SessionCookie(
            "BDUSS", "value", ".example.com", "/", DateTimeOffset.UtcNow.AddHours(1), true, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionEnvelope(
            SessionEnvelope.CurrentVersion,
            AccountId,
            BaiduUserId,
            DateTimeOffset.UtcNow,
            Enumerable.Range(0, SessionEnvelope.MaxCookieCount + 1)
                .Select(i => new SessionCookie($"cookie-{i}", "value", ".baidu.com", "/", DateTimeOffset.UtcNow.AddHours(1), true, true))));
        Assert.DoesNotContain("cookie-value", envelope.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(BaiduUserId, envelope.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-value", CreateSession().ToString(), StringComparison.Ordinal);
        var renderedCookie = CreateCookie().ToString();
        Assert.DoesNotContain("cookie-value", renderedCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("BDUSS", renderedCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("baidu.com", renderedCookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionStorePersistsOnlyDpapiCiphertextBlobAndHonorsBinding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bbt-encrypted-store-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
            await new DatabaseMigrator(
                factory,
                new DatabaseBackupService(factory, Path.Combine(root, "backups")),
                "test").MigrateAsync();
            var unitFactory = new SqliteUnitOfWorkFactory(factory);
            await using (var unit = await unitFactory.BeginAsync())
            {
                await unit.Accounts.UpsertAsync(AccountRecord.Create(AccountId, BaiduUserId, "Account"));
                await unit.CommitAsync();
            }
            var store = new EncryptedBaiduSessionStore(unitFactory, new DpapiSessionProtector());

            await store.SaveAsync(CreateSession(), CancellationToken.None);

            await using (var connection = await factory.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT typeof(EncryptedSession), EncryptedSession FROM Accounts WHERE Id=$id;";
                command.Parameters.AddWithValue("$id", AccountId.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("blob", reader.GetString(0));
                var encrypted = Assert.IsType<byte[]>(reader.GetValue(1));
                Assert.DoesNotContain("cookie-value", Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
            }

            var loaded = await store.LoadAsync(AccountId.ToString("D"), CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.True(loaded.IsBoundTo(AccountId, BaiduUserId));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveAsync(CreateSession().Bind(AccountId, "wrong-user"), CancellationToken.None));
            await store.RemoveAsync(AccountId.ToString("D"), CancellationToken.None);
            Assert.Null(await store.LoadAsync(AccountId.ToString("D"), CancellationToken.None));

            await using (var unit = await unitFactory.BeginAsync())
            {
                var account = Assert.IsType<AccountRecord>(await unit.Accounts.GetByIdAsync(AccountId));
                await unit.Accounts.UpsertAsync(account with { Status = "Disabled", IsEnabled = false });
                await unit.CommitAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveAsync(CreateSession(), CancellationToken.None));
            Assert.Null(await store.LoadAsync(AccountId.ToString("D"), CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SameCookieNameOnDifferentBaiduDomainsPreservesTheFullSessionSnapshot()
    {
        var session = new AccountSession(
            AccountId,
            BaiduUserId,
            [
                CreateCookie(),
                new SessionCookie("BDUSS", "passport-value", "passport.baidu.com", "/", DateTimeOffset.UtcNow.AddHours(1), true, true)
            ],
            DateTimeOffset.UtcNow);

        Assert.Equal(2, session.SessionCookies.Count);
        var protectedBytes = Assert.IsType<byte[]>(new DpapiSessionProtector().Protect(session).ProtectedBytes);
        var restored = new DpapiSessionProtector().Unprotect(protectedBytes, AccountId, BaiduUserId);
        Assert.Equal(2, Assert.IsType<AccountSession>(restored.Session).SessionCookies.Count);
    }

    private static AccountSession CreateSession() => new(
        AccountId,
        BaiduUserId,
        [CreateCookie()],
        new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero));

    private static SessionCookie CreateCookie() => new(
        "BDUSS",
        "cookie-value",
        ".baidu.com",
        "/",
        new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
        isSecure: true,
        isHttpOnly: true);
}
