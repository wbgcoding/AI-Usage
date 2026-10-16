using Microsoft.Data.Sqlite;

namespace AiUsage.Providers.Parsing;

/// <summary>Opens a database file that belongs to another tool.</summary>
internal static class ForeignDatabase
{
    /// <summary>Opens the file read-only, with no pooled connection, a one second lock wait, and with
    /// nothing the file declares (generated columns, views, triggers) allowed to run with the app's
    /// rights.</summary>
    internal static SqliteConnection Open(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA trusted_schema = OFF";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
