using MediaSkipDetector;

var builder = Host.CreateApplicationBuilder(args);

// Log to stderr (not stdout) so container orchestrators can separate app logs from output
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
