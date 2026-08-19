using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using System.Collections.Immutable;

namespace BaiduBatchTransfer.BaiduAdapter.Tests;

public sealed class AdapterContractTests
{
    [Fact]
    public void InvalidExtractionCode_IsGlobalResourceError()
    {
        var result = BaiduAdapterResult.Failure(
            BaiduErrorKind.InvalidExtractionCode,
            "The extraction code is invalid.");

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorScope.Resource, result.Scope);
    }

    [Fact]
    public void Failure_ScrubsUnsafeCredentialMaterialFromSafeMessage()
    {
        var result = BaiduAdapterResult<object>.Failure(
            BaiduErrorKind.UnknownResponse,
            "Cookie: BDUSS=secret; Authorization: Bearer private-token");

        Assert.Equal("The service returned an unrecognized response.", result.SafeMessage);
        Assert.DoesNotContain("Cookie", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BDUSS", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("X-Baidu-Signature: opaque-signature-value")]
    [InlineData("sign=opaque-sign-value")]
    [InlineData("csrf=opaque-csrf-value")]
    [InlineData("opaque-sensitive-value")]
    public void Failure_NeverPublishesRawAdapterDiagnostics(string rawDiagnostic)
    {
        var result = BaiduAdapterResult<object>.Failure(
            BaiduErrorKind.UnknownResponse,
            rawDiagnostic);

        Assert.Equal("The service returned an unrecognized response.", result.SafeMessage);
        Assert.DoesNotContain(rawDiagnostic, result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_UsesCanonicalPublicMessagesForEveryErrorKind()
    {
        foreach (var errorKind in Enum.GetValues<BaiduErrorKind>())
        {
            var result = BaiduAdapterResult<object>.Failure(errorKind, "raw adapter diagnostic");

            Assert.False(string.IsNullOrWhiteSpace(result.SafeMessage));
            Assert.DoesNotContain("raw adapter diagnostic", result.SafeMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Success_RequiresAValue_AndCarriesNoError()
    {
        Assert.Throws<ArgumentNullException>(() => BaiduAdapterResult<string>.Success(null!));

        var result = BaiduAdapterResult<string>.Success("ok");

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", result.Value);
        Assert.Null(result.ErrorKind);
        Assert.Null(result.Scope);
        Assert.Null(result.SafeMessage);
    }

    [Fact]
    public void Failure_CarriesNoValue_AndRequiresAKnownErrorKind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BaiduAdapterResult<string>.Failure((BaiduErrorKind)999, "Unexpected response."));

        var result = BaiduAdapterResult<string>.Failure(
            BaiduErrorKind.ExpiredLogin,
            "Please sign in again.");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(BaiduErrorKind.ExpiredLogin, result.ErrorKind);
        Assert.Equal(BaiduErrorScope.Account, result.Scope);
        Assert.Equal("The account session has expired.", result.SafeMessage);
    }

    [Fact]
    public void ConfirmationQuery_ReconstructsWithStructuralEquality()
    {
        var start = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(2);
        var descriptor = new OperationConfirmationDescriptor(
            "share-identity",
            new RemotePath("//batch//2026/"),
            "outer-name-fingerprint",
            "operation-fingerprint",
            start,
            end);

        var enriched = descriptor.WithResponseIdentifiers(
            "request-42",
            ["remote-2", "remote-1", "remote-1"]);
        var rebuilt = new TransferConfirmationQuery(
            "share-identity",
            new RemotePath("/batch/2026"),
            "outer-name-fingerprint",
            "operation-fingerprint",
            start,
            end,
            "request-42",
            ["remote-1", "remote-2"]);

        Assert.Equal(rebuilt, enriched.ToQuery());
        Assert.Equal(rebuilt.GetHashCode(), enriched.ToQuery().GetHashCode());
        Assert.Equal("/batch/2026", rebuilt.TargetPath.Value);
    }

    [Fact]
    public void SensitiveDomainValues_HaveExplicitlyRedactedStringRepresentations()
    {
        const string shareId = "fake-share-id-secret";
        const string extractionCode = "fake-extraction-code-secret";
        const string fullUrl = "https://pan.baidu.com/s/fake-url-secret";
        const string userId = "fake-full-user-id-secret";
        const string displayName = "fake-display-name-secret";
        const string outerName = "fake-outer-name-secret";
        const string fingerprint = "fake-fingerprint-secret";
        var share = new ShareReference(shareId, fullUrl, extractionCode);
        var identity = new BaiduAccountIdentity(userId, displayName);
        var resolved = new ResolvedShareLink(fullUrl, shareId);
        var metadata = new ShareMetadata(share, outerName, 1, 25, fingerprint);

        AssertRedacted(share.ToString(), shareId, extractionCode, fullUrl);
        AssertRedacted(identity.ToString(), userId, displayName);
        AssertRedacted(resolved.ToString(), shareId, fullUrl);
        AssertRedacted(metadata.ToString(), shareId, extractionCode, fullUrl, outerName, fingerprint);
    }

    [Fact]
    public void DirectoryListing_UsesAnImmutableSnapshot()
    {
        var source = new List<RemoteDirectory>
        {
            new("directory-1", new RemotePath("/batch/2026"))
        };
        BaiduAdapterResult<ImmutableArray<RemoteDirectory>> result =
            BaiduAdapterResult<ImmutableArray<RemoteDirectory>>.Success(source.ToImmutableArray());

        source.Clear();

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.IsType<ImmutableArray<RemoteDirectory>>(result.Value);
    }

    [Fact]
    public void TransferReceipt_ReconstructsWithStructuralValueEquality()
    {
        var submittedAt = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
        var first = new TransferReceipt(submittedAt, "request-42", ["remote-2", "remote-1"]);
        var rebuilt = new TransferReceipt(submittedAt, "request-42", ["remote-1", "remote-2"]);

        Assert.Equal(first, rebuilt);
        Assert.Equal(first.GetHashCode(), rebuilt.GetHashCode());
    }

    [Fact]
    public void ConfirmationDescriptor_ReconstructsWithStructuralValueEquality()
    {
        var start = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
        var first = new OperationConfirmationDescriptor(
            "share-identity",
            new RemotePath("/batch/2026"),
            "outer-name-fingerprint",
            "operation-fingerprint",
            start,
            start.AddMinutes(2),
            "request-42",
            ["remote-2", "remote-1"]);
        var rebuilt = new OperationConfirmationDescriptor(
            "share-identity",
            new RemotePath("//batch//2026/"),
            "outer-name-fingerprint",
            "operation-fingerprint",
            start,
            start.AddMinutes(2),
            "request-42",
            ["remote-1", "remote-2"]);

        Assert.Equal(first, rebuilt);
        Assert.Equal(first.GetHashCode(), rebuilt.GetHashCode());
    }

    [Fact]
    public void ConfirmationDescriptor_RejectsImpossibleWindowsAndIdentifierReplacement()
    {
        var start = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => new OperationConfirmationDescriptor(
            "share-identity",
            new RemotePath("/batch/2026"),
            "outer-name-fingerprint",
            "operation-fingerprint",
            start,
            start.AddSeconds(-1)));

        var descriptor = new OperationConfirmationDescriptor(
            "share-identity",
            new RemotePath("/batch/2026"),
            "outer-name-fingerprint",
            "operation-fingerprint",
            start,
            start.AddMinutes(2),
            "request-42");

        Assert.Throws<InvalidOperationException>(() =>
            descriptor.WithResponseIdentifiers("different-request", []));
    }

    [Fact]
    public async Task Interfaces_ExposeOnlyStableCoreContracts()
    {
        IBaiduNetdiskAdapter adapter = new ContractAdapter();
        IShareLinkResolver resolver = new ContractResolver();
        IBaiduSessionStore sessionStore = new ContractSessionStore();
        var session = CreateSession();
        var share = new ShareReference(
            "share-identity",
            "https://pan.baidu.com/s/share-identity",
            "abcd");
        var path = new RemotePath("/batch/2026");
        var directory = new RemoteDirectory("directory-1", path);
        var query = new TransferConfirmationQuery(
            share.StableShareId,
            path,
            "outer-name-fingerprint",
            "operation-fingerprint",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1));

        await adapter.ValidateSessionAsync(session, CancellationToken.None);
        await adapter.GetCapacityAsync(session, CancellationToken.None);
        await adapter.ListDirectoriesAsync(session, path, CancellationToken.None);
        await adapter.EnsureDirectoryAsync(session, path, CancellationToken.None);
        await adapter.InspectShareAsync(session, share, CancellationToken.None);
        await adapter.SaveWholeShareAsync(
            session,
            share,
            directory,
            DuplicatePolicy.Skip,
            CancellationToken.None);
        await adapter.ConfirmTransferAsync(session, query, CancellationToken.None);
        await resolver.ResolveAsync(share.OriginalUrl, CancellationToken.None);
        await sessionStore.SaveAsync(session, CancellationToken.None);
        Assert.Equal(session, await sessionStore.LoadAsync(session.AccountId, CancellationToken.None));
        await sessionStore.RemoveAsync(session.AccountId, CancellationToken.None);
    }

    private static AccountSession CreateSession() => new(
        "account-1",
        [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero), true, true)],
        new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero));

    private static void AssertRedacted(string rendered, params string[] secrets)
    {
        Assert.Contains("[REDACTED]", rendered, StringComparison.Ordinal);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        }
    }

    private sealed class ContractAdapter : IBaiduNetdiskAdapter
    {
        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(
            AccountSession session,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<BaiduAccountIdentity>.Success(
                new BaiduAccountIdentity("baidu-user", "Test User")));

        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(
            AccountSession session,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<AccountCapacity>.Success(
                new AccountCapacity(100, 25)));

        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(
            AccountSession session,
            RemotePath path,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<ImmutableArray<RemoteDirectory>>.Success([]));

        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(
            AccountSession session,
            RemotePath path,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<RemoteDirectory>.Success(
                new RemoteDirectory("directory-1", path)));

        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(
            AccountSession session,
            ShareReference share,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<ShareMetadata>.Success(
                new ShareMetadata(share, "Shared folder", 1, 25, "outer-name-fingerprint")));

        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(
            AccountSession session,
            ShareReference share,
            RemoteDirectory destination,
            DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<TransferReceipt>.Success(
                new TransferReceipt(DateTimeOffset.UtcNow, "request-42", ["remote-1"])));

        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(
            AccountSession session,
            TransferConfirmationQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<TransferConfirmation>.Success(
                TransferConfirmation.ConfirmedPresent));
    }

    private sealed class ContractResolver : IShareLinkResolver
    {
        public Task<BaiduAdapterResult<ResolvedShareLink>> ResolveAsync(
            string rawUrl,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<ResolvedShareLink>.Success(
                new ResolvedShareLink(rawUrl, "share-identity")));
    }

    private sealed class ContractSessionStore : IBaiduSessionStore
    {
        private AccountSession? _session;

        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken)
        {
            _session = session;
            return Task.CompletedTask;
        }

        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(_session?.AccountId == accountId ? _session : null);

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken)
        {
            if (_session?.AccountId == accountId)
            {
                _session = null;
            }

            return Task.CompletedTask;
        }
    }
}
