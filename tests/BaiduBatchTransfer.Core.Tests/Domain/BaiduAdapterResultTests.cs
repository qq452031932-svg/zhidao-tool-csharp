using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests.Domain;

public sealed class BaiduAdapterResultTests
{
    [Fact]
    public void TransientFailureCarriesTypedServerRetryDelayWithoutLeakingDiagnostic()
    {
        var result = BaiduAdapterResult<string>.Failure(
            BaiduErrorKind.TransientService,
            "Retry-After: 23; cookie=private",
            TimeSpan.FromSeconds(23));

        Assert.Equal(TimeSpan.FromSeconds(23), result.RetryAfter);
        Assert.DoesNotContain("private", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The service is temporarily unavailable.", result.SafeMessage);
    }

    [Fact]
    public void RejectsNegativeServerRetryDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BaiduAdapterResult<string>.Failure(
                BaiduErrorKind.TransientService,
                null,
                TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void FailureCarriesTypedSubmissionAcceptanceWithoutInferringFromErrorText()
    {
        var definite = BaiduAdapterResult<string>.Failure(
            BaiduErrorKind.ExpiredLogin,
            "untrusted response",
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);
        var unknown = BaiduAdapterResult<string>.Failure(
            BaiduErrorKind.ExpiredLogin,
            "not accepted maybe");

        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, definite.SubmissionAcceptance);
        Assert.Equal(SubmissionAcceptance.Unknown, unknown.SubmissionAcceptance);
    }

    [Fact]
    public void NotValidatedFailurePublishesOnlyTypedSafeDiagnosticCode()
    {
        const string untrusted =
            "https://pan.baidu.com/s/private?pwd=1234 uid=secret raw-js-response";

        var result = BaiduAdapterResult<string>.Failure(
            BaiduErrorKind.NotValidated,
            untrusted,
            safeDiagnosticCode: BaiduCandidateDiagnosticCode.TrustedRootMissing);

        Assert.Equal(BaiduCandidateDiagnosticCode.TrustedRootMissing, result.SafeDiagnosticCode);
        Assert.Equal("百度转存或分享能力尚未通过现场验收。 [TrustedRootMissing]", result.SafeMessage);
        Assert.DoesNotContain("private", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-js-response", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UndefinedOrNonNotValidatedSafeDiagnosticCodeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BaiduAdapterResult<string>.Failure(
                BaiduErrorKind.NotValidated,
                null,
                safeDiagnosticCode: (BaiduCandidateDiagnosticCode)int.MaxValue));
        Assert.Throws<ArgumentException>(() =>
            BaiduAdapterResult<string>.Failure(
                BaiduErrorKind.InvalidLink,
                null,
                safeDiagnosticCode: BaiduCandidateDiagnosticCode.TrustedRootMissing));
    }
}
