using System.Collections.Immutable;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class ExecutionBoundTransferAdapterTests
{
    private const string BindingMismatch = "The execution session does not match the operation snapshot.";
    private const string SourceMismatch = "The transfer source does not match the operation.";
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);

    public enum Phase { Ensure, Prepare, Save, Confirm }

    public static IEnumerable<object[]> Routes()
    {
        foreach (var family in Enum.GetValues<BrowserFamily>())
        foreach (var phase in Enum.GetValues<Phase>())
            yield return [family, phase];
    }

    public static IEnumerable<object[]> BindingMismatches()
    {
        foreach (var route in Routes())
        {
            var family = (BrowserFamily)route[0];
            var mismatches = family == BrowserFamily.LegacyWebView2
                ? new[] { "cross-engine", "account", "unbound", "profile", "profile-case" }
                : new[] { "cross-engine", "account", "session", "installation", "family", "profile", "profile-case" };
            foreach (var mismatch in mismatches)
                yield return [route[0], route[1], mismatch];
        }
    }

    public static IEnumerable<object[]> InvalidInputs()
    {
        foreach (var route in Routes())
        {
            foreach (var invalid in new[] { "operation-null", "execution-null", "operation-id", "batch-id",
                         "account-id", "generation", "source-null", "cancel" })
                yield return [route[0], route[1], invalid];
            var phase = (Phase)route[1];
            var arguments = phase switch
            {
                Phase.Ensure => new[] { "path-null" },
                Phase.Confirm => new[] { "query-null", "query-source", "query-source-case" },
                _ => new[] { "share-null", "destination-null", "policy-negative", "policy-unknown",
                    "share-id", "share-id-case", "share-url", "share-code", "share-code-null" }
            };
            foreach (var argument in arguments)
                yield return [route[0], route[1], argument];
        }
    }

    public static IEnumerable<object[]> DownstreamOutcomes()
    {
        foreach (var route in Routes())
        foreach (var outcome in new[] { "failure", "throw", "fault", "cancel" })
            yield return [route[0], route[1], outcome];
    }

    public static IEnumerable<object[]> ExactSets()
    {
        foreach (var family in Enum.GetValues<BrowserFamily>())
        foreach (var phase in new[] { Phase.Save, Phase.Confirm })
        foreach (var shape in new[] { "folder", "apk", "multiple" })
            yield return [family, phase, shape];
    }

    [Fact]
    public void ManagedInterfaceDoesNotInheritCookieBasedContracts()
    {
        Assert.Empty(typeof(IManagedChromiumTransferAdapter).GetInterfaces());
        Assert.Equal(4, typeof(IManagedChromiumTransferAdapter).GetMethods().Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConstructorRejectsNullDependencies(bool legacy)
    {
        Assert.Throws<ArgumentNullException>(() => new ExecutionBoundTransferAdapter(
            legacy ? null! : new LegacyAdapter(), legacy ? new ManagedAdapter() : null!));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task RoutesOnlyToSnapshotEngineAndPreservesArgumentsAndResult(BrowserFamily family, Phase phase)
    {
        var scenario = new Scenario(family);
        using var cancellation = new CancellationTokenSource();
        scenario.Token = cancellation.Token;
        // Distinct but fully equal source values must be accepted without replacing the passed object.
        scenario.Share = new ShareReference(scenario.Operation.SourceShare.StableShareId,
            scenario.Operation.SourceShare.OriginalUrl, scenario.Operation.SourceShare.ExtractionCode);

        var result = await scenario.Invoke(phase);

        Assert.Same(scenario.Selected.Results[phase], result);
        var call = Assert.Single(scenario.Selected.Calls);
        Assert.Empty(scenario.Other.Calls);
        Assert.Equal(phase, call.Phase);
        Assert.Same(scenario.Operation, call.Operation);
        Assert.Equal(7, call.Operation.Generation);
        Assert.True(call.Operation.IsRecovery);
        Assert.Same(scenario.Operation.AuthorizationBinding, call.Operation.AuthorizationBinding);
        Assert.NotNull(call.Operation.AuthorizationBinding);
        Assert.Same((object?)scenario.Execution.LegacySession ?? scenario.Execution.ManagedSession, call.Session);
        Assert.Equal(scenario.Token, call.Token);
        Assert.Same(phase switch
        {
            Phase.Ensure => (object)scenario.Path,
            Phase.Confirm => scenario.Query,
            _ => scenario.Share
        }, call.Argument);
        if (phase is Phase.Prepare or Phase.Save)
        {
            Assert.Same(scenario.Destination, call.Destination);
            Assert.Equal(scenario.Policy, call.Policy);
        }
    }

    [Theory]
    [MemberData(nameof(BindingMismatches))]
    public async Task RejectsEveryBindingMismatchBeforeEitherAdapter(BrowserFamily family, Phase phase, string mismatch)
    {
        var scenario = new Scenario(family);
        if (mismatch == "cross-engine")
            scenario.Execution = family == BrowserFamily.LegacyWebView2
                ? BaiduExecutionContext.Managed(ManagedSession(scenario.Operation.AccountId, BrowserFamily.Chrome))
                : BaiduExecutionContext.Legacy(LegacySession(scenario.Operation.AccountId));
        else if (family == BrowserFamily.LegacyWebView2)
        {
            if (mismatch is "profile" or "profile-case")
                scenario.Operation = scenario.Operation with
                {
                    PersistedExecutionProfile = ExecutionProfileSnapshot.Legacy(mismatch == "profile"
                        ? "different-private-profile"
                        : scenario.Operation.ExecutionProfile.ProfileId.ToUpperInvariant())
                };
            else
                scenario.Execution = BaiduExecutionContext.Legacy(mismatch == "account"
                    ? LegacySession(Guid.NewGuid())
                    : new AccountSession("unbound-private-capture", Cookies(), Now));
        }
        else
        {
            var original = scenario.Execution.ManagedSession!;
            scenario.Execution = BaiduExecutionContext.Managed(new ManagedBrowserSessionReference(
                mismatch == "session" ? Guid.NewGuid() : original.SessionId,
                mismatch == "installation" ? Guid.NewGuid() : original.InstallationId,
                mismatch == "account" ? Guid.NewGuid() : original.AccountId,
                original.StableBaiduUserId,
                mismatch == "family" ? (family == BrowserFamily.Chrome ? BrowserFamily.Edge : BrowserFamily.Chrome) : family,
                mismatch switch { "profile" => "different-private-profile", "profile-case" => original.ProfileId.ToUpperInvariant(), _ => original.ProfileId },
                original.ProfileOwnershipId));
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Invoke(phase));

        Assert.Equal(BindingMismatch, error.Message);
        scenario.AssertNoCalls();
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task RejectsInvalidInputsBeforeEitherAdapter(BrowserFamily family, Phase phase, string invalid)
    {
        var scenario = new Scenario(family);
        switch (invalid)
        {
            case "operation-null": scenario.Operation = null!; break;
            case "execution-null": scenario.Execution = null!; break;
            case "operation-id": scenario.Operation = scenario.Operation with { OperationId = Guid.Empty }; break;
            case "batch-id": scenario.Operation = scenario.Operation with { BatchId = Guid.Empty }; break;
            case "account-id": scenario.Operation = scenario.Operation with { AccountId = Guid.Empty }; break;
            case "generation": scenario.Operation = scenario.Operation with { Generation = 0 }; break;
            case "source-null": scenario.Operation = scenario.Operation with { SourceShare = null! }; break;
            case "cancel": scenario.Token = new CancellationToken(canceled: true); break;
            case "path-null": scenario.Path = null!; break;
            case "query-null": scenario.Query = null!; break;
            case "share-null": scenario.Share = null!; break;
            case "destination-null": scenario.Destination = null!; break;
            case "policy-negative": scenario.Policy = (DuplicatePolicy)(-1); break;
            case "policy-unknown": scenario.Policy = (DuplicatePolicy)999; break;
            case "query-source": scenario.Query = Query("different-private-source"); break;
            case "query-source-case": scenario.Query = Query(scenario.Share.StableShareId.ToUpperInvariant()); break;
            default:
                scenario.Share = new ShareReference(
                    invalid == "share-id" ? "different-private-source" : invalid == "share-id-case"
                        ? scenario.Share.StableShareId.ToUpperInvariant() : scenario.Share.StableShareId,
                    invalid == "share-url" ? "https://example.invalid/another-private-url" : scenario.Share.OriginalUrl,
                    invalid == "share-code" ? "z9x8" : invalid == "share-code-null" ? null : scenario.Share.ExtractionCode);
                break;
        }

        if (invalid == "cancel")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Invoke(phase));
            Assert.Equal(scenario.Token, error.CancellationToken);
        }
        else if (invalid.StartsWith("share-", StringComparison.Ordinal) && invalid != "share-null" ||
                 invalid.StartsWith("query-source", StringComparison.Ordinal))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Invoke(phase));
            Assert.Equal(SourceMismatch, error.Message);
        }
        else if (invalid.EndsWith("-null", StringComparison.Ordinal))
            await Assert.ThrowsAsync<ArgumentNullException>(() => scenario.Invoke(phase));
        else if (invalid == "generation" || invalid.StartsWith("policy-", StringComparison.Ordinal))
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scenario.Invoke(phase));
        else
            await Assert.ThrowsAsync<ArgumentException>(() => scenario.Invoke(phase));
        scenario.AssertNoCalls();
    }

    [Theory]
    [MemberData(nameof(DownstreamOutcomes))]
    public async Task PreservesDownstreamOutcomeWithoutRetryOrFallback(BrowserFamily family, Phase phase, string outcome)
    {
        var scenario = new Scenario(family);
        if (outcome == "failure")
        {
            scenario.Selected.Results[phase] = Failure(phase);
            Assert.Same(scenario.Selected.Results[phase], await scenario.Invoke(phase));
        }
        else
        {
            var token = new CancellationToken(canceled: true);
            Exception expected = outcome == "cancel"
                ? new OperationCanceledException("synthetic downstream cancellation", token)
                : new InvalidOperationException("synthetic downstream exception");
            scenario.Selected.Error = expected;
            scenario.Selected.ThrowSynchronously = outcome == "throw";
            var actual = await Record.ExceptionAsync(() => scenario.Invoke(phase));
            Assert.Same(expected, actual);
        }

        Assert.Equal(phase, Assert.Single(scenario.Selected.Calls).Phase);
        Assert.Empty(scenario.Other.Calls);
    }

    [Theory]
    [MemberData(nameof(ExactSets))]
    public async Task PreservesExactDestinationSetForLaterSharing(BrowserFamily family, Phase phase, string shape)
    {
        var scenario = new Scenario(family);
        // These labels model folder/APK/multiple receipts, not verified browser object types.
        var items = new RemoteItemIdentitySet(shape switch
        {
            "folder" => [new RemoteItemIdentity("folder-exact", "Course folder")],
            "apk" => [new RemoteItemIdentity("file-exact", "application.apk")],
            _ => [new RemoteItemIdentity("folder-exact", "Same display name"),
                new RemoteItemIdentity("file-exact", "Same display name"), new RemoteItemIdentity("apk-exact", "application.apk")]
        });
        var expectedIds = items.Items.Select(item => item.ExactId).ToArray();
        var expectedNames = items.Items.Select(item => item.DisplayedTopLevelName).ToArray();
        scenario.Selected.Results[phase] = phase == Phase.Save
            ? BaiduAdapterResult<TransferReceipt>.Success(TransferReceipt.WithExactEvidence(Now, "request-exact", items))
            : BaiduAdapterResult<TransferConfirmationResult>.Success(TransferConfirmationResult.ExactPresent(items));

        var result = await scenario.Invoke(phase);

        Assert.Same(scenario.Selected.Results[phase], result);
        var actualItems = phase == Phase.Save
            ? Assert.IsType<BaiduAdapterResult<TransferReceipt>>(result).Value!.ExactRemoteItems
            : Assert.IsType<BaiduAdapterResult<TransferConfirmationResult>>(result).Value!.RemoteItems;
        Assert.Same(items, actualItems);
        Assert.Equal(expectedIds, actualItems!.Items.Select(item => item.ExactId));
        Assert.Equal(expectedNames, actualItems.Items.Select(item => item.DisplayedTopLevelName));
        var laterShare = new PermanentShareRequest(actualItems);
        Assert.Same(items, laterShare.RemoteItems);
        Assert.Equal(phase, Assert.Single(scenario.Selected.Calls).Phase);
        Assert.Empty(scenario.Other.Calls);
    }

    [Theory]
    [InlineData(DuplicatePolicy.Skip)]
    [InlineData(DuplicatePolicy.Overwrite)]
    [InlineData(DuplicatePolicy.Rename)]
    public async Task AcceptsAllDefinedDuplicatePolicies(DuplicatePolicy policy)
    {
        foreach (var family in Enum.GetValues<BrowserFamily>())
        foreach (var phase in new[] { Phase.Prepare, Phase.Save })
        {
            var scenario = new Scenario(family) { Policy = policy };
            await scenario.Invoke(phase);
            Assert.Equal(policy, Assert.Single(scenario.Selected.Calls).Policy);
            Assert.Empty(scenario.Other.Calls);
        }
    }

    [Theory]
    [InlineData(BrowserFamily.Chrome)]
    [InlineData(BrowserFamily.Edge)]
    public async Task MatchingSnapshotDoesNotPretendToValidateLiveUidOrOwnership(BrowserFamily family)
    {
        foreach (var phase in Enum.GetValues<Phase>())
        {
            var scenario = new Scenario(family);
            var reference = scenario.Execution.ManagedSession!;
            scenario.Execution = BaiduExecutionContext.Managed(new ManagedBrowserSessionReference(
                reference.SessionId, reference.InstallationId, reference.AccountId,
                "different-uid-to-be-checked-downstream", reference.BrowserFamily, reference.ProfileId,
                "different-ownership-to-be-checked-downstream"));
            await scenario.Invoke(phase);
            Assert.Same(scenario.Execution.ManagedSession, Assert.Single(scenario.Selected.Calls).Session);
            Assert.Empty(scenario.Other.Calls);
        }
    }

    private static object Failure(Phase phase) => phase switch
    {
        Phase.Ensure => Failed<TransferDirectoryPreparationOutcome>(),
        Phase.Prepare => Failed<TransferPreparationReceipt>(),
        Phase.Save => Failed<TransferReceipt>(),
        Phase.Confirm => Failed<TransferConfirmationResult>(),
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    private static BaiduAdapterResult<T> Failed<T>() => BaiduAdapterResult<T>.Failure(
        BaiduErrorKind.VerificationRequired, "private discarded diagnostic", TimeSpan.FromSeconds(17),
        SubmissionAcceptance.DefinitelyNotAccepted);

    private static SessionCookie[] Cookies() =>
        [new SessionCookie("BDUSS", "synthetic-only", ".baidu.com", "/", null, true, true)];

    private static AccountSession LegacySession(Guid accountId) => new(accountId, "synthetic-uid", Cookies(), Now);

    private static ManagedBrowserSessionReference ManagedSession(Guid accountId, BrowserFamily family) =>
        new(Guid.NewGuid(), Guid.NewGuid(), accountId, "synthetic-uid", family,
            "synthetic-private-profile", "synthetic-ownership");

    private static TransferConfirmationQuery Query(string sourceId) => new(
        sourceId, new RemotePath("/synthetic-target"), "outer-fingerprint", "operation-fingerprint",
        Now.AddMinutes(-1), Now.AddMinutes(1), "request-exact", ["exact-a", "exact-b"]);

    private sealed class Scenario
    {
        public Scenario(BrowserFamily family)
        {
            var accountId = Guid.NewGuid();
            var operationId = Guid.NewGuid();
            var batchId = Guid.NewGuid();
            Share = new ShareReference("synthetic-private-share", "https://example.invalid/private-share", "a1b2");
            Execution = family == BrowserFamily.LegacyWebView2
                ? BaiduExecutionContext.Legacy(LegacySession(accountId))
                : BaiduExecutionContext.Managed(ManagedSession(accountId, family));
            var managed = Execution.ManagedSession;
            Operation = new(operationId, batchId, accountId, 7, Share, true,
                new AdapterWriteAuthorizationBinding(AdapterWriteAuthorizationSource.ExactFormalRecord,
                    new AdapterCapabilityVersion("BaiduTransfer", "synthetic-p", "synthetic-s", "synthetic-b"),
                    accountId, operationId, 7, batchId, Now, Now.AddMinutes(1), true, "synthetic-evidence"),
                managed is null ? null : ExecutionProfileSnapshot.Managed(
                    managed.BrowserFamily, managed.ProfileId, managed.InstallationId, managed.SessionId));
            Query = ExecutionBoundTransferAdapterTests.Query(Share.StableShareId);
            Selected = family == BrowserFamily.LegacyWebView2 ? Legacy : Managed;
            Other = family == BrowserFamily.LegacyWebView2 ? Managed : Legacy;
            Sut = new ExecutionBoundTransferAdapter(Legacy, Managed);
        }

        public LegacyAdapter Legacy { get; } = new();
        public ManagedAdapter Managed { get; } = new();
        public RecordingAdapter Selected { get; }
        public RecordingAdapter Other { get; }
        public ExecutionBoundTransferAdapter Sut { get; }
        public TransferOperationAutomationContext Operation { get; set; }
        public BaiduExecutionContext Execution { get; set; }
        public ShareReference Share { get; set; }
        public RemotePath Path { get; set; } = new("/synthetic-target");
        public RemoteDirectory Destination { get; set; } = new("destination-exact", new("/synthetic-target"));
        public TransferConfirmationQuery Query { get; set; }
        public DuplicatePolicy Policy { get; set; } = DuplicatePolicy.Rename;
        public CancellationToken Token { get; set; }

        public async Task<object> Invoke(Phase phase) => phase switch
        {
            Phase.Ensure => await Sut.EnsureDirectoryForOperationAsync(Operation, Execution, Path, Token),
            Phase.Prepare => await Sut.PrepareWholeShareForOperationAsync(Operation, Execution, Share, Destination, Policy, Token),
            Phase.Save => await Sut.SaveWholeShareForOperationAsync(Operation, Execution, Share, Destination, Policy, Token),
            Phase.Confirm => await Sut.ConfirmExactTransferForOperationAsync(Operation, Execution, Query, Token),
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };

        public void AssertNoCalls()
        {
            Assert.Empty(Legacy.Calls);
            Assert.Empty(Managed.Calls);
        }
    }

    private sealed record Call(Phase Phase, TransferOperationAutomationContext Operation, object Session,
        object Argument, RemoteDirectory? Destination, DuplicatePolicy? Policy, CancellationToken Token);

    private abstract class RecordingAdapter
    {
        public List<Call> Calls { get; } = [];
        public Exception? Error { get; set; }
        public bool ThrowSynchronously { get; set; }
        public Dictionary<Phase, object> Results { get; } = new()
        {
            [Phase.Ensure] = BaiduAdapterResult<TransferDirectoryPreparationOutcome>.Success(
                TransferDirectoryPreparationOutcome.Prepared(new("prepared-exact", new("/prepared")))),
            [Phase.Prepare] = BaiduAdapterResult<TransferPreparationReceipt>.Success(TransferPreparationReceipt.Ready),
            [Phase.Save] = BaiduAdapterResult<TransferReceipt>.Success(new(Now, "synthetic-request", ["exact-a"])),
            [Phase.Confirm] = BaiduAdapterResult<TransferConfirmationResult>.Success(TransferConfirmationResult.Ambiguous)
        };

        protected Task<BaiduAdapterResult<T>> Record<T>(Phase phase, TransferOperationAutomationContext operation,
            object session, object argument, CancellationToken token,
            RemoteDirectory? destination = null, DuplicatePolicy? policy = null)
        {
            Calls.Add(new(phase, operation, session, argument, destination, policy, token));
            if (Error is not null)
            {
                if (ThrowSynchronously) throw Error;
                return Task.FromException<BaiduAdapterResult<T>>(Error);
            }
            return Task.FromResult((BaiduAdapterResult<T>)Results[phase]);
        }
    }

    private sealed class ManagedAdapter : RecordingAdapter, IManagedChromiumTransferAdapter
    {
        public Task<BaiduAdapterResult<TransferDirectoryPreparationOutcome>> EnsureDirectoryForOperationAsync(
            TransferOperationAutomationContext operation, ManagedBrowserSessionReference session, RemotePath path,
            CancellationToken token) => Record<TransferDirectoryPreparationOutcome>(Phase.Ensure, operation, session, path, token);

        public Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(
            TransferOperationAutomationContext operation, ManagedBrowserSessionReference session, ShareReference share,
            RemoteDirectory destination, DuplicatePolicy policy, CancellationToken token) =>
            Record<TransferPreparationReceipt>(Phase.Prepare, operation, session, share, token, destination, policy);

        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
            TransferOperationAutomationContext operation, ManagedBrowserSessionReference session, ShareReference share,
            RemoteDirectory destination, DuplicatePolicy policy, CancellationToken token) =>
            Record<TransferReceipt>(Phase.Save, operation, session, share, token, destination, policy);

        public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
            TransferOperationAutomationContext operation, ManagedBrowserSessionReference session,
            TransferConfirmationQuery query, CancellationToken token) =>
            Record<TransferConfirmationResult>(Phase.Confirm, operation, session, query, token);
    }

    private sealed class LegacyAdapter : RecordingAdapter, IOperationBoundBaiduNetdiskAdapter
    {
        public Task<BaiduAdapterResult<TransferDirectoryPreparationOutcome>> EnsureDirectoryForOperationAsync(
            TransferOperationAutomationContext operation, AccountSession session, RemotePath path,
            CancellationToken token) => Record<TransferDirectoryPreparationOutcome>(Phase.Ensure, operation, session, path, token);

        public Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(
            TransferOperationAutomationContext operation, AccountSession session, ShareReference share,
            RemoteDirectory destination, DuplicatePolicy policy, CancellationToken token) =>
            Record<TransferPreparationReceipt>(Phase.Prepare, operation, session, share, token, destination, policy);

        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
            TransferOperationAutomationContext operation, AccountSession session, ShareReference share,
            RemoteDirectory destination, DuplicatePolicy policy, CancellationToken token) =>
            Record<TransferReceipt>(Phase.Save, operation, session, share, token, destination, policy);

        public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
            TransferOperationAutomationContext operation, AccountSession session,
            TransferConfirmationQuery query, CancellationToken token) =>
            Record<TransferConfirmationResult>(Phase.Confirm, operation, session, query, token);

        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected non-operation-bound call.");
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected non-operation-bound call.");
        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected non-operation-bound call.");
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected non-operation-bound call.");
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected non-operation-bound call.");
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy policy, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected non-operation-bound call.");
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected non-operation-bound call.");
    }
}
