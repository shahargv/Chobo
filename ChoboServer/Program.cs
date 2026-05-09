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
AddChoboEnvironmentAliases(builder.Configuration);
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

static string GetChoboDataDirectory(IConfiguration configuration)
{
    var storage = configuration.GetSection("Chobo").Get<ChoboStorageOptions>() ?? new ChoboStorageOptions();
    var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
    Directory.CreateDirectory(dataDirectory);
    return dataDirectory;
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
