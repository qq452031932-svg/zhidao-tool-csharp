using System.Text;
using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedSessionOperationClientTests
{
    [Theory]
    [InlineData("EnsureDirectory")]
    [InlineData("SubmitTransfer")]
    [InlineData("SubmitShare")]
    public async Task MutatingOperationsAreDeniedBeforeDispatchWhenContractIsUnavailable(string operation)
    {
        var dispatcher = new RecordingDispatcher();
        var client = new ManagedSessionOperationClient(
            dispatcher, ManagedSessionOperationContractAvailability.ReadOnly);

        var result = await InvokeAsync(client, Request(operation));

        Assert.Equal(ManagedSessionOperationResultKind.ContractUnavailable, result.Kind);
        Assert.Equal(0, dispatcher.CallCount);
        Assert.DoesNotContain("123456789", result.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("InspectSource")]
    [InlineData("ObserveTransfer")]
    [InlineData("ObserveShare")]
    public async Task ReadOperationsUseTheFixedDispatcher(string operation)
    {
        var dispatcher = new RecordingDispatcher();
        var client = new ManagedSessionOperationClient(
            dispatcher, ManagedSessionOperationContractAvailability.ReadOnly);

        var request = Request(operation);
        var result = await InvokeAsync(client, request);

        Assert.Equal(ManagedSessionOperationResultKind.Succeeded, result.Kind);
        Assert.Equal(1, dispatcher.CallCount);
        Assert.Same(request, dispatcher.LastRequest);
    }

    [Fact]
    public async Task ExplicitAvailabilityEnablesOnlyTheNamedMutation()
    {
        var dispatcher = new RecordingDispatcher();
        var availability = ManagedSessionOperationContractAvailability.ReadOnly
            .WithEnabledMutation(ManagedSessionOperationKind.SubmitTransfer);
        var client = new ManagedSessionOperationClient(dispatcher, availability);

        var transfer = await client.SubmitTransferAsync(Request("SubmitTransfer"), CancellationToken.None);
        var share = await client.SubmitShareAsync(Request("SubmitShare"), CancellationToken.None);

        Assert.Equal(ManagedSessionOperationResultKind.Succeeded, transfer.Kind);
        Assert.Equal(ManagedSessionOperationResultKind.ContractUnavailable, share.Kind);
        Assert.Equal(1, dispatcher.CallCount);
    }

    private static Task<ManagedSessionOperationResult> InvokeAsync(
        ManagedSessionOperationClient client,
        ManagedSessionOperationRequest request) => request.Kind switch
    {
        ManagedSessionOperationKind.InspectSource => client.InspectSourceAsync(request, CancellationToken.None),
        ManagedSessionOperationKind.EnsureDirectory => client.EnsureDirectoryAsync(request, CancellationToken.None),
        ManagedSessionOperationKind.SubmitTransfer => client.SubmitTransferAsync(request, CancellationToken.None),
        ManagedSessionOperationKind.ObserveTransfer => client.ObserveTransferAsync(request, CancellationToken.None),
        ManagedSessionOperationKind.SubmitShare => client.SubmitShareAsync(request, CancellationToken.None),
        ManagedSessionOperationKind.ObserveShare => client.ObserveShareAsync(request, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(),
    };

    private static ManagedSessionOperationRequest Request(string operation)
    {
        var arguments = operation switch
        {
            "InspectSource" => "{\"sourceUri\":\"https://pan.baidu.com/s/1safe\"}",
            "EnsureDirectory" => "{\"targetCanonicalPath\":\"/测试\"}",
            "SubmitTransfer" => "{\"sourceUri\":\"https://pan.baidu.com/s/1safe\",\"targetCanonicalPath\":\"/测试\",\"duplicatePolicy\":\"Rename\"}",
            "ObserveTransfer" => "{\"adapterRequestId\":\"request-1\"}",
            "SubmitShare" => "{\"exactItemIds\":[\"item-1\"],\"fixedCode\":\"a1b2\"}",
            "ObserveShare" => "{\"adapterRequestId\":\"request-2\"}",
            _ => "{}",
        };
        var json = $$"""
            {
              "operation":"{{operation}}",
              "operationId":"22222222-2222-2222-2222-222222222222",
              "attemptOrdinal":1,
              "binding":{
                "browserId":"11111111-1111-1111-1111-111111111111",
                "targetId":"target-1",
                "devToolsEndpoint":"http://127.0.0.1:43127/",
                "timeoutMilliseconds":30000,
                "baiduUserId":"123456789"
              },
              "arguments":{{arguments}}
            }
            """;
        return ManagedSessionOperationProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json));
    }

    private sealed class RecordingDispatcher : IManagedSessionOperationDispatcher
    {
        public int CallCount { get; private set; }
        public ManagedSessionOperationRequest? LastRequest { get; private set; }

        public Task<ManagedSessionOperationResult> DispatchAsync(
            ManagedSessionOperationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(ManagedSessionOperationResult.Succeeded());
        }
    }
}
