using ChoboServer;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
ChoboConfiguration.AddChoboConfigurationSources(builder.Configuration, args);
var choboDataDirectory = GetChoboDataDirectory(builder.Configuration);
var dbPath = Path.Combine(choboDataDirectory, "chobo.db");
var initializedMarkerPath = Path.Combine(choboDataDirectory, "_initialized");
var firstStartup = !File.Exists(dbPath) && !File.Exists(initializedMarkerPath);
if (!File.Exists(dbPath) && File.Exists(initializedMarkerPath))
{
    throw new InvalidOperationException($"Chobo data directory is marked initialized but SQLite database is missing at {dbPath}.");
}

await EnsureDatabaseSchemaBeforeLoggingAsync(builder.Configuration);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Sink(new ApplicationLogSqliteSink(choboDataDirectory))
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddChoboServer(builder.Configuration);

var app = builder.Build();
await app.Services.InitializeChoboDatabaseAsync(firstStartup);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.UseMiddleware<TokenAuthMiddleware>();
app.MapControllers();
app.Run();

static async Task EnsureDatabaseSchemaBeforeLoggingAsync(IConfiguration configuration)
{
    var storage = configuration.GetSection("Chobo").Get<ChoboStorageOptions>() ?? new ChoboStorageOptions();
    var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
    Directory.CreateDirectory(dataDirectory);
    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(dataDirectory, "chobo.db"),
        DefaultTimeout = 60
    }.ToString();
    var options = new DbContextOptionsBuilder<ChoboDbContext>()
        .UseSqlite(connectionString)
        .Options;

    await using var db = new ChoboDbContext(options);
    await DatabaseBootstrap.EnsureDatabaseObjectsAsync(db);
    await DatabaseBootstrap.EnsureSchemaStateAsync(db);
    await DatabasePerformanceMaintenance.EnsureAsync(db);
}

static string GetChoboDataDirectory(IConfiguration configuration)
{
    var storage = configuration.GetSection("Chobo").Get<ChoboStorageOptions>() ?? new ChoboStorageOptions();
    var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
    Directory.CreateDirectory(dataDirectory);
    return dataDirectory;
}

public partial class Program;
