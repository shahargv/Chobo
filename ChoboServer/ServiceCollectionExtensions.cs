using ChoboServer.Data;
using ChoboServer.Application;
using ChoboServer.BackgroundServices;
using ChoboServer.Options;
using ChoboServer.Repositories;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace ChoboServer;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChoboServer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ChoboStorageOptions>().Bind(configuration.GetSection("Chobo"));
        services.AddOptions<ChoboSecurityOptions>().Bind(configuration.GetSection("Chobo"));
        services.AddOptions<ChoboInitOptions>().Bind(configuration.GetSection("Chobo:Init"));
        services.AddOptions<ChoboDataRetentionOptions>().Bind(configuration.GetSection("Chobo:DataRetention"));
        services.AddOptions<ChoboBackupRestoreOptions>().Bind(configuration.GetSection("Chobo:BackupRestore"));

        services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            options.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            options.JsonSerializerOptions.WriteIndented = true;
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        services.AddOpenApi();
        services.AddSingleton<Serilog.ILogger>(serviceProvider =>
        {
            var storage = serviceProvider.GetRequiredService<IOptions<ChoboStorageOptions>>().Value;
            var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
            Directory.CreateDirectory(dataDirectory);
            return new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .WriteTo.Sink(new ApplicationLogSqliteSink(dataDirectory))
                .CreateLogger();
        });

        services.AddDbContext<ChoboDbContext>((serviceProvider, options) =>
        {
            var storage = serviceProvider.GetRequiredService<IOptions<ChoboStorageOptions>>().Value;
            var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
            Directory.CreateDirectory(dataDirectory);
            var dbPath = Path.Combine(dataDirectory, "chobo.db");
            options.UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                DefaultTimeout = 60
            }.ToString());
        });
        services.AddScoped<ActorContext>();
        services.AddScoped<TokenService>();
        services.AddScoped<AuditService>();
        services.AddScoped<CredentialProtector>();
        services.AddScoped<ExportImportService>();
        services.AddScoped<ClickHouseAdapter>();
        services.AddScoped<IClickHouseAdapter>(serviceProvider => serviceProvider.GetRequiredService<ClickHouseAdapter>());
        services.AddScoped<ApplicationLogTimelineStore>();
        services.AddScoped<AuditTimelineStore>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IClusterRepository, ClusterRepository>();
        services.AddScoped<ITargetRepository, TargetRepository>();
        services.AddScoped<IPolicyRepository, PolicyRepository>();
        services.AddScoped<IScheduleRepository, ScheduleRepository>();
        services.AddScoped<UserApplicationService>();
        services.AddScoped<ClusterApplicationService>();
        services.AddScoped<TargetApplicationService>();
        services.AddScoped<PolicySelectorEvaluationService>();
        services.AddScoped<PolicyApplicationService>();
        services.AddScoped<ScheduleApplicationService>();
        services.AddScoped<DashboardApplicationService>();
        services.AddScoped<BackupApplicationService>();
        services.AddScoped<RestoreApplicationService>();
        services.AddScoped<BackupRunnerService>();
        services.AddScoped<RestoreRunnerService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<BackupRestoreQueues>();
        services.AddHostedService<BackupRestoreResumeBackgroundService>();
        services.AddHostedService<BackupExecutorBackgroundService>();
        services.AddHostedService<RestoreExecutorBackgroundService>();
        services.AddHostedService<BackupSchedulerDispatcherBackgroundService>();
        services.AddHostedService<DataRetentionBackgroundService>();
        return services;
    }

    public static async Task InitializeChoboDatabaseAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        await DatabaseBootstrap.EnsureDatabaseObjectsAsync(db);
        await DatabaseBootstrap.EnsureSchemaStateAsync(db);
        await DatabasePerformanceMaintenance.EnsureAsync(db);
        await DatabaseBootstrap.TryInitializeFromOptionsAsync(scope.ServiceProvider);
    }
}
