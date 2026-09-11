using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserDirectorySnapshotTests
{
    private const string Source = "https://pan.baidu.com/s/synthetic?pwd=a1B2";

    [Fact]
    public void SnapshotCopiesInputArraysAndCanonicalizesOnlyItemOrdering()
    {
        var ancestors = new[] { "root", "parent" };
        var items = new[] { Folder("z", " z "), Folder("a", " ") };
        var snapshot = Snapshot("parent", "/parent", ancestors, items);
        ancestors[0] = "mutated-root";
        items[0] = Folder("replacement");
        Assert.Equal(new[] { "root", "parent" }, snapshot.AncestorExactIds);
        Assert.Equal(new[] { "a", "z" }, snapshot.Items.Select(item => item.ExactId));
        Assert.Equal(new[] { " ", " z " }, snapshot.Items.Select(item => item.ServerName));
        Assert.Equal("/parent", snapshot.CanonicalPath);
    }

    [Fact]
    public void OversizedEnumerationStopsAtTheSentinelWithoutHanging()
    {
        var ancestorsRead = 0;
        IEnumerable<string> Ancestors()
        {
            for (var index = 0; index < 34; index++) { ancestorsRead++; yield return $"id{index}"; }
            throw new InvalidOperationException("Ancestor enumeration exceeded its bound.");
        }
        Assert.Throws<ArgumentException>(() => Snapshot("parent", "/parent", Ancestors(), []));
        Assert.Equal(34, ancestorsRead);

        var itemsRead = 0;
        IEnumerable<ManagedBrowserSourceItem> Items()
        {
            for (var index = 0; index < 33; index++) { itemsRead++; yield return Folder($"item{index}"); }
            throw new InvalidOperationException("Item enumeration exceeded its bound.");
        }
        Assert.Throws<ArgumentException>(() => new ManagedBrowserDirectorySnapshot("uid", "gen", "root", "/",
            ["root"], 33, true, true, Items(), new string('0', 64)));
        Assert.Equal(33, itemsRead);
    }

    [Fact]
    public void MaximumDepthAndMaximumFolderCountAreAcceptedExactly()
    {
        var ancestors = Enumerable.Range(0, 33).Select(index => $"parent{index}").ToArray();
        var folders = Enumerable.Range(0, 32).Select(index => Folder($"child{index}")).ToArray();
        var snapshot = Snapshot("parent32", "/" + string.Join('/', Enumerable.Repeat("segment", 32)), ancestors, folders);
        Assert.Equal(33, snapshot.AncestorExactIds.Length);
        Assert.Equal(32, snapshot.TotalCount);
    }

    [Fact]
    public void Utf8PathLimitIsIndependentOfSegmentAndDepthLimits()
    {
        var segments = Enumerable.Repeat(new string('a', 255), 16).ToArray();
        var ancestors = Enumerable.Range(0, 17).Select(index => $"parent{index}").ToArray();
        var limitPath = "/" + string.Join('/', segments);
        Assert.Equal(4096, System.Text.Encoding.UTF8.GetByteCount(limitPath));
        Assert.Equal(limitPath, Snapshot("parent16", limitPath, ancestors, []).CanonicalPath);
        segments[0] += "a"; // Each segment remains <=256; total bytes alone exceed the limit.
        Assert.Throws<ArgumentException>(() => Snapshot("parent16", "/" + string.Join('/', segments), ancestors, []));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("parent")]
    [InlineData("path")]
    [InlineData("ancestor")]
    [InlineData("name")]
    [InlineData("uid")]
    public void FingerprintChangesWhenAnyDirectoryEvidenceChanges(string changed)
    {
        var original = Snapshot("parent", "/parent", ["root", "parent"], [Folder("child", "name")]);
        var parent = changed == "parent" ? "other-parent" : "parent";
        var altered = new ManagedBrowserDirectorySnapshot(changed == "uid" ? "other-uid" : "uid",
            changed == "generation" ? "other-gen" : "gen", parent, changed == "path" ? "/other" : "/parent",
            [changed == "ancestor" ? "other-root" : "root", parent], 1, true, true,
            [Folder("child", changed == "name" ? "name " : "name")], new string('0', 64));
        Assert.NotEqual(original.ComputeFingerprint(Source), altered.ComputeFingerprint(Source));
    }

    [Fact]
    public void FolderPresentationOrderDoesNotChangeFingerprint()
    {
        var folders = new[] { Folder("z"), Folder("a") };
        var one = Snapshot("root", "/", ["root"], folders);
        var two = Snapshot("root", "/", ["root"], folders.Reverse().ToArray());
        Assert.Equal(one.ComputeFingerprint(Source), two.ComputeFingerprint(Source));
    }

    private static ManagedBrowserSourceItem Folder(string id, string name = "folder") => new(id, ManagedBrowserSourceItemKind.Folder, name);
    private static ManagedBrowserDirectorySnapshot Snapshot(string parent, string path, IEnumerable<string> ancestors,
        ManagedBrowserSourceItem[] items) => new("uid", "gen", parent, path, ancestors,
            items.Length, true, true, items, new string('0', 64));
}
