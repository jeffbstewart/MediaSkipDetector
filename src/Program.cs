using MediaSkipDetector;

// Parse --port argument (default 16004)
var port = 16004;
foreach (var arg in args)
{
    if (arg.StartsWith("--port=") && int.TryParse(arg["--port=".Length..], out var p))
        port = p;
}

var builder = WebApplication.CreateBuilder(args);

// Log to stderr with timestamps
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});

builder.WebHost.UseUrls($"http://*:{port}");
builder.Services.AddSingleton<ServerStatus>();
builder.Services.AddHostedService<Worker>();

var app = builder.Build();
app.MapEndpoints();

app.Logger.LogInformation("MediaSkipDetector starting on port {Port}", port);
app.Run();
