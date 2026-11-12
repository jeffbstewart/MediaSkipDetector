using Microsoft.Data.Sqlite;

namespace MediaSkipDetector;

/// <summary>
/// A cached fingerprint result for an audio file.
/// </summary>
public record CachedFingerprint(string RelativePath, uint[] Fingerprint, double DurationSeconds);

/// <summary>
/// Persistent cache for Chromaprint audio fingerprints, backed by SQLite.
/// </summary>
public interface IFingerprintCache
{
    /// <summary>
    /// Retrieves a cached fingerprint if it exists and is fresh (file size and modification time match).
    /// Returns null if not cached or stale.
    /// </summary>
    CachedFingerprint? Get(string relativePath, long fileSize, DateTime lastModifiedUtc,
        string fingerprintType = "INTRO");

    /// <summary>
    /// Stores or replaces a fingerprint in the cache.
    /// </summary>
    void Put(string relativePath, long fileSize, DateTime lastModifiedUtc,
        uint[] fingerprint, double durationSeconds, string fingerprintType = "INTRO");

    /// <summary>
    /// Removes entries whose relative paths no longer exist on disk.
    /// The predicate returns true if the relative path still exists.
    /// </summary>
    int Prune(Func<string, bool> relativePathExists);

    /// <summary>
    /// Number of entries in the cache.
    /// </summary>
    int Count { get; }
}

public class FingerprintCache : IFingerprintCache
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<FingerprintCache> _logger;

    public FingerprintCache(SqliteConnection connection, ILogger<FingerprintCache> logger)
    {
        _connection = connection;
        _logger = logger;
        _logger.LogInformation("Fingerprint cache ready ({Count} entries)", Count);
    }

    public CachedFingerprint? Get(string relativePath, long fileSize, DateTime lastModifiedUtc,
        string fingerprintType = "INTRO")
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT file_size, last_modified, fingerprint, duration_seconds
            FROM fingerprint_cache
            WHERE relative_path = @path AND fingerprint_type = @type;
            """;
        cmd.Parameters.AddWithValue("@path", relativePath);
        cmd.Parameters.AddWithValue("@type", fingerprintType);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var cachedSize = reader.GetInt64(0);
        var cachedModified = reader.GetString(1);
        var expectedModified = lastModifiedUtc.ToString("O");

        // Freshness check: size and modification time must match
        if (cachedSize != fileSize || cachedModified != expectedModified)
            return null;

        var blob = (byte[])reader[2];
        var fingerprint = BlobToUintArray(blob);
        var duration = reader.GetDouble(3);

        return new CachedFingerprint(relativePath, fingerprint, duration);
    }

    public void Put(string relativePath, long fileSize, DateTime lastModifiedUtc,
        uint[] fingerprint, double durationSeconds, string fingerprintType = "INTRO")
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO fingerprint_cache
                (relative_path, fingerprint_type, file_size, last_modified, fingerprint, duration_seconds)
            VALUES
                (@path, @type, @size, @modified, @fingerprint, @duration);
            """;
        cmd.Parameters.AddWithValue("@path", relativePath);
        cmd.Parameters.AddWithValue("@type", fingerprintType);
        cmd.Parameters.AddWithValue("@size", fileSize);
        cmd.Parameters.AddWithValue("@modified", lastModifiedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@fingerprint", UintArrayToBlob(fingerprint));
        cmd.Parameters.AddWithValue("@duration", durationSeconds);
        cmd.ExecuteNonQuery();
    }

    public int Prune(Func<string, bool> relativePathExists)
    {
        // Read all paths first
        var pathsToDelete = new List<string>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT relative_path FROM fingerprint_cache;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                if (!relativePathExists(path))
                    pathsToDelete.Add(path);
            }
        }

        if (pathsToDelete.Count == 0)
            return 0;

        // Batch delete in a transaction
        using var transaction = _connection.BeginTransaction();
        using var deleteCmd = _connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM fingerprint_cache WHERE relative_path = @path;";
        var param = deleteCmd.Parameters.Add("@path", SqliteType.Text);

        foreach (var path in pathsToDelete)
        {
            param.Value = path;
            deleteCmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return pathsToDelete.Count;
    }

    public int Count
    {
        get
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM fingerprint_cache;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // ── Serialization helpers ──────────────────────────────────────────

    internal static byte[] UintArrayToBlob(uint[] data)
    {
        var blob = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, blob, 0, blob.Length);
        return blob;
    }

    internal static uint[] BlobToUintArray(byte[] blob)
    {
        var result = new uint[blob.Length / 4];
        Buffer.BlockCopy(blob, 0, result, 0, blob.Length);
        return result;
    }

    /// <summary>
    /// Converts an absolute file path to a portable relative path using forward slashes.
    /// </summary>
    public static string ToRelativePath(string mediaRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(mediaRoot, fullPath);
        return relative.Replace('\\', '/');
    }
}
