// Minimal shims for Jellyfin types referenced by vendored intro-skipper code.
// These satisfy the compiler without pulling in the Jellyfin SDK.
// See docs/REBASING.md for context.

using IntroSkipper.Configuration;
using IntroSkipper.Data;

namespace MediaBrowser.Model.Plugins
{
    /// <summary>
    /// Empty base class replacing Jellyfin's BasePluginConfiguration.
    /// PluginConfiguration inherits from this in the vendored code.
    /// </summary>
    public class BasePluginConfiguration { }
}

namespace IntroSkipper.Analyzers
{
    /// <summary>
    /// Shim for the Jellyfin plugin analyzer interface.
    /// ChromaprintAnalyzer implements this; we don't call AnalyzeMediaFiles
    /// (we call CompareEpisodes directly), but the class declaration requires it.
    /// </summary>
    public interface IMediaFileAnalyzer
    {
        Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
            IReadOnlyList<QueuedEpisode> analysisQueue,
            AnalysisMode mode,
            CancellationToken cancellationToken);
    }
}

namespace MediaBrowser.Model.Entities
{
    /// <summary>
    /// Stub for Jellyfin's ChapterInfo, referenced by TimeAdjustmentHelper.
    /// </summary>
    public class ChapterInfo
    {
        public long StartPositionTicks { get; set; }
    }
}

namespace IntroSkipper
{
    /// <summary>
    /// Shim for intro-skipper's Plugin singleton.
    /// ChromaprintAnalyzer reads Plugin.Instance.Configuration for algorithm parameters
    /// and calls Plugin.Instance.UpdateTimestampAsync to save results.
    /// We provide a minimal implementation that returns our config and discards updates.
    /// </summary>
    public class Plugin
    {
        /// <summary>
        /// Singleton instance. Must be set before ChromaprintAnalyzer is used.
        /// </summary>
        public static Plugin? Instance { get; set; }

        /// <summary>
        /// Algorithm configuration (thresholds, durations, etc.).
        /// </summary>
        public PluginConfiguration Configuration { get; set; } = new();

        /// <summary>
        /// No-op: ChromaprintAnalyzer calls this to persist results in Jellyfin's DB.
        /// We handle result storage separately in our own pipeline.
        /// </summary>
        public Task UpdateTimestampAsync(Segment segment, AnalysisMode mode, CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <summary>
        /// Stub for TimeAdjustmentHelper chapter lookup. Returns empty list.
        /// </summary>
        public IReadOnlyList<MediaBrowser.Model.Entities.ChapterInfo> GetChapters(Guid episodeId) => [];
    }

    /// <summary>
    /// Shim for intro-skipper's FFmpegWrapper.
    /// ChromaprintAnalyzer.AnalyzeMediaFiles calls these to generate fingerprints.
    /// We don't use AnalyzeMediaFiles (we call CompareEpisodes with pre-computed
    /// fingerprints), so these are never invoked at runtime.
    /// </summary>
    public static class FFmpegWrapper
    {
        public static uint[] Fingerprint(QueuedEpisode episode, AnalysisMode mode)
            => throw new NotSupportedException("Use FpcalcService instead of FFmpegWrapper");

        public static string GetFingerprintCachePath(QueuedEpisode episode, AnalysisMode mode)
            => throw new NotSupportedException("Use FingerprintCache instead of FFmpegWrapper");

        public static TimeRange[]? DetectSilence(QueuedEpisode episode, TimeRange searchRange)
            => throw new NotSupportedException("Silence detection not supported outside Jellyfin");

        public static List<double> DetectKeyFrames(QueuedEpisode episode, TimeRange searchRange)
            => throw new NotSupportedException("Keyframe detection not supported outside Jellyfin");
    }
}
