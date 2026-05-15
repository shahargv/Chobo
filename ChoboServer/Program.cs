using ChoboServer;
using ChoboServer.Options;
using ChoboServer.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
ChoboConfiguration.AddChoboConfigurationSources(builder.Configuration, args);
var choboDataDirectory = GetChoboDataDirectory(builder.Configuration);
var dbPath = Path.Combine(choboDataDirectory, "chobo.db");
var initializedMarkerPath = Path.Combine(choboDataDirectory, "_initialized");
var missingDatabaseAfterInitialized = !File.Exists(dbPath) && File.Exists(initializedMarkerPath);
var firstStartup = !File.Exists(dbPath);

builder.Services.AddChoboServer(builder.Configuration);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Sink(new ApplicationLogSqliteSink(choboDataDirectory))
    .CreateLogger();
builder.Host.UseSerilog();

var app = builder.Build();
await app.Services.InitializeChoboDatabaseAsync(firstStartup, missingDatabaseAfterInitialized);
if (missingDatabaseAfterInitialized)
{
    Log.Warning("Chobo SQLite database was missing at {DatabasePath} even though the data directory was already initialized. Started with a fresh SQLite database and fresh local encrypted credential state; use backup metadata recovery to rebuild backup metadata.", dbPath);
}

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Chobo API v1");
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.UseMiddleware<TokenAuthMiddleware>();
app.MapControllers();
app.Run();

static string GetChoboDataDirectory(IConfiguration configuration)
{
    var storage = configuration.GetSection("Chobo").Get<ChoboStorageOptions>() ?? new ChoboStorageOptions();
    var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
    Directory.CreateDirectory(dataDirectory);
    return dataDirectory;
}

public partial class Program;
