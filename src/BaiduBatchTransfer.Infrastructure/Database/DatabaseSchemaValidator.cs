using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed record DatabaseSchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed partial class DatabaseSchemaValidator
{
    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "UNIQUE", "TABLE", "INDEX", "ON", "WHERE", "CHECK", "IN", "NOT", "NULL", "DEFAULT",
        "PRIMARY", "KEY", "INTEGER", "TEXT", "FOREIGN", "REFERENCES", "DELETE", "CASCADE", "OR", "AND", "IS"
    };

    public async Task<DatabaseSchemaValidationResult> ValidateAsync(
        SqliteConnection connection,
        int expectedVersion,
        CancellationToken cancellationToken = default,
        SqliteTransaction? transaction = null)
    {
        var errors = new List<string>();
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type IN ('table','index','view','trigger') AND sql IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                if (!name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) actual[name] = Normalize(reader.GetString(1));
            }
        }
        if (!actual.ContainsKey("SchemaVersions")) return new(false, ["SchemaVersions is missing."]);
        await using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "SELECT COALESCE(MAX(Version),-1) FROM SchemaVersions;";
            try
            {
                var rawVersion = await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                var textVersion = Convert.ToString(rawVersion, CultureInfo.InvariantCulture);
                if (!long.TryParse(textVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVersion) ||
                    parsedVersion is < 0 or > int.MaxValue || parsedVersion != expectedVersion)
                    errors.Add("Schema version does not match the backup identity.");
            }
            catch (SqliteException) { errors.Add("SchemaVersions has an invalid shape."); }
        }
        if (expectedVersion == 0) return new(errors.Count == 0, errors);
        if (expectedVersion is < 1 or > DatabaseMigrator.LatestVersion)
            return new(false, [.. errors, "Unsupported schema version."]);

        var expected = await LoadExpectedSchemaAsync(expectedVersion, cancellationToken).ConfigureAwait(false);
        foreach (var (name, sql) in expected)
        {
            if (!actual.TryGetValue(name, out var actualSql)) errors.Add($"Required schema object {name} is missing.");
            else if (!string.Equals(sql, actualSql, StringComparison.Ordinal)) errors.Add($"Schema object {name} does not match the v{expectedVersion} fingerprint.");
        }
        foreach (var name in actual.Keys.Except(expected.Keys, StringComparer.OrdinalIgnoreCase))
            errors.Add($"Unexpected schema object {name} is present.");
        return new(errors.Count == 0, errors);
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadExpectedSchemaAsync(
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var expectedConnection = new SqliteConnection("Data Source=:memory:");
        await expectedConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        for (var version = 1; version <= expectedVersion; version++)
        {
            await using var migration = expectedConnection.CreateCommand();
            migration.CommandText = DatabaseMigrator.ReadMigrationSql(version);
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var command = expectedConnection.CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type IN ('table','index','view','trigger') AND sql IS NOT NULL;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (!name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                result[name] = Normalize(reader.GetString(1));
        }
        return result;
    }

    private static string Normalize(string sql)
    {
        var quoted = new List<string>();
        var protectedSql = ProtectQuotedSegments(sql, quoted);
        var value = IfNotExists().Replace(protectedSql, "$1");
        value = Whitespace().Replace(value, " ").Trim().TrimEnd(';');
        value = PunctuationWhitespace().Replace(value, "$1");
        value = SqlWord().Replace(value, match => SqlKeywords.Contains(match.Value) ? match.Value.ToUpperInvariant() : match.Value);
        for (var index = 0; index < quoted.Count; index++)
            value = value.Replace($"\uE000{index}\uE001", quoted[index], StringComparison.Ordinal);
        return value;
    }

    private static string ProtectQuotedSegments(string sql, ICollection<string> quoted)
    {
        var output = new StringBuilder(sql.Length);
        for (var index = 0; index < sql.Length; index++)
        {
            var opening = sql[index];
            if (opening is not ('\'' or '"' or '`' or '['))
            {
                output.Append(opening);
                continue;
            }

            var closing = opening == '[' ? ']' : opening;
            var segment = new StringBuilder().Append(opening);
            while (++index < sql.Length)
            {
                var current = sql[index];
                segment.Append(current);
                if (current != closing) continue;
                if (closing != ']' && index + 1 < sql.Length && sql[index + 1] == closing)
                {
                    segment.Append(sql[++index]);
                    continue;
                }
                break;
            }
            var placeholder = $"\uE000{quoted.Count}\uE001";
            quoted.Add(segment.ToString());
            output.Append(placeholder);
        }
        return output.ToString();
    }

    [GeneratedRegex("(?i)^(CREATE\\s+(?:UNIQUE\\s+)?(?:TABLE|INDEX)\\s+)IF\\s+NOT\\s+EXISTS\\s+")]
    private static partial Regex IfNotExists();
    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex("\\s*([(),])\\s*")]
    private static partial Regex PunctuationWhitespace();
    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex SqlWord();
}
