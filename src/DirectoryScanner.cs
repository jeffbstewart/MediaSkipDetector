using System.Text.RegularExpressions;

namespace MediaSkipDetector;

/// <summary>
/// A TV season directory that needs intro detection processing.
/// </summary>
/// <param name="Directory">The directory containing the MKV episodes.</param>
/// <param name="OutputFileName">The output filename (name only, no path): {first_mkv_basename}.introskip.skip.json</param>
/// <param name="MkvFileNames">Bare filenames of qualifying MKVs (no directory prefix).</param>
/// <param name="NewestMkvTimestamp">Timestamp of the most recently modified MKV in this directory.</param>
public record ScanCandidate(
    DirectoryInfo Directory,
    string OutputFileName,
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
public class DirectoryScanner(string mediaRoot, ILogger<DirectoryScanner> logger)
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
            var outputFileName = Path.GetFileNameWithoutExtension(sortedNames[0].Name) + ".introskip.skip.json";
            var outputFile = new FileInfo(Path.Combine(dir.FullName, outputFileName));

            if (outputFile.Exists && outputFile.LastWriteTime >= newestTimestamp)
            {
                upToDate++;
                continue;
            }

            var candidate = new ScanCandidate(
                dir,
                outputFileName,
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
}
