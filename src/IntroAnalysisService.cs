using System.Text.Json;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Data.Sqlite;

namespace MediaSkipDetector;

/// <summary>
/// Result of analyzing a bundle's fingerprints for intro segments.
/// </summary>
public record AnalysisResult(int EpisodesWithIntros, int TotalComparisons, string? OutputFilePath);

/// <summary>
/// Compares fingerprints across episodes in a directory to find shared intro sequences,
/// using the vendored intro-skipper ChromaprintAnalyzer algorithm.
/// </summary>
public interface IIntroAnalysisService
{
    /// <summary>
    /// Analyzes all fingerprinted episodes in a bundle, comparing pairwise to find intros.
    /// Writes results to skip_segment table and outputs a .skip.json file.
    /// </summary>
    AnalysisResult AnalyzeBundle(ScanCandidate candidate);
}

public class IntroAnalysisService : IIntroAnalysisService
{
    private readonly SqliteConnection _connection;
    private readonly IFingerprintCache _fingerprintCache;
    private readonly AppConfig _appConfig;
    private readonly IClock _clock;
    private readonly ILogger<IntroAnalysisService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public IntroAnalysisService(
        SqliteConnection connection,
        IFingerprintCache fingerprintCache,
        AppConfig appConfig,
        IClock clock,
        ILogger<IntroAnalysisService> logger,
        ILoggerFactory loggerFactory)
    {
        _connection = connection;
        _fingerprintCache = fingerprintCache;
        _appConfig = appConfig;
        _clock = clock;
        _logger = logger;
        _loggerFactory = loggerFactory;

        // Initialize the Plugin shim with algorithm parameters from AppConfig
        IntroSkipper.Plugin.Instance ??= new IntroSkipper.Plugin
        {
            Configuration = new PluginConfiguration
            {
                MaximumFingerprintPointDifferences = appConfig.MaxFingerprintPointDifferences,
                MaximumTimeSkip = appConfig.MaxTimeSkip,
                InvertedIndexShift = appConfig.InvertedIndexShift,
                MinimumIntroDuration = appConfig.MinIntroDuration,
                MaximumIntroDuration = appConfig.MaxIntroDuration,
            }
        };
    }

    public AnalysisResult AnalyzeBundle(ScanCandidate candidate)
    {
        var directoryPath = candidate.Directory.FullName;

        // Step 1: Load fingerprints from cache for all MKVs
        var episodes = new List<(string FileName, string RelativePath, uint[] Fingerprint, double Duration)>();

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
                continue;
            }

            var cached = _fingerprintCache.Get(relativePath, fileInfo.Length, fileInfo.LastWriteTimeUtc);
            if (cached == null)
            {
                _logger.LogWarning("No cached fingerprint for {File}, skipping", fileName);
                continue;
            }

