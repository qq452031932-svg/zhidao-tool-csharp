using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BaiduBatchTransfer.App.Services;

internal interface INativeClipboardApi
{
    nint CreateOwnerWindow();
    void DestroyOwnerWindow(nint owner);
    nint Allocate(nuint bytes);
    nint Lock(nint memory);
    void Unlock(nint memory);
    void Free(nint memory);
    void Open(nint owner);
    void Empty();
    void SetUnicodeData(nint memory);
    void Close();
}

internal sealed class ClipboardNativeException : Win32Exception
{
    public ClipboardNativeException(string operation, int error) : base(error, $"{operation} failed (Win32 {error}).")
    {
        Operation = operation;
        HResult = error > 0 ? unchecked((int)(0x80070000u | (uint)(error & 0xffff))) : unchecked((int)0x80004005);
    }
    public string Operation { get; }
}

internal sealed class NativeTextClipboard(INativeClipboardApi api)
{
    public void SetText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Contains('\0')) throw new ArgumentException("Clipboard text cannot contain a null character.", nameof(text));
        var characters = (text + '\0').ToCharArray();
        var owner = api.CreateOwnerWindow();
        nint memory = 0;
        try
        {
            // Prepare CF_UNICODETEXT before replacing existing clipboard data.
            memory = api.Allocate(checked((nuint)characters.Length * sizeof(char)));
            var address = api.Lock(memory);
            try { Marshal.Copy(characters, 0, address, characters.Length); }
            finally { api.Unlock(memory); }

            api.Open(owner);
            try
            {
                api.Empty();
                api.SetUnicodeData(memory);
                // Windows now owns this allocation. It is fully rendered: no IDataObject or OLE Flush.
                memory = 0;
            }
            finally { api.Close(); }
        }
        finally
        {
            try { if (memory != 0) api.Free(memory); }
            finally { api.DestroyOwnerWindow(owner); }
        }
    }
}
