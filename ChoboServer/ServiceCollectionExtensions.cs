using ChoboServer.Data;
using ChoboServer.Application;
using ChoboServer.BackgroundServices;
using ChoboServer.Options;
using ChoboServer.Repositories;
using ChoboServer.Services;
using ChoboServer.Controllers.Validators;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
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
        services.AddOptions<ChoboSqliteSelfBackupOptions>().Bind(configuration.GetSection("Chobo:SqliteSelfBackup"));
        services.AddOptions<ChoboBackupRestoreOptions>().Bind(configuration.GetSection("Chobo:BackupRestore"));
        services.AddOptions<BackupStorageOperationOptions>().Bind(configuration.GetSection("Chobo:BackupStorageOperations"));
        services.AddOptions<RetentionManagementOptions>().Bind(configuration.GetSection("Chobo:RetentionManagement"));
        services.AddOptions<BackupsGarbageCollectorOptions>().Bind(configuration.GetSection("Chobo:BackupsGarbageCollector"));
        services.AddOptions<ChoboWebOptions>().Bind(configuration.GetSection("Chobo:Web"));
        services.AddOptions<ChoboEndpointRewriteOptions>().Bind(configuration.GetSection("Chobo:EndpointRewrites"));
        services.AddOptions<ChoboTestHooksOptions>().Bind(configuration.GetSection("Chobo:TestHooks"));

        services.AddValidatorsFromAssemblyContaining<UpsertClusterRequestValidator>();
        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var message = string.Join("; ", context.ModelState
                    .SelectMany(x => x.Value?.Errors.Select(error => error.ErrorMessage) ?? [])
                    .Where(x => !string.IsNullOrWhiteSpace(x)));

                return new BadRequestObjectResult(new Chobo.Contracts.ErrorResponse(string.IsNullOrWhiteSpace(message) ? "Request validation failed." : message));
            };
        });
        services.AddControllers(options => options.Filters.Add<FluentValidationActionFilter>()).AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            options.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            options.JsonSerializerOptions.WriteIndented = true;
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Chobo API",
                Version = Chobo.Contracts.ChoboApi.ApiVersion.ToString()
            });

            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "Paste a Chobo access token. Swagger UI sends it as a Bearer token.",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "Opaque"
            });

            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document, null)] = []
            });
        });
        services.AddSingleton<Serilog.ILogger>(serviceProvider =>
        {
            var storage = serviceProvider.GetRequiredService<IOptions<ChoboStorageOptions>>().Value;
            var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
            Directory.CreateDirectory(dataDirectory);
            return new LoggerConfiguration()
                .Enrich.FromLogContext()
                .ReadFrom.Configuration(configuration)
                .WriteTo.Sink(new ApplicationLogSqliteSink(dataDirectory))
                .CreateLogger();
        });

        services.AddDbContext<ChoboDbContext>((serviceProvider, options) =>
        {
            var storage = serviceProvider.GetRequiredService<IOptions<ChoboStorageOptions>>().Value;
            ConfigureChoboSqlite(options, storage);
        });
        services.AddDbContextFactory<ChoboDbContext>((serviceProvider, options) =>
        {
            var storage = serviceProvider.GetRequiredService<IOptions<ChoboStorageOptions>>().Value;
            ConfigureChoboSqlite(options, storage);
        }, ServiceLifetime.Scoped);
        services.AddScoped<ActorContext>();
        services.AddScoped<IActorContext>(serviceProvider => serviceProvider.GetRequiredService<ActorContext>());
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ISchemaUpgradeService, SchemaUpgradeService>();
        services.AddScoped<IDatabaseBootstrap, DatabaseBootstrap>();
        services.AddSingleton<IAesKeyRepository, FileAesKeyRepository>();
        services.AddScoped<ICredentialProtector, CredentialProtector>();
        services.AddScoped<IExportImportService, ExportImportService>();
        services.AddSingleton<IEndpointRewriteService, EndpointRewriteService>();
        services.AddScoped<ClickHouseAdapter>();
        services.AddScoped<IClickHouseAdapter>(serviceProvider => serviceProvider.GetRequiredService<ClickHouseAdapter>());
        services.AddScoped<S3BackupStorageOperations>();
        services.AddScoped<IBackupStorageOperations, BackupStorageOperations>();
        services.AddScoped<IApplicationLogStore, ApplicationLogStore>();
        services.AddScoped<IAuditStore, AuditStore>();
        services.AddMemoryCache();
        services.AddSingleton<ITestHookCoordinator, TestHookCoordinator>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IClusterRepository, ClusterRepository>();
        services.AddScoped<ITargetRepository, TargetRepository>();
        services.AddScoped<IPolicyRepository, PolicyRepository>();
        services.AddScoped<IScheduleRepository, ScheduleRepository>();
        services.AddScoped<UserApplicationService>();
        services.AddScoped<ClusterApplicationService>();
        services.AddScoped<SystemDefaultBackupPolicyService>();
        services.AddScoped<TargetApplicationService>();
        services.AddScoped<PolicySelectorEvaluationService>();
        services.AddScoped<PolicyApplicationService>();
        services.AddScoped<ScheduleApplicationService>();
        services.AddScoped<DashboardApplicationService>();
        services.AddScoped<SchemaBrowserApplicationService>();
        services.AddScoped<BackupApplicationService>();
        services.AddScoped<IBackupStorageManifestService, BackupStorageManifestService>();
        services.AddScoped<RestoreApplicationService>();
        services.AddScoped<BackupRunnerService>();
        services.AddScoped<RestoreRunnerService>();
        services.AddScoped<BackupCleanupService>();
        services.AddScoped<BackupRestoreQueueApplicationService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBackupRestoreQueues, BackupRestoreQueues>();
        services.AddHostedService<BackupRestoreResumeBackgroundService>();
        services.AddHostedService<BackupExecutorBackgroundService>();
        services.AddHostedService<SchemaOnlyBackupExecutorBackgroundService>();
        services.AddHostedService<RestoreExecutorBackgroundService>();
        services.AddHostedService<BackupSchedulerDispatcherBackgroundService>();
        services.AddHostedService<RetentionManagementBackgroundService>();
        services.AddSingleton<BackupsGarbageCollectorBackgroundService>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<BackupsGarbageCollectorBackgroundService>());
        services.AddHostedService<DataRetentionBackgroundService>();
        services.AddHostedService<SqliteSelfBackupBackgroundService>();
        return services;
    }


    private static void ConfigureChoboSqlite(DbContextOptionsBuilder options, ChoboStorageOptions storage)
    {
        var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "chobo.db");
        options.UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 60
        }.ToString());
    }
    public static async Task InitializeChoboDatabaseAsync(this IServiceProvider services, bool firstStartup, bool missingDatabaseAfterInitialized = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var bootstrap = scope.ServiceProvider.GetRequiredService<IDatabaseBootstrap>();
        await bootstrap.EnsureDatabaseObjectsAsync();
        await bootstrap.EnsureSchemaStateAsync();
        await DatabasePerformanceMaintenance.EnsureAsync(db);

        var hasUsers = await db.Users.AnyAsync();
        if (!hasUsers)
        {
            await bootstrap.TryInitializeFromOptionsAsync();
        }
    }
}

