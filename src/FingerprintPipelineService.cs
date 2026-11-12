using Microsoft.Data.Sqlite;

namespace MediaSkipDetector;

public record PrepareResult(long BundleId, int PendingCount, int CachedCount, int FailedCount);
public record WorkItemInfo(long Id, string FileName, string FullPath, string RelativePath);

public interface IFingerprintPipelineService
{
    PrepareResult PrepareBundle(ScanCandidate candidate);
    WorkItemInfo? GetNextPendingItem(long bundleId);
    void CompleteWorkItem(long workItemId);
    void FailWorkItem(long workItemId, string errorMessage);
    bool CheckBundleCompletion(long bundleId);
    void DeleteBundle(long bundleId);
}

public class FingerprintPipelineService : IFingerprintPipelineService
{
    private const int MaxRetries = 3;

    private readonly SqliteConnection _connection;
    private readonly IFingerprintCache _fingerprintCache;
    private readonly AppConfig _appConfig;
    private readonly ILogger<FingerprintPipelineService> _logger;
    private readonly IClock _clock;

    public FingerprintPipelineService(
        SqliteConnection connection,
        IFingerprintCache fingerprintCache,
        AppConfig appConfig,
        IClock clock,
        ILogger<FingerprintPipelineService> logger)
    {
        _connection = connection;
        _fingerprintCache = fingerprintCache;
        _appConfig = appConfig;
        _clock = clock;
        _logger = logger;

        // Enable foreign keys (required for CASCADE deletes)
        using var fkCmd = _connection.CreateCommand();
        fkCmd.CommandText = "PRAGMA foreign_keys = ON;";
        fkCmd.ExecuteNonQuery();
    }

    public PrepareResult PrepareBundle(ScanCandidate candidate)
    {
        var directoryPath = candidate.Directory.FullName;
        var now = _clock.Now.ToString("O");

        // Step 1: Find or create bundle
        var bundleId = GetBundleId(directoryPath);
        string? existingStatus = null;

        if (bundleId.HasValue)
        {
            existingStatus = GetBundleStatus(bundleId.Value);
        }
        else
        {
            bundleId = InsertBundle(directoryPath, candidate.MkvFileNames.Count, now);
            ScanMetrics.BundlesCreated.Inc();
        }

        // Step 2: For each MKV, check cache and create work items as needed
        var cachedCount = 0;
        var newPendingCreated = false;

        foreach (var fileName in candidate.MkvFileNames)
        {
            var fullPath = Path.Combine(directoryPath, fileName);
            var relativePath = FingerprintCache.ToRelativePath(_appConfig.MediaRoot, fullPath);

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Cannot access file {Path}: {Error}", fullPath, ex.Message);
                continue;
            }

            var cached = _fingerprintCache.Get(relativePath, fileInfo.Length, fileInfo.LastWriteTimeUtc);
            if (cached != null)
            {
                cachedCount++;
                ScanMetrics.FingerprintCacheHits.Inc();
                continue;
            }

            ScanMetrics.FingerprintCacheMisses.Inc();

            // Check if a PENDING work item already exists for this file
            if (!WorkItemExists(bundleId.Value, fileName))
            {
                InsertWorkItem(bundleId.Value, fileName, relativePath, now);
                newPendingCreated = true;
            }
        }

        // Step 3: Update total_files on bundle
        UpdateBundleTotalFiles(bundleId.Value, candidate.MkvFileNames.Count);

        // Step 4: Count states
        var pendingCount = CountWorkItems(bundleId.Value, "PENDING");
        var failedCount = CountFailedItems(bundleId.Value);

        // Step 5: Staleness — if bundle was READY/COMPLETE and new PENDING items created, reset
        if (newPendingCreated && existingStatus is "READY" or "COMPLETE")
        {
            _logger.LogInformation("Bundle {Dir} had new files, resetting from {Status} to FINGERPRINTING",
                directoryPath, existingStatus);
            UpdateBundleStatus(bundleId.Value, "FINGERPRINTING");
            ClearBundleCompletedAt(bundleId.Value);
        }

        // Step 6: If zero PENDING remain, check completion
        if (pendingCount == 0)
        {
            CheckBundleCompletion(bundleId.Value);
        }

