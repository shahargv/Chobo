using System.Runtime;
using ChoboServer;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
ChoboConfiguration.AddChoboConfigurationSources(builder.Configuration, args);
var choboDataDirectory = GetChoboDataDirectory(builder.Configuration);
var dbPath = Path.Combine(choboDataDirectory, "chobo.db");
var initializedMarkerPath = Path.Combine(choboDataDirectory, "_initialized");
var missingDatabaseAfterInitialized = !File.Exists(dbPath) && File.Exists(initializedMarkerPath);
var firstStartup = !File.Exists(dbPath);
var webOptions = builder.Configuration.GetSection("Chobo:Web").Get<ChoboWebOptions>() ?? new ChoboWebOptions();
Console.WriteLine($"ChoboServer starting. Data directory: {choboDataDirectory}");
Console.WriteLine($"Chobo GUI: {(webOptions.IsGuiEnabled ? "enabled" : "disabled")}; port: {(webOptions.GuiPort?.ToString() ?? "same as API")}.");
if (webOptions.IsGuiEnabled && webOptions.GuiPort is { } guiPort)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        foreach (var apiPort in GetConfiguredHttpPorts(builder.Configuration))
        {
            options.ListenAnyIP(apiPort);
        }

        if (!GetConfiguredHttpPorts(builder.Configuration).Contains(guiPort))
        {
            options.ListenAnyIP(guiPort);
        }
    });
}

builder.Services.AddChoboServer(builder.Configuration);
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("System.Runtime", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel")
        .AddPrometheusExporter());

builder.Host.UseSerilog((context, _, loggerConfiguration) =>
{
    var dataDirectory = GetChoboDataDirectory(context.Configuration);
    var sqlite = context.Configuration.GetSection("Chobo:Sqlite").Get<ChoboSqliteOptions>() ?? new ChoboSqliteOptions();
    loggerConfiguration
        .Enrich.FromLogContext()
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Sink(new ApplicationLogSqliteSink(dataDirectory, sqlite));
});

var app = builder.Build();
Log.Information("ChoboServer runtime GC mode: ServerGC={ServerGC}.", GCSettings.IsServerGC);
Console.WriteLine("Initializing Chobo SQLite database...");
await app.Services.InitializeChoboDatabaseAsync(firstStartup, missingDatabaseAfterInitialized);
Console.WriteLine("Chobo SQLite database is ready.");
await using (var installScope = app.Services.CreateAsyncScope())
{
    var installStatus = await installScope.ServiceProvider.GetRequiredService<IDatabaseBootstrap>().GetInstallStatusAsync();
    if (installStatus.RequiresInstallation)
    {
        Console.WriteLine("Chobo is running in initialization mode.");
        Console.WriteLine("Use the web UI to install Chobo, or run: ChoboCli install --server-url http://<host>:8080");
        Console.WriteLine("The initial access token will be shown once and cannot be recovered after installation.");
    }
}
if (missingDatabaseAfterInitialized)
{
    Log.Warning("Chobo SQLite database was missing at {DatabasePath} even though the data directory was already initialized. Started with a fresh SQLite database and fresh local encrypted credential state; use backup metadata recovery to rebuild backup metadata.", dbPath);
}

app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
    };
});
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Chobo API v1");
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
if (app.Services.GetRequiredService<IOptions<ChoboWebOptions>>().Value.IsGuiEnabled)
{
    var guiFiles = GetGuiFileProvider(app.Environment);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = guiFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = guiFiles });
}
app.UseMiddleware<TokenAuthMiddleware>();
app.MapPrometheusScrapingEndpoint("/api/v1/metrics/prometheus");
app.MapControllers();
if (app.Services.GetRequiredService<IOptions<ChoboWebOptions>>().Value.IsGuiEnabled)
{
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = GetGuiFileProvider(app.Environment) });
}
app.Lifetime.ApplicationStopped.Register(SqliteConnection.ClearAllPools);
try
{
    app.Run();
}
finally
{
    SqliteConnection.ClearAllPools();
}

static string GetChoboDataDirectory(IConfiguration configuration)
{
    var storage = configuration.GetSection("Chobo").Get<ChoboStorageOptions>() ?? new ChoboStorageOptions();
    var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
    Directory.CreateDirectory(dataDirectory);
    return dataDirectory;
}

static IFileProvider GetGuiFileProvider(IWebHostEnvironment environment)
{
    var localDist = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "ChoboWeb", "dist"));
    return Directory.Exists(localDist)
        ? new PhysicalFileProvider(localDist)
        : environment.WebRootFileProvider;
}

static IReadOnlyList<int> GetConfiguredHttpPorts(IConfiguration configuration)
{
    var ports = new List<int>();
    AddPorts(ports, configuration["ASPNETCORE_HTTP_PORTS"]);
    AddPorts(ports, Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
    AddUrlPorts(ports, configuration["ASPNETCORE_URLS"]);
    AddUrlPorts(ports, Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
    return ports.Count == 0 ? [8080] : ports.Distinct().ToList();
}

static void AddPorts(List<int> ports, string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return;
    foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (int.TryParse(item, out var port)) ports.Add(port);
    }
}

static void AddUrlPorts(List<int> ports, string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return;
    foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (Uri.TryCreate(item.Replace("+", "localhost").Replace("*", "localhost"), UriKind.Absolute, out var uri) && !uri.IsDefaultPort)
        {
            ports.Add(uri.Port);
        }
    }
}

public partial class Program;
