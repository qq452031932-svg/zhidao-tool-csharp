using System.Text;
using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedSessionOperationProtocolTests
{
    private const string OperationId = "22222222-2222-2222-2222-222222222222";
    private const string BrowserId = "11111111-1111-1111-1111-111111111111";

    [Theory]
    [InlineData("InspectSource")]
    [InlineData("EnsureDirectory")]
    [InlineData("SubmitTransfer")]
    [InlineData("ObserveTransfer")]
    [InlineData("SubmitShare")]
    [InlineData("ObserveShare")]
    public void AcceptsOnlyTheSixFixedOperationNames(string operation)
    {
        var request = ManagedSessionOperationProtocol.DecodeRequest(
            Encoding.UTF8.GetBytes(RequestJson(operation, ArgumentsFor(operation))));

        Assert.Equal(operation, request.Kind.ToString());
        Assert.Equal(Guid.Parse(OperationId), request.OperationId);
        Assert.Equal(1, request.AttemptOrdinal);
        Assert.Equal("123456789", request.Binding.BaiduUserId);
    }

    [Theory]
    [InlineData("ExecuteScript")]
    [InlineData("Navigate")]
    [InlineData("RawHttp")]
    public void RejectsUnknownOrGenericOperationsBeforeDispatch(string operation)
    {
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedSessionOperationProtocol.DecodeRequest(
                Encoding.UTF8.GetBytes(RequestJson(operation, "{}"))));

        Assert.Equal(ManagedBrowserProtocolError.UnknownAction, error.Error);
    }

    [Theory]
    [InlineData("script")]
    [InlineData("selector")]
    [InlineData("cookie")]
    [InlineData("authorization")]
    [InlineData("token")]
    [InlineData("origin")]
    [InlineData("referer")]
    [InlineData("url")]
    [InlineData("verb")]
    public void RejectsCallerControlledTransportAndSecretFieldsRecursively(string field)
    {
        var arguments = $"{{\"sourceUri\":\"https://pan.baidu.com/s/1safe\",\"{field}\":\"sensitive-value\"}}";

        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedSessionOperationProtocol.DecodeRequest(
                Encoding.UTF8.GetBytes(RequestJson("InspectSource", arguments))));

        Assert.Equal(ManagedBrowserProtocolError.ForbiddenField, error.Error);
        Assert.DoesNotContain("sensitive-value", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownFieldsAndDuplicateFields()
    {
        var unknown = RequestJson("InspectSource",
            "{\"sourceUri\":\"https://pan.baidu.com/s/1safe\",\"unexpected\":true}");
        var unknownError = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedSessionOperationProtocol.DecodeRequest(Encoding.UTF8.GetBytes(unknown)));
        Assert.Equal(ManagedBrowserProtocolError.UnknownField, unknownError.Error);

        var duplicate = RequestJson("InspectSource",
            "{\"sourceUri\":\"https://pan.baidu.com/s/1safe\",\"sourceUri\":\"https://pan.baidu.com/s/1safe\"}");
        var duplicateError = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedSessionOperationProtocol.DecodeRequest(Encoding.UTF8.GetBytes(duplicate)));
        Assert.Equal(ManagedBrowserProtocolError.DuplicateField, duplicateError.Error);
    }

    [Theory]
    [InlineData("http://pan.baidu.com/s/1unsafe")]
    [InlineData("https://example.com/s/1unsafe")]
    [InlineData("https://pan.baidu.com.evil.example/s/1unsafe")]
    public void InspectSourceAcceptsOnlyCanonicalHttpsBaiduShareUris(string sourceUri)
    {
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedSessionOperationProtocol.DecodeRequest(Encoding.UTF8.GetBytes(
                RequestJson("InspectSource", $"{{\"sourceUri\":\"{sourceUri}\"}}"))));

        Assert.Equal(ManagedBrowserProtocolError.InvalidPayload, error.Error);
    }

    [Fact]
    public void SafeDiagnosticsNeverExposeBindingOrArguments()
    {
        var request = ManagedSessionOperationProtocol.DecodeRequest(Encoding.UTF8.GetBytes(
            RequestJson("InspectSource", "{\"sourceUri\":\"https://pan.baidu.com/s/1safe\"}")));

        var text = request.ToString();
        Assert.Contains("InspectSource", text, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1safe", text, StringComparison.Ordinal);
        Assert.DoesNotContain(BrowserId, text, StringComparison.Ordinal);
    }

    private static string RequestJson(string operation, string arguments) => $$"""
        {
          "operation":"{{operation}}",
          "operationId":"{{OperationId}}",
          "attemptOrdinal":1,
          "binding":{
            "browserId":"{{BrowserId}}",
            "targetId":"target-1",
            "devToolsEndpoint":"http://127.0.0.1:43127/",
            "timeoutMilliseconds":30000,
            "baiduUserId":"123456789"
          },
          "arguments":{{arguments}}
        }
        """;

    private static string ArgumentsFor(string operation) => operation switch
    {
        "InspectSource" => "{\"sourceUri\":\"https://pan.baidu.com/s/1safe\"}",
        "EnsureDirectory" => "{\"targetCanonicalPath\":\"/测试\"}",
        "SubmitTransfer" => "{\"sourceUri\":\"https://pan.baidu.com/s/1safe\",\"targetCanonicalPath\":\"/测试\",\"duplicatePolicy\":\"Rename\"}",
        "ObserveTransfer" => "{\"adapterRequestId\":\"request-1\"}",
        "SubmitShare" => "{\"exactItemIds\":[\"item-1\",\"item-2\"],\"fixedCode\":\"a1b2\"}",
        "ObserveShare" => "{\"adapterRequestId\":\"request-2\"}",
        _ => "{}",
    };
}
