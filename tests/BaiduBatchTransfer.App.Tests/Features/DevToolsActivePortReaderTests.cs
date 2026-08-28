using System.Net;
using System.Text;
using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class DevToolsActivePortReaderTests
{
    private static readonly Guid BrowserId = Guid.Parse("76df3c18-1013-45fd-a989-2fbb60477286");
    private static readonly DateTimeOffset Launched = DateTimeOffset.UtcNow;
    private static string Valid => $"49152\n/devtools/browser/{BrowserId:D}";

    [Fact]
    public void AcceptsChromiumTwoLineFileAndCreatesLiteralLoopbackEndpoints()
    {
        var result = Parse(Valid);
        Assert.Equal(49152, result.Port);
        Assert.Equal(BrowserId, result.BrowserId);
        Assert.Equal("http://127.0.0.1:49152/", result.Endpoint.AbsoluteUri);
        Assert.Equal($"ws://127.0.0.1:49152/devtools/browser/{BrowserId:D}", result.WebSocketEndpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("49152")]
    [InlineData("49152\n/devtools/browser/")]
    [InlineData("49152\n/devtools/browser/76df3c18-1013-45fd-a989-2fbb60477286\nextra")]
    [InlineData("0\n/devtools/browser/76df3c18-1013-45fd-a989-2fbb60477286")]
    [InlineData("65536\n/devtools/browser/76df3c18-1013-45fd-a989-2fbb60477286")]
    [InlineData("80\n/devtools/browser/76df3c18-1013-45fd-a989-2fbb60477286")]
    [InlineData("049152\n/devtools/browser/76df3c18-1013-45fd-a989-2fbb60477286")]
    [InlineData("49152\nws://192.0.2.1/devtools/browser/76df3c18-1013-45fd-a989-2fbb60477286")]
    public void RejectsTruncatedMalformedOrExtraLines(string text) => Assert.Throws<InvalidDataException>(() => Parse(text));

    [Fact]
    public void RejectsOversizedFile() => Assert.Throws<InvalidDataException>(() => Parse(new string('1', 513)));

    [Fact]
    public void RejectsStaleFile() => Assert.Throws<InvalidDataException>(() =>
        DevToolsActivePortReader.Parse(Encoding.ASCII.GetBytes(Valid), Launched.AddTicks(-1), Launched, IPAddress.Loopback));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.1")]
    [InlineData("::")]
    public void RejectsNonLoopback(string address) => Assert.Throws<InvalidDataException>(() =>
        DevToolsActivePortReader.Parse(Encoding.ASCII.GetBytes(Valid), Launched, Launched, IPAddress.Parse(address)));

    private static DevToolsActivePort Parse(string text) =>
        DevToolsActivePortReader.Parse(Encoding.ASCII.GetBytes(text), Launched, Launched, IPAddress.Loopback);
}
