using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Core.Tests;

public sealed class OperationResultRecordTests
{
    [Fact]
    public void CarriesAttemptInputAndLatestOutputShareValues()
    {
        var inputUrl = "https://pan.baidu.com/s/input";
        var inputCode = "old1";
        var latestUrl = "https://pan.baidu.com/s/latest";
        var latestCode = "new2";
        var record = new OperationResultRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1,
            inputUrl, null, Guid.NewGuid(), "账号", "/", "Succeeded", null,
            null, null, DateTimeOffset.UtcNow,
            InputShareUrl: inputUrl,
            InputExtractionCode: inputCode,
            LatestShareUrl: latestUrl,
            LatestExtractionCode: latestCode);

        Assert.Equal(inputUrl, record.InputShareUrl);
        Assert.Equal(inputCode, record.InputExtractionCode);
        Assert.Equal(latestUrl, record.LatestShareUrl);
        Assert.Equal(latestCode, record.LatestExtractionCode);
    }
}
