using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database.Repositories;

public abstract class SqliteRepositoryBase
{
    protected SqliteRepositoryBase(SqliteConnection connection, SqliteTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    protected SqliteConnection Connection { get; }
    protected SqliteTransaction Transaction { get; }

    protected SqliteCommand Command(string sql)
    {
        var command = Connection.CreateCommand(); command.Transaction = Transaction; command.CommandText = sql; return command;
    }

    protected static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    protected static string Id(Guid value) => value.ToString("D");
    protected static object NullableId(Guid? value) => value is null ? DBNull.Value : Id(value.Value);
    protected static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    protected static object NullableTime(DateTimeOffset? value) => value is null ? DBNull.Value : Time(value.Value);
    protected static DateTimeOffset ReadTime(SqliteDataReader reader, int ordinal) => DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    protected static DateTimeOffset? ReadNullableTime(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ReadTime(reader, ordinal);
    protected static Guid? ReadNullableId(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
}
