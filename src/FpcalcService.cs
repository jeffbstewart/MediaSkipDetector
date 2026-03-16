using System.Diagnostics;
using System.Text.Json;

namespace MediaSkipDetector;

/// <summary>
/// Result of running fpcalc on a single file.
/// </summary>
public record FpcalcResult(uint[] Fingerprint, double DurationSeconds);

/// <summary>
/// Invokes the fpcalc (Chromaprint) CLI tool to extract audio fingerprints.
/// </summary>
public interface IFpcalcService
{
    /// <summary>
    /// Whether fpcalc is available (path configured and binary exists).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Whether ffmpeg is available (needed for credits tail extraction).
    /// </summary>
    bool IsFfmpegAvailable { get; }

    /// <summary>
    /// Runs fpcalc on the given file and returns the raw fingerprint + duration.
    /// Throws on fpcalc error or timeout.
    /// </summary>
    FpcalcResult Run(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Extracts the last tailSeconds of audio via ffmpeg, fingerprints it with fpcalc.
    /// Returns FpcalcResult where DurationSeconds is the full episode duration (from ffprobe).
    /// </summary>
    FpcalcResult RunTail(string filePath, int tailSeconds, CancellationToken ct = default);
}

public class FpcalcService : IFpcalcService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly string? _fpcalcPath;
    private readonly string? _ffmpegPath;
    private readonly string? _ffprobePath;
    private readonly int _fingerprintLengthSeconds;
    private readonly ILogger<FpcalcService> _logger;

    public FpcalcService(AppConfig appConfig, ILogger<FpcalcService> logger)
    {
        _logger = logger;
        _fpcalcPath = ResolvePath(appConfig.FpcalcPath, "fpcalc");
        _fingerprintLengthSeconds = appConfig.FingerprintLengthSeconds;

        _ffmpegPath = ResolvePath(appConfig.FfmpegPath, "ffmpeg");
        _ffprobePath = _ffmpegPath != null
            ? ResolveSibling(_ffmpegPath, "ffprobe")
            : ResolvePath(null, "ffprobe");

        if (_fpcalcPath != null)
            _logger.LogInformation("fpcalc available at {Path}", _fpcalcPath);
        else
            _logger.LogWarning("fpcalc not available — set FPCALC_PATH environment variable");

        if (_ffmpegPath != null)
            _logger.LogInformation("ffmpeg available at {Path}", _ffmpegPath);
        else
            _logger.LogWarning("ffmpeg not available — credits detection disabled. Set FFMPEG_PATH environment variable.");

        if (_ffprobePath != null)
            _logger.LogInformation("ffprobe available at {Path}", _ffprobePath);
    }

    public bool IsAvailable => _fpcalcPath != null;
    public bool IsFfmpegAvailable => _ffmpegPath != null && _ffprobePath != null;

    public FpcalcResult Run(string filePath, CancellationToken ct = default)
        => RunFpcalc(filePath, _fingerprintLengthSeconds, ct);

    public FpcalcResult RunTail(string filePath, int tailSeconds, CancellationToken ct = default)
    {
        if (_fpcalcPath == null)
            throw new InvalidOperationException("fpcalc path not configured — set FPCALC_PATH");
        if (_ffmpegPath == null || _ffprobePath == null)
            throw new InvalidOperationException("ffmpeg/ffprobe not configured — set FFMPEG_PATH");

        // 1. Get full file duration via ffprobe
        var fullDuration = GetFileDuration(filePath, ct);

        // 2. Compute extraction offset
        var offset = Math.Max(0, fullDuration - tailSeconds);
        var actualTailLength = (int)Math.Ceiling(Math.Min(tailSeconds, fullDuration));

        // 3. Extract tail audio to temp WAV file
        var tempWav = Path.Combine(Path.GetTempPath(), $"msd-tail-{Guid.NewGuid():N}.wav");
        try
        {
            ExtractAudio(filePath, offset, actualTailLength, tempWav, ct);

            // 4. Run fpcalc on the temp WAV
            var result = RunFpcalc(tempWav, actualTailLength, ct);

            // Return with full episode duration (needed for credits timestamp conversion)
            return new FpcalcResult(result.Fingerprint, fullDuration);
        }
        finally
        {
            try { File.Delete(tempWav); } catch { /* best effort */ }
        }
    }

