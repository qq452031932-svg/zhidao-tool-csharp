using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteAdapterCapabilityStore : IAdapterCapabilityStore
{
    private readonly SqliteConnectionFactory factory;
    public SqliteAdapterCapabilityStore(SqliteConnectionFactory factory) => this.factory=factory??throw new ArgumentNullException(nameof(factory));

    public async Task AcceptAsync(AdapterCapabilityAcceptanceRecord acceptance,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        ValidateKey(acceptance.AdapterKind,acceptance.ProtocolVersion,acceptance.SelectorVersion,acceptance.AdapterBuildVersion);
        if(acceptance.EvidenceHash.Length!=64||acceptance.EvidenceHash.Any(value=>value is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("A nonsecret uppercase SHA-256 evidence hash is required.",nameof(acceptance));
        await using var connection=await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction=connection.BeginTransaction(deferred:false);
        try
        {
            await using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText="""
                INSERT INTO AdapterCapabilityAcceptances(AdapterKind,ProtocolVersion,SelectorVersion,AdapterBuildVersion,AcceptedAt,EvidenceHash)
                VALUES($kind,$protocol,$selector,$build,$accepted,$evidence)
                ON CONFLICT(AdapterKind,ProtocolVersion,SelectorVersion,AdapterBuildVersion) DO UPDATE SET AcceptedAt=excluded.AcceptedAt,EvidenceHash=excluded.EvidenceHash;
                """;
            command.Parameters.AddWithValue("$kind",acceptance.AdapterKind);command.Parameters.AddWithValue("$protocol",acceptance.ProtocolVersion);
            command.Parameters.AddWithValue("$selector",acceptance.SelectorVersion);
            command.Parameters.AddWithValue("$build",acceptance.AdapterBuildVersion);command.Parameters.AddWithValue("$accepted",acceptance.AcceptedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$evidence",acceptance.EvidenceHash);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch{await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);throw;}
    }

    public async Task<AdapterCapabilityAcceptanceRecord?> GetAsync(string adapterKind,string protocolVersion,string selectorVersion,string adapterBuildVersion,CancellationToken cancellationToken=default)
    {
        ValidateKey(adapterKind,protocolVersion,selectorVersion,adapterBuildVersion);
        await using var connection=await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction=connection.BeginTransaction(deferred:true);
        await using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT AdapterKind,ProtocolVersion,SelectorVersion,AdapterBuildVersion,AcceptedAt,EvidenceHash FROM AdapterCapabilityAcceptances WHERE AdapterKind=$kind AND ProtocolVersion=$protocol AND SelectorVersion=$selector AND AdapterBuildVersion=$build;";
        command.Parameters.AddWithValue("$kind",adapterKind);command.Parameters.AddWithValue("$protocol",protocolVersion);command.Parameters.AddWithValue("$selector",selectorVersion);command.Parameters.AddWithValue("$build",adapterBuildVersion);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var value=await reader.ReadAsync(cancellationToken).ConfigureAwait(false)?new AdapterCapabilityAcceptanceRecord(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),DateTimeOffset.Parse(reader.GetString(4),System.Globalization.CultureInfo.InvariantCulture),reader.GetString(5)):null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);return value;
    }

    private static void ValidateKey(string kind,string protocol,string selector,string build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);ArgumentException.ThrowIfNullOrWhiteSpace(protocol);ArgumentException.ThrowIfNullOrWhiteSpace(selector);ArgumentException.ThrowIfNullOrWhiteSpace(build);
        if(kind.Length>100||protocol.Length>100||selector.Length>100||build.Length>100)throw new ArgumentException("Adapter capability version keys are too long.");
    }
}
