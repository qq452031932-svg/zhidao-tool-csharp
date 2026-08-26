using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduTransferCandidateScriptsTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ObserveScriptIsReadOnlyBoundedAndChecksTrustedVisibleTopmostRoots()
    {
        var script = BaiduTransferCandidateScripts.ObserveScript;

        Assert.Contains("protocol === 'https:'", script, StringComparison.Ordinal);
        Assert.Contains("hostname === 'pan.baidu.com'", script, StringComparison.Ordinal);
        Assert.Contains("getAttribute('role')", script, StringComparison.Ordinal);
        Assert.Contains("getBoundingClientRect", script, StringComparison.Ordinal);
        Assert.Contains("elementFromPoint", script, StringComparison.Ordinal);
        Assert.Contains("cursor", script, StringComparison.Ordinal);
        Assert.Contains("generation", script, StringComparison.Ordinal);
        Assert.Contains("shadowRoot", script, StringComparison.Ordinal);
        Assert.Contains("mutationCount: 0", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.cookie", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outerHTML", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("innerHTML", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetch(", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XMLHttpRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localStorage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionStorage", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MutationScriptRevalidatesEveryBindingAndContainsOnlyAllowlistedActions()
    {
        var script = BaiduTransferCandidateScripts.MutateScript;

        Assert.Contains("location.href !== args.officialUri", script, StringComparison.Ordinal);
        Assert.Contains("expectedBaiduUserId", script, StringComparison.Ordinal);
        Assert.Contains("expectedParentExactId", script, StringComparison.Ordinal);
        Assert.Contains("expectedCanonicalPath", script, StringComparison.Ordinal);
        Assert.Contains("expectedObservationToken", script, StringComparison.Ordinal);
        Assert.Contains("elementFromPoint", script, StringComparison.Ordinal);
        Assert.Contains("SetExtractionCode", script, StringComparison.Ordinal);
        Assert.Contains("SubmitExtraction", script, StringComparison.Ordinal);
        Assert.Contains("OpenDestinationPicker", script, StringComparison.Ordinal);
        Assert.Contains("EnterDirectory", script, StringComparison.Ordinal);
        Assert.Contains("CreateDirectory", script, StringComparison.Ordinal);
        Assert.Contains("ConfirmDirectoryCreation", script, StringComparison.Ordinal);
        Assert.Contains("CancelDestinationPicker", script, StringComparison.Ordinal);
        Assert.Contains("ClickConfirmTransfer", script, StringComparison.Ordinal);
        Assert.Contains("mutationCount: 1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("T3ST", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransferMutationUsesOnePermitOneScriptAndNeverLeaksTypedValueOnHostFailure()
    {
        const string secret = "T3ST";
        var read = Request(BaiduCandidatePageState.DestinationPicker, ["item-1"], true);
        var legacy = CandidateAuthorization();
        var mutation = new BaiduCandidateDomMutationRequest(
            legacy.Authorization, legacy.Binding, BaiduAutomationAction.CreateDirectory, read, secret);
        var observation = Assert.IsType<BaiduTransferCandidateObservation>(
            BaiduTransferCandidateScripts.ParseAndValidate(
                Response(BaiduCandidatePageState.DestinationPicker, true, true), read).Observation);
        var authorizer = new TransferAuthorization(allow: true);
        var successHost = new ReturningTransferHost(Response(
            BaiduCandidatePageState.DestinationPicker, true, true, mutationCount: 1));
        var executor = new WebView2CandidateDomExecutor(successHost, authorizer);

        var result = await executor.MutateTransferAsync(mutation, observation);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, authorizer.AcquireCount);
        Assert.Equal(1, authorizer.DisposeCount);
        Assert.Equal(1, successHost.Calls);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);

        var throwingAuthorizer = new TransferAuthorization(allow: true);
        var host = new ThrowingTransferHost(secret);
        var failed = await new WebView2CandidateDomExecutor(host, throwingAuthorizer)
            .MutateTransferAsync(mutation, observation);
        Assert.False(failed.IsSuccess);
        Assert.Equal(1, throwingAuthorizer.DisposeCount);
        Assert.DoesNotContain(secret, failed.ToString(), StringComparison.Ordinal);

        var deniedHost = new ThrowingTransferHost(secret);
        var denied = await new WebView2CandidateDomExecutor(
                deniedHost, new TransferAuthorization(allow: false))
            .MutateTransferAsync(mutation, observation);
        Assert.False(denied.IsSuccess);
        Assert.Equal(0, deniedHost.Calls);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.MutateTransferAsync(mutation, observation, cancelled.Token));
        Assert.DoesNotContain(secret, cancellation.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BaiduCandidatePageState.ExtractionCode, false)]
    [InlineData(BaiduCandidatePageState.ExternalShare, true)]
    [InlineData(BaiduCandidatePageState.DestinationPicker, true)]
    [InlineData(BaiduCandidatePageState.OwnDrive, true)]
    [InlineData(BaiduCandidatePageState.TransferResult, true)]
    public async Task StrictReadParserAcceptsOnlyCompleteBoundOfficialEvidence(
        BaiduCandidatePageState state,
        bool hasItem)
    {
        var directory = state == BaiduCandidatePageState.DestinationPicker;
        var request = Request(state, hasItem ? ["item-1"] : [], directory);
        var host = new TransferReadHost(Response(state, hasItem, directory: directory));

        var result = await new WebView2CandidateDomExecutor(host, new DenyAuthorization())
            .ObserveTransferAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, host.MutationCalls);
        Assert.Equal("uid-1", result.Observation!.BaiduUserId);
        Assert.Equal(hasItem ? 1 : 0, result.Observation.TotalCount);
        Assert.True(result.Observation.ReachedEnd);
        Assert.DoesNotContain("item-1", result.Observation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("目录", result.Observation.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hidden-duplicate")]
    [InlineData("advertisement-outside-root")]
    [InlineData("overlay-hit-test-failed")]
    [InlineData("cursor-cycle")]
    [InlineData("cursor-gap")]
    [InlineData("changing-generation")]
    [InlineData("unapproved-shadow-root")]
    public async Task ScriptAmbiguityAlwaysReturnsNoMutationAndIsRejected(string reason)
    {
        var request = Request(BaiduCandidatePageState.ExternalShare, ["item-1"], false);
        var host = new TransferReadHost(NotValidated(reason));

        var result = await new WebView2CandidateDomExecutor(host, new DenyAuthorization())
            .ObserveTransferAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduAutomationDenialReason.ObservationNotValidated, result.DenialReason);
        Assert.Equal(0, host.MutationCalls);
    }

    [Theory]
    [InlineData("wrong-uid")]
    [InlineData("wrong-parent")]
    [InlineData("wrong-path")]
    [InlineData("wrong-ancestor")]
    [InlineData("total-mismatch")]
    [InlineData("not-at-end")]
    [InlineData("not-stable")]
    [InlineData("duplicate-id")]
    [InlineData("unsorted-id")]
    public async Task ParserRejectsMismatchedIdentityPathAndEnumerationEvidence(string variant)
    {
        var request = Request(BaiduCandidatePageState.DestinationPicker, ["item-1", "item-2"], true);
        var response = variant switch
        {
            "wrong-uid" => Response(BaiduCandidatePageState.DestinationPicker, true, true, uid: "uid-2", secondItem: true),
            "wrong-parent" => Response(BaiduCandidatePageState.DestinationPicker, true, true, parent: "parent-2", secondItem: true),
            "wrong-path" => Response(BaiduCandidatePageState.DestinationPicker, true, true, path: "/另一个目录", secondItem: true),
            "wrong-ancestor" => Response(BaiduCandidatePageState.DestinationPicker, true, true, ancestor: "ancestor-2", secondItem: true),
            "total-mismatch" => Response(BaiduCandidatePageState.DestinationPicker, true, true, total: 3, secondItem: true),
            "not-at-end" => Response(BaiduCandidatePageState.DestinationPicker, true, true, reachedEnd: false, secondItem: true),
            "not-stable" => Response(BaiduCandidatePageState.DestinationPicker, true, true, stable: false, secondItem: true),
            "duplicate-id" => Response(BaiduCandidatePageState.DestinationPicker, true, true, itemIds: ["item-1", "item-1"]),
            "unsorted-id" => Response(BaiduCandidatePageState.DestinationPicker, true, true, itemIds: ["item-2", "item-1"]),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        var result = await new WebView2CandidateDomExecutor(
                new TransferReadHost(response), new DenyAuthorization())
            .ObserveTransferAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduAutomationDenialReason.ObservationNotValidated, result.DenialReason);
    }

    [Fact]
    public async Task ParserRejectsUnknownAndDuplicateFieldsAndOversizedResponses()
    {
        var request = Request(BaiduCandidatePageState.ExternalShare, ["item-1"], false);
        var valid = Response(BaiduCandidatePageState.ExternalShare, true);
        var unknown = valid[..^1] + ",\"rawHtml\":\"secret\"}";
        var duplicate = valid.Replace("\"status\":\"validated\"",
            "\"status\":\"validated\",\"status\":\"validated\"", StringComparison.Ordinal);
        var executor = new WebView2CandidateDomExecutor(new TransferReadHost(unknown), new DenyAuthorization());

        Assert.False((await executor.ObserveTransferAsync(request)).IsSuccess);
        Assert.False((await new WebView2CandidateDomExecutor(
            new TransferReadHost(duplicate), new DenyAuthorization()).ObserveTransferAsync(request)).IsSuccess);
        Assert.False((await new WebView2CandidateDomExecutor(
            new TransferReadHost(new string('x', 65_537)), new DenyAuthorization())
            .ObserveTransferAsync(request)).IsSuccess);
    }

    [Fact]
    public async Task EmptyExpectedSetCanDiscoverACompleteExactDirectoryListing()
    {
        var request = new BaiduCandidateDomReadRequest(
            new Uri("https://pan.baidu.com/disk/main"),
            BaiduCandidatePageState.DestinationPicker, "uid-1", [], null, null);

        var result = await new WebView2CandidateDomExecutor(
                new TransferReadHost(Response(
                    BaiduCandidatePageState.DestinationPicker, true, directory: true)),
                new DenyAuthorization())
            .ObserveTransferAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal("parent-1", result.Observation!.ParentExactId);
        Assert.Equal("item-1", Assert.Single(result.Observation.Items).ExactId);
    }

    private static BaiduCandidateDomReadRequest Request(
        BaiduCandidatePageState state,
        string[] ids,
        bool directory) =>
        new(new Uri(state == BaiduCandidatePageState.ExternalShare
                ? "https://pan.baidu.com/s/share-id"
                : "https://pan.baidu.com/disk/main"),
            state, "uid-1", ids, directory ? "parent-1" : null,
            directory ? new RemotePath("/目录") : null);

    private static string Response(
        BaiduCandidatePageState state,
        bool hasItem,
        bool directory = false,
        string uid = "uid-1",
        string? parent = null,
        string? path = null,
        string? ancestor = null,
        int? total = null,
        bool reachedEnd = true,
        bool stable = true,
        bool secondItem = false,
        string[]? itemIds = null,
        int mutationCount = 0)
    {
        var ids = itemIds ?? (secondItem ? ["item-1", "item-2"] : hasItem ? ["item-1"] : []);
        var items = string.Join(',', ids.Select(id =>
            $"{{\"exactId\":\"{id}\",\"kind\":\"File\",\"serverName\":\"server-name\"}}"));
        var parentValue = directory ? parent ?? "parent-1" : null;
        var pathValue = directory ? path ?? "/目录" : null;
        var ancestors = directory ? $"[\"{ancestor ?? parentValue}\"]" : "[]";
        var officialRequestId = state == BaiduCandidatePageState.TransferResult
            ? "\"request-1\""
            : "null";
        var officialMapping = state == BaiduCandidatePageState.TransferResult
            ? $"[{{\"sourceExactId\":\"source-1\",\"destinationExactId\":\"{ids[0]}\"}}]"
            : "[]";
        return $$"""
            {"status":"validated","reason":"none","officialOriginMatched":true,
             "pageState":"{{state}}","controlCount":1,"baiduUserId":"{{uid}}",
             "parentExactId":{{(parentValue is null ? "null" : $"\"{parentValue}\"")}},
             "canonicalPath":{{(pathValue is null ? "null" : $"\"{pathValue}\"")}},
             "ancestorExactIds":{{ancestors}},"totalCount":{{total ?? ids.Length}},
             "reachedEnd":{{reachedEnd.ToString().ToLowerInvariant()}},"complete":true,
             "stable":{{stable.ToString().ToLowerInvariant()}},"generationToken":"generation-1",
             "items":[{{items}}],"observationToken":"{{Token}}","mutationCount":{{mutationCount}},
             "officialRequestId":{{officialRequestId}},"officialMapping":{{officialMapping}}}
            """;
    }

    private static string NotValidated(string reason) => $$"""
        {"status":"notValidated","reason":"{{reason}}","officialOriginMatched":true,
         "pageState":"ExternalShare","controlCount":0,"baiduUserId":"uid-1",
         "parentExactId":null,"canonicalPath":null,"ancestorExactIds":[],"totalCount":0,
         "reachedEnd":false,"complete":false,"stable":false,"generationToken":"none",
         "items":[],"observationToken":"","mutationCount":0,
         "officialRequestId":null,"officialMapping":[]}
        """;

    private sealed class TransferReadHost(string response) : IBaiduCandidateDomScriptHost
    {
        public int MutationCalls { get; private set; }

        public Task<string> ExecuteAsync(
            BaiduCandidateBuiltInScript script,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            if (script != BaiduCandidateBuiltInScript.ObserveTransfer) MutationCalls++;
            return Task.FromResult(response);
        }
    }

    private sealed class DenyAuthorization : IBaiduCandidateActionAuthorizationSource
    {
        public ValueTask<bool> IsAuthorizedAsync(
            BaiduCandidateDomRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireMutationPermitAsync(
            BaiduCandidateDomRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IBaiduCandidateMutationPermit?>(null);
    }

    private static (AdapterWriteAuthorizationRequest Authorization, AdapterWriteAuthorizationBinding Binding)
        CandidateAuthorization()
    {
        var capability = new AdapterCapabilityVersion(
            "BaiduTransfer", BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion, "build-1");
        var authorization = new AdapterWriteAuthorizationRequest(
            capability, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            AdapterWriteAction.EnsureDirectory);
        var now = DateTimeOffset.UtcNow;
        return (authorization, new AdapterWriteAuthorizationBinding(
            AdapterWriteAuthorizationSource.ConsumedLease, capability,
            authorization.AccountId, authorization.OperationId, authorization.Generation,
            authorization.BatchId, now, now.AddMinutes(5), false, null));
    }

    private sealed class TransferAuthorization(bool allow) : IBaiduCandidateActionAuthorizationSource
    {
        public int AcquireCount { get; private set; }
        public int DisposeCount { get; private set; }

        public ValueTask<bool> IsAuthorizedAsync(
            BaiduCandidateDomRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireMutationPermitAsync(
            BaiduCandidateDomRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IBaiduCandidateMutationPermit?>(null);

        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireTransferMutationPermitAsync(
            BaiduCandidateDomMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            return ValueTask.FromResult<IBaiduCandidateMutationPermit?>(
                allow ? new Permit(() => DisposeCount++) : null);
        }

        private sealed class Permit(Action dispose) : IBaiduCandidateMutationPermit
        {
            public ValueTask DisposeAsync()
            {
                dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ThrowingTransferHost(string secret) : IBaiduCandidateDomScriptHost
    {
        public int Calls { get; private set; }

        public Task<string> ExecuteAsync(
            BaiduCandidateBuiltInScript script,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException($"synthetic host failure {secret}");
        }
    }

    private sealed class ReturningTransferHost(string response) : IBaiduCandidateDomScriptHost
    {
        public int Calls { get; private set; }

        public Task<string> ExecuteAsync(
            BaiduCandidateBuiltInScript script,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(response);
        }
    }
}
