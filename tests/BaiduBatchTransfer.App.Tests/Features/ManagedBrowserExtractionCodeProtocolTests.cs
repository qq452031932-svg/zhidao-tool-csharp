using System.Text;
using System.Text.Json.Nodes;
using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserExtractionCodeProtocolTests
{
    [Fact]
    public void FixedExtractionActionRoundTripsWithoutCallerSuppliedDomInstructions()
    {
        Assert.Equal(25, (int)ManagedBrowserAction.SubmitExtractionCode);
        var binding = new ManagedBrowserOpenTransferSourceRequest(
            Guid.NewGuid(), "target-1", new Uri("http://127.0.0.1:43127/"), 30_000,
            "https://pan.baidu.com/s/share-id?pwd=1111", "uid-1");
        var request = new ManagedBrowserRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            ManagedBrowserAction.SubmitExtractionCode,
            new ManagedBrowserSubmitExtractionCodeRequest(binding, "1111"));

        var decoded = ManagedBrowserProtocol.DecodeRequest(
            ManagedBrowserProtocol.EncodeRequestPayload(request));
        Assert.IsType<ManagedBrowserSubmitExtractionCodeRequest>(decoded.Payload);
        var response = ManagedBrowserResponse.For(request,
            new ManagedBrowserSubmitExtractionCodeResult(
                ManagedBrowserExtractionCodeResultKind.SourceOpened,
                ManagedBrowserExtractionDispatchState.ClickDispatched));
        ManagedBrowserProtocol.ValidateResponseFor(request,
            ManagedBrowserProtocol.DecodeResponse(ManagedBrowserProtocol.EncodeResponsePayload(response)));
    }

    [Theory]
    [InlineData("selector")]
    [InlineData("script")]
    [InlineData("url")]
    [InlineData("retry")]
    public void ArbitraryDomOrReplayFieldsAreRejected(string field)
    {
        var request = new ManagedBrowserRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            ManagedBrowserAction.SubmitExtractionCode,
            new ManagedBrowserSubmitExtractionCodeRequest(new ManagedBrowserOpenTransferSourceRequest(
                Guid.NewGuid(), "target-1", new Uri("http://127.0.0.1:43127/"), 30_000,
                "https://pan.baidu.com/s/share-id", "uid-1"), "A1b2"));
        var root = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
        root["payload"]!.AsObject()[field] = "private";

        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.Contains(error.Error, new[]
        {
            ManagedBrowserProtocolError.ForbiddenField,
            ManagedBrowserProtocolError.UnknownField,
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abcde")]
    [InlineData("**11")]
    public void ExtractionCodeMustBeExactlyFourAsciiLettersOrDigits(string code)
    {
        var binding = new ManagedBrowserOpenTransferSourceRequest(
            Guid.NewGuid(), "target-1", new Uri("http://127.0.0.1:43127/"), 30_000,
            "https://pan.baidu.com/s/share-id", "uid-1");

        Assert.Throws<ArgumentException>(() => new ManagedBrowserSubmitExtractionCodeRequest(binding, code));
    }
}
