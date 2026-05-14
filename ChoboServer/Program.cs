using ChoboServer;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;

if (args.Length > 0 && string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
{
    await LocalCommands.InitializeAsync(args.Skip(1).ToArray());
    return;
}

var builder = WebApplication.CreateBuilder(args);
ChoboConfiguration.AddChoboConfigurationSources(builder.Configuration, args);
var choboDataDirectory = GetChoboDataDirectory(builder.Configuration);

await EnsureDatabaseSchemaBeforeLoggingAsync(builder.Configuration);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Sink(new ApplicationLogSqliteSink(choboDataDirectory))
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddChoboServer(builder.Configuration);

var app = builder.Build();
await app.Services.InitializeChoboDatabaseAsync();

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
