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
        Assert.True(File.Exists(fixture.StatePath));
        Assert.False(File.Exists(fixture.PreviousStatePath));
        Assert.True(File.Exists(fixture.CommitPointerPath));
        using var document = JsonDocument.Parse(File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal("2.0.0", document.RootElement.GetProperty("highestAcceptedVersion").GetString());
        Assert.Equal(64, document.RootElement.GetProperty("recordChecksumSha256").GetString()!.Length);
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
    public void ReadExposesTheCurrentApplicationVersionAsTheEffectiveFloor()
    {
        using var fixture = new StateFixture();
        fixture.CreateStore().Accept("1.5.0", new string('a', 64));

        var effective = fixture.CreateStore(currentVersion: "2.0.0").Read();

        Assert.Equal("2.0.0", effective.HighestAcceptedVersion);
        Assert.Equal(string.Empty, effective.ManifestSha256);
    }

    [Fact]
    public void TornInactiveSlotBeforePointerSwitchKeepsTheCommittedFloor()
    {
        using var fixture = new StateFixture();
        var store = fixture.CreateStore();
        store.Accept("2.0.0", new string('a', 64));
        File.WriteAllText(fixture.PreviousStatePath, "{torn-inactive-candidate");

        var recovered = fixture.CreateStore().Read();

        Assert.Equal("2.0.0", recovered.HighestAcceptedVersion);
        Assert.Equal(new string('a', 64), recovered.ManifestSha256);
    }

    [Fact]
    public void CorruptionOfThePointerDesignatedCommittedSlotFailsClosed()
    {
        using var fixture = new StateFixture();
        var store = fixture.CreateStore();
        store.Accept("2.0.0", new string('a', 64));
        store.Accept("3.0.0", new string('b', 64));
        var committed = fixture.CommittedSlotPath();
        var tampered = File.ReadAllText(committed).Replace(
            "\"highestAcceptedVersion\":\"3.0.0\"",
            "\"highestAcceptedVersion\":\"9.0.0\"",
            StringComparison.Ordinal);
        File.WriteAllText(committed, tampered);

        Assert.Throws<InvalidDataException>(() => fixture.CreateStore().Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrCorruptCommitPointerWithJournalArtifactsFailsClosed(bool deletePointer)
    {
        using var fixture = new StateFixture();
        fixture.CreateStore().Accept("2.0.0", new string('a', 64));
        if (deletePointer)
            File.Delete(fixture.CommitPointerPath);
        else
            File.WriteAllText(fixture.CommitPointerPath, "{torn-pointer");

        Assert.Throws<InvalidDataException>(() => fixture.CreateStore().Read());
        Assert.Throws<InvalidDataException>(() =>
            fixture.CreateStore().Accept("9.0.0", new string('a', 64)));
    }

    [Fact]
    public void CompletedCommitReopensAtTheNewHighFloor()
    {
        using var fixture = new StateFixture();
        var store = fixture.CreateStore();
        store.Accept("2.0.0", new string('a', 64));
        store.Accept("3.0.0", new string('b', 64));

        var reopened = fixture.CreateStore().Read();

        Assert.Equal("3.0.0", reopened.HighestAcceptedVersion);
        Assert.Equal(new string('b', 64), reopened.ManifestSha256);
        Assert.Throws<InvalidDataException>(() =>
            fixture.CreateStore().Accept("2.9.9", new string('c', 64)));
    }

    [Theory]
    [InlineData("pointer")]
    [InlineData("slot")]
    public void UnreadableDesignatedArtifactFailsClosed(string artifact)
    {
        using var fixture = new StateFixture();
        fixture.CreateStore().Accept("2.0.0", new string('a', 64));
        var path = artifact == "pointer"
            ? fixture.CommitPointerPath
            : fixture.CommittedSlotPath();
        using var exclusive = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.Throws<InvalidDataException>(() => fixture.CreateStore().Read());
    }

    [Fact]
    public void StateLockCoordinatesWithASeparateProcess()
    {
        using var fixture = new StateFixture();
        var store = fixture.CreateStore(lockTimeout: TimeSpan.FromSeconds(5));
        Directory.CreateDirectory(fixture.DirectoryPath);
        var readyPath = Path.Combine(fixture.DirectoryPath, "holder.ready");
        var script =
            $"$s=[IO.File]::Open('{EscapePowerShell(store.LockFilePath)}',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);" +
            $"[IO.File]::WriteAllText('{EscapePowerShell(readyPath)}','ready');" +
            "Start-Sleep -Milliseconds 900;$s.Dispose()";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        using var holder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(5)));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            store.Accept("2.0.0", new string('a', 64));

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(400), stopwatch.Elapsed.ToString());
            Assert.True(holder.WaitForExit(5000));
            Assert.Equal(0, holder.ExitCode);
        }
        finally
        {
            if (!holder.HasExited) holder.Kill(entireProcessTree: true);
        }
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

        public string PreviousStatePath => StatePath + ".previous";

        public string CommitPointerPath => StatePath + ".commit";

        public SignedFeedStateStore CreateStore(
            string currentVersion = "1.0.0",
            TimeSpan? lockTimeout = null) =>
            new(StatePath, FeedIdentity, currentVersion, lockTimeout);

        public string CommittedSlotPath()
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(CommitPointerPath));
            return document.RootElement.GetProperty("slot").GetInt32() == 0
                ? StatePath
                : PreviousStatePath;
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''");
}
