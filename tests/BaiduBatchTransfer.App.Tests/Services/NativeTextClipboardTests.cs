using System.Runtime.InteropServices;
using System.Text;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class NativeTextClipboardTests
{
    [Fact]
    public void PublishesImmediateNullTerminatedUnicodeWithANonzeroOwnerAndTransfersMemoryOwnership()
    {
        using var api = new FakeApi();
        var clipboard = new NativeTextClipboard(api);

        clipboard.SetText("中文 😀\r\nhttps://pan.baidu.com/s/example?pwd=s16c");

        Assert.Equal(Encoding.Unicode.GetBytes("中文 😀\r\nhttps://pan.baidu.com/s/example?pwd=s16c\0"), api.PublishedBytes);
        Assert.Equal(["CreateOwner", "Allocate", "Lock", "Unlock", "Open", "Empty", "Set", "Close", "DestroyOwner"], api.Calls);
        Assert.Equal((nint)123, api.OpenOwner);
        Assert.True(api.Transferred);
        Assert.Equal(0, api.Frees);
    }

    [Theory]
    [InlineData("Allocate", false, false)]
    [InlineData("Lock", true, false)]
    [InlineData("Unlock", true, false)]
    [InlineData("Open", true, false)]
    [InlineData("Empty", true, true)]
    [InlineData("Set", true, true)]
    public void FailuresReleaseOnlyApplicationOwnedResources(string operation, bool shouldFree, bool shouldClose)
    {
        using var api = new FakeApi { FailureAt = operation };
        var clipboard = new NativeTextClipboard(api);

        Assert.Same(api.Failure, Assert.Throws<ClipboardNativeException>(() => clipboard.SetText("test")));

        Assert.Equal(shouldFree ? 1 : 0, api.Frees);
        Assert.Equal(shouldClose, api.Calls.Contains("Close"));
        Assert.Equal("DestroyOwner", api.Calls.Last());
        Assert.False(api.Transferred);
        if (operation is "Allocate" or "Lock" or "Unlock" or "Open") Assert.DoesNotContain("Empty", api.Calls);
    }

    [Fact]
    public void CloseFailureIsReportedWithoutFreeingMemoryAlreadyOwnedByWindows()
    {
        using var api = new FakeApi { FailureAt = "Close" };

        Assert.Same(api.Failure, Assert.Throws<ClipboardNativeException>(() => new NativeTextClipboard(api).SetText("test")));

        Assert.True(api.Transferred);
        Assert.Equal(0, api.Frees);
        Assert.Equal("DestroyOwner", api.Calls.Last());
    }

    [Fact]
    public void OwnerCreationFailureDoesNotTouchClipboardOrMemory()
    {
        using var api = new FakeApi { FailureAt = "CreateOwner" };
        Assert.Same(api.Failure, Assert.Throws<ClipboardNativeException>(() => new NativeTextClipboard(api).SetText("test")));
        Assert.Equal(["CreateOwner"], api.Calls);
    }

    [Fact]
    public void EmbeddedNullIsRejectedBeforeAnyNativeOperation()
    {
        using var api = new FakeApi();
        Assert.Throws<ArgumentException>(() => new NativeTextClipboard(api).SetText("first\0truncated"));
        Assert.Empty(api.Calls);
    }

    internal sealed class FakeApi : INativeClipboardApi, IDisposable
    {
        public List<string> Calls { get; } = [];
        public string? FailureAt { get; init; }
        public ClipboardNativeException Failure { get; } = new("test operation", 5);
        public nint OpenOwner;
        public byte[]? PublishedBytes;
        public bool Transferred;
        public int Frees;
        private nint memory;
        private int byteCount;

        private void Record(string operation)
        {
            Calls.Add(operation);
            if (FailureAt == operation) throw Failure;
        }
        public nint CreateOwnerWindow() { Record("CreateOwner"); return 123; }
        public void DestroyOwnerWindow(nint owner) { Assert.Equal((nint)123, owner); Record("DestroyOwner"); }
        public nint Allocate(nuint bytes)
        {
            Record("Allocate");
            byteCount = checked((int)bytes);
            return memory = Marshal.AllocHGlobal(byteCount);
        }
        public nint Lock(nint handle) { Assert.Equal(memory, handle); Record("Lock"); return memory; }
        public void Unlock(nint handle) { Assert.Equal(memory, handle); Record("Unlock"); }
        public void Free(nint handle)
        {
            Assert.False(Transferred);
            Assert.Equal(memory, handle);
            Record("Free");
            Frees++;
            Marshal.FreeHGlobal(memory);
            memory = 0;
        }
        public void Open(nint owner) { Assert.NotEqual((nint)0, owner); OpenOwner = owner; Record("Open"); }
        public void Empty() => Record("Empty");
        public void SetUnicodeData(nint handle)
        {
            Assert.Equal(memory, handle);
            Assert.NotEqual((nint)0, handle);
            Record("Set");
            PublishedBytes = new byte[byteCount];
            Marshal.Copy(memory, PublishedBytes, 0, byteCount);
            Transferred = true;
        }
        public void Close() => Record("Close");
        public void Dispose() { if (memory != 0) Marshal.FreeHGlobal(memory); }
    }
}
