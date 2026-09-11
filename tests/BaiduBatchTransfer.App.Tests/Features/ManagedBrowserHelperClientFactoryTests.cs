using BaiduBatchTransfer.App.Features.ManagedBrowser;
using Microsoft.Extensions.DependencyInjection;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserHelperClientFactoryTests
{
    [Fact]
    public void ConstructionAndDiResolutionDoNotStartHelper()
    {
        var starts = 0;
        var factory = new ManagedBrowserHelperClientFactory(_ =>
        {
            starts++;
            return Task.FromResult<IManagedBrowserHelperConnection>(new FakeConnection());
        });
        var services = new ServiceCollection();
        services.AddSingleton(factory);

        using var provider = services.BuildServiceProvider();

        Assert.Same(factory, provider.GetRequiredService<ManagedBrowserHelperClientFactory>());
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task OperationLeaseOwnsAndReleasesExactlyOneHelper()
    {
        var connection = new FakeConnection();
        var starts = 0;
        var factory = new ManagedBrowserHelperClientFactory(_ =>
        {
            starts++;
            return Task.FromResult<IManagedBrowserHelperConnection>(connection);
        });
        var operationId = Guid.NewGuid();

        var helper = await factory.CreateAsync(operationId, CancellationToken.None);

        Assert.Equal(1, starts);
        await helper.DisposeAsync();
        await helper.DisposeAsync();
        Assert.Equal(1, connection.Disposals);

        var replacement = await factory.CreateAsync(operationId, CancellationToken.None);
        await replacement.DisposeAsync();
        Assert.Equal(2, connection.Disposals);
    }

    [Fact]
    public async Task ConcurrentLeaseForSameOperationIsRejectedWithoutStartingSecondHelper()
    {
        var starts = 0;
        var factory = new ManagedBrowserHelperClientFactory(_ =>
        {
            starts++;
            return Task.FromResult<IManagedBrowserHelperConnection>(new FakeConnection());
        });
        var operationId = Guid.NewGuid();
        await using var first = await factory.CreateAsync(operationId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreateAsync(operationId, CancellationToken.None).AsTask());

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task FactoryDisposalReleasesEveryActiveOperationHelper()
    {
        var firstConnection = new FakeConnection();
        var secondConnection = new FakeConnection();
        var connections = new Queue<FakeConnection>([firstConnection, secondConnection]);
        var factory = new ManagedBrowserHelperClientFactory(_ =>
            Task.FromResult<IManagedBrowserHelperConnection>(connections.Dequeue()));
        var first = await factory.CreateAsync(Guid.NewGuid(), CancellationToken.None);
        var second = await factory.CreateAsync(Guid.NewGuid(), CancellationToken.None);

        await factory.DisposeAsync();

        Assert.Equal(1, firstConnection.Disposals);
        Assert.Equal(1, secondConnection.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            factory.CreateAsync(Guid.NewGuid(), CancellationToken.None).AsTask());
    }

    private sealed class FakeConnection : IManagedBrowserHelperConnection
    {
        internal int Disposals { get; private set; }

        public Task<ManagedBrowserResponse> SendAsync(
            ManagedBrowserRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
