using MediaSkipDetector;

// Parse CLI arguments
var port = 16004;
string? mediaRoot = null;
string? dataDir = null;

foreach (var arg in args)
{
    if (arg.StartsWith("--port=") && int.TryParse(arg["--port=".Length..], out var p))
        port = p;
    else if (arg.StartsWith("--media-root="))
        mediaRoot = arg["--media-root=".Length..];
    else if (arg.StartsWith("--data-dir="))
        dataDir = arg["--data-dir=".Length..];
}

// MEDIA_ROOT: environment variable takes precedence, then CLI arg
mediaRoot = Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? mediaRoot;
if (string.IsNullOrWhiteSpace(mediaRoot))
{
    Console.Error.WriteLine("Error: MEDIA_ROOT must be set via environment variable or --media-root= argument");
    return 1;
}

// DATA_DIR: environment variable takes precedence, then CLI arg, then default
dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? dataDir ?? "./data";

// FPCALC_PATH: environment variable or CLI arg
string? fpcalcPath = null;
foreach (var arg in args)
{
    if (arg.StartsWith("--fpcalc-path="))
        fpcalcPath = arg["--fpcalc-path=".Length..];
}
fpcalcPath = Environment.GetEnvironmentVariable("FPCALC_PATH") ?? fpcalcPath;

var builder = WebApplication.CreateBuilder(args);

// Log to stderr with timestamps
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});

builder.WebHost.UseUrls($"http://*:{port}");

// Register services — analysis tuning via environment variables (see docs/TUNING.md)
static int EnvInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
static double EnvDouble(string name, double fallback) =>
    double.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

// FFMPEG_PATH: environment variable or CLI arg
string? ffmpegPath = null;
foreach (var arg in args)
{
    if (arg.StartsWith("--ffmpeg-path="))
        ffmpegPath = arg["--ffmpeg-path=".Length..];
}
ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? ffmpegPath;

var appConfig = new AppConfig(mediaRoot, dataDir, fpcalcPath)
{
    MaxFingerprintPointDifferences = EnvInt("MAX_FINGERPRINT_POINT_DIFFERENCES", 6),
    MaxTimeSkip = EnvDouble("MAX_TIME_SKIP", 3.5),
    InvertedIndexShift = EnvInt("INVERTED_INDEX_SHIFT", 2),
    MinIntroDuration = EnvInt("MIN_INTRO_DURATION", 15),
    MaxIntroDuration = EnvInt("MAX_INTRO_DURATION", 120),
    MaxComparisonCandidates = EnvInt("MAX_COMPARISON_CANDIDATES", 7),
    FingerprintLengthSeconds = EnvInt("FINGERPRINT_LENGTH_SECONDS", 600),
    FfmpegPath = ffmpegPath,
    CreditsFingerprintSeconds = EnvInt("CREDITS_FINGERPRINT_SECONDS", 300),
    MinCreditsDuration = EnvInt("MIN_CREDITS_DURATION", 15),
    MaxCreditsDuration = EnvInt("MAX_CREDITS_DURATION", 300),
};
builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ServerStatus>();
builder.Services.AddSingleton<WorkQueue>();
builder.Services.AddSingleton(sp => new DirectoryScanner(
    mediaRoot,
    sp.GetRequiredService<ILogger<DirectoryScanner>>()));
builder.Services.AddSingleton(sp => new DatabaseInitializer(
    appConfig.DataDir,
    sp.GetRequiredService<ILogger<DatabaseInitializer>>()));
builder.Services.AddSingleton<IFingerprintCache>(sp => new FingerprintCache(
    sp.GetRequiredService<DatabaseInitializer>().Connection,
    sp.GetRequiredService<ILogger<FingerprintCache>>()));
builder.Services.AddSingleton<IFpcalcService>(sp => new FpcalcService(
    sp.GetRequiredService<AppConfig>(),
    sp.GetRequiredService<ILogger<FpcalcService>>()));
builder.Services.AddSingleton<IFingerprintPipelineService>(sp => new FingerprintPipelineService(
    sp.GetRequiredService<DatabaseInitializer>().Connection,
    sp.GetRequiredService<IFingerprintCache>(),
    sp.GetRequiredService<AppConfig>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<ILogger<FingerprintPipelineService>>()));
builder.Services.AddSingleton<IIntroAnalysisService>(sp => new IntroAnalysisService(
    sp.GetRequiredService<DatabaseInitializer>().Connection,
    sp.GetRequiredService<IFingerprintCache>(),
    sp.GetRequiredService<AppConfig>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<ILogger<IntroAnalysisService>>(),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

var app = builder.Build();
app.MapEndpoints();

app.Logger.LogInformation("MediaSkipDetector starting on port {Port}, MEDIA_ROOT={MediaRoot}, DATA_DIR={DataDir}", port, mediaRoot, dataDir);
app.Run();

return 0;
