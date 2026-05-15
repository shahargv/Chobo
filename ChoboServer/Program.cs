using ChoboServer;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
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

builder.Services.AddChoboServer(builder.Configuration);
await EnsureDatabaseSchemaBeforeLoggingAsync(builder.Services);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Sink(new ApplicationLogSqliteSink(choboDataDirectory))
    .CreateLogger();
builder.Host.UseSerilog();

var app = builder.Build();
await app.Services.InitializeChoboDatabaseAsync(firstStartup);

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Chobo API v1");
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.UseMiddleware<TokenAuthMiddleware>();
app.MapControllers();
app.Run();

static async Task EnsureDatabaseSchemaBeforeLoggingAsync(IServiceCollection services)
{
    using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var bootstrap = scope.ServiceProvider.GetRequiredService<DatabaseBootstrap>();
    await bootstrap.EnsureDatabaseObjectsAsync();
    await bootstrap.EnsureSchemaStateAsync();
    var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
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
