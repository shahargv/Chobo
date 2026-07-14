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
        services.AddOptions<ChoboSqliteOptions>().Bind(configuration.GetSection("Chobo:Sqlite"));
        services.AddOptions<ChoboSecurityOptions>().Bind(configuration.GetSection("Chobo"));
        services.AddOptions<ChoboInitOptions>().Bind(configuration.GetSection("Chobo:Init"));
        services.AddOptions<ChoboDataRetentionOptions>().Bind(configuration.GetSection("Chobo:DataRetention"));
        services.AddOptions<ChoboSqliteSelfBackupOptions>().Bind(configuration.GetSection("Chobo:SqliteSelfBackup"));
        services.AddOptions<ChoboBackupRestoreOptions>().Bind(configuration.GetSection("Chobo:BackupRestore"));
        services.AddOptions<ChoboClusterMetadataOptions>().Bind(configuration.GetSection("Chobo:ClusterMetadata"));
        services.AddOptions<ChoboDatabaseLoggingOptions>().Bind(configuration.GetSection("Chobo:DatabaseLogging"));
        services.AddOptions<BackupStorageOperationOptions>().Bind(configuration.GetSection("Chobo:BackupStorageOperations"));
        services.AddOptions<RetentionManagementOptions>().Bind(configuration.GetSection("Chobo:RetentionManagement"));
        services.AddOptions<BackupsGarbageCollectorOptions>().Bind(configuration.GetSection("Chobo:BackupsGarbageCollector"));
        services.AddOptions<ChoboWebOptions>().Bind(configuration.GetSection("Chobo:Web"));
        services.AddOptions<ChoboEndpointRewriteOptions>().Bind(configuration.GetSection("Chobo:EndpointRewrites"));
        services.AddOptions<ChoboTestHooksOptions>().Bind(configuration.GetSection("Chobo:TestHooks"));
        services.AddOptions<ChoboRuntimeSettingsOptions>().Bind(configuration.GetSection("Chobo:Settings"));

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
            var sqlite = serviceProvider.GetRequiredService<IOptions<ChoboSqliteOptions>>().Value;
            return new LoggerConfiguration()
                .Enrich.FromLogContext()
                .ReadFrom.Configuration(configuration)
                .WriteTo.Sink(new ApplicationLogSqliteSink(dataDirectory, sqlite))
                .CreateLogger();
        });

        services.AddSingleton<SlowSqliteQueryLoggingInterceptor>();
        services.AddSingleton<SqlitePragmaConnectionInterceptor>();
        services.AddDbContext<ChoboDbContext>((serviceProvider, options) =>
        {
            var storage = serviceProvider.GetRequiredService<IOptions<ChoboStorageOptions>>().Value;
            ConfigureChoboSqlite(
                options,
                storage,
                serviceProvider.GetRequiredService<IOptions<ChoboSqliteOptions>>().Value,
                serviceProvider.GetRequiredService<SlowSqliteQueryLoggingInterceptor>(),
                serviceProvider.GetRequiredService<SqlitePragmaConnectionInterceptor>());
        });
        services.AddDbContextFactory<ChoboDbContext>((serviceProvider, options) =>
        {
            var storage = serviceProvider.GetRequiredService<IOptions<ChoboStorageOptions>>().Value;
            ConfigureChoboSqlite(
                options,
                storage,
                serviceProvider.GetRequiredService<IOptions<ChoboSqliteOptions>>().Value,
                serviceProvider.GetRequiredService<SlowSqliteQueryLoggingInterceptor>(),
                serviceProvider.GetRequiredService<SqlitePragmaConnectionInterceptor>());
        }, ServiceLifetime.Scoped);
        services.AddScoped<ActorContext>();
        services.AddScoped<IActorContext>(serviceProvider => serviceProvider.GetRequiredService<ActorContext>());
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ISchemaUpgradeService, SchemaUpgradeService>();
        services.AddScoped<IDatabaseBootstrap, DatabaseBootstrap>();
        services.AddSingleton<IAesKeyRepository, FileAesKeyRepository>();
        services.AddScoped<ICredentialProtector, CredentialProtector>();
        services.AddSingleton<IBackupPasswordGenerator, BackupPasswordGenerator>();
        services.AddScoped<IExportImportService, ExportImportService>();
        services.AddSingleton<IEndpointRewriteService, EndpointRewriteService>();
        services.AddScoped<ClickHouseAdapter>();
        services.AddScoped<IClickHouseAdapter>(serviceProvider => serviceProvider.GetRequiredService<ClickHouseAdapter>());
        services.AddSingleton<IClickHouseClusterMetadataService, ClickHouseClusterMetadataService>();
        services.AddScoped<IBackupStorageProvider, S3StorageProvider>();
        services.AddScoped<IBackupStorageProviderRegistry, BackupStorageProviderRegistry>();
        services.AddScoped<IBackupStorageOperations, BackupStorageOperations>();
        services.AddScoped<IApplicationLogStore, ApplicationLogStore>();
        services.AddScoped<IAuditStore, AuditStore>();
        services.AddScoped<IRuntimeSettingsService, RuntimeSettingsService>();
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
        services.AddScoped<BackupPreparationService>();
        services.AddScoped<IBackupStorageManifestService, BackupStorageManifestService>();
        services.AddScoped<RestoreApplicationService>();
        services.AddScoped<BackupRunnerService>();
        services.AddScoped<RestoreRunnerService>();
        services.AddScoped<BackupCleanupService>();
        services.AddScoped<BackupGarbageCollectionEvaluationService>();
        services.AddScoped<BackupRestoreQueueApplicationService>();
        services.AddSingleton<BackupRestoreQueueClaimPolicy>();
        services.AddSingleton<BackupRestoreOperationGate>();
        services.AddSingleton<IBackupRestoreConcurrencyCoordinator, BackupRestoreConcurrencyCoordinator>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBackupRestoreQueues, BackupRestoreQueues>();
        services.AddHostedService<BackupRestoreResumeBackgroundService>();
        services.AddHostedService<ClickHouseClusterMetadataRefreshBackgroundService>();
        services.AddHostedService<BackupRestoreOperationDispatcherBackgroundService>();
        services.AddHostedService<BackupSchedulerDispatcherBackgroundService>();
        services.AddHostedService<RetentionManagementBackgroundService>();
        services.AddSingleton<BackupsGarbageCollectorBackgroundService>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<BackupsGarbageCollectorBackgroundService>());
        services.AddHostedService<DataRetentionBackgroundService>();
        services.AddHostedService<SqliteSelfBackupBackgroundService>();
        return services;
    }


    private static void ConfigureChoboSqlite(
        DbContextOptionsBuilder options,
        ChoboStorageOptions storage,
        ChoboSqliteOptions sqliteOptions,
        SlowSqliteQueryLoggingInterceptor slowQueryInterceptor,
        SqlitePragmaConnectionInterceptor pragmaConnectionInterceptor)
    {
        var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "chobo.db");
        options.UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = SqliteDefaultTimeoutSeconds(sqliteOptions)
        }.ToString());
        options.AddInterceptors(slowQueryInterceptor, pragmaConnectionInterceptor);
    }

    private static int SqliteDefaultTimeoutSeconds(ChoboSqliteOptions options)
    {
        if (options.BusyTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException("SQLite busy timeout cannot be negative.");
        }

        return options.BusyTimeout.TotalSeconds >= int.MaxValue
            ? int.MaxValue
            : Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds));
    }

    public static async Task InitializeChoboDatabaseAsync(this IServiceProvider services, bool firstStartup, bool missingDatabaseAfterInitialized = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var bootstrap = scope.ServiceProvider.GetRequiredService<IDatabaseBootstrap>();
        await bootstrap.EnsureDatabaseObjectsAsync();
        await bootstrap.EnsureSchemaStateAsync();
        var sqliteOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ChoboSqliteOptions>>();
        await DatabasePerformanceMaintenance.EnsureAsync(db, sqliteOptions.CurrentValue);

        var hasUsers = await db.Users.AnyAsync();
        if (!hasUsers)
        {
            await bootstrap.TryInitializeFromOptionsAsync();
        }
    }
}
