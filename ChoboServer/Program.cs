using ChoboServer;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

if (args.Length > 0 && string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
{
    await LocalCommands.InitializeAsync(args.Skip(1).ToArray());
    return;
}

var builder = WebApplication.CreateBuilder(args);
AddChoboEnvironmentAliases(builder.Configuration);
AddChoboSerilogSqlitePath(builder.Configuration);

await EnsureDatabaseSchemaBeforeLoggingAsync(builder.Configuration);

builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

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
    var options = new DbContextOptionsBuilder<ChoboDbContext>()
        .UseSqlite($"Data Source={Path.Combine(dataDirectory, "chobo.db")}")
        .Options;

    await using var db = new ChoboDbContext(options);
    await DatabaseBootstrap.EnsureDatabaseObjectsAsync(db);
    await DatabaseBootstrap.EnsureSchemaStateAsync(db);
    await DatabasePerformanceMaintenance.EnsureAsync(db);
}

static void AddChoboEnvironmentAliases(IConfigurationBuilder configuration)
{
    var values = new Dictionary<string, string?>();
    AddAlias(values, "CHOBO_DATA_DIRECTORY", "Chobo:DataDirectory");
    AddAlias(values, "CHOBO_ENCRYPTION_KEY_BASE64", "Chobo:EncryptionKeyBase64");
    AddAlias(values, "CHOBO_INIT_ADMIN_USER", "Chobo:Init:AdminUser");
    AddAlias(values, "CHOBO_INIT_ACCESS_TOKEN", "Chobo:Init:AccessToken");
    if (values.Count > 0)
    {
        configuration.AddInMemoryCollection(values);
    }
}

static void AddChoboSerilogSqlitePath(IConfigurationBuilder configuration)
{
    var root = (IConfiguration)configuration;
    var storage = root.GetSection("Chobo").Get<ChoboStorageOptions>() ?? new ChoboStorageOptions();
    var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
    Directory.CreateDirectory(dataDirectory);
    var writeTo = root.GetSection("Serilog:WriteTo").GetChildren().ToList();
    var sqliteSink = writeTo.FirstOrDefault(x => string.Equals(x["Name"], "SQLite", StringComparison.OrdinalIgnoreCase));
    var sqliteSinkIndex = sqliteSink?.Key ?? "0";
    configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        [$"Serilog:WriteTo:{sqliteSinkIndex}:Args:sqliteDbPath"] = Path.Combine(dataDirectory, "chobo.db")
    });
}

static void AddAlias(IDictionary<string, string?> values, string environmentName, string configurationKey)
{
    var value = Environment.GetEnvironmentVariable(environmentName);
    if (!string.IsNullOrWhiteSpace(value))
    {
        values[configurationKey] = value;
    }
}

public partial class Program;
