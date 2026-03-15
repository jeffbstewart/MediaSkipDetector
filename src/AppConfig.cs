namespace MediaSkipDetector;

public record AppConfig(string MediaRoot, string DataDir, string? FpcalcPath)
{
    // ── Analysis tuning parameters ──
    // All have sensible defaults from Jellyfin intro-skipper. See docs/TUNING.md.

    /// <summary>Max differing bits (of 32) between two fingerprint points to consider them a match.</summary>
    public int MaxFingerprintPointDifferences { get; init; } = 6;

    /// <summary>Max gap (seconds) between matching points before breaking contiguity.</summary>
    public double MaxTimeSkip { get; init; } = 3.5;

    /// <summary>Fuzzy shift tolerance when searching the inverted index.</summary>
    public int InvertedIndexShift { get; init; } = 2;

    /// <summary>Shortest valid intro segment (seconds).</summary>
    public int MinIntroDuration { get; init; } = 15;

    /// <summary>Longest valid intro segment (seconds).</summary>
    public int MaxIntroDuration { get; init; } = 120;

    /// <summary>Max other episodes to compare each episode against.</summary>
    public int MaxComparisonCandidates { get; init; } = 7;

    /// <summary>Seconds of audio to fingerprint per file.</summary>
    public int FingerprintLengthSeconds { get; init; } = 600;
}
