using System.Net;
using System.Net.Sockets;
using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WindowsTcpOwnerTableTests
{
    [Fact]
    public void ResolvesActualLoopbackListenerToCurrentProcess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Equal(Environment.ProcessId, WindowsTcpOwnerTable.RequireUniqueLoopbackOwner(new WindowsTcpOwnerTable().ReadListeners(), port));
    }

    [Fact]
    public void RejectsMissingAmbiguousAndNonLoopbackListeners()
    {
        var owner = new TcpListenerOwner(IPAddress.Loopback, 49152, 123);
        Assert.Throws<InvalidDataException>(() => WindowsTcpOwnerTable.RequireUniqueLoopbackOwner([], 49152));
        Assert.Throws<InvalidDataException>(() => WindowsTcpOwnerTable.RequireUniqueLoopbackOwner([owner, owner], 49152));
        Assert.Throws<InvalidDataException>(() => WindowsTcpOwnerTable.RequireUniqueLoopbackOwner([owner with { Address = IPAddress.Any }], 49152));
        Assert.Throws<InvalidDataException>(() => WindowsTcpOwnerTable.RequireUniqueLoopbackOwner([owner with { ProcessId = 0 }], 49152));
        Assert.Equal(123, WindowsTcpOwnerTable.RequireUniqueLoopbackOwner([owner, owner with { Port = 50000 }], 49152));
    }
}
