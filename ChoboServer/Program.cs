using ChoboServer;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
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

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Sink(new ApplicationLogSqliteSink(choboDataDirectory))
    .CreateLogger();
builder.Host.UseSerilog();

var app = builder.Build();
Console.WriteLine("Initializing Chobo SQLite database...");
await app.Services.InitializeChoboDatabaseAsync(firstStartup, missingDatabaseAfterInitialized);
Console.WriteLine("Chobo SQLite database is ready.");
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
if (app.Services.GetRequiredService<IOptions<ChoboWebOptions>>().Value.IsGuiEnabled)
{
    var guiFiles = GetGuiFileProvider(app.Environment);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = guiFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = guiFiles });
}
app.UseMiddleware<TokenAuthMiddleware>();
app.MapControllers();
if (app.Services.GetRequiredService<IOptions<ChoboWebOptions>>().Value.IsGuiEnabled)
{
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = GetGuiFileProvider(app.Environment) });
}
app.Run();

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