        return new PrepareResult(bundleId.Value, pendingCount, cachedCount, failedCount);
    }

    public WorkItemInfo? GetNextPendingItem(long bundleId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, file_name, relative_path
            FROM fingerprint_work_item
            WHERE bundle_id = @bundleId AND status = 'PENDING' AND attempt_count < @maxRetries
            ORDER BY id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@bundleId", bundleId);
        cmd.Parameters.AddWithValue("@maxRetries", MaxRetries);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var id = reader.GetInt64(0);
        var fileName = reader.GetString(1);
        var relativePath = reader.GetString(2);

        // Reconstruct full path from bundle directory + file name
        var directoryPath = GetBundleDirectoryPath(bundleId);
        var fullPath = Path.Combine(directoryPath, fileName);

        return new WorkItemInfo(id, fileName, fullPath, relativePath);
    }

    public void CompleteWorkItem(long workItemId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprint_work_item
            SET status = 'COMPLETE', completed_at = @now
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", workItemId);
        cmd.Parameters.AddWithValue("@now", _clock.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void FailWorkItem(long workItemId, string errorMessage)
    {
        // Increment attempt_count. If at max, set FAILED permanently. Otherwise stay PENDING.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprint_work_item
            SET attempt_count = attempt_count + 1,
                error_message = @error,
                status = CASE
                    WHEN attempt_count + 1 >= @maxRetries THEN 'FAILED'
                    ELSE 'PENDING'
                END,
                completed_at = CASE
                    WHEN attempt_count + 1 >= @maxRetries THEN @now
                    ELSE NULL
                END
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", workItemId);
        cmd.Parameters.AddWithValue("@error", errorMessage);
        cmd.Parameters.AddWithValue("@maxRetries", MaxRetries);
        cmd.Parameters.AddWithValue("@now", _clock.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public bool CheckBundleCompletion(long bundleId)
    {
        var pendingCount = CountWorkItems(bundleId, "PENDING");
        if (pendingCount > 0)
            return false;

        var currentStatus = GetBundleStatus(bundleId);
        if (currentStatus == "READY" || currentStatus == "COMPLETE")
            return false; // Already done

        UpdateBundleStatus(bundleId, "READY");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE fingerprint_bundle SET completed_at = @now WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", bundleId);
        cmd.Parameters.AddWithValue("@now", _clock.Now.ToString("O"));
        cmd.ExecuteNonQuery();

        ScanMetrics.BundlesReady.Inc();
        return true;
    }

    public void DeleteBundle(long bundleId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM fingerprint_bundle WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", bundleId);
        cmd.ExecuteNonQuery();
    }

    // ── Private helpers ──────────────────────────────────────────────

    private long? GetBundleId(string directoryPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM fingerprint_bundle WHERE directory_path = @path;";
        cmd.Parameters.AddWithValue("@path", directoryPath);
        var result = cmd.ExecuteScalar();
        return result == null ? null : (long)result;
    }

    private string GetBundleStatus(long bundleId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM fingerprint_bundle WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", bundleId);
        return (string)cmd.ExecuteScalar()!;
    }

    private string GetBundleDirectoryPath(long bundleId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT directory_path FROM fingerprint_bundle WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", bundleId);
        return (string)cmd.ExecuteScalar()!;
    }

    private long InsertBundle(string directoryPath, int totalFiles, string now)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprint_bundle (directory_path, total_files, status, created_at)
            VALUES (@path, @total, 'FINGERPRINTING', @now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@path", directoryPath);
        cmd.Parameters.AddWithValue("@total", totalFiles);
        cmd.Parameters.AddWithValue("@now", now);
        return (long)cmd.ExecuteScalar()!;
    }

    private void UpdateBundleTotalFiles(long bundleId, int totalFiles)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE fingerprint_bundle SET total_files = @total WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", bundleId);
        cmd.Parameters.AddWithValue("@total", totalFiles);
        cmd.ExecuteNonQuery();
    }

    private void UpdateBundleStatus(long bundleId, string status)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE fingerprint_bundle SET status = @status WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", bundleId);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.ExecuteNonQuery();
    }

    private void ClearBundleCompletedAt(long bundleId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE fingerprint_bundle SET completed_at = NULL WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", bundleId);
        cmd.ExecuteNonQuery();
    }

    private bool WorkItemExists(long bundleId, string fileName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM fingerprint_work_item
            WHERE bundle_id = @bundleId AND file_name = @fileName AND status = 'PENDING';
            """;
        cmd.Parameters.AddWithValue("@bundleId", bundleId);
        cmd.Parameters.AddWithValue("@fileName", fileName);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private void InsertWorkItem(long bundleId, string fileName, string relativePath, string now)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprint_work_item (bundle_id, file_name, relative_path, status, attempt_count, created_at)
            VALUES (@bundleId, @fileName, @relativePath, 'PENDING', 0, @now);
            """;
        cmd.Parameters.AddWithValue("@bundleId", bundleId);
        cmd.Parameters.AddWithValue("@fileName", fileName);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    private int CountWorkItems(long bundleId, string status)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM fingerprint_work_item
            WHERE bundle_id = @bundleId AND status = @status;
            """;
        cmd.Parameters.AddWithValue("@bundleId", bundleId);
        cmd.Parameters.AddWithValue("@status", status);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int CountFailedItems(long bundleId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM fingerprint_work_item
            WHERE bundle_id = @bundleId AND status = 'FAILED' AND attempt_count >= @maxRetries;
            """;
        cmd.Parameters.AddWithValue("@bundleId", bundleId);
        cmd.Parameters.AddWithValue("@maxRetries", MaxRetries);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
