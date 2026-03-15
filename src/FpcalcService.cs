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
    /// Runs fpcalc on the given file and returns the raw fingerprint + duration.
    /// Throws on fpcalc error or timeout.
    /// </summary>
    FpcalcResult Run(string filePath, CancellationToken ct = default);
}

public class FpcalcService : IFpcalcService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly string? _fpcalcPath;
    private readonly int _fingerprintLengthSeconds;
    private readonly ILogger<FpcalcService> _logger;

    public FpcalcService(AppConfig appConfig, ILogger<FpcalcService> logger)
    {
        _logger = logger;
        _fpcalcPath = ResolvePath(appConfig.FpcalcPath);
        _fingerprintLengthSeconds = appConfig.FingerprintLengthSeconds;

        if (_fpcalcPath != null)
            _logger.LogInformation("fpcalc available at {Path}", _fpcalcPath);
        else
            _logger.LogWarning("fpcalc not available — set FPCALC_PATH environment variable");
    }

    public bool IsAvailable => _fpcalcPath != null;

    public FpcalcResult Run(string filePath, CancellationToken ct = default)
    {
        if (_fpcalcPath == null)
            throw new InvalidOperationException("fpcalc path not configured — set FPCALC_PATH");

        var psi = new ProcessStartInfo
        {
            FileName = _fpcalcPath,
            ArgumentList = { "-raw", "-json", "-length", _fingerprintLengthSeconds.ToString(), filePath },
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

    private static string? ResolvePath(string? configuredPath)
    {
        // Explicit path from config
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        // Try PATH lookup
        var name = OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc";
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string Truncate(string s, int maxLength) =>
        s.Length <= maxLength ? s.Trim() : s[..maxLength].Trim() + "...";
}
