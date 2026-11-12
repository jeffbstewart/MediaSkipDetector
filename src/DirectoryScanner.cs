using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaSkipDetector;

/// <summary>
/// A TV season directory that needs intro detection processing.
/// </summary>
/// <param name="Directory">The directory containing the MKV episodes.</param>
/// <param name="MkvFileNames">Bare filenames of qualifying MKVs (no directory prefix).</param>
/// <param name="NewestMkvTimestamp">Timestamp of the most recently modified MKV in this directory.</param>
public record ScanCandidate(
    DirectoryInfo Directory,
    List<string> MkvFileNames,
    DateTime NewestMkvTimestamp
);

/// <summary>
/// Results of a directory scan.
/// </summary>
/// <param name="Pending">Directories that need processing.</param>
/// <param name="TotalDirectories">All qualifying directories found.</param>
/// <param name="UpToDate">Directories already processed and current.</param>
public record ScanResult(
    List<ScanCandidate> Pending,
    int TotalDirectories,
    int UpToDate
);

/// <summary>
/// Discovers TV season directories on the NAS that contain episodes needing intro detection.
/// A qualifying directory has 2+ files matching .*S\d+E\d+.*\.mkv (case-insensitive).
/// </summary>
public class DirectoryScanner(string mediaRoot, DateTime? invalidateSkipBefore, ILogger<DirectoryScanner> logger)
{
    private static readonly Regex EpisodePattern =
        new(@"S\d+E\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ScanResult Scan()
    {
        var rootDir = new DirectoryInfo(mediaRoot);
        if (!rootDir.Exists)
        {
            logger.LogWarning("MEDIA_ROOT does not exist: {Path}", mediaRoot);
            return new ScanResult([], 0, 0);
        }

        logger.LogInformation("Starting directory scan of {Path}", mediaRoot);

        var pending = new List<ScanCandidate>();
        var totalDirectories = 0;
        var upToDate = 0;
        var dirsScanned = 0;

        foreach (var dir in EnumerateDirectoriesSafe(rootDir))
        {
            dirsScanned++;
            if (dirsScanned % 100 == 0)
                logger.LogInformation("Scanning: examined {Count} directories so far...", dirsScanned);

            var mkvFiles = GetQualifyingMkvFiles(dir);
            if (mkvFiles.Count < 2)
                continue;

            totalDirectories++;

            var newestTimestamp = mkvFiles.Max(f => f.LastWriteTime);
            var sortedNames = mkvFiles.OrderBy(f => f.Name).ToList();

            // Up-to-date if any *.introskip.skip.json exists and is newer than the newest MKV.
            // But first, delete any combined files (multiple distinct "file" values) —
            // these are leftover from a bug and must not count as up-to-date.
            var skipFiles = dir.GetFiles("*.introskip.skip.json");
            foreach (var sf in skipFiles)
            {
                if (IsCombinedSkipFile(sf))
                {
                    logger.LogInformation("Deleting combined skip file: {File}", sf.FullName);
                    sf.Delete();
                }
            }

            var newestSkipFile = dir.GetFiles("*.introskip.skip.json")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (newestSkipFile != null && newestSkipFile.LastWriteTime >= newestTimestamp
                && (invalidateSkipBefore == null || newestSkipFile.LastWriteTimeUtc >= invalidateSkipBefore))
            {
                upToDate++;
                continue;
            }

            var candidate = new ScanCandidate(
                dir,
                sortedNames.Select(f => f.Name).ToList(),
                newestTimestamp
            );
            pending.Add(candidate);
        }

        logger.LogInformation(
            "Scan complete: {Found} directories, {UpToDate} up to date, {Pending} pending",
            totalDirectories, upToDate, pending.Count);

        return new ScanResult(pending, totalDirectories, upToDate);
    }

    private List<FileInfo> GetQualifyingMkvFiles(DirectoryInfo dir)
    {
        try
        {
            return dir.GetFiles("*.mkv")
                .Where(f => EpisodePattern.IsMatch(f.Name))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning("Cannot access directory {Path}: {Error}", dir.FullName, ex.Message);
            return [];
        }
    }

    private IEnumerable<DirectoryInfo> EnumerateDirectoriesSafe(DirectoryInfo root)
    {
        // Include root itself
        yield return root;

        IEnumerable<DirectoryInfo> children;
        try
        {
            children = root.EnumerateDirectories();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning("Cannot enumerate {Path}: {Error}", root.FullName, ex.Message);
            yield break;
        }

        foreach (var child in children)
        {
            // Skip .mm-ignore directories
            if (File.Exists(Path.Combine(child.FullName, ".mm-ignore")))
                continue;

            foreach (var descendant in EnumerateDirectoriesSafe(child))
                yield return descendant;
        }
    }

    /// <summary>
    /// Returns true if a skip.json file contains segments for multiple distinct episodes
    /// (the "file" field). These are leftover from a bug that combined all episodes into one file.
    /// </summary>
    private static bool IsCombinedSkipFile(FileInfo file)
    {
        try
        {
            var text = File.ReadAllText(file.FullName);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            var distinctFiles = new HashSet<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("file", out var fileProp) &&
                    fileProp.ValueKind == JsonValueKind.String)
                {
                    distinctFiles.Add(fileProp.GetString()!);
                }
            }

            return distinctFiles.Count > 1;
        }
        catch
        {
            return false;
        }
    }
}
