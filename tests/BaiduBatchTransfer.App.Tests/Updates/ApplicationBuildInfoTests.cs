using BaiduBatchTransfer.App.Updates;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class ApplicationBuildInfoTests
{
    [Fact]
    public void ProductionBuildRejectsLocalOrMutableFeed()
    {
        Assert.False(ApplicationBuildInfo.TryCreateProduction("file:///c:/feed", out _));
        Assert.False(ApplicationBuildInfo.TryCreateProduction(@"C:\feed", out _));
        Assert.False(ApplicationBuildInfo.TryCreateProduction("http://updates.example.test", out _));
        Assert.False(ApplicationBuildInfo.TryCreateProduction("https://user:secret@updates.example.test/stable/", out _));
        Assert.False(ApplicationBuildInfo.TryCreateProduction("https://updates.example.test/stable/?token=secret", out _));
        Assert.False(ApplicationBuildInfo.TryCreateProduction("https://updates.example.test/stable/#latest", out _));

        Assert.True(ApplicationBuildInfo.TryCreateProduction(
            "https://updates.example.test/stable/",
            out var info));
        Assert.NotNull(info);
        Assert.Equal("win-x64-stable", info.Channel);
        Assert.Equal("https://updates.example.test/stable/", info.HttpsFeedUrl);
        Assert.Null(info.AcceptanceFeedPath);
        Assert.True(info.IsFeedConfigured);
    }

    [Fact]
    public void ProductAndAdapterVersionsHaveSeparateSemantics()
    {
        var info = ApplicationBuildInfo.CreateForTests("1.2.3", "managed-browser-v3", null);

        Assert.Equal("1.2.3", info.ProductVersion);
        Assert.Equal("managed-browser-v3", info.AdapterBuildVersion);
        Assert.False(info.IsFeedConfigured);
    }

    [Fact]
    public void AssemblyVersionTracksValidatedProductMajor()
    {
        var versions = ApplicationBuildInfo.DeriveAssemblyVersions("2.3.4");

        Assert.Equal("2.0.0.0", versions.AssemblyVersion);
        Assert.Equal("2.3.4.0", versions.FileVersion);
    }

    [Theory]
    [InlineData("2.3")]
    [InlineData("2.3.4.5")]
    [InlineData("2.3.4-beta")]
    [InlineData("v2.3.4")]
    public void AssemblyVersionRejectsNonThreePartProductVersion(string productVersion)
    {
        Assert.Throws<ArgumentException>(() =>
            ApplicationBuildInfo.DeriveAssemblyVersions(productVersion));
    }
}
