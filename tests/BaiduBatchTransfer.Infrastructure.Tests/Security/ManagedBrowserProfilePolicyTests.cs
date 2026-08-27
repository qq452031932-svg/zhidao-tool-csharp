using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace BaiduBatchTransfer.Infrastructure.Tests.Security;

public sealed class ManagedBrowserProfilePolicyTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"bbt-managed-profile-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreatesOpaqueCanonicalAccountPathAndReusesMarkerIdentityAfterRestart()
    {
        var accountId = Guid.Parse("CB75EC33-9A9C-4215-A2A7-4AC72CDF21AA");
        var policy = new FileSystemManagedBrowserProfilePolicy(root);

        var first = await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome);
        var restarted = await new FileSystemManagedBrowserProfilePolicy(root)
            .GetOrCreateAsync(accountId, BrowserFamily.Chrome);

        Assert.Equal(ManagedBrowserProfileStatus.Ready, first.Status);
        Assert.NotNull(first.Profile);
        Assert.Equal(Path.Combine(
            Path.GetFullPath(root),
            "managed-browser-profiles",
            "chrome",
            accountId.ToString("N")), first.Profile.CanonicalSourcePath);
        Assert.Equal(first.Profile.ProfileId, restarted.Profile!.ProfileId);
        Assert.Equal(first.Profile.OwnershipId, restarted.Profile.OwnershipId);
        Assert.DoesNotContain(accountId.ToString("D"), first.Profile.CanonicalSourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowserFamiliesAndAccountsHaveIndependentPathsAndIdentities()
    {
        var firstAccount = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var secondAccount = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var policy = new FileSystemManagedBrowserProfilePolicy(root);

        var chrome = (await policy.GetOrCreateAsync(firstAccount, BrowserFamily.Chrome)).Profile!;
        var edge = (await policy.GetOrCreateAsync(firstAccount, BrowserFamily.Edge)).Profile!;
        var other = (await policy.GetOrCreateAsync(secondAccount, BrowserFamily.Chrome)).Profile!;

        Assert.NotEqual(chrome.CanonicalSourcePath, edge.CanonicalSourcePath);
        Assert.NotEqual(chrome.CanonicalSourcePath, other.CanonicalSourcePath);
        Assert.NotEqual(chrome.ProfileId, edge.ProfileId);
        Assert.NotEqual(chrome.OwnershipId, edge.OwnershipId);
        Assert.Contains($"{Path.DirectorySeparatorChar}chrome{Path.DirectorySeparatorChar}", chrome.CanonicalSourcePath);
        Assert.Contains($"{Path.DirectorySeparatorChar}edge{Path.DirectorySeparatorChar}", edge.CanonicalSourcePath);
    }

    [Fact]
    public async Task ExistingDirectoryWithMissingMarkerRequiresIsolationWithoutOverwritingFiles()
    {
        var accountId = Guid.NewGuid();
        var policy = new FileSystemManagedBrowserProfilePolicy(root);
        var profilePath = (await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome)).Profile!
            .CanonicalSourcePath;
        File.Delete(Path.Combine(profilePath, FileSystemManagedBrowserProfilePolicy.MarkerName));
        var existing = Path.Combine(profilePath, "login-data.db");
        await File.WriteAllTextAsync(existing, "must-survive");

        var result = await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome);

        Assert.Equal(ManagedBrowserProfileStatus.IsolationRequired, result.Status);
        Assert.Equal(ManagedBrowserProfileIsolationReason.MissingMarker, result.IsolationReason);
        Assert.Equal("must-survive", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(Path.Combine(profilePath, FileSystemManagedBrowserProfilePolicy.MarkerName)));
    }

    [Fact]
    public async Task CorruptOrMismatchedMarkerRequiresIsolationAndIsNeverOverwritten()
    {
        var accountId = Guid.NewGuid();
        var policy = new FileSystemManagedBrowserProfilePolicy(root);
        var created = (await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome)).Profile!;
        var markerPath = Path.Combine(created.CanonicalSourcePath, FileSystemManagedBrowserProfilePolicy.MarkerName);
        const string corrupt = "marker contains a stable-baidu-uid and must not be logged or replaced";
        await File.WriteAllTextAsync(markerPath, corrupt);

        var corruptResult = await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome);

        Assert.Equal(ManagedBrowserProfileIsolationReason.InvalidMarker, corruptResult.IsolationReason);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(markerPath));

        var cleanAccount = Guid.NewGuid();
        var clean = (await policy.GetOrCreateAsync(cleanAccount, BrowserFamily.Edge)).Profile!;
        var wrongExpected = new ManagedBrowserProfileDescriptor(
            clean.AccountId,
            clean.Family,
            Guid.NewGuid(),
            clean.OwnershipId,
            clean.CanonicalSourcePath);

        var mismatch = await policy.OpenExistingAsync(wrongExpected);

        Assert.Equal(ManagedBrowserProfileIsolationReason.IdentityMismatch, mismatch.IsolationReason);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("family")]
    [InlineData("profile")]
    [InlineData("ownership")]
    public async Task EveryMarkerIdentityFieldMustMatchExactly(string field)
    {
        var accountId = Guid.NewGuid();
        var policy = new FileSystemManagedBrowserProfilePolicy(root);
        var expected = (await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome)).Profile!;
        var markerPath = Path.Combine(expected.CanonicalSourcePath, FileSystemManagedBrowserProfilePolicy.MarkerName);
        var original = await File.ReadAllTextAsync(markerPath);
        var replacement = field switch
        {
            "account" => original.Replace($"account-id={accountId:N}", $"account-id={Guid.NewGuid():N}", StringComparison.Ordinal),
            "family" => original.Replace("browser-family=Chrome", "browser-family=Edge", StringComparison.Ordinal),
            "profile" => original.Replace($"profile-id={expected.ProfileId:N}", $"profile-id={Guid.NewGuid():N}", StringComparison.Ordinal),
            "ownership" => original.Replace($"profile-ownership-id={expected.OwnershipId:N}", $"profile-ownership-id={Guid.NewGuid():N}", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        await File.WriteAllTextAsync(markerPath, replacement);

        var result = field is "profile" or "ownership"
            ? await policy.OpenExistingAsync(expected)
            : await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome);

        Assert.Equal(ManagedBrowserProfileStatus.IsolationRequired, result.Status);
        Assert.Equal(ManagedBrowserProfileIsolationReason.IdentityMismatch, result.IsolationReason);
        Assert.Equal(replacement, await File.ReadAllTextAsync(markerPath));
    }

    [Fact]
    public async Task ConcurrentFirstCreationHasOneCompleteCanonicalWinner()
    {
        var accountId = Guid.NewGuid();
        var policy = new FileSystemManagedBrowserProfilePolicy(root);

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome)));

        Assert.All(results, result => Assert.Equal(ManagedBrowserProfileStatus.Ready, result.Status));
        Assert.Single(results.Select(result => result.Profile!.ProfileId).Distinct());
        Assert.Single(results.Select(result => result.Profile!.OwnershipId).Distinct());
        var marker = await File.ReadAllTextAsync(Path.Combine(
            results[0].Profile!.CanonicalSourcePath,
            FileSystemManagedBrowserProfilePolicy.MarkerName));
        Assert.EndsWith("\n", marker, StringComparison.Ordinal);
        Assert.Equal(5, marker.Count(character => character == '\n'));
    }

    [Fact]
    public async Task CancellationAndLegacyFamilyAreRejectedBeforeCreatingAnything()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var policy = new FileSystemManagedBrowserProfilePolicy(root);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.GetOrCreateAsync(Guid.NewGuid(), BrowserFamily.Chrome, cancellation.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            policy.GetOrCreateAsync(Guid.NewGuid(), BrowserFamily.LegacyWebView2));

        Assert.False(Directory.Exists(Path.Combine(root, "managed-browser-profiles")));
    }

    [Theory]
    [InlineData(@"\\server\share\bbt-app-data")]
    [InlineData(@"\\?\C:\bbt-app-data")]
    public async Task UnpinnableNamespacesAreRejectedWithoutFallback(string appDataRoot)
    {
        var policy = new FileSystemManagedBrowserProfilePolicy(appDataRoot);

        var result = await policy.GetOrCreateAsync(Guid.NewGuid(), BrowserFamily.Chrome);

        Assert.Equal(ManagedBrowserProfileStatus.IsolationRequired, result.Status);
        Assert.Equal(ManagedBrowserProfileIsolationReason.UnsupportedNamespace, result.IsolationReason);
    }

    [Fact]
    public async Task DescriptorAndResultToStringRedactEveryIdentityAndPath()
    {
        var result = await new FileSystemManagedBrowserProfilePolicy(root)
            .GetOrCreateAsync(Guid.NewGuid(), BrowserFamily.Edge);
        var profile = result.Profile!;

        Assert.DoesNotContain(profile.AccountId.ToString(), profile.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.ProfileId.ToString(), profile.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.OwnershipId.ToString(), profile.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.CanonicalSourcePath, profile.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.CanonicalSourcePath, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatedDirectoriesAndMarkerUseProtectedCurrentUserOnlyAcl()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await new FileSystemManagedBrowserProfilePolicy(root)
            .GetOrCreateAsync(Guid.NewGuid(), BrowserFamily.Chrome);
        var accountPath = result.Profile!.CanonicalSourcePath;
        var paths = new[]
        {
            Path.Combine(root, "managed-browser-profiles"),
            Path.GetDirectoryName(accountPath)!,
            accountPath,
            Path.Combine(accountPath, FileSystemManagedBrowserProfilePolicy.MarkerName),
        };
        var currentUser = WindowsIdentity.GetCurrent().User!;

        foreach (var path in paths)
            AssertCurrentUserOnlyAcl(path, currentUser);
    }

    [Fact]
    public async Task AccountDirectoryReparseRequiresIsolationWithoutTouchingTarget()
    {
        var accountId = Guid.NewGuid();
        var policy = new FileSystemManagedBrowserProfilePolicy(root);
        var profilePath = (await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome)).Profile!
            .CanonicalSourcePath;
        var displaced = profilePath + ".saved";
        var outside = Path.Combine(root, "outside-account-target");
        Directory.CreateDirectory(outside);
        var canary = Path.Combine(outside, "must-survive.txt");
        await File.WriteAllTextAsync(canary, "outside");
        Directory.Move(profilePath, displaced);
        CreateDirectoryLink(profilePath, outside);

        try
        {
            var result = await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome);

            Assert.Equal(ManagedBrowserProfileStatus.IsolationRequired, result.Status);
            Assert.Equal(ManagedBrowserProfileIsolationReason.UnsafePath, result.IsolationReason);
            Assert.Equal("outside", await File.ReadAllTextAsync(canary));
        }
        finally
        {
            if (Directory.Exists(profilePath) &&
                (File.GetAttributes(profilePath) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(profilePath, recursive: false);
        }
    }

    [Fact]
    public async Task MarkerReparseRequiresIsolationWithoutReadingOrReplacingTarget()
    {
        var accountId = Guid.NewGuid();
        var policy = new FileSystemManagedBrowserProfilePolicy(root);
        var profile = (await policy.GetOrCreateAsync(accountId, BrowserFamily.Edge)).Profile!;
        var markerPath = Path.Combine(profile.CanonicalSourcePath, FileSystemManagedBrowserProfilePolicy.MarkerName);
        var target = Path.Combine(root, "outside-marker-target");
        Directory.CreateDirectory(target);
        var canary = Path.Combine(target, "must-survive.txt");
        const string secret = "outside-secret-marker-content";
        await File.WriteAllTextAsync(canary, secret);
        File.Delete(markerPath);
        CreateDirectoryLink(markerPath, target);

        try
        {
            var result = await policy.GetOrCreateAsync(accountId, BrowserFamily.Edge);

            Assert.Equal(ManagedBrowserProfileIsolationReason.UnsafePath, result.IsolationReason);
            Assert.Equal(secret, await File.ReadAllTextAsync(canary));
        }
        finally
        {
            if (Directory.Exists(markerPath) && (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(markerPath, recursive: false);
        }
    }

    [Fact]
    public async Task ExistingProfileWithAdditionalAclPrincipalFailsClosedWithoutRepair()
    {
        if (!OperatingSystem.IsWindows()) return;
        var accountId = Guid.NewGuid();
        var policy = new FileSystemManagedBrowserProfilePolicy(root);
        var profile = (await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome)).Profile!;
        AddEveryoneReadAcl(profile.CanonicalSourcePath);
        var markerPath = Path.Combine(profile.CanonicalSourcePath, FileSystemManagedBrowserProfilePolicy.MarkerName);
        var markerBefore = await File.ReadAllTextAsync(markerPath);

        var result = await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome);

        Assert.Equal(ManagedBrowserProfileIsolationReason.AccessControlMismatch, result.IsolationReason);
        Assert.Equal(markerBefore, await File.ReadAllTextAsync(markerPath));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AclChangedAfterAccountPinFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var accountId = Guid.NewGuid();
        var initial = await new FileSystemManagedBrowserProfilePolicy(root)
            .GetOrCreateAsync(accountId, BrowserFamily.Chrome);
        var policy = new FileSystemManagedBrowserProfilePolicy(root, null,
            profilePath => AddEveryoneReadAcl(profilePath));

        var result = await policy.GetOrCreateAsync(accountId, BrowserFamily.Chrome);

        Assert.Equal(ManagedBrowserProfileStatus.IsolationRequired, result.Status);
        Assert.Equal(ManagedBrowserProfileIsolationReason.AccessControlMismatch, result.IsolationReason);
        Assert.True(Directory.Exists(initial.Profile!.CanonicalSourcePath));
    }

    [Fact]
    public async Task ReplacementAfterValidationIsDetectedBeforeMarkerAccess()
    {
        var accountId = Guid.NewGuid();
        var initial = new FileSystemManagedBrowserProfilePolicy(root);
        var profile = (await initial.GetOrCreateAsync(accountId, BrowserFamily.Chrome)).Profile!;
        var displaced = profile.CanonicalSourcePath + ".saved";
        var outside = Path.Combine(root, "replacement-target");
        Directory.CreateDirectory(outside);
        var canary = Path.Combine(outside, "must-survive.txt");
        await File.WriteAllTextAsync(canary, "outside");
        var replaced = false;
        var raced = new FileSystemManagedBrowserProfilePolicy(root, path =>
        {
            if (replaced || !string.Equals(path, profile.CanonicalSourcePath, StringComparison.OrdinalIgnoreCase)) return;
            replaced = true;
            Directory.Move(path, displaced);
            CreateDirectoryLink(path, outside);
        });

        try
        {
            var result = await raced.GetOrCreateAsync(accountId, BrowserFamily.Chrome);

            Assert.True(replaced);
            Assert.Equal(ManagedBrowserProfileIsolationReason.UnsafePath, result.IsolationReason);
            Assert.Equal("outside", await File.ReadAllTextAsync(canary));
        }
        finally
        {
            if (Directory.Exists(profile.CanonicalSourcePath) &&
                (File.GetAttributes(profile.CanonicalSourcePath) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(profile.CanonicalSourcePath, recursive: false);
        }
    }

    [Fact]
    public async Task PinnedDirectoryChainDetectsReplacementBeforeMarkerAccess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var accountId = Guid.NewGuid();
        var initial = new FileSystemManagedBrowserProfilePolicy(root);
        var profile = (await initial.GetOrCreateAsync(accountId, BrowserFamily.Edge)).Profile!;
        var replacement = profile.CanonicalSourcePath + ".replacement";
        var policy = new FileSystemManagedBrowserProfilePolicy(root, null, path =>
        {
            Directory.Move(path, replacement);
        });

        var result = await policy.GetOrCreateAsync(accountId, BrowserFamily.Edge);

        Assert.Equal(ManagedBrowserProfileStatus.IsolationRequired, result.Status);
        Assert.Equal(ManagedBrowserProfileIsolationReason.ConcurrentMutation, result.IsolationReason);
        Assert.True(File.Exists(Path.Combine(replacement, FileSystemManagedBrowserProfilePolicy.MarkerName)));
    }

    [Fact]
    public async Task OverlappingWritersCannotExposeAPartialCanonicalMarker()
    {
        if (!OperatingSystem.IsWindows()) return;
        var accountId = Guid.NewGuid();
        var markerPrefixFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new FileSystemManagedBrowserProfilePolicy(root, new ManagedBrowserProfilePolicyTestHooks
        {
            AfterMarkerPrefixFlushed = () =>
            {
                markerPrefixFlushed.TrySetResult();
                releaseWriter.Task.GetAwaiter().GetResult();
            },
        });
        var reader = new FileSystemManagedBrowserProfilePolicy(root);

        var firstTask = Task.Run(() => writer.GetOrCreateAsync(accountId, BrowserFamily.Chrome));
        await markerPrefixFlushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = Task.Run(() => reader.GetOrCreateAsync(accountId, BrowserFamily.Chrome));
        ManagedBrowserProfileResult second;
        try
        {
            second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseWriter.TrySetResult();
        }
        var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ManagedBrowserProfileStatus.Ready, first.Status);
        Assert.Equal(ManagedBrowserProfileStatus.Ready, second.Status);
        Assert.Equal(first.Profile!.ProfileId, second.Profile!.ProfileId);
        Assert.Equal(first.Profile.OwnershipId, second.Profile.OwnershipId);
    }

    [Fact]
    public async Task SimultaneousNamespaceCreationStillProducesOneCanonicalWinner()
    {
        if (!OperatingSystem.IsWindows()) return;
        var accountId = Guid.NewGuid();
        using var barrier = new Barrier(2);
        ManagedBrowserProfilePolicyTestHooks Hooks() => new()
        {
            AfterTrustedNamespacePinned = () => barrier.SignalAndWait(TimeSpan.FromSeconds(5)),
        };
        var firstPolicy = new FileSystemManagedBrowserProfilePolicy(root, Hooks());
        var secondPolicy = new FileSystemManagedBrowserProfilePolicy(root, Hooks());

        var results = await Task.WhenAll(
            Task.Run(() => firstPolicy.GetOrCreateAsync(accountId, BrowserFamily.Chrome)),
            Task.Run(() => secondPolicy.GetOrCreateAsync(accountId, BrowserFamily.Chrome)));

        Assert.All(results, result => Assert.Equal(ManagedBrowserProfileStatus.Ready, result.Status));
        Assert.Single(results.Select(result => result.Profile!.ProfileId).Distinct());
        Assert.Single(results.Select(result => result.Profile!.OwnershipId).Distinct());
    }

    [Fact]
    public async Task CrashBeforePublishLeavesOnlyOpaqueStagingAndNextRunPublishesCanonical()
    {
        if (!OperatingSystem.IsWindows()) return;
        var accountId = Guid.NewGuid();
        var crashing = new FileSystemManagedBrowserProfilePolicy(root, new ManagedBrowserProfilePolicyTestHooks
        {
            BeforePublish = () => throw new SimulatedCrashException(),
        });

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            crashing.GetOrCreateAsync(accountId, BrowserFamily.Edge));
        var familyRoot = Path.Combine(root, "managed-browser-profiles", "edge");
        Assert.False(Directory.Exists(ProfilePath(accountId, BrowserFamily.Edge)));
        var staging = Assert.Single(Directory.EnumerateDirectories(familyRoot));
        Assert.StartsWith(".staging-", Path.GetFileName(staging), StringComparison.Ordinal);

        var result = await new FileSystemManagedBrowserProfilePolicy(root)
            .GetOrCreateAsync(accountId, BrowserFamily.Edge);

        Assert.Equal(ManagedBrowserProfileStatus.Ready, result.Status);
        Assert.True(Directory.Exists(staging));
        Assert.True(Directory.Exists(result.Profile!.CanonicalSourcePath));
    }

    [Fact]
    public async Task AppDataRootReplacementAfterPinCausesZeroWritesThroughReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        var displaced = root + ".trusted";
        var outside = root + ".outside";
        Directory.CreateDirectory(outside);
        var canary = Path.Combine(outside, "must-survive.txt");
        await File.WriteAllTextAsync(canary, "outside");
        var switched = false;
        var policy = new FileSystemManagedBrowserProfilePolicy(root, new ManagedBrowserProfilePolicyTestHooks
        {
            AfterTrustedNamespacePinned = () =>
            {
                Directory.Move(root, displaced);
                CreateDirectoryLink(root, outside);
                switched = true;
            },
        });

        try
        {
            var result = await policy.GetOrCreateAsync(Guid.NewGuid(), BrowserFamily.Chrome);

            Assert.True(switched);
            Assert.Equal(ManagedBrowserProfileStatus.IsolationRequired, result.Status);
            Assert.Equal(ManagedBrowserProfileIsolationReason.ConcurrentMutation, result.IsolationReason);
            Assert.Equal("outside", await File.ReadAllTextAsync(canary));
            Assert.False(Directory.Exists(Path.Combine(outside, "managed-browser-profiles")));
        }
        finally
        {
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(root, recursive: false);
            if (Directory.Exists(displaced)) Directory.Delete(displaced, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentUserOnlyAcl(string path, SecurityIdentifier currentUser)
    {
        FileSystemSecurity security = (File.GetAttributes(path) & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(currentUser, security.GetOwner(typeof(SecurityIdentifier)));
        var allowRules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToArray();
        Assert.NotEmpty(allowRules);
        Assert.All(allowRules, rule => Assert.Equal(currentUser, rule.IdentityReference));
    }

    [SupportedOSPlatform("windows")]
    private static void AddEveryoneReadAcl(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
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
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not start junction creation.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Could not create the test junction.");
    }

    private sealed class SimulatedCrashException : Exception;

    private string ProfilePath(Guid accountId, BrowserFamily family) => Path.Combine(
        Path.GetFullPath(root),
        "managed-browser-profiles",
        family == BrowserFamily.Chrome ? "chrome" : "edge",
        accountId.ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
