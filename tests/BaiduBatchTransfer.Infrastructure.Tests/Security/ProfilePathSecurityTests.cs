using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.Infrastructure.Tests.Security;

public sealed class ProfilePathSecurityTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"bbt-profile-security-{Guid.NewGuid():N}");
    private static readonly Guid AccountId = Guid.Parse("cb75ec33-9a9c-4215-a2a7-4ac72cdf21aa");

    [Fact]
    public void ExistingAncestorJunctionIsRejectedEvenWhenFinalProfileDirectoryIsOrdinary()
    {
        var outside = Path.Combine(testRoot, "outside");
        var link = Path.Combine(testRoot, "container", "linked-parent");
        Directory.CreateDirectory(Path.Combine(outside, "profiles", AccountId.ToString("D")));
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateDirectoryLink(link, outside);

        Assert.Throws<IOException>(() =>
            new FileSystemAccountProfilePathPolicy(Path.Combine(link, "profiles")));
    }

    [Fact]
    public void DanglingAncestorJunctionIsRejectedInsteadOfBeingTreatedAsAbsent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(testRoot, "dangling-target");
        var link = Path.Combine(testRoot, "dangling-link");
        Directory.CreateDirectory(target);
        CreateDirectoryLink(link, target);
        Directory.Delete(target, recursive: true);

        Assert.Throws<IOException>(() =>
            new FileSystemAccountProfilePathPolicy(Path.Combine(link, "profiles")));

        Directory.Delete(link, recursive: false);
    }

    [Fact]
    public void AttributeInspectionRejectsDanglingReparseEvenWhenExistenceProbesWouldBeFalse()
    {
        var dangling = Path.Combine(testRoot, "simulated-dangling-link");
        var fileSystem = new DanglingReparseFileSystem(dangling);

        Assert.Throws<IOException>(() =>
            new FileSystemAccountProfilePathPolicy(Path.Combine(dangling, "profiles"), fileSystem));

        Assert.Contains(dangling, fileSystem.InspectedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteRevalidatesAncestorsAfterEarlierValidationAndRefusesReplacementJunction()
    {
        var anchor = Path.Combine(testRoot, "anchor");
        var profileRoot = Path.Combine(anchor, "profiles");
        var profilePath = Path.Combine(profileRoot, AccountId.ToString("D"));
        var outside = Path.Combine(testRoot, "outside");
        var outsideProfile = Path.Combine(outside, "profiles", AccountId.ToString("D"));
        Directory.CreateDirectory(profilePath);
        Directory.CreateDirectory(outsideProfile);
        var policy = new FileSystemAccountProfilePathPolicy(profileRoot);
        policy.EnsureSafeAccountPath(AccountId, profilePath);
        var closer = new CallbackProfileCloser(() =>
        {
            Directory.Delete(anchor, recursive: true);
            CreateDirectoryLink(anchor, outside);
        });
        var store = new FileSystemWebViewProfileStore(policy, closer);

        await store.CloseAsync(AccountId, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() =>
            store.DeleteAsync(AccountId, profilePath, CancellationToken.None));

        Assert.True(Directory.Exists(outsideProfile));
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", link, target },
            CreateNoWindow = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start junction creation.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Could not create the test junction.");
    }

    public void Dispose()
    {
        var anchor = Path.Combine(testRoot, "anchor");
        var link = Path.Combine(testRoot, "container", "linked-parent");
        if (Directory.Exists(anchor) && (File.GetAttributes(anchor) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(anchor, recursive: false);
        if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(link, recursive: false);
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
    }

    private sealed class CallbackProfileCloser(Action callback) : IWebViewProfileCloser
    {
        public Task CloseAsync(Guid accountId, CancellationToken cancellationToken)
        {
            callback();
            return Task.CompletedTask;
        }
    }

    private sealed class DanglingReparseFileSystem(string danglingPath) : IProfilePathFileSystem
    {
        public List<string> InspectedPaths { get; } = [];

        public FileAttributes GetAttributes(string path)
        {
            InspectedPaths.Add(path);
            if (string.Equals(path, danglingPath, StringComparison.OrdinalIgnoreCase))
                return FileAttributes.Directory | FileAttributes.ReparsePoint;
            throw new DirectoryNotFoundException();
        }
    }
}
