using System.Reflection;
using DbUp;
using DbUp.Engine.Output;
using Microsoft.Data.Sqlite;

namespace MediaSkipDetector;

/// <summary>
/// Centralizes database startup: ensures data directory exists, runs DbUp migrations,
/// and owns the long-lived SQLite connection used by all services.
/// </summary>
public class DatabaseInitializer : IDisposable
{
    public SqliteConnection Connection { get; }
    public string DbPath { get; }

    public DatabaseInitializer(string dataDir, ILogger<DatabaseInitializer> logger)
    {
        Directory.CreateDirectory(dataDir);
        DbPath = Path.Combine(dataDir, "skipdetector.db");
        var connectionString = $"Data Source={DbPath}";

        // Run DbUp migrations
        var upgrader = DeployChanges.To
            .SqliteDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .LogTo(new MsLogAdapter(logger))
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
            throw result.Error;

        var appliedScripts = result.Scripts.ToList();
        if (appliedScripts.Count > 0)
            logger.LogInformation("Applied {Count} migration(s): {Scripts}",
                appliedScripts.Count,
                string.Join(", ", appliedScripts.Select(s => s.Name)));
        else
            logger.LogInformation("Database up to date (0 migrations applied)");

        // Open long-lived connection with WAL mode
        Connection = new SqliteConnection(connectionString);
        Connection.Open();

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();

        logger.LogInformation("Database opened: {DbPath}", DbPath);
    }

    /// <summary>
    /// Reads a DateTime value from the metadata table, or null if the key doesn't exist.
    /// Used by DirectoryScanner to invalidate old .skip.json files after migrations.
    /// </summary>
    public DateTime? GetMetadataDateTime(string key)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        var result = cmd.ExecuteScalar();
        if (result is string s && DateTime.TryParse(s, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt;
        return null;
    }

    public void Dispose()
    {
        Connection.Dispose();
    }

    /// <summary>
    /// Adapts DbUp's IUpgradeLog to Microsoft.Extensions.Logging.
    /// </summary>
    private class MsLogAdapter : IUpgradeLog
    {
        private readonly ILogger _logger;
        public MsLogAdapter(ILogger logger) => _logger = logger;
        public void LogDebug(string format, params object[] args) => _logger.LogDebug(format, args);
        public void LogInformation(string format, params object[] args) => _logger.LogInformation(format, args);
        public void LogWarning(string format, params object[] args) => _logger.LogWarning(format, args);
        public void LogError(string format, params object[] args) => _logger.LogError(format, args);
        public void LogError(Exception ex, string format, params object[] args) => _logger.LogError(ex, format, args);
        public void LogTrace(string format, params object[] args) => _logger.LogTrace(format, args);
    }
}