    private FpcalcResult RunFpcalc(string filePath, int lengthSeconds, CancellationToken ct)
    {
        if (_fpcalcPath == null)
            throw new InvalidOperationException("fpcalc path not configured — set FPCALC_PATH");

        var psi = new ProcessStartInfo
        {
            FileName = _fpcalcPath,
            ArgumentList = { "-raw", "-json", "-length", lengthSeconds.ToString(), filePath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"fpcalc timed out after {Timeout.TotalMinutes} minutes on {Path.GetFileName(filePath)}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            var errorDetail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                $"fpcalc exited with code {process.ExitCode}: {Truncate(errorDetail, 200)}");
        }

        return ParseJson(stdout);
    }

    private double GetFileDuration(string filePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffprobePath!,
            ArgumentList =
            {
                "-v", "quiet",
                "-show_entries", "format=duration",
                "-of", "csv=p=0",
                filePath
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"ffprobe timed out on {Path.GetFileName(filePath)}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            var errorDetail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                $"ffprobe exited with code {process.ExitCode}: {Truncate(errorDetail, 200)}");
        }

        var trimmed = stdout.Trim();
        if (!double.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out var duration) || duration <= 0)
            throw new InvalidOperationException($"ffprobe returned invalid duration: '{trimmed}'");

        return duration;
    }

    private void ExtractAudio(string filePath, double offsetSeconds, int lengthSeconds,
        string outputPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath!,
            ArgumentList =
            {
                "-ss", offsetSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-i", filePath,
                "-t", lengthSeconds.ToString(),
                "-f", "wav",
                "-ac", "1",
                "-ar", "16000",
                "-y",
                outputPath
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // ffmpeg writes progress to stderr; drain it to prevent deadlock
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"ffmpeg timed out extracting tail audio from {Path.GetFileName(filePath)}");
        }

        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg exited with code {process.ExitCode}: {Truncate(stderr, 200)}");
    }

    private static FpcalcResult ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var duration = root.GetProperty("duration").GetDouble();
        var fpArray = root.GetProperty("fingerprint");
        var fingerprint = new uint[fpArray.GetArrayLength()];
        var i = 0;
        foreach (var element in fpArray.EnumerateArray())
        {
            // fpcalc -raw outputs unsigned 32-bit integers as JSON numbers
            fingerprint[i++] = element.GetUInt32();
        }

        return new FpcalcResult(fingerprint, duration);
    }

    /// <summary>
    /// Resolves a binary path: explicit configured path, or searches PATH.
    /// </summary>
    private static string? ResolvePath(string? configuredPath, string binaryName)
    {
        // Explicit path from config
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        // Try PATH lookup
        var name = OperatingSystem.IsWindows() ? $"{binaryName}.exe" : binaryName;
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Given a resolved path to one binary (e.g., ffmpeg), find a sibling binary (e.g., ffprobe)
    /// in the same directory.
    /// </summary>
    private static string? ResolveSibling(string resolvedPath, string siblingName)
    {
        var dir = Path.GetDirectoryName(resolvedPath);
        if (dir == null) return null;

        var name = OperatingSystem.IsWindows() ? $"{siblingName}.exe" : siblingName;
        var candidate = Path.Combine(dir, name);
        return File.Exists(candidate) ? candidate : null;
    }

    private static string Truncate(string s, int maxLength) =>
        s.Length <= maxLength ? s.Trim() : s[..maxLength].Trim() + "...";
}
