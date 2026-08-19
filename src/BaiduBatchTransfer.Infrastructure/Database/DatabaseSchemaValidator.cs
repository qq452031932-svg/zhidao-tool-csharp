using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed record DatabaseSchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed partial class DatabaseSchemaValidator
{
    public async Task<DatabaseSchemaValidationResult> ValidateAsync(SqliteConnection connection, int expectedVersion, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type IN ('table','index') AND sql IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) actual[reader.GetString(0)] = Normalize(reader.GetString(1));
        }
        if (!actual.ContainsKey("SchemaVersions")) return new(false, ["SchemaVersions is missing."]);
        await using (var version = connection.CreateCommand())
        {
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
        if (expectedVersion != 1) return new(false, [.. errors, "Unsupported schema version."]);

        foreach (var (name, sql) in LoadExpectedV1())
        {
            if (!actual.TryGetValue(name, out var actualSql)) errors.Add($"Required schema object {name} is missing.");
            else if (!string.Equals(sql, actualSql, StringComparison.Ordinal)) errors.Add($"Schema object {name} does not match the v1 fingerprint.");
        }
        return new(errors.Count == 0, errors);
    }

    private static IReadOnlyDictionary<string, string> LoadExpectedV1()
    {
        var assembly = typeof(DatabaseSchemaValidator).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(x => x.EndsWith("Database.Migrations.001_InitialSchema.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Missing v1 migration.");
        using var reader = new StreamReader(stream); var text = reader.ReadToEnd();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = CreateObject().Match(statement);
            if (match.Success) result[match.Groups[1].Value] = Normalize(statement);
        }
        return result;
    }

    private static string Normalize(string sql)
    {
        var value = IfNotExists().Replace(sql, string.Empty);
        value = Whitespace().Replace(value, " ").Trim().TrimEnd(';').ToUpperInvariant();
        return PunctuationWhitespace().Replace(value, "$1");
    }

    [GeneratedRegex("(?i)^CREATE\\s+(?:UNIQUE\\s+)?(?:TABLE|INDEX)\\s+(?:IF\\s+NOT\\s+EXISTS\\s+)?([A-Za-z0-9_]+)")]
    private static partial Regex CreateObject();
    [GeneratedRegex("(?i)IF\\s+NOT\\s+EXISTS\\s+")]
    private static partial Regex IfNotExists();
    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex("\\s*([(),])\\s*")]
    private static partial Regex PunctuationWhitespace();
}
