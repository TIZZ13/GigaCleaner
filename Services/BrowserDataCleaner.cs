using System.IO;
using Microsoft.Data.Sqlite;

namespace GigaCleaner.Services;

/// <summary>Правка баз Chromium через SQLite: cookies с белым списком и таблицы автозаполнения.</summary>
public static class BrowserDataCleaner
{
    private static readonly string[] AutofillTables =
    [
        "autofill",
        "autofill_profiles",
        "autofill_profile_names",
        "autofill_profile_emails",
        "autofill_profile_phones",
        "autofill_profiles_truncated",
    ];

    public static (long Freed, int Removed, bool Ok) CleanCookies(
        string dbPath, IReadOnlyList<string> domains, CancellationToken ct)
    {
        if (!File.Exists(dbPath)) return (0, 0, true);
        var before = Size.OfFile(dbPath);

        try
        {
            using var connection = Open(dbPath);
            if (!TableExists(connection, "cookies")) return (0, 0, true);

            using var command = connection.CreateCommand();
            command.CommandText = BuildCookieDeleteSql(domains);
            for (var i = 0; i < domains.Count; i++)
            {
                command.Parameters.AddWithValue($"@d{i}", domains[i]);
                command.Parameters.AddWithValue($"@p{i}", "." + domains[i]);
                command.Parameters.AddWithValue($"@s{i}", "%." + domains[i]);
            }

            ct.ThrowIfCancellationRequested();
            var removed = command.ExecuteNonQuery();
            TryVacuum(connection);
            connection.Close();
            return (Math.Max(0, before - Size.OfFile(dbPath)), removed, true);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (0, 0, false);
        }
    }

    public static (long Freed, int Removed, bool Ok) CleanAutofill(string dbPath, CancellationToken ct)
    {
        if (!File.Exists(dbPath)) return (0, 0, true);
        var before = Size.OfFile(dbPath);

        try
        {
            using var connection = Open(dbPath);
            var removed = 0;

            foreach (var table in AutofillTables)
            {
                if (!TableExists(connection, table)) continue;
                using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM \"{table}\";";
                ct.ThrowIfCancellationRequested();
                removed += command.ExecuteNonQuery();
            }

            TryVacuum(connection);
            connection.Close();
            return (Math.Max(0, before - Size.OfFile(dbPath)), removed, true);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (0, 0, false);
        }
    }

    private static string BuildCookieDeleteSql(IReadOnlyList<string> domains)
    {
        if (domains.Count == 0) return "DELETE FROM cookies;";

        var clauses = new List<string>(domains.Count * 3);
        for (var i = 0; i < domains.Count; i++)
        {
            clauses.Add($"host_key = @d{i}");
            clauses.Add($"host_key = @p{i}");
            clauses.Add($"host_key LIKE @s{i}");
        }
        return $"DELETE FROM cookies WHERE NOT ({string.Join(" OR ", clauses)});";
    }

    private static SqliteConnection Open(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        command.Parameters.AddWithValue("@name", table);
        return command.ExecuteScalar() is not null;
    }

    private static void TryVacuum(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM;";
            command.CommandTimeout = 10;
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Освобождение файла не критично: строки уже удалены.
        }
    }
}
