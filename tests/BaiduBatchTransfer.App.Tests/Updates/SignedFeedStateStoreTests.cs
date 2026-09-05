using System.Text.Json;
using BaiduBatchTransfer.App.Updates;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class SignedFeedStateStoreTests
{
    private const string FeedIdentity = "https://updates.example.test/releases/win-x64/stable/";

    [Fact]
    public void FirstAcceptancePersistsTheCurrentApplicationFloorAtomically()
    {
        using var fixture = new StateFixture();
        var store = fixture.CreateStore(currentVersion: "2.0.0");

        var accepted = store.Accept("2.0.0", new string('a', 64));

        Assert.Equal("2.0.0", accepted.HighestAcceptedVersion);
        Assert.Equal(FeedIdentity, accepted.FeedIdentity);
        Assert.Single(Directory.EnumerateFiles(fixture.DirectoryPath));
        Assert.Equal(fixture.StatePath, Directory.EnumerateFiles(fixture.DirectoryPath).Single());
        using var document = JsonDocument.Parse(File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2.0.0", document.RootElement.GetProperty("highestAcceptedVersion").GetString());
    }

    [Fact]
    public void RejectsCandidateBelowCurrentApplicationVersion()
    {
        using var fixture = new StateFixture();
        var store = fixture.CreateStore(currentVersion: "2.0.0");

        Assert.Throws<InvalidDataException>(() =>
            store.Accept("1.9.9", new string('a', 64)));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void HighestAcceptedVersionNeverMovesBackward()
    {
        using var fixture = new StateFixture();
        var store = fixture.CreateStore();
        store.Accept("3.0.0", new string('a', 64));

        Assert.Throws<InvalidDataException>(() =>
            store.Accept("2.9.9", new string('b', 64)));
        var reread = fixture.CreateStore().Read();
        Assert.Equal("3.0.0", reread.HighestAcceptedVersion);
        Assert.Equal(new string('a', 64), reread.ManifestSha256);
    }

    [Fact]
    public void EqualVersionIsIdempotentOnlyForTheSameSignedManifest()
    {
        using var fixture = new StateFixture();
        var store = fixture.CreateStore();
        var first = store.Accept("2.0.0", new string('a', 64));

        var repeated = store.Accept("2.0.0", new string('a', 64));

        Assert.Equal(first, repeated);
        Assert.Throws<InvalidDataException>(() =>
            store.Accept("2.0.0", new string('b', 64)));
    }

    [Fact]
    public void CorruptStateFailsClosedAndIsNotReplaced()
    {
        using var fixture = new StateFixture();
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.StatePath, "{broken");
        var before = File.ReadAllBytes(fixture.StatePath);

        var exception = Assert.Throws<InvalidDataException>(() =>
            fixture.CreateStore().Accept("2.0.0", new string('a', 64)));

        Assert.Contains("state", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(fixture.StatePath));
    }

    [Fact]
    public void DifferentFeedIdentityIsRejected()
    {
        using var fixture = new StateFixture();
        fixture.CreateStore().Accept("2.0.0", new string('a', 64));
        var other = new SignedFeedStateStore(
            fixture.StatePath,
            "https://other.example.test/releases/win-x64/stable/",
            "1.0.0");

        Assert.Throws<InvalidDataException>(() =>
            other.Accept("3.0.0", new string('b', 64)));
    }

    [Fact]
    public async Task ConcurrentWritersSerializeAndPreserveTheHighestVersion()
    {
        using var fixture = new StateFixture();
        var versions = Enumerable.Range(1, 24).Select(value => $"1.0.{value}").ToArray();

        await Task.WhenAll(versions.Select(version => Task.Run(() =>
        {
            try
            {
                fixture.CreateStore().Accept(version, HashFor(version));
            }
            catch (InvalidDataException)
            {
                // A higher concurrent winner is allowed to make this candidate a replay.
            }
        })));

        var state = fixture.CreateStore().Read();
        Assert.Equal("1.0.24", state.HighestAcceptedVersion);
        Assert.Equal(HashFor("1.0.24"), state.ManifestSha256);
    }

    private static string HashFor(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class StateFixture : IDisposable
    {
        public StateFixture()
        {
            DirectoryPath = Directory.CreateTempSubdirectory("signed-feed-state-").FullName;
            StatePath = Path.Combine(DirectoryPath, "state.json");
        }

        public string DirectoryPath { get; }

        public string StatePath { get; }

        public SignedFeedStateStore CreateStore(string currentVersion = "1.0.0") =>
            new(StatePath, FeedIdentity, currentVersion);

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