            episodes.Add((fileName, relativePath, cached.Fingerprint, cached.DurationSeconds));
        }

        if (episodes.Count < 2)
        {
            _logger.LogInformation("Only {Count} fingerprinted episodes in {Dir}, need at least 2 for comparison",
                episodes.Count, candidate.Directory.Name);
            return new AnalysisResult(0, 0, null);
        }

        // Step 2: Compare episodes using ChromaprintAnalyzer.
        // For each episode, pick up to MaxComparisonCandidates other episodes to compare
        // against, spread across the season via hashing (not just nearest neighbors).
        // Stop comparing an episode once a valid match is found.
        var maxCandidates = _appConfig.MaxComparisonCandidates;

        var analyzer = new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>());
        var introSegments = new Dictionary<string, (double Start, double End)>();
        var totalComparisons = 0;

        // Assign stable GUIDs based on relative path (for analyzer's inverted index cache)
        var episodeGuids = episodes.ToDictionary(
            e => e.RelativePath,
            e => GuidFromPath(e.RelativePath));

        for (var i = 0; i < episodes.Count; i++)
        {
            // Skip if this episode already has a detected intro (from being the rhs of a prior match)
            if (introSegments.ContainsKey(episodes[i].RelativePath))
                continue;

            // Build candidate list: hash (thisFile, otherFile) to spread picks across the season
            var candidates = GetComparisonCandidates(episodes, i, maxCandidates);

            foreach (var j in candidates)
            {
                var lhs = episodes[i];
                var rhs = episodes[j];
                totalComparisons++;

                var (lhsSegment, rhsSegment) = analyzer.CompareEpisodes(
                    episodeGuids[lhs.RelativePath], lhs.Fingerprint,
                    episodeGuids[rhs.RelativePath], rhs.Fingerprint);

                if (!lhsSegment.Valid)
                    continue;

                // Valid match — record both sides
                introSegments[lhs.RelativePath] = (lhsSegment.Start, lhsSegment.End);

                if (rhsSegment.Valid)
                    introSegments.TryAdd(rhs.RelativePath, (rhsSegment.Start, rhsSegment.End));

                break; // First valid match is sufficient for this episode
            }
        }

        _logger.LogInformation("Compared {Comparisons} pairs, found intros in {Count}/{Total} episodes",
            totalComparisons, introSegments.Count, episodes.Count);

        if (introSegments.Count == 0)
            return new AnalysisResult(0, totalComparisons, null);

        // Step 3: Write to skip_segment table
        var now = _clock.Now.ToString("O");
        WriteSkipSegments(introSegments, now);

        // Step 4: Write .skip.json file
        var outputPath = WriteSkipJson(candidate, introSegments, episodes);

        return new AnalysisResult(introSegments.Count, totalComparisons, outputPath);
    }

    private void WriteSkipSegments(Dictionary<string, (double Start, double End)> segments, string now)
    {
        using var transaction = _connection.BeginTransaction();

        foreach (var (relativePath, (start, end)) in segments)
        {
            // Delete existing intro segments for this episode
            using var deleteCmd = _connection.CreateCommand();
            deleteCmd.CommandText = """
                DELETE FROM skip_segment
                WHERE episode_path = @path AND region_type = 'INTRO';
                """;
            deleteCmd.Parameters.AddWithValue("@path", relativePath);
            deleteCmd.ExecuteNonQuery();

            // Insert new segment
            using var insertCmd = _connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO skip_segment (episode_path, region_type, start_seconds, end_seconds, confidence, computed_at)
                VALUES (@path, 'INTRO', @start, @end, NULL, @now);
                """;
            insertCmd.Parameters.AddWithValue("@path", relativePath);
            insertCmd.Parameters.AddWithValue("@start", start);
            insertCmd.Parameters.AddWithValue("@end", end);
            insertCmd.Parameters.AddWithValue("@now", now);
            insertCmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private string WriteSkipJson(
        ScanCandidate candidate,
        Dictionary<string, (double Start, double End)> segments,
        List<(string FileName, string RelativePath, uint[] Fingerprint, double Duration)> episodes)
    {
        // Build per-episode JSON entries
        var entries = new List<object>();

        foreach (var episode in episodes)
        {
            if (!segments.TryGetValue(episode.RelativePath, out var intro))
                continue;

            entries.Add(new
            {
                file = episode.FileName,
                start = Math.Round(intro.Start, 2),
                end = Math.Round(intro.End, 2),
                region_type = "INTRO"
            });
        }

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        var outputPath = Path.Combine(candidate.Directory.FullName, candidate.OutputFileName);
        File.WriteAllText(outputPath, json);

        return outputPath;
    }

    /// <summary>
    /// Selects up to maxCandidates other episodes to compare against, spread across the
    /// season via hashing. For episode i, we hash (fileName_i, fileName_j) for each j != i,
    /// sort by hash, and take the first maxCandidates indices. This gives each episode a
    /// deterministic but well-distributed set of comparison partners rather than always
    /// comparing nearest neighbors.
    /// </summary>
    private static List<int> GetComparisonCandidates(
        List<(string FileName, string RelativePath, uint[] Fingerprint, double Duration)> episodes,
        int sourceIndex,
        int maxCandidates)
    {
        var sourceName = episodes[sourceIndex].FileName;

        return Enumerable.Range(0, episodes.Count)
            .Where(j => j != sourceIndex)
            .OrderBy(j => HashPair(sourceName, episodes[j].FileName))
            .Take(maxCandidates)
            .ToList();
    }

    /// <summary>
    /// Deterministic hash of two filenames for candidate ordering.
    /// </summary>
    private static uint HashPair(string a, string b)
    {
        var hash = (uint)2166136261;
        foreach (var c in a) { hash ^= c; hash *= 16777619; }
        hash ^= 0xFF; // separator
        foreach (var c in b) { hash ^= c; hash *= 16777619; }
        return hash;
    }

    /// <summary>
    /// Creates a deterministic GUID from a relative path string.
    /// ChromaprintAnalyzer uses GUIDs as episode identifiers for its inverted index cache.
    /// </summary>
    private static Guid GuidFromPath(string path)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(path));
        return new Guid(bytes);
    }
}
