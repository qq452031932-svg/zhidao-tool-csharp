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

    [Fact]
    public async Task AtomicQuarantineDoesNotFollowProfileReplacedByJunctionAtMutationBoundary()
    {
        var profileRoot = Path.Combine(testRoot, "race-profiles");
        var profilePath = Path.Combine(profileRoot, AccountId.ToString("D"));
        var outside = Path.Combine(testRoot, "race-outside");
        Directory.CreateDirectory(profilePath);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "must-survive.txt"), "outside");
        var policy = new FileSystemAccountProfilePathPolicy(profileRoot);
        var raceFileSystem = new RaceBeforeQuarantineFileSystem(() =>
        {
            Directory.Delete(profilePath, recursive: true);
            CreateDirectoryLink(profilePath, outside);
        });
        var store = new FileSystemWebViewProfileStore(
            policy,
            new CallbackProfileCloser(() => { }),
            raceFileSystem);

        await store.DeleteAsync(AccountId, profilePath, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(outside, "must-survive.txt")));
        Assert.False(Directory.Exists(profilePath));
    }

    [Fact]
    public async Task QuarantineRecursionUnlinksNestedJunctionWithoutFollowingIt()
    {
        var profileRoot = Path.Combine(testRoot, "nested-profiles");
        var profilePath = Path.Combine(profileRoot, AccountId.ToString("D"));
        var outside = Path.Combine(testRoot, "nested-outside");
        Directory.CreateDirectory(profilePath);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "must-survive.txt"), "outside");
        CreateDirectoryLink(Path.Combine(profilePath, "linked-child"), outside);
        var policy = new FileSystemAccountProfilePathPolicy(profileRoot);
        var store = new FileSystemWebViewProfileStore(
            policy, new CallbackProfileCloser(() => { }), CreatePhysical());

        await store.DeleteAsync(AccountId, profilePath, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(outside, "must-survive.txt")));
        Assert.False(Directory.Exists(profilePath));
    }

    [Fact]
    public async Task AncestorReplacementAfterStoreValidationIsRejectedBeforeMutation()
    {
        var anchor = Path.Combine(testRoot, "mutation-anchor");
        var profileRoot = Path.Combine(anchor, "profiles");
        var profilePath = Path.Combine(profileRoot, AccountId.ToString("D"));
        var outside = Path.Combine(testRoot, "mutation-outside");
        var outsideProfile = Path.Combine(outside, "profiles", AccountId.ToString("D"));
        Directory.CreateDirectory(profilePath);
        Directory.CreateDirectory(outsideProfile);
        File.WriteAllText(Path.Combine(outsideProfile, "must-survive.txt"), "outside");
        var policy = new FileSystemAccountProfilePathPolicy(profileRoot);
        var fileSystem = new RaceBeforeQuarantineFileSystem(() =>
        {
            Directory.Delete(anchor, recursive: true);
            CreateDirectoryLink(anchor, outside);
        });
        var store = new FileSystemWebViewProfileStore(policy, new CallbackProfileCloser(() => { }), fileSystem);

        await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync(AccountId, profilePath, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(outsideProfile, "must-survive.txt")));
        Directory.Delete(anchor, recursive: false);
    }

    [Fact]
    public async Task ChildReplacementBetweenEnumerationAndOpenIsUnlinkedWithoutFollowing()
    {
        var profileRoot = Path.Combine(testRoot, "child-race-profiles");
        var profilePath = Path.Combine(profileRoot, AccountId.ToString("D"));
        var child = Path.Combine(profilePath, "child");
        var outside = Path.Combine(testRoot, "child-race-outside");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "must-survive.txt"), "outside");
        var raced = false;
        var physical = CreatePhysical(path =>
        {
            if (raced || !string.Equals(path, Path.Combine(profileRoot, $".delete-{AccountId:N}", "child"), StringComparison.OrdinalIgnoreCase)) return;
            raced = true;
            Directory.Delete(path, recursive: true);
            CreateDirectoryLink(path, outside);
        });
        var store = new FileSystemWebViewProfileStore(
            new FileSystemAccountProfilePathPolicy(profileRoot), new CallbackProfileCloser(() => { }), physical);

        await store.DeleteAsync(AccountId, profilePath, CancellationToken.None);

        Assert.True(raced);
        Assert.True(File.Exists(Path.Combine(outside, "must-survive.txt")));
        Assert.False(Directory.Exists(profilePath));
    }

    [Fact]
    public async Task InterruptedQuarantineIsDiscoveredAndDrainedOnRetry()
    {
        var ownershipToken = Guid.Parse("f545802a-d747-431c-b953-b76e5fe559ca");
        var profileRoot = Path.Combine(testRoot, "retry-profiles");
        var profilePath = Path.Combine(profileRoot, AccountId.ToString("D"));
        Directory.CreateDirectory(profilePath);
        File.WriteAllText(Path.Combine(profilePath, "credential.db"), "credential");
        var policy = new FileSystemAccountProfilePathPolicy(profileRoot);
        using var cancellation = new CancellationTokenSource();
        var canceled = false;
        var first = new FileSystemWebViewProfileStore(
            policy,
            new CallbackProfileCloser(() => { }),
            CreatePhysical(path =>
            {
                if (canceled || !path.EndsWith("credential.db", StringComparison.OrdinalIgnoreCase)) return;
                canceled = true;
                cancellation.Cancel();
            }));
        await first.RegisterOwnershipAsync(AccountId, profilePath, ownershipToken, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            first.DeleteOwnedAsync(
                AccountId, profilePath, ownershipToken, _ => Task.FromResult(true), cancellation.Token));
        var quarantine = Path.Combine(profileRoot, $".delete-{AccountId:N}");
        Assert.True(Directory.Exists(quarantine));
        Assert.False(Directory.Exists(profilePath));

        var retry = new FileSystemWebViewProfileStore(
            policy, new CallbackProfileCloser(() => { }), CreatePhysical());
        var result = await retry.DeleteOwnedAsync(
            AccountId, profilePath, ownershipToken, _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(OwnedProfileDeletionStatus.Completed, result);
        Assert.False(Directory.Exists(quarantine));
    }

    [Fact]
    public async Task OwnershipIsRecheckedUnderPinnedHandleAfterValidationBeforeDelete()
    {
        var originalToken = Guid.Parse("f545802a-d747-431c-b953-b76e5fe559ca");
        var replacementToken = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var profileRoot = Path.Combine(testRoot, "ownership-race-profiles");
        var profilePath = Path.Combine(profileRoot, AccountId.ToString("D"));
        Directory.CreateDirectory(profilePath);
        var policy = new FileSystemAccountProfilePathPolicy(profileRoot);
        var bootstrap = new FileSystemWebViewProfileStore(
            policy, new CallbackProfileCloser(() => { }), CreatePhysical());
        await bootstrap.RegisterOwnershipAsync(AccountId, profilePath, originalToken, CancellationToken.None);
        var physical = CreatePhysical(path =>
        {
            if (!string.Equals(path, profilePath, StringComparison.OrdinalIgnoreCase)) return;
            Directory.Delete(profilePath, recursive: true);
            Directory.CreateDirectory(profilePath);
            File.WriteAllText(
                Path.Combine(profilePath, PhysicalProfileDeletionFileSystem.OwnershipMarkerName),
                replacementToken.ToString("D"));
            File.WriteAllText(Path.Combine(profilePath, "replacement.db"), "replacement");
        });
        var store = new FileSystemWebViewProfileStore(policy, new CallbackProfileCloser(() => { }), physical);

        var result = await store.DeleteOwnedAsync(
            AccountId, profilePath, originalToken, _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(OwnedProfileDeletionStatus.OwnershipMismatch, result);
        Assert.True(File.Exists(Path.Combine(profilePath, "replacement.db")));
    }

    [Fact]
    public async Task AccessDeniedWhilePinningAnyAncestorFailsClosedBeforeMutation()
    {
        const int errorAccessDenied = 5;
        var profileRoot = Path.Combine(testRoot, "denied-ancestor-profiles");
        var profilePath = Path.Combine(profileRoot, AccountId.ToString("D"));
        Directory.CreateDirectory(profilePath);
        var deniedAncestor = Path.GetDirectoryName(profileRoot)!;
        var physical = CreatePhysical(
            ancestorOpenError: path => string.Equals(path, deniedAncestor, StringComparison.OrdinalIgnoreCase)
                ? errorAccessDenied
                : null);
        var store = new FileSystemWebViewProfileStore(
            new FileSystemAccountProfilePathPolicy(profileRoot), new CallbackProfileCloser(() => { }), physical);

        await Assert.ThrowsAsync<IOException>(() =>
            store.DeleteAsync(AccountId, profilePath, CancellationToken.None));

        Assert.True(Directory.Exists(profilePath));
    }

    [Fact]
    public async Task CommitLeaseExcludesSameTokenCleanupUntilDatabaseCommitCompletes()
    {
        var ownershipToken = Guid.Parse("f545802a-d747-431c-b953-b76e5fe559ca");
        var profileRoot = Path.Combine(testRoot, "commit-lease-profiles");
        var profilePath = Path.Combine(profileRoot, AccountId.ToString("D"));
        Directory.CreateDirectory(profilePath);
        var store = new FileSystemWebViewProfileStore(
            new FileSystemAccountProfilePathPolicy(profileRoot),
            new CallbackProfileCloser(() => { }),
            CreatePhysical());
        await store.RegisterOwnershipAsync(AccountId, profilePath, ownershipToken, CancellationToken.None);
        var commitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = store.CommitOwnedAsync(
            AccountId,
            profilePath,
            ownershipToken,
            async _ =>
            {
                commitStarted.SetResult();
                await releaseCommit.Task;
            },
            CancellationToken.None);
        await commitStarted.Task;

        await Assert.ThrowsAsync<IOException>(() => store.DeleteOwnedAsync(
            AccountId, profilePath, ownershipToken, _ => Task.FromResult(true), CancellationToken.None));

        releaseCommit.SetResult();
        Assert.True(await commit);
        Assert.True(Directory.Exists(profilePath));
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

    private static PhysicalProfileDeletionFileSystem CreatePhysical(
        Action<string>? beforeEntryOpen = null,
        Func<string, int?>? ancestorOpenError = null) =>
        new(
            beforeEntryOpen,
            ancestorOpenError,
            path => string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty),
                StringComparison.OrdinalIgnoreCase));

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

    private sealed class RaceBeforeQuarantineFileSystem(Action beforeDelete) : IProfileDeletionFileSystem
    {
        private readonly PhysicalProfileDeletionFileSystem physical = CreatePhysical();
        public void RegisterOwnership(string canonicalRoot, string exactProfilePath, Guid ownershipToken, CancellationToken cancellationToken) =>
            physical.RegisterOwnership(canonicalRoot, exactProfilePath, ownershipToken, cancellationToken);
        public Task<bool> CommitOwnedAsync(
            string canonicalRoot,
            string exactProfilePath,
            Guid ownershipToken,
            Func<CancellationToken, Task> commitAction,
            CancellationToken cancellationToken) =>
            physical.CommitOwnedAsync(canonicalRoot, exactProfilePath, ownershipToken, commitAction, cancellationToken);

        public Task<OwnedProfileDeletionStatus> DeleteProfileAsync(
            string canonicalRoot,
            string exactProfilePath,
            string quarantineName,
            Guid? expectedOwnershipToken,
            Func<CancellationToken, Task<bool>>? confirmNoActiveAccount,
            CancellationToken cancellationToken)
        {
            beforeDelete();
            return physical.DeleteProfileAsync(
                canonicalRoot,
                exactProfilePath,
                quarantineName,
                expectedOwnershipToken,
                confirmNoActiveAccount,
                cancellationToken);
        }
    }
}
