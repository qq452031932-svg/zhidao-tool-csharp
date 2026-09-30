using System.Runtime.InteropServices;

namespace BaiduBatchTransfer.App.Services;

internal sealed class Win32ClipboardApi : INativeClipboardApi
{
    private const uint UnicodeText = 13;
    private const uint MoveableMemory = 0x0002;

    public nint CreateOwnerWindow()
    {
        // OpenClipboard(NULL) followed by EmptyClipboard cannot establish a SetClipboardData owner.
        // A message-only STATIC window provides a real HWND without showing or activating any UI.
        var owner = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, new nint(-3), 0, GetModuleHandleW(null), 0);
        if (owner == 0) throw Error("CreateWindowExW");
        return owner;
    }

    public void DestroyOwnerWindow(nint owner)
    {
        if (!DestroyWindow(owner)) throw Error("DestroyWindow");
    }

    public nint Allocate(nuint bytes)
    {
        var memory = GlobalAlloc(MoveableMemory, bytes);
        if (memory == 0) throw Error("GlobalAlloc");
        return memory;
    }

    public nint Lock(nint memory)
    {
        var address = GlobalLock(memory);
        if (address == 0) throw Error("GlobalLock");
        return address;
    }

    public void Unlock(nint memory)
    {
        // FALSE with NO_ERROR means the last lock was released successfully.
        SetLastError(0);
        if (!GlobalUnlock(memory) && Marshal.GetLastWin32Error() != 0) throw Error("GlobalUnlock");
    }

    public void Free(nint memory)
    {
        if (GlobalFree(memory) != 0) throw Error("GlobalFree");
    }

    public void Open(nint owner)
    {
        if (!OpenClipboard(owner)) throw Error("OpenClipboard");
    }

    public void Empty()
    {
        if (!EmptyClipboard()) throw Error("EmptyClipboard");
    }

    public void SetUnicodeData(nint memory)
    {
        if (SetClipboardData(UnicodeText, memory) == 0) throw Error("SetClipboardData");
    }

    public void Close()
    {
        if (!CloseClipboard()) throw Error("CloseClipboard");
    }

    private static ClipboardNativeException Error(string operation) => new(operation, Marshal.GetLastWin32Error());

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void SetLastError(uint error);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
}
