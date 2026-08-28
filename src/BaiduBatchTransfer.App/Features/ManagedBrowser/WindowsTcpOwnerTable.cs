using System.Net;
using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed record TcpListenerOwner(IPAddress Address, int Port, int ProcessId);

public interface IWindowsTcpOwnerTable
{
    IReadOnlyList<TcpListenerOwner> ReadListeners();
}

public sealed class WindowsTcpOwnerTable : IWindowsTcpOwnerTable
{
    public IReadOnlyList<TcpListenerOwner> ReadListeners()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var result = new List<TcpListenerOwner>();
        ReadFamily(2, 24, result);
        ReadFamily(23, 56, result);
        return result;
    }

    public static int RequireUniqueLoopbackOwner(IEnumerable<TcpListenerOwner> listeners, int port)
    {
        if (port is < 1 or > 65535) throw new InvalidDataException("Invalid listener port.");
        var matches = listeners.Where(row => row.Port == port).Take(2).ToArray();
        if (matches.Length != 1 || !matches[0].Address.Equals(IPAddress.Loopback) || matches[0].ProcessId <= 0)
            throw new InvalidDataException("The managed browser listener is missing or ambiguous.");
        return matches[0].ProcessId;
    }

    private static void ReadFamily(int family, int rowBytes, List<TcpListenerOwner> result)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (size > 4 * 1024 * 1024) throw new IOException("Listener table exceeds its bound.");
            var buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(checked((int)size));
            try
            {
                var allocated = size;
                var status = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0); // OWNER_PID_LISTENER
                if (status == 122) continue; // Table grew; retry with the newly reported bound.
                if (status != 0 || buffer == IntPtr.Zero || size > allocated || size < 4)
                    throw new IOException("The listener owner table is unavailable.");
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                if (count > (size - 4) / rowBytes) throw new IOException("Invalid listener owner table.");
                for (var index = 0; index < count; index++)
                {
                    var row = bytes.AsSpan(4 + index * rowBytes, rowBytes);
                    var address = family == 2 ? new IPAddress(row.Slice(4, 4)) :
                        new IPAddress(row[..16], BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4)));
                    var port = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(family == 2 ? 8 : 20, 2));
                    var pid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(family == 2 ? 20 : 52, 4));
                    if (pid > int.MaxValue) throw new IOException("Invalid listener process identity.");
                    result.Add(new(address, port, (int)pid));
                }
                return;
            }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("The listener owner table changed repeatedly.");
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool ordered, int family, int tableClass, uint reserved);
}
