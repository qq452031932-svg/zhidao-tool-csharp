using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.Infrastructure.Tests.Security;

public sealed class ManagedBrowserProfileStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-managed-profile-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task PreparationIsDeterministicAndMoveValidatesMarkerOnBothSides()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateAsync(BrowserFamily.Chrome);

        var first = fixture.Store.PrepareRemoval(fixture.Session);
        var second = fixture.Store.PrepareRemoval(fixture.Session);

        Assert.Equal(first.ManifestId, second.ManifestId);
        Assert.Equal(first.QuarantinePath, second.QuarantinePath);
        Assert.Equal(ManagedBrowserProfileLocationStatus.SourceOnly,
            await fixture.Store.ObserveAsync(first, default));

        var moved = await fixture.Store.MoveToQuarantineAsync(first, default);

        Assert.Equal(ManagedBrowserProfileMoveStatus.Moved, moved);
        Assert.False(Directory.Exists(first.SourceProfilePath));
        Assert.True(Directory.Exists(first.QuarantinePath));
        Assert.Equal(ManagedBrowserProfileLocationStatus.QuarantineOnly,
            await fixture.Store.ObserveAsync(first, default));
        Assert.Equal(ManagedBrowserProfileMoveStatus.AlreadyQuarantined,
            await fixture.Store.MoveToQuarantineAsync(first, default));
    }

    [Fact]
    public async Task OccupiedDestinationAndMissingBothAreReportedWithoutMovingAnything()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateAsync(BrowserFamily.Edge);
        var preparation = fixture.Store.PrepareRemoval(fixture.Session);
        Directory.CreateDirectory(Path.GetDirectoryName(preparation.QuarantinePath)!);
        Directory.CreateDirectory(preparation.QuarantinePath);

        Assert.Equal(ManagedBrowserProfileLocationStatus.BothPresent,
            await fixture.Store.ObserveAsync(preparation, default));
        Assert.Equal(ManagedBrowserProfileMoveStatus.Conflict,
            await fixture.Store.MoveToQuarantineAsync(preparation, default));
        Assert.True(Directory.Exists(preparation.SourceProfilePath));
        Assert.True(Directory.Exists(preparation.QuarantinePath));

        Directory.Delete(preparation.QuarantinePath);
        Directory.Delete(preparation.SourceProfilePath, recursive: true);
        Assert.Equal(ManagedBrowserProfileLocationStatus.NeitherPresent,
            await fixture.Store.ObserveAsync(preparation, default));
        Assert.Equal(ManagedBrowserProfileMoveStatus.Conflict,
            await fixture.Store.MoveToQuarantineAsync(preparation, default));
    }

    [Fact]
    public async Task CorruptOwnershipMarkerIsIsolatedAndNeverMovedOrDeleted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateAsync(BrowserFamily.Chrome);
        var preparation = fixture.Store.PrepareRemoval(fixture.Session);
        File.WriteAllText(
            Path.Combine(preparation.SourceProfilePath, FileSystemManagedBrowserProfilePolicy.MarkerName),
            "corrupt");

        Assert.Equal(ManagedBrowserProfileLocationStatus.IsolationRequired,
            await fixture.Store.ObserveAsync(preparation, default));
        Assert.Equal(ManagedBrowserProfileMoveStatus.IsolationRequired,
            await fixture.Store.MoveToQuarantineAsync(preparation, default));
        Assert.True(Directory.Exists(preparation.SourceProfilePath));
        Assert.False(Directory.Exists(preparation.QuarantinePath));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.DeleteQuarantinedAsync(preparation, string.Empty, default));
    }

    [Fact]
    public async Task ConfirmedDeleteTouchesOnlyExactQuarantineAndIsIdempotent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateAsync(BrowserFamily.Chrome);
        var preparation = fixture.Store.PrepareRemoval(fixture.Session);
        Assert.Equal(ManagedBrowserProfileMoveStatus.Moved,
            await fixture.Store.MoveToQuarantineAsync(preparation, default));
        var sibling = Path.Combine(Path.GetDirectoryName(preparation.QuarantinePath)!, "keep-sibling");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(preparation.QuarantinePath, "Default"));
        File.WriteAllText(Path.Combine(preparation.QuarantinePath, "Default", "state.bin"), "state");

        Assert.Equal(ManagedBrowserProfileDeleteStatus.Deleted,
            await fixture.Store.DeleteQuarantinedAsync(preparation, "confirmation-hash", default));
        Assert.False(Directory.Exists(preparation.QuarantinePath));
        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
        Assert.Equal(ManagedBrowserProfileDeleteStatus.AlreadyDeleted,
            await fixture.Store.DeleteQuarantinedAsync(preparation, "confirmation-hash", default));
    }

    [Fact]
    public async Task PathReplacementAfterQuarantineIsPinnedDeletesNothingAndReturnsConflict()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateAsync(BrowserFamily.Chrome);
        var preparation = fixture.Store.PrepareRemoval(fixture.Session);
        Assert.Equal(ManagedBrowserProfileMoveStatus.Moved,
            await fixture.Store.MoveToQuarantineAsync(preparation, default));

        var replacement = preparation.QuarantinePath + "-replacement";
        Directory.CreateDirectory(replacement);
        var replacementFile = Path.Combine(replacement, "must-survive.txt");
        File.WriteAllText(replacementFile, "keep");

        var attackedStore = new FileSystemManagedBrowserProfileStore(
            root,
            fixture.Policy,
            _ =>
            {
                var displaced = preparation.QuarantinePath + "-displaced";
                Directory.Move(preparation.QuarantinePath, displaced);
                Directory.Move(replacement, preparation.QuarantinePath);
            });

        var result = await attackedStore.DeleteQuarantinedAsync(
            preparation,
            "confirmation-hash",
            default);

        Assert.Equal(ManagedBrowserProfileDeleteStatus.Conflict, result);
        Assert.True(File.Exists(replacementFile));
        Assert.True(Directory.Exists(preparation.QuarantinePath));
    }

    [Fact]
    public async Task ReparsePointAtExactQuarantinePathIsRejectedWithoutTouchingItsTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateAsync(BrowserFamily.Edge);
        var preparation = fixture.Store.PrepareRemoval(fixture.Session);
        Assert.Equal(ManagedBrowserProfileMoveStatus.Moved,
            await fixture.Store.MoveToQuarantineAsync(preparation, default));

        var displaced = preparation.QuarantinePath + "-displaced";
        Directory.Move(preparation.QuarantinePath, displaced);
        var outside = Path.Combine(root, "outside-target");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "must-survive.txt");
        File.WriteAllText(outsideFile, "keep");
        CreateDirectoryLink(preparation.QuarantinePath, outside);

        try
        {
            Assert.Equal(
                ManagedBrowserProfileLocationStatus.IsolationRequired,
                await fixture.Store.ObserveAsync(preparation, default));
            var result = await fixture.Store.DeleteQuarantinedAsync(
                preparation,
                "confirmation-hash",
                default);

            Assert.Equal(ManagedBrowserProfileDeleteStatus.IsolationRequired, result);
            Assert.True(File.Exists(outsideFile));
            Assert.True(File.Exists(Path.Combine(
                displaced,
                FileSystemManagedBrowserProfilePolicy.MarkerName)));
        }
        finally
        {
            if (Directory.Exists(preparation.QuarantinePath))
                Directory.Delete(preparation.QuarantinePath);
        }
    }

    [Fact]
    public async Task PreparationRejectsSourceOutsideTheExactManagedFamilyAccountPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateAsync(BrowserFamily.Chrome);
        var outside = new ManagedBrowserLifecycleSession(
            fixture.Session.Session,
            fixture.Session.InstallationRevision,
            fixture.Session.SessionRevision,
            Path.Combine(root, "outside"));

        Assert.Throws<InvalidOperationException>(() => fixture.Store.PrepareRemoval(outside));
    }

    [Fact]
    public async Task CancellationBeforeMoveOrDeleteTouchesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateAsync(BrowserFamily.Edge);
        var preparation = fixture.Store.PrepareRemoval(fixture.Session);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.MoveToQuarantineAsync(preparation, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.DeleteQuarantinedAsync(preparation, "confirmation-hash", cancelled.Token));

        Assert.True(Directory.Exists(preparation.SourceProfilePath));
        Assert.False(Directory.Exists(preparation.QuarantinePath));
    }

    private async Task<Fixture> CreateAsync(BrowserFamily family)
    {
        Directory.CreateDirectory(root);
        var accountId = Guid.NewGuid();
        var policy = new FileSystemManagedBrowserProfilePolicy(root);
        var result = await policy.GetOrCreateAsync(accountId, family, default);
        var descriptor = Assert.IsType<ManagedBrowserProfileDescriptor>(result.Profile);
        var reference = new ManagedBrowserSessionReference(
            Guid.NewGuid(), Guid.NewGuid(), accountId, "stable-uid", family,
            descriptor.ProfileId.ToString("N"), descriptor.OwnershipId.ToString("N"));
        var session = new ManagedBrowserLifecycleSession(reference, 3, 5, descriptor.CanonicalSourcePath);
        return new Fixture(new FileSystemManagedBrowserProfileStore(root, policy), policy, session);
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", link, target },
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not start junction creation.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Could not create the test junction.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed record Fixture(
        FileSystemManagedBrowserProfileStore Store,
        IManagedBrowserProfilePolicy Policy,
        ManagedBrowserLifecycleSession Session);
}
