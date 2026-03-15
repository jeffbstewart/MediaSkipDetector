using MediaSkipDetector;

// Parse CLI arguments
var port = 16004;
string? mediaRoot = null;

foreach (var arg in args)
{
    if (arg.StartsWith("--port=") && int.TryParse(arg["--port=".Length..], out var p))
        port = p;
    else if (arg.StartsWith("--media-root="))
        mediaRoot = arg["--media-root=".Length..];
}

// MEDIA_ROOT: environment variable takes precedence, then CLI arg
mediaRoot = Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? mediaRoot;
if (string.IsNullOrWhiteSpace(mediaRoot))
{
    Console.Error.WriteLine("Error: MEDIA_ROOT must be set via environment variable or --media-root= argument");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);

// Log to stderr with timestamps
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});

builder.WebHost.UseUrls($"http://*:{port}");

// Register services
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ServerStatus>();
builder.Services.AddSingleton<WorkQueue>();
builder.Services.AddSingleton(sp => new DirectoryScanner(
    mediaRoot,
    sp.GetRequiredService<ILogger<DirectoryScanner>>()));
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

var app = builder.Build();
app.MapEndpoints();

app.Logger.LogInformation("MediaSkipDetector starting on port {Port}, MEDIA_ROOT={MediaRoot}", port, mediaRoot);
app.Run();

return 0;
