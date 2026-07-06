using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer;
using ChoboServer.Application;
using ChoboServer.BackgroundServices;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Repositories;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Chobo.Tests;

public sealed class ChoboFoundationTests
{
    private const string Token = "static-test-token";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public void Chobo_configuration_supports_environment_variables_for_appsettings_values()
    {
        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["CHOBO_DATA_DIRECTORY"] = "env-data",
            ["Chobo__EncryptionKeyBase64"] = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            ["CHOBO_INIT_ADMIN_USER"] = "env-admin",
            ["Chobo__Init__AccessToken"] = "env-token",
            ["CHOBO_DATA_RETENTION_INTERVAL"] = "00:10:00",
            ["Chobo__DataRetention__LogsBefore"] = "2026-05-14T10:00:00+00:00",
            ["CHOBO_DATA_RETENTION_AUDITS_BEFORE"] = "2026-05-14T11:00:00+00:00",
            ["CHOBO_DATA_RETENTION_DELETED_BACKUP_RESTORE_RECORD_RETENTION"] = "30.00:00:00",
            ["CHOBO_SQLITE_JOURNAL_MODE"] = "TRUNCATE",
            ["CHOBO_SQLITE_SYNCHRONOUS"] = "FULL",
            ["CHOBO_SQLITE_BUSY_TIMEOUT"] = "00:00:08",
            ["CHOBO_SQLITE_WAL_AUTO_CHECKPOINT"] = "2000",
            ["CHOBO_SQLITE_SELF_BACKUP_ENABLED"] = "true",
            ["CHOBO_SQLITE_SELF_BACKUP_DIRECTORY"] = "env-sqlite-backups",
            ["CHOBO_SQLITE_SELF_BACKUP_INTERVAL"] = "12:00:00",
            ["CHOBO_SQLITE_SELF_BACKUP_POLL_INTERVAL"] = "00:04:00",
            ["Chobo__BackupRestore__MaxDop"] = "7",
            ["CHOBO_BACKUP_RESTORE_QUEUE_CAPACITY"] = "33",
            ["CHOBO_BACKUP_RESTORE_SCHEDULER_INTERVAL"] = "00:02:00",
            ["Chobo__BackupRestore__SchedulerMissedRunGracePeriod"] = "00:07:00",
            ["CHOBO_BACKUP_RESTORE_POLL_INTERVAL"] = "00:00:03",
            ["CHOBO_DATABASE_LOGGING_SLOW_QUERY_THRESHOLD"] = "00:00:04",
            ["CHOBO_WEB_IS_GUI_ENABLED"] = "false",
            ["CHOBO_WEB_GUI_PORT"] = "18081",
            ["CHOBO_TEST_HOOKS_ENABLED"] = "true",
            ["Chobo__EndpointRewrites__ClickHouse__0__Host"] = "clickhouse-cluster-s1-r1",
            ["Chobo__EndpointRewrites__ClickHouse__0__Port"] = "9000",
            ["Chobo__EndpointRewrites__ClickHouse__0__ServerHost"] = "localhost",
            ["Chobo__EndpointRewrites__ClickHouse__0__ServerPort"] = "18111",
            ["Chobo__EndpointRewrites__S3ForClickHouse__0__ServerEndpoint"] = "http://localhost:9000",
            ["Chobo__EndpointRewrites__S3ForClickHouse__0__ClickHouseEndpoint"] = "http://minio:9000",
            ["AllowedHosts"] = "env-host",
            ["Serilog__MinimumLevel__Default"] = "Debug"
        });

        var builder = new ConfigurationBuilder();
        ChoboConfiguration.AddChoboConfigurationSources(builder, [], addStandardEnvironmentAndCommandLine: true);
        var configuration = builder.Build();

        var storage = configuration.GetSection("Chobo").Get<ChoboStorageOptions>()!;
        var security = configuration.GetSection("Chobo").Get<ChoboSecurityOptions>()!;
        var init = configuration.GetSection("Chobo:Init").Get<ChoboInitOptions>()!;
        var retention = configuration.GetSection("Chobo:DataRetention").Get<ChoboDataRetentionOptions>()!;
        var sqlite = configuration.GetSection("Chobo:Sqlite").Get<ChoboSqliteOptions>()!;
        var selfBackup = configuration.GetSection("Chobo:SqliteSelfBackup").Get<ChoboSqliteSelfBackupOptions>()!;
        var backupRestore = configuration.GetSection("Chobo:BackupRestore").Get<ChoboBackupRestoreOptions>()!;
        var databaseLogging = configuration.GetSection("Chobo:DatabaseLogging").Get<ChoboDatabaseLoggingOptions>()!;
        var web = configuration.GetSection("Chobo:Web").Get<ChoboWebOptions>()!;
        var endpointRewrites = configuration.GetSection("Chobo:EndpointRewrites").Get<ChoboEndpointRewriteOptions>()!;
        var testHooks = configuration.GetSection("Chobo:TestHooks").Get<ChoboTestHooksOptions>()!;

        Assert.Equal("env-data", storage.DataDirectory);
        Assert.Equal("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=", security.EncryptionKeyBase64);
        Assert.Equal("env-admin", init.AdminUser);
        Assert.Equal("env-token", init.AccessToken);
        Assert.Equal(TimeSpan.FromMinutes(10), retention.Interval);
        Assert.Equal(DateTimeOffset.Parse("2026-05-14T10:00:00+00:00"), retention.LogsBefore);
        Assert.Equal(DateTimeOffset.Parse("2026-05-14T11:00:00+00:00"), retention.AuditsBefore);
        Assert.Equal(TimeSpan.FromDays(30), retention.DeletedBackupRestoreRecordRetention);
        Assert.Equal("TRUNCATE", sqlite.JournalMode);
        Assert.Equal("FULL", sqlite.Synchronous);
        Assert.Equal(TimeSpan.FromSeconds(8), sqlite.BusyTimeout);
        Assert.Equal(2000, sqlite.WalAutoCheckpoint);
        Assert.True(selfBackup.Enabled);
        Assert.Equal("env-sqlite-backups", selfBackup.Directory);
        Assert.Equal(TimeSpan.FromHours(12), selfBackup.BackupInterval);
        Assert.Equal(TimeSpan.FromMinutes(4), selfBackup.PollInterval);
        Assert.Equal(7, backupRestore.MaxDop);
        Assert.Equal(33, backupRestore.QueueCapacity);
        Assert.Equal(TimeSpan.FromMinutes(2), backupRestore.SchedulerInterval);
        Assert.Equal(TimeSpan.FromMinutes(7), backupRestore.SchedulerMissedRunGracePeriod);
        Assert.Equal(TimeSpan.FromSeconds(3), backupRestore.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(4), databaseLogging.SlowQueryThreshold);
        Assert.False(web.IsGuiEnabled);
        Assert.Equal(18081, web.GuiPort);
        Assert.Equal("clickhouse-cluster-s1-r1", endpointRewrites.ClickHouse[0].Host);
        Assert.Equal(18111, endpointRewrites.ClickHouse[0].ServerPort);
        Assert.Equal("http://minio:9000", endpointRewrites.S3ForClickHouse[0].ClickHouseEndpoint);
        Assert.True(testHooks.Enabled);
        Assert.Equal("env-host", configuration["AllowedHosts"]);
        Assert.Equal("Debug", configuration["Serilog:MinimumLevel:Default"]);
    }

    [Fact]
    public void Chobo_configuration_loads_extra_appsettings_path_from_environment()
    {
        var dataDir = NewTestDataDirectory();
        var appSettingsPath = Path.Combine(dataDir, "custom-appsettings.json");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(appSettingsPath, """
        {
          "AllowedHosts": "json-host",
          "Chobo": {
            "DataDirectory": "json-data",
            "BackupRestore": {
              "MaxDop": 11
            }
          }
        }
        """);

        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            [ChoboConfiguration.AppSettingsPathEnvironmentVariable] = appSettingsPath,
            ["CHOBO_BACKUP_RESTORE_MAX_DOP"] = "12"
        });

        var builder = new ConfigurationBuilder();
        ChoboConfiguration.AddChoboConfigurationSources(builder, [], addStandardEnvironmentAndCommandLine: true);
        var configuration = builder.Build();
        var storage = configuration.GetSection("Chobo").Get<ChoboStorageOptions>()!;
        var backupRestore = configuration.GetSection("Chobo:BackupRestore").Get<ChoboBackupRestoreOptions>()!;

        Assert.Equal("json-host", configuration["AllowedHosts"]);
        Assert.Equal("json-data", storage.DataDirectory);
        Assert.Equal(12, backupRestore.MaxDop);
    }

    [Fact]
    public async Task First_startup_uses_configured_values_without_writing_initial_token_to_stdout()
    {
        var dataDir = NewTestDataDirectory();
        using var writer = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(writer);

        try
        {
            await using var factory = CreateFactory(dataDir, adminUser: "env-admin", accessToken: Token);
            var client = AuthenticatedClient(factory);
            var users = await client.GetFromJsonAsync<List<UserDto>>("/api/v1/users", JsonOptions);

            Assert.Contains(users!, x => x.UserName == "env-admin" && x.IsActive);
            Assert.DoesNotContain(Token, writer.ToString());
            Assert.Contains("Chobo initialized from configured", writer.ToString());
            Assert.True(File.Exists(Path.Combine(dataDir, "_initialized")));
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    [Fact]
    public async Task Startup_creates_sqlite_schema_and_initial_user_when_database_file_does_not_exist()
    {
        var dataDir = NewTestDataDirectory();
        var dbPath = Path.Combine(dataDir, "chobo.db");
        Assert.False(File.Exists(dbPath));

        await using var factory = CreateFactory(dataDir);
        var client = AuthenticatedClient(factory);
        var users = await client.GetFromJsonAsync<List<UserDto>>("/api/v1/users", JsonOptions);

        Assert.True(File.Exists(dbPath));
        Assert.Contains(users!, x => x.UserName == "admin" && x.IsActive);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.Equal(ChoboApi.SchemaVersion, (await db.SchemaStates.SingleAsync()).SchemaVersion);
        Assert.True(await db.Users.AnyAsync());
        Assert.True(await db.AccessTokens.AnyAsync());
        Assert.True(await db.AuditEntries.AnyAsync(x => x.Action == "initialize" && x.EntityType == "server"));
    }

    [Fact]
    public async Task Startup_applies_configured_sqlite_pragmas()
    {
        var dataDir = NewTestDataDirectory();
        await using var factory = CreateFactory(dataDir, extraConfiguration: new Dictionary<string, string?>
        {
            ["Chobo:Sqlite:JournalMode"] = "WAL",
            ["Chobo:Sqlite:Synchronous"] = "FULL",
            ["Chobo:Sqlite:BusyTimeout"] = "00:00:08",
            ["Chobo:Sqlite:WalAutoCheckpoint"] = "2000"
        });
        _ = AuthenticatedClient(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();

        await db.Database.OpenConnectionAsync();
        try
        {
            Assert.Equal("wal", await GetPragmaStringAsync(db, "journal_mode"));
            Assert.Equal(2L, await GetPragmaInt64Async(db, "synchronous"));
            Assert.Equal(8000L, await GetPragmaInt64Async(db, "busy_timeout"));
            Assert.Equal(2000L, await GetPragmaInt64Async(db, "wal_autocheckpoint"));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Unconfigured_first_startup_requires_one_time_install_before_authenticated_use()
    {
        var dataDir = NewTestDataDirectory();
        await using var factory = CreateFactory(dataDir, adminUser: "", accessToken: "");
        var anonymous = factory.CreateClient();

        var usersBeforeInstall = await anonymous.GetAsync("/api/v1/users");
        var status = await anonymous.GetFromJsonAsync<InstallStatusDto>("/api/v1/server/install/status", JsonOptions);
        var install = await Post<InstallResponse>(anonymous, "/api/v1/server/install", new InstallRequest(null));
        var repeatedInstall = await anonymous.PostAsJsonAsync("/api/v1/server/install", new InstallRequest(null), JsonOptions);

        Assert.Equal(HttpStatusCode.Unauthorized, usersBeforeInstall.StatusCode);
        Assert.True(status!.RequiresInstallation);
        Assert.Equal("admin", install.UserName);
        Assert.False(string.IsNullOrWhiteSpace(install.AccessToken));
        Assert.Equal(HttpStatusCode.Conflict, repeatedInstall.StatusCode);

        var installedStatus = await anonymous.GetFromJsonAsync<InstallStatusDto>("/api/v1/server/install/status", JsonOptions);
        Assert.False(installedStatus!.RequiresInstallation);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", install.AccessToken);
        var users = await client.GetFromJsonAsync<List<UserDto>>("/api/v1/users", JsonOptions);
        Assert.Contains(users!, x => x.UserName == "admin" && x.IsActive);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.True(await db.AccessTokens.AnyAsync());
        Assert.True(await db.AuditEntries.AnyAsync(x => x.ActorName == "system" && x.Action == "initialize" && x.EntityType == "server"));
    }

    [Fact]
    public async Task Startup_reuses_pre_existing_sqlite_file_without_reinitializing_state()
    {
        var dataDir = NewTestDataDirectory();
        await using (var firstFactory = CreateFactory(dataDir))
        {
            var firstClient = AuthenticatedClient(firstFactory);
            await Post<CreateUserResponse>(firstClient, "/api/v1/users", new CreateUserRequest("operator"));
            Assert.True(File.Exists(Path.Combine(dataDir, "chobo.db")));
        }

        await using var secondFactory = CreateFactory(dataDir, adminUser: "different-admin", accessToken: "different-token");
        var client = AuthenticatedClient(secondFactory);
        var users = await client.GetFromJsonAsync<List<UserDto>>("/api/v1/users", JsonOptions);

        Assert.Contains(users!, x => x.UserName == "admin");
        Assert.Contains(users!, x => x.UserName == "operator");
        Assert.DoesNotContain(users!, x => x.UserName == "different-admin");

        using var scope = secondFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.Single(await db.SchemaStates.ToListAsync());
        Assert.Single(await db.AuditEntries.Where(x => x.Action == "initialize" && x.EntityType == "server").ToListAsync());
    }

    [Fact]
    public async Task Server_requires_token_and_initializes_static_admin()
    {
        await using var factory = CreateFactory();
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/users")).StatusCode);

        var client = AuthenticatedClient(factory);
        var version = await client.GetFromJsonAsync<ServerVersionDto>("/api/v1/server/version", JsonOptions);
        Assert.Equal(ChoboApi.ApiVersion, version!.ApiVersion);
        Assert.Equal(ChoboApi.ProductVersion, version.ProductVersion);
        var users = await client.GetFromJsonAsync<List<UserDto>>("/api/v1/users", JsonOptions);
        Assert.Single(users!);
        Assert.Equal("admin", users![0].UserName);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.Equal(ChoboApi.SchemaVersion, (await db.SchemaStates.SingleAsync()).SchemaVersion);
        Assert.DoesNotContain(Token, string.Join(' ', await db.AccessTokens.Select(x => x.TokenHash).ToListAsync()));
    }

    [Fact]
    public async Task Swagger_ui_exposes_bearer_token_authentication_without_opening_api_access()
    {
        await using var factory = CreateFactory();
        var anonymous = factory.CreateClient();

        var ui = await anonymous.GetAsync("/swagger/index.html");
        var document = await anonymous.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json", JsonOptions);
        var users = await anonymous.GetAsync("/api/v1/users");

        Assert.True(ui.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, users.StatusCode);
        var schemes = document
            .GetProperty("components")
            .GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("Bearer", out var bearer));
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Contains(document.GetProperty("security").EnumerateArray(), requirement =>
            requirement.TryGetProperty("Bearer", out _));
    }

    [Fact]
    public async Task Gui_is_served_anonymously_on_same_port_by_default_while_api_still_requires_token()
    {
        await using var factory = CreateFactory();
        var anonymous = factory.CreateClient();

        var gui = await anonymous.GetAsync("/");
        var nestedRoute = await anonymous.GetAsync("/policies");
        var api = await anonymous.GetAsync("/api/v1/users");

        Assert.True(gui.IsSuccessStatusCode);
        Assert.True(nestedRoute.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [Fact]
    public async Task Gui_can_be_disabled_by_options()
    {
        await using var factory = CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Chobo:Web:IsGuiEnabled"] = "false"
        });
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public void S3_target_address_uses_configured_path_style_mode()
    {
        var pathStyleSettings = new S3TargetSettingsDto("https://s3.example.com", "us-east-1", "backup-bucket", "prod/backups", true);
        var virtualHostSettings = pathStyleSettings with { ForcePathStyle = false };

        var pathStyle = S3TargetUrlBuilder.BuildObjectUrl(pathStyleSettings, "db/table/file name.bin");
        var virtualHost = S3TargetUrlBuilder.BuildObjectUrl(virtualHostSettings, "db/table/file name.bin");

        Assert.Equal("https://s3.example.com/backup-bucket/prod/backups/db/table/file%20name.bin", pathStyle.AbsoluteUri);
        Assert.Equal("https://backup-bucket.s3.example.com/prod/backups/db/table/file%20name.bin", virtualHost.AbsoluteUri);
    }

    [Fact]
    public void Endpoint_rewrites_map_each_clickhouse_node_for_local_server_access()
    {
        var rewrites = new EndpointRewriteService(Options.Create(new ChoboEndpointRewriteOptions
        {
            ClickHouse =
            [
                new ClickHouseEndpointRewriteOptions
                {
                    Host = "clickhouse-cluster-s1-r1",
                    Port = 9000,
                    UseTls = false,
                    ServerHost = "localhost",
                    ServerPort = 18111,
                    ServerUseTls = false
                }
            ]
        }));

        var endpoint = rewrites.RewriteClickHouseEndpointForServer(new ClickHouseNodeEndpoint("clickhouse-cluster-s1-r1", 9000, false));
        var unknown = rewrites.RewriteClickHouseEndpointForServer(new ClickHouseNodeEndpoint("clickhouse-other", 9000, false));

        Assert.Equal(new ClickHouseNodeEndpoint("localhost", 18111, false), endpoint);
        Assert.Equal(new ClickHouseNodeEndpoint("clickhouse-other", 9000, false), unknown);
    }

    [Fact]
    public void Endpoint_rewrites_map_server_s3_endpoint_for_clickhouse_sql_only()
    {
        var rewrites = new EndpointRewriteService(Options.Create(new ChoboEndpointRewriteOptions
        {
            S3ForClickHouse =
            [
                new S3EndpointRewriteOptions
                {
                    ServerEndpoint = "http://localhost:9000",
                    ClickHouseEndpoint = "http://minio:9000"
                }
            ]
        }));

        var rewritten = rewrites.RewriteS3EndpointForClickHouse(new Uri("http://localhost:9000/data-bucket/path/to/file.bin"));
        var unchanged = rewrites.RewriteS3EndpointForClickHouse(new Uri("http://s3.example.com/data-bucket/path/to/file.bin"));

        Assert.Equal("http://minio:9000/data-bucket/path/to/file.bin", rewritten.AbsoluteUri);
        Assert.Equal("http://s3.example.com/data-bucket/path/to/file.bin", unchanged.AbsoluteUri);
    }

    [Fact]
    public async Task Cluster_credentials_are_encrypted_and_hidden_from_api()
    {
        var dataDir = NewTestDataDirectory();
        await using var factory = CreateFactory(dataDir);
        var client = AuthenticatedClient(factory);
        var created = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "prod",
            ClusterMode.Cluster,
            [new UpsertAccessNodeRequest("clickhouse-1"), new UpsertAccessNodeRequest("clickhouse-2", 9440, true)],
            "default",
            "secret",
            3));

        Assert.Equal("prod", created.Name);
        Assert.Equal(2, created.AccessNodes.Count);
        var json = await client.GetStringAsync("/api/v1/clusters");
        Assert.DoesNotContain("secret", json);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var stored = await db.ClickHouseClusters.SingleAsync(x => x.Id == created.Id);
        Assert.NotEqual("secret", stored.EncryptedPassword);
        Assert.NotNull(stored.EncryptedPassword);
        Assert.Contains('.', stored.EncryptedPassword);
        Assert.NotNull(stored.EncryptedPasswordKeyId);

        var export = await client.GetFromJsonAsync<ExportEnvelope>("/api/v1/config/export", JsonOptions);
        var exported = Assert.Single(export!.Data.Clusters, x => x.Id == created.Id);
        Assert.Equal(stored.EncryptedPasswordKeyId, exported.EncryptedPasswordKeyId);
        var keyFile = Directory.EnumerateFiles(Path.Combine(dataDir, "secrets", "aes-keys")).Single();
        Assert.DoesNotContain(await File.ReadAllTextAsync(keyFile), JsonSerializer.Serialize(export, JsonOptions));
    }

    [Fact]
    public async Task Backup_target_credentials_are_encrypted_and_hidden_from_api_and_key_export()
    {
        var dataDir = NewTestDataDirectory();
        await using var factory = CreateFactory(dataDir);
        var client = AuthenticatedClient(factory);
        var created = await Post<BackupTargetDto>(client, "/api/v1/targets/s3", new UpsertS3TargetRequest(
            "prod-minio",
            "http://minio:9000",
            "us-east-1",
            "backup-bucket",
            null,
            true,
            "raw-access-token",
            "raw-secret-token"));

        var json = await client.GetStringAsync("/api/v1/targets");
        Assert.DoesNotContain("raw-access-token", json);
        Assert.DoesNotContain("raw-secret-token", json);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var stored = await db.BackupTargets.SingleAsync(x => x.Id == created.Id);
        Assert.DoesNotContain("raw-access-token", stored.SecretsJson);
        Assert.DoesNotContain("raw-secret-token", stored.SecretsJson);
        Assert.Contains("ciphertext", stored.SecretsJson);
        Assert.Contains("keyId", stored.SecretsJson);

        var export = await client.GetFromJsonAsync<ExportEnvelope>("/api/v1/config/export", JsonOptions);
        var exportJson = JsonSerializer.Serialize(export, JsonOptions);
        Assert.DoesNotContain("raw-access-token", exportJson);
        Assert.DoesNotContain("raw-secret-token", exportJson);
        var keyFileContents = string.Join('\n', Directory.EnumerateFiles(Path.Combine(dataDir, "secrets", "aes-keys")).Select(File.ReadAllText));
        Assert.DoesNotContain(keyFileContents, exportJson);
    }

    [Fact]
    public async Task S3_target_facade_requires_credential_pair_and_preserves_credentials_when_omitted_on_update()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);

        var missingCredentials = await client.PostAsJsonAsync("/api/v1/targets/s3", new UpsertS3TargetRequest("minio", "http://minio:9000", "us-east-1", "bucket", null, true, null, null), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, missingCredentials.StatusCode);

        var partialCredentials = await client.PostAsJsonAsync("/api/v1/targets/s3", new UpsertS3TargetRequest("minio", "http://minio:9000", "us-east-1", "bucket", null, true, "access", null), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, partialCredentials.StatusCode);

        var created = await Post<BackupTargetDto>(client, "/api/v1/targets/s3", new UpsertS3TargetRequest("minio", "http://minio:9000", "us-east-1", "bucket", null, true, "access", "secret"));
        string originalSecrets;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            originalSecrets = (await db.BackupTargets.SingleAsync(x => x.Id == created.Id)).SecretsJson;
        }

        var update = await client.PutAsJsonAsync($"/api/v1/targets/{created.Id}/s3", new UpsertS3TargetRequest("minio-updated", "http://minio:9000", "us-east-1", "bucket", "prefix", true, "new-access", null), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);

        var updated = await Put<BackupTargetDto>(client, $"/api/v1/targets/{created.Id}/s3", new UpsertS3TargetRequest("minio-updated", "http://minio:9000", "us-east-1", "bucket", "prefix", true, null, null));
        Assert.Equal("prefix", updated.Settings["pathPrefix"].GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            Assert.Equal(originalSecrets, (await db.BackupTargets.SingleAsync(x => x.Id == created.Id)).SecretsJson);
        }
    }

    [Fact]
    public async Task Cluster_credentials_can_be_updated_without_changing_topology()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var created = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "prod",
            ClusterMode.Cluster,
            [new UpsertAccessNodeRequest("clickhouse-1"), new UpsertAccessNodeRequest("clickhouse-2")],
            null,
            null,
            2,
            "prod_cluster"));

        var updated = await Post<ClusterDto>(client, $"/api/v1/clusters/{created.Id}/credentials", new UpdateClusterCredentialsRequest("default", "new-secret"));

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(created.Name, updated.Name);
        Assert.Equal(created.Mode, updated.Mode);
        Assert.Equal(2, updated.AccessNodes.Count);
        Assert.Equal("prod_cluster", updated.ClickHouseClusterName);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var stored = await db.ClickHouseClusters.SingleAsync(x => x.Id == created.Id);
        Assert.NotNull(stored.EncryptedPassword);
        Assert.NotEqual("new-secret", stored.EncryptedPassword);
        Assert.True(await db.AuditEntries.AnyAsync(x => x.Action == "update-credentials" && x.EntityType == "cluster" && x.EntityId == created.Id.ToString()));
    }

    [Fact]
    public async Task Aes_key_repository_creates_guid_named_keys_and_credential_protector_uses_key_ids()
    {
        var dataDir = NewTestDataDirectory();
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(new ChoboStorageOptions { DataDirectory = dataDir }))
            .AddSingleton(Options.Create(new ChoboSecurityOptions()))
            .AddSingleton<IAesKeyRepository, FileAesKeyRepository>()
            .AddSingleton<ICredentialProtector, CredentialProtector>()
            .BuildServiceProvider();

        var repository = services.GetRequiredService<IAesKeyRepository>();
        var key = await repository.CreateNewAsync();
        var keyPath = Path.Combine(dataDir, "secrets", "aes-keys", key.KeyId.ToString());

        Assert.True(File.Exists(keyPath));
        Assert.Equal(32, (await repository.GetKeyByIdAsync(key.KeyId))!.KeyBytes.Length);

        var protector = services.GetRequiredService<ICredentialProtector>();
        var secret = await protector.EncryptAsync("credential");

        Assert.NotNull(secret);
        Assert.Equal("credential", await protector.DecryptAsync(secret!.Ciphertext, secret.KeyId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => protector.DecryptAsync(secret.Ciphertext, Guid.NewGuid()));
    }

    [Fact]
    public async Task Startup_recovers_with_fresh_sqlite_when_initialized_marker_exists_but_database_is_missing()
    {
        var dataDir = NewTestDataDirectory();
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(Path.Combine(dataDir, "_initialized"), "initialized");

        await using var factory = CreateFactory(dataDir);
        using var client = AuthenticatedClient(factory);
        var users = await client.GetFromJsonAsync<List<UserDto>>("/api/v1/users", JsonOptions);

        Assert.True(File.Exists(Path.Combine(dataDir, "chobo.db")));
        Assert.Contains(users!, x => x.UserName == "admin" && x.IsActive);
    }

    [Fact]
    public async Task Cluster_create_requires_global_maxdop_and_defaults_node_and_shard_maxdop()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var created = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "dop-defaults",
            ClusterMode.SingleInstance,
            [new UpsertAccessNodeRequest("localhost")],
            null,
            null,
            7));

        Assert.Equal(7, created.BackupRestoreMaxDop);
        Assert.Equal(1, created.NodeMaxDopDefault);
        Assert.Equal(1, created.ShardMaxDopDefault);
        Assert.Empty(created.NodeMaxDopOverrides);
        Assert.Empty(created.ShardMaxDopOverrides);
    }

    [Fact]
    public async Task Queue_endpoints_list_move_and_force_backup_shard_rows()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        var backupId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var shard1 = Guid.NewGuid();
        var shard2 = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var clusterId = Guid.NewGuid();
            db.ClickHouseClusters.Add(new ClickHouseClusterEntity
            {
                Id = clusterId,
                Name = "queue-cluster",
                Mode = ClusterMode.SingleInstance,
                BackupRestoreMaxDop = 3,
                CreatedAt = DateTimeOffset.UtcNow,
                AccessNodes = [new ClickHouseAccessNodeEntity { Host = "localhost", Port = 9000 }]
            });
            db.Backups.Add(new BackupEntity
            {
                Id = backupId,
                TriggerType = BackupTriggerType.Manual,
                Status = BackupRunStatus.Succeeded,
                BackupType = BackupType.Full,
                SourceClusterId = clusterId,
                RequestedByName = "test",
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.BackupTables.Add(new BackupTableEntity
            {
                Id = tableId,
                BackupId = backupId,
                EffectiveBackupType = BackupType.Full,
                Database = "db",
                Table = "tbl",
                Engine = "MergeTree",
                DataBackedUp = true,
                StoragePath = "s3://bucket/db/tbl",
                Status = BackupTableStatus.Queued
            });
            db.BackupTableShards.AddRange(
                new BackupTableShardEntity { Id = shard1, BackupTableId = tableId, EffectiveBackupType = BackupType.Full, SourceShardNumber = 1, SourceShardName = "s1", ReplicaNumber = 1, Host = "node1", Port = 9000, StoragePath = "s3://bucket/db/tbl/1", Status = BackupTableStatus.Queued },
                new BackupTableShardEntity { Id = shard2, BackupTableId = tableId, EffectiveBackupType = BackupType.Full, SourceShardNumber = 2, SourceShardName = "s2", ReplicaNumber = 1, Host = "node2", Port = 9000, StoragePath = "s3://bucket/db/tbl/2", Status = BackupTableStatus.Queued });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureBackupQueueItemsAsync(backupId);
        }

        var listed = await client.GetFromJsonAsync<List<BackupRestoreQueueItemDto>>("/api/v1/queue?kind=Backup&status=queued", JsonOptions);
        Assert.NotNull(listed);
        Assert.Equal(2, listed!.Count);
        var second = listed.Single(x => x.ShardId == shard2);

        var moved = await Post<BackupRestoreQueueItemDto>(client, $"/api/v1/queue/items/{second.Id}/move", new MoveQueueItemRequest(BackupRestoreQueueMoveDirection.Top));
        Assert.Equal(second.Id, moved.Id);
        var afterMove = await client.GetFromJsonAsync<List<BackupRestoreQueueItemDto>>("/api/v1/queue?kind=Backup&status=queued", JsonOptions);
        Assert.Equal(second.Id, afterMove![0].Id);

        var forced = await Post<BackupRestoreQueueItemDto>(client, $"/api/v1/queue/items/{listed[0].Id}/force", new { });
        Assert.True(forced.IsForced);
        Assert.NotNull(forced.ForcedAt);
    }

    [Fact]
    public async Task Cluster_update_replaces_nodes_without_concurrency_errors()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var created = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "prod",
            ClusterMode.Cluster,
            [new UpsertAccessNodeRequest("clickhouse-1"), new UpsertAccessNodeRequest("clickhouse-2")],
            null,
            null,
            3));

        var updated = await Put<ClusterDto>(client, $"/api/v1/clusters/{created.Id}", new UpsertClusterRequest(
            "prod-updated",
            ClusterMode.Cluster,
            [new UpsertAccessNodeRequest("clickhouse-3", 9440, true)],
            null,
            null,
            3));

        Assert.Equal("prod-updated", updated.Name);
        Assert.Single(updated.AccessNodes);
        Assert.Equal("clickhouse-3", updated.AccessNodes[0].Host);
    }

    [Fact]
    public async Task Cluster_update_preserves_clickhouse_settings_when_omitted_and_clears_with_explicit_empty_object()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var created = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "prod-settings",
            ClusterMode.SingleInstance,
            [new UpsertAccessNodeRequest("clickhouse-1")],
            null,
            null,
            3,
            ClickHouseBackupSettings: Settings(("backup_threads", 4)),
            ClickHouseRestoreSettings: Settings(("restore_threads", 2))));

        var updated = await Put<ClusterDto>(client, $"/api/v1/clusters/{created.Id}", new UpsertClusterRequest(
            "prod-settings-renamed",
            ClusterMode.SingleInstance,
            [new UpsertAccessNodeRequest("clickhouse-2")],
            null,
            null,
            3));

        Assert.Equal(4, updated.ClickHouseBackupSettings["backup_threads"].GetInt32());
        Assert.Equal(2, updated.ClickHouseRestoreSettings["restore_threads"].GetInt32());

        var cleared = await Put<ClusterDto>(client, $"/api/v1/clusters/{created.Id}", new UpsertClusterRequest(
            "prod-settings-cleared",
            ClusterMode.SingleInstance,
            [new UpsertAccessNodeRequest("clickhouse-2")],
            null,
            null,
            3,
            ClickHouseBackupSettings: new Dictionary<string, JsonElement>(),
            ClickHouseRestoreSettings: new Dictionary<string, JsonElement>()));

        Assert.Empty(cleared.ClickHouseBackupSettings);
        Assert.Empty(cleared.ClickHouseRestoreSettings);
    }

    [Fact]
    public async Task Policy_update_preserves_clickhouse_settings_when_omitted_and_clears_with_explicit_empty_object()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var cluster = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "policy-source",
            ClusterMode.SingleInstance,
            [new UpsertAccessNodeRequest("clickhouse-1")],
            null,
            null,
            3));
        var target = await Post<BackupTargetDto>(client, "/api/v1/targets/s3", new UpsertS3TargetRequest("s3", "http://minio:9000", "us-east-1", "bucket", null, true, "access", "secret"));
        var created = await Post<BackupPolicyDto>(client, "/api/v1/policies", new UpsertPolicyRequest(
            "policy-settings",
            cluster.Id,
            target.Id,
            PolicySelector.Empty,
            ClickHouseBackupSettings: Settings(("backup_threads", 4)),
            ClickHouseRestoreSettings: Settings(("restore_threads", 2))));

        var updated = await Put<BackupPolicyDto>(client, $"/api/v1/policies/{created.Id}", new UpsertPolicyRequest(
            "policy-settings-renamed",
            cluster.Id,
            target.Id,
            PolicySelector.Empty));

        Assert.Equal(4, updated.ClickHouseBackupSettings["backup_threads"].GetInt32());
        Assert.Equal(2, updated.ClickHouseRestoreSettings["restore_threads"].GetInt32());

        var cleared = await Put<BackupPolicyDto>(client, $"/api/v1/policies/{created.Id}", new UpsertPolicyRequest(
            "policy-settings-cleared",
            cluster.Id,
            target.Id,
            PolicySelector.Empty,
            ClickHouseBackupSettings: new Dictionary<string, JsonElement>(),
            ClickHouseRestoreSettings: new Dictionary<string, JsonElement>()));

        Assert.Empty(cleared.ClickHouseBackupSettings);
        Assert.Empty(cleared.ClickHouseRestoreSettings);
    }

    [Fact]
    public async Task Policy_max_base_age_uses_runtime_default_and_policy_override()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        await Put<RuntimeSettingUpdateResult>(client, "/api/v1/settings/Chobo%3ABackupRestore%3ADefaultMaxAgeHoursForBaseBackup", new UpdateRuntimeSettingRequest("24"));
        var cluster = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "policy-source",
            ClusterMode.SingleInstance,
            [new UpsertAccessNodeRequest("clickhouse-1")],
            null,
            null,
            3));
        var target = await Post<BackupTargetDto>(client, "/api/v1/targets/s3", new UpsertS3TargetRequest("s3", "http://minio:9000", "us-east-1", "bucket", null, true, "access", "secret"));

        var created = await Post<BackupPolicyDto>(client, "/api/v1/policies", new UpsertPolicyRequest(
            "policy-base-age",
            cluster.Id,
            target.Id,
            PolicySelector.Empty));

        Assert.Null(created.MaxAgeHoursForBaseBackup);
        Assert.Equal(24, created.EffectiveMaxAgeHoursForBaseBackup);

        var updated = await Put<BackupPolicyDto>(client, $"/api/v1/policies/{created.Id}", new UpsertPolicyRequest(
            "policy-base-age",
            cluster.Id,
            target.Id,
            PolicySelector.Empty,
            MaxAgeHoursForBaseBackup: 12));

        Assert.Equal(12, updated.MaxAgeHoursForBaseBackup);
        Assert.Equal(12, updated.EffectiveMaxAgeHoursForBaseBackup);
        var listed = await client.GetFromJsonAsync<List<BackupPolicyDto>>("/api/v1/policies", JsonOptions);
        Assert.Contains(listed!, x => x.Id == created.Id && x.MaxAgeHoursForBaseBackup == 12 && x.EffectiveMaxAgeHoursForBaseBackup == 12);
    }
    [Fact]
    public async Task Policy_update_returns_count_only_retention_limits()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var cluster = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "policy-count-retention-source",
            ClusterMode.SingleInstance,
            [new UpsertAccessNodeRequest("clickhouse-1")],
            null,
            null,
            3));
        var target = await Post<BackupTargetDto>(client, "/api/v1/targets/s3", new UpsertS3TargetRequest("s3", "http://minio:9000", "us-east-1", "bucket", null, true, "access", "secret"));
        var created = await Post<BackupPolicyDto>(client, "/api/v1/policies", new UpsertPolicyRequest(
            "policy-count-retention",
            cluster.Id,
            target.Id,
            PolicySelector.Empty));

        Assert.Null(created.Retention);

        var updated = await Put<BackupPolicyDto>(client, $"/api/v1/policies/{created.Id}", new UpsertPolicyRequest(
            "policy-count-retention",
            cluster.Id,
            target.Id,
            PolicySelector.Empty,
            Retention: new BackupRetentionDto(null, null, 2, 2)));

        Assert.NotNull(updated.Retention);
        Assert.Null(updated.Retention!.FullRetentionMinutes);
        Assert.Null(updated.Retention.IncrementalRetentionMinutes);
        Assert.Equal(2, updated.Retention.MinBackupsToKeep);
        Assert.Equal(2, updated.Retention.MinFullBackupsToKeep);

        var listed = await client.GetFromJsonAsync<List<BackupPolicyDto>>("/api/v1/policies", JsonOptions);
        var listedPolicy = Assert.Single(listed!, x => x.Id == created.Id);
        Assert.NotNull(listedPolicy.Retention);
        Assert.Equal(2, listedPolicy.Retention!.MinBackupsToKeep);
        Assert.Equal(2, listedPolicy.Retention.MinFullBackupsToKeep);
    }

    [Fact]
    public async Task Users_can_add_list_and_deactivate_named_access_tokens()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var user = await Post<CreateUserResponse>(client, "/api/v1/users", new CreateUserRequest("operator"));
        var token = await Post<CreateAccessTokenResponse>(client, $"/api/v1/users/{user.UserId}/tokens", new CreateAccessTokenRequest("automation"));

        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        var tokens = await client.GetFromJsonAsync<List<AccessTokenDto>>($"/api/v1/users/{user.UserId}/tokens", JsonOptions);
        Assert.Contains(tokens!, x => x.Id == token.TokenId && x.Name == "automation" && x.IsActive);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/users/{user.UserId}/tokens/{token.TokenId}")).StatusCode);
        tokens = await client.GetFromJsonAsync<List<AccessTokenDto>>($"/api/v1/users/{user.UserId}/tokens", JsonOptions);
        Assert.Contains(tokens!, x => x.Id == token.TokenId && !x.IsActive);
    }

    [Fact]
    public async Task Mutating_actions_create_audit_records_and_config_export_excludes_operational_state()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var user = await Post<CreateUserResponse>(client, "/api/v1/users", new CreateUserRequest("operator"));
        Assert.False(string.IsNullOrWhiteSpace(user.AccessToken));

        var audits = await client.GetFromJsonAsync<PagedResultDto<AuditEntryDto>>("/api/v1/audit?last=20", JsonOptions);
        Assert.Contains(audits!.Items, x => x.Action == "create" && x.EntityType == "user");

        var config = await client.GetFromJsonAsync<ExportEnvelope>("/api/v1/config/export", JsonOptions);
        Assert.NotNull(config);
        Assert.Equal(ChoboApi.ProductVersion, config!.ProductVersion);
        Assert.Empty(config.Data.Backups);
        Assert.Empty(config.Data.Restores);
        Assert.Empty(config.Data.Users);
        Assert.Empty(config.Data.AccessTokens);
    }

    [Fact]
    public async Task Import_rejects_future_schema_without_deleting_existing_data()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        await Post<CreateUserResponse>(client, "/api/v1/users", new CreateUserRequest("operator"));
        var export = await client.GetFromJsonAsync<ExportEnvelope>("/api/v1/data/export", JsonOptions);
        Assert.NotNull(export);

        var invalid = export! with { SchemaVersion = ChoboApi.SchemaVersion + 1 };
        var response = await client.PostAsJsonAsync("/api/v1/data/import", invalid, JsonOptions);
        Assert.False(response.IsSuccessStatusCode);

        var users = await client.GetFromJsonAsync<List<UserDto>>("/api/v1/users", JsonOptions);
        Assert.Contains(users!, x => x.UserName == "operator");
    }

    [Fact]
    public async Task Data_import_rejects_when_local_operation_is_active()
    {
        await using var sourceFactory = CreateFactory();
        var sourceClient = AuthenticatedClient(sourceFactory);
        await SeedFullExportGraphAsync(sourceFactory, includeCredentials: false);
        var export = await sourceClient.GetFromJsonAsync<ExportEnvelope>("/api/v1/data/export", JsonOptions);
        Assert.NotNull(export);

        await using var targetFactory = CreateFactory();
        var targetClient = AuthenticatedClient(targetFactory);
        var activeBackupId = Guid.NewGuid();
        using (var targetScope = targetFactory.Services.CreateScope())
        {
            var db = targetScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var clusterId = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            db.ClickHouseClusters.Add(new ClickHouseClusterEntity
            {
                Id = clusterId,
                Name = "active-import-source",
                Mode = ClusterMode.SingleInstance,
                BackupRestoreMaxDop = 3,
                AccessNodes = [new ClickHouseAccessNodeEntity { Host = "clickhouse-1", Port = 9000 }]
            });
            db.BackupTargets.Add(CreateS3TargetEntity(targetId, "active-import-target", "http://minio:9000", "us-east-1", "backups", null, true, false));
            var tableId = Guid.NewGuid();
            var shardId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;
            db.Backups.Add(new BackupEntity
            {
                Id = activeBackupId,
                TriggerType = BackupTriggerType.Manual,
                Status = BackupRunStatus.Queued,
                BackupType = BackupType.Full,
                SourceClusterId = clusterId,
                TargetId = targetId,
                RequestedByName = "test",
                CreatedAt = createdAt,
                QueuedAt = createdAt
            });
            db.BackupTables.Add(new BackupTableEntity
            {
                Id = tableId,
                BackupId = activeBackupId,
                EffectiveBackupType = BackupType.Full,
                Database = "sales",
                Table = "active_import_guard",
                Engine = "MergeTree",
                DataBackedUp = true,
                StoragePath = "backups/active-import-guard",
                Status = BackupTableStatus.Queued
            });
            db.BackupTableShards.Add(new BackupTableShardEntity
            {
                Id = shardId,
                BackupTableId = tableId,
                EffectiveBackupType = BackupType.Full,
                SourceShardNumber = 1,
                SourceShardName = "single",
                ReplicaNumber = 1,
                Host = "clickhouse-1",
                Port = 9000,
                StoragePath = "backups/active-import-guard/shard-1",
                Status = BackupTableStatus.Queued
            });
            db.BackupRestoreQueueItems.Add(new BackupRestoreQueueItemEntity
            {
                Kind = BackupRestoreQueueKind.Backup,
                OperationId = activeBackupId,
                TableId = tableId,
                ShardId = shardId,
                ClusterId = clusterId,
                LogicalShardNumber = 1,
                LogicalShardName = "single",
                Position = 1000,
                CreatedAt = createdAt
            });
            await db.SaveChangesAsync();
        }

        using (var precheckScope = targetFactory.Services.CreateScope())
        {
            var precheckDb = precheckScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            Assert.True(await precheckDb.Backups.AnyAsync(x =>
                x.Id == activeBackupId &&
                (x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running)));
            Assert.True(await precheckDb.BackupRestoreQueueItems.AnyAsync(x => x.OperationId == activeBackupId && x.CompletedAt == null));
        }
        var targetExportBeforeImport = await targetClient.GetFromJsonAsync<ExportEnvelope>("/api/v1/data/export", JsonOptions);
        Assert.Contains(targetExportBeforeImport!.Data.Backups, x =>
            x.Id == activeBackupId &&
            (x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running));

        var response = await targetClient.PostAsJsonAsync("/api/v1/data/import", export, JsonOptions);
        var responseText = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("queued or running", responseText);
        using var verifyScope = targetFactory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.True(await verifyDb.Backups.AnyAsync(x => x.Id == activeBackupId));
    }
    [Fact]
    public async Task Config_import_preserves_logs_and_audits_and_writes_import_audit()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        await Post<CreateUserResponse>(client, "/api/v1/users", new CreateUserRequest("operator"));
        var export = await client.GetFromJsonAsync<ExportEnvelope>("/api/v1/config/export", JsonOptions);
        Assert.NotNull(export);

        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var timestamp = ToUnixMilliseconds(DateTimeOffset.Parse("2026-05-15T10:11:12+00:00"));
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, Details) VALUES ({timestamp}, 'system', 'existing-config-import-audit', 'test', '{{}}');");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, Properties) VALUES ({timestamp}, 'Information', NULL, 'existing config import log', '{{}}');");
        }

        var response = await client.PostAsJsonAsync("/api/v1/config/import", export, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbAfter = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.True(await dbAfter.AuditEntries.AnyAsync(x => x.Action == "existing-config-import-audit"));
        Assert.True(await dbAfter.ApplicationLogEntries.AnyAsync(x => x.RenderedMessage == "existing config import log"));
        var importAudit = await dbAfter.AuditEntries.SingleAsync(x => x.Action == "import" && x.EntityType == "config");
        Assert.Contains("\"schemaVersion\"", importAudit.Details);
    }

    [Fact]
    public async Task Config_import_accepts_previous_version_config_payload_without_storage_root_path()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var previousVersionConfigJson = JsonSerializer.Serialize(new
        {
            exportVersion = ChoboApi.ExportVersion,
            schemaVersion = ChoboApi.SchemaVersion,
            generatedAt = DateTimeOffset.Parse("2026-05-15T10:11:12+00:00"),
            productVersion = "0.12.0",
            data = new
            {
                users = Array.Empty<object>(),
                accessTokens = Array.Empty<object>(),
                clusters = Array.Empty<object>(),
                backupTargets = Array.Empty<object>(),
                backupPolicies = Array.Empty<object>(),
                backupSchedules = Array.Empty<object>(),
                schemaDefinitions = Array.Empty<object>(),
                backups = Array.Empty<object>(),
                backupTables = Array.Empty<object>(),
                backupTableShards = Array.Empty<object>(),
                restores = Array.Empty<object>(),
                restoreTables = Array.Empty<object>(),
                restoreTableShards = Array.Empty<object>()
            }
        }, JsonOptions);
        using var content = new StringContent(previousVersionConfigJson);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.PostAsync("/api/v1/config/import", content);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var audit = await db.AuditEntries.SingleAsync(x => x.Action == "import" && x.EntityType == "config");
        Assert.Contains("\"schemaVersion\"", audit.Details);
    }
    [Fact]
    public async Task Config_import_treats_encrypted_credentials_as_empty()
    {
        await using var sourceFactory = CreateFactory();
        var ids = await SeedFullExportGraphAsync(sourceFactory, includeCredentials: true);
        var sourceClient = AuthenticatedClient(sourceFactory);
        var export = await sourceClient.GetFromJsonAsync<ExportEnvelope>("/api/v1/config/export", JsonOptions);
        Assert.NotNull(export);
        Assert.NotNull(Assert.Single(export!.Data.Clusters).EncryptedPassword);
        Assert.NotEmpty(Assert.Single(export.Data.BackupTargets).Secrets ?? new Dictionary<string, JsonElement>());

        await using var targetFactory = CreateFactory();
        var targetClient = AuthenticatedClient(targetFactory);
        var response = await targetClient.PostAsJsonAsync("/api/v1/config/import", export, JsonOptions);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = targetFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var cluster = await db.ClickHouseClusters.SingleAsync(x => x.Id == ids.ClusterId);
        var target = await db.BackupTargets.SingleAsync(x => x.Id == ids.TargetId);
        Assert.Equal("prod_cluster", cluster.ClickHouseClusterName);
        Assert.Null(cluster.EncryptedUserName);
        Assert.Null(cluster.EncryptedUserNameKeyId);
        Assert.Null(cluster.EncryptedPassword);
        Assert.Null(cluster.EncryptedPasswordKeyId);
        Assert.Equal("{}", target.SecretsJson);
        Assert.Empty(await db.Backups.ToListAsync());
        var audit = await db.AuditEntries.SingleAsync(x => x.Action == "import" && x.EntityType == "config");
        Assert.Contains("\"credentialsImportedAsEmpty\":true", audit.Details);
    }

    [Fact]
    public async Task Config_import_export_preserves_soft_deleted_configuration_entities()
    {
        await using var sourceFactory = CreateFactory();
        var ids = await SeedFullExportGraphAsync(sourceFactory, includeCredentials: false, softDeletedConfig: true);
        var sourceClient = AuthenticatedClient(sourceFactory);
        var export = await sourceClient.GetFromJsonAsync<ExportEnvelope>("/api/v1/config/export", JsonOptions);
        Assert.NotNull(export);

        var exportedCluster = Assert.Single(export!.Data.Clusters, x => x.Id == ids.ClusterId);
        var exportedTarget = Assert.Single(export.Data.BackupTargets, x => x.Id == ids.TargetId);
        var exportedPolicy = Assert.Single(export.Data.BackupPolicies, x => x.Id == ids.PolicyId);
        var exportedSchedule = Assert.Single(export.Data.BackupSchedules, x => x.Id == ids.ScheduleId);
        Assert.True(exportedCluster.IsDeleted);
        Assert.True(exportedTarget.IsDeleted);
        Assert.True(exportedPolicy.IsDeleted);
        Assert.Equal(72, exportedPolicy.MaxAgeHoursForBaseBackup);
        Assert.True(exportedSchedule.IsDeleted);
        Assert.NotNull(exportedCluster.DeletedAt);
        Assert.NotNull(exportedTarget.DeletedAt);
        Assert.NotNull(exportedPolicy.DeletedAt);
        Assert.NotNull(exportedSchedule.DeletedAt);

        await using var targetFactory = CreateFactory();
        var targetClient = AuthenticatedClient(targetFactory);
        var response = await targetClient.PostAsJsonAsync("/api/v1/config/import", export, JsonOptions);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, responseText);

        using var scope = targetFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.True(await db.ClickHouseClusters.AnyAsync(x => x.Id == ids.ClusterId && x.IsDeleted && x.DeletedAt == exportedCluster.DeletedAt));
        Assert.True(await db.BackupTargets.AnyAsync(x => x.Id == ids.TargetId && x.IsDeleted && x.DeletedAt == exportedTarget.DeletedAt));
        Assert.True(await db.BackupPolicies.AnyAsync(x => x.Id == ids.PolicyId && x.IsDeleted && x.DeletedAt == exportedPolicy.DeletedAt));
        Assert.True(await db.BackupSchedules.AnyAsync(x => x.Id == ids.ScheduleId && x.IsDeleted && x.DeletedAt == exportedSchedule.DeletedAt));
    }

    [Fact]
    public async Task Data_import_export_round_trips_operational_metadata_without_audits_logs_or_credentials()
    {
        await using var sourceFactory = CreateFactory();
        var sourceClient = AuthenticatedClient(sourceFactory);
        var ids = await SeedFullExportGraphAsync(sourceFactory, includeCredentials: true, softDeletedConfig: true);

        using (var sourceScope = sourceFactory.Services.CreateScope())
        {
            var db = sourceScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var timestamp = ToUnixMilliseconds(DateTimeOffset.Parse("2026-05-15T10:11:12+00:00"));
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, Details) VALUES ({timestamp}, 'system', 'source-export-audit', 'test', '{{}}');");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, Properties) VALUES ({timestamp}, 'Information', NULL, 'source export log', '{{}}');");
        }

        var export = await sourceClient.GetFromJsonAsync<ExportEnvelope>("/api/v1/data/export", JsonOptions);
        Assert.NotNull(export);
        Assert.Single(export!.Data.SchemaDefinitions);
        Assert.Single(export.Data.Backups);
        Assert.Single(export.Data.BackupTables);
        Assert.Single(export.Data.BackupTableShards);
        Assert.Single(export.Data.Restores);
        Assert.Single(export.Data.RestoreTables);
        Assert.Single(export.Data.RestoreTableShards);
        var exportedCluster = Assert.Single(export.Data.Clusters, x => x.Id == ids.ClusterId);
        var exportedTarget = Assert.Single(export.Data.BackupTargets, x => x.Id == ids.TargetId);
        var exportedPolicy = Assert.Single(export.Data.BackupPolicies, x => x.Id == ids.PolicyId);
        var exportedSchedule = Assert.Single(export.Data.BackupSchedules, x => x.Id == ids.ScheduleId);
        Assert.Equal("prod_cluster", exportedCluster.ClickHouseClusterName);
        Assert.True(exportedCluster.IsDeleted);
        Assert.True(exportedTarget.IsDeleted);
        Assert.True(exportedPolicy.IsDeleted);
        Assert.Equal(72, exportedPolicy.MaxAgeHoursForBaseBackup);
        Assert.True(exportedSchedule.IsDeleted);
        var legacyExportJson = JsonSerializer.Serialize(export, JsonOptions).Replace("\"storagePath\":", "\"s3Path\":");
        export = JsonSerializer.Deserialize<ExportEnvelope>(legacyExportJson, JsonOptions);
        Assert.NotNull(export);

        await using var targetFactory = CreateFactory();
        var targetClient = AuthenticatedClient(targetFactory);
        using (var targetScope = targetFactory.Services.CreateScope())
        {
            var db = targetScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var timestamp = ToUnixMilliseconds(DateTimeOffset.Parse("2026-05-15T10:12:12+00:00"));
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, Details) VALUES ({timestamp}, 'system', 'target-local-audit', 'test', '{{}}');");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, Properties) VALUES ({timestamp}, 'Information', NULL, 'target local log', '{{}}');");
        }

        var response = await targetClient.PostAsJsonAsync("/api/v1/data/import", export, JsonOptions);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, responseText);

        using var verifyScope = targetFactory.Services.CreateScope();
        var imported = verifyScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var cluster = await imported.ClickHouseClusters.SingleAsync(x => x.Id == ids.ClusterId);
        var target = await imported.BackupTargets.SingleAsync(x => x.Id == ids.TargetId);
        Assert.Equal("prod_cluster", cluster.ClickHouseClusterName);
        Assert.True(cluster.IsDeleted);
        Assert.Equal(exportedCluster.DeletedAt, cluster.DeletedAt);
        Assert.True(target.IsDeleted);
        Assert.Equal(exportedTarget.DeletedAt, target.DeletedAt);
        Assert.Null(cluster.EncryptedUserName);
        Assert.Null(cluster.EncryptedUserNameKeyId);
        Assert.Null(cluster.EncryptedPassword);
        Assert.Null(cluster.EncryptedPasswordKeyId);
        Assert.Equal("{}", target.SecretsJson);
        Assert.True(await imported.BackupPolicies.AnyAsync(x => x.Id == ids.PolicyId && x.IsDeleted && x.DeletedAt == exportedPolicy.DeletedAt && x.MaxAgeHoursForBaseBackup == 72));
        Assert.True(await imported.BackupSchedules.AnyAsync(x => x.Id == ids.ScheduleId && x.IsDeleted && x.DeletedAt == exportedSchedule.DeletedAt));
        Assert.True(await imported.SchemaDefinitions.AnyAsync(x => x.Id == ids.SchemaDefinitionId));
        Assert.True(await imported.Backups.AnyAsync(x => x.Id == ids.BackupId && x.Status == BackupRunStatus.Succeeded));
        Assert.True(await imported.BackupTables.AnyAsync(x => x.Id == ids.BackupTableId && x.SchemaDefinitionId == ids.SchemaDefinitionId));
        Assert.True(await imported.BackupTableShards.AnyAsync(x => x.Id == ids.BackupTableShardId));
        Assert.True(await imported.Restores.AnyAsync(x => x.Id == ids.RestoreId && x.Status == RestoreRunStatus.Succeeded));
        Assert.True(await imported.RestoreTables.AnyAsync(x => x.Id == ids.RestoreTableId));
        Assert.True(await imported.RestoreTableShards.AnyAsync(x => x.Id == ids.RestoreTableShardId));
        Assert.True(await imported.AuditEntries.AnyAsync(x => x.Action == "target-local-audit"));
        Assert.True(await imported.ApplicationLogEntries.AnyAsync(x => x.RenderedMessage == "target local log"));
        Assert.False(await imported.AuditEntries.AnyAsync(x => x.Action == "source-export-audit"));
        Assert.False(await imported.ApplicationLogEntries.AnyAsync(x => x.RenderedMessage == "source export log"));
        var importAudit = await imported.AuditEntries.SingleAsync(x => x.Action == "import" && x.EntityType == "data");
        Assert.Contains("\"credentialsImportedAsEmpty\":true", importAudit.Details);
        Assert.Contains("\"importedBackups\":1", importAudit.Details);
        Assert.Contains("\"importedRestores\":1", importAudit.Details);
    }

    [Theory]
    [InlineData("sourceShardsEmpty", "SourceShards must not be empty")]
    [InlineData("sourceShardsDuplicate", "SourceShards must not contain duplicates")]
    [InlineData("sourceShardsNonPositive", "greater than '0'")]
    [InlineData("targetShardsEmpty", "TargetShards must not be empty")]
    [InlineData("targetShardsDuplicate", "TargetShards must not contain duplicates")]
    [InlineData("targetShardsNonPositive", "greater than '0'")]
    public async Task Restore_initiate_http_rejects_invalid_shard_selection_payloads(string shape, string expectedMessage)
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var request = shape switch
        {
            "sourceShardsEmpty" => new InitiateRestoreRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, false, false, SourceShards: []),
            "sourceShardsDuplicate" => new InitiateRestoreRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, false, false, SourceShards: [1, 1]),
            "sourceShardsNonPositive" => new InitiateRestoreRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, false, false, SourceShards: [0]),
            "targetShardsEmpty" => new InitiateRestoreRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, false, false, Layout: RestoreLayout.Redistribute, TargetShards: []),
            "targetShardsDuplicate" => new InitiateRestoreRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, false, false, Layout: RestoreLayout.Redistribute, TargetShards: [2, 2]),
            "targetShardsNonPositive" => new InitiateRestoreRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, false, false, Layout: RestoreLayout.Redistribute, TargetShards: [0]),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };

        var response = await client.PostAsJsonAsync("/api/v1/restores/initiate", request, JsonOptions);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedMessage, text);
    }

    [Fact]
    public async Task Data_import_marks_in_flight_backup_and_restore_rows_failed()
    {
        await using var sourceFactory = CreateFactory();
        var sourceClient = AuthenticatedClient(sourceFactory);
        var ids = await SeedFullExportGraphAsync(sourceFactory, includeCredentials: false);
        var export = await sourceClient.GetFromJsonAsync<ExportEnvelope>("/api/v1/data/export", JsonOptions);
        Assert.NotNull(export);

        var inFlight = export! with
        {
            Data = export.Data with
            {
                Backups = [export.Data.Backups.Single() with { Status = BackupRunStatus.Running, CompletedAt = null, FailureReason = null }],
                BackupTables = [export.Data.BackupTables.Single() with { Status = BackupTableStatus.Running, CompletedAt = null, Error = null }],
                BackupTableShards = [export.Data.BackupTableShards.Single() with { Status = BackupTableStatus.Queued, CompletedAt = null, Error = null }],
                Restores = [export.Data.Restores.Single() with { Status = RestoreRunStatus.Running, CompletedAt = null, FailureReason = null }],
                RestoreTables = [export.Data.RestoreTables.Single() with { Status = RestoreTableStatus.Running, CompletedAt = null, Error = null }],
                RestoreTableShards = [export.Data.RestoreTableShards.Single() with { Status = RestoreTableStatus.Queued, CompletedAt = null, Error = null }]
            }
        };

        await using var targetFactory = CreateFactory();
        var targetClient = AuthenticatedClient(targetFactory);
        var response = await targetClient.PostAsJsonAsync("/api/v1/data/import", inFlight, JsonOptions);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, responseText);

        using var verifyScope = targetFactory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var backup = await db.Backups.SingleAsync(x => x.Id == ids.BackupId);
        var backupTable = await db.BackupTables.SingleAsync(x => x.Id == ids.BackupTableId);
        var backupShard = await db.BackupTableShards.SingleAsync(x => x.Id == ids.BackupTableShardId);
        var restore = await db.Restores.SingleAsync(x => x.Id == ids.RestoreId);
        var restoreTable = await db.RestoreTables.SingleAsync(x => x.Id == ids.RestoreTableId);
        var restoreShard = await db.RestoreTableShards.SingleAsync(x => x.Id == ids.RestoreTableShardId);
        Assert.Equal(BackupRunStatus.Failed, backup.Status);
        Assert.Equal(BackupTableStatus.Failed, backupTable.Status);
        Assert.Equal(BackupTableStatus.Failed, backupShard.Status);
        Assert.Equal(RestoreRunStatus.Failed, restore.Status);
        Assert.Equal(RestoreTableStatus.Failed, restoreTable.Status);
        Assert.Equal(RestoreTableStatus.Failed, restoreShard.Status);
        Assert.Contains("Imported in-flight operation", backup.FailureReason);
        Assert.Contains("Imported in-flight operation", backupTable.Error);
        Assert.Contains("Imported in-flight operation", restore.FailureReason);
        Assert.NotNull(backup.CompletedAt);
        Assert.NotNull(restore.CompletedAt);
        var audit = await db.AuditEntries.SingleAsync(x => x.Action == "import" && x.EntityType == "data");
        Assert.Contains("\"inFlightImportedAsFailed\":6", audit.Details);
    }

    [Fact]
    public async Task Data_import_skips_malformed_operational_references_and_preserves_local_users()
    {
        await using var sourceFactory = CreateFactory();
        var sourceClient = AuthenticatedClient(sourceFactory);
        await SeedFullExportGraphAsync(sourceFactory, includeCredentials: false);
        var export = await sourceClient.GetFromJsonAsync<ExportEnvelope>("/api/v1/data/export", JsonOptions);
        Assert.NotNull(export);

        var malformed = export! with
        {
            Data = export.Data with
            {
                BackupTables = [export.Data.BackupTables.Single() with { SchemaDefinitionId = Guid.NewGuid() }]
            }
        };

        await using var targetFactory = CreateFactory();
        var targetClient = AuthenticatedClient(targetFactory);
        await Post<CreateUserResponse>(targetClient, "/api/v1/users", new CreateUserRequest("keep-me"));
        var response = await targetClient.PostAsJsonAsync("/api/v1/data/import", malformed, JsonOptions);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, responseText);

        using var scope = targetFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.True(await db.Users.AnyAsync(x => x.UserName == "keep-me"));
        Assert.True(await db.Backups.AnyAsync(x => x.Id == export.Data.Backups.Single().Id));
        Assert.False(await db.BackupTables.AnyAsync(x => x.Id == export.Data.BackupTables.Single().Id));
        var audit = await db.AuditEntries.SingleAsync(x => x.Action == "import" && x.EntityType == "data");
        Assert.Contains("\"skippedRows\":", audit.Details);
    }

    [Fact]
    public void Backup_retention_contract_reads_legacy_retention_minutes()
    {
        var retention = JsonSerializer.Deserialize<BackupRetentionDto>(
            """{"retentionMinutes":60,"minBackupsToKeep":2}""",
            JsonOptions);

        Assert.NotNull(retention);
        Assert.Equal(60, retention!.FullRetentionMinutes);
        Assert.Equal(60, retention.IncrementalRetentionMinutes);
        Assert.Equal(2, retention.MinBackupsToKeep);
        Assert.Equal(0, retention.MinFullBackupsToKeep);
        Assert.DoesNotContain("retentionMinutes", JsonSerializer.Serialize(retention, JsonOptions));
    }

    [Fact]
    public async Task Audit_details_include_previous_and_current_configuration_and_deactivation_details()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);

        var created = await Post<BackupTargetDto>(client, "/api/v1/targets/s3", new UpsertS3TargetRequest(
            "minio",
            "http://minio:9000",
            "us-east-1",
            "bucket-a",
            null,
            true,
            "raw-audit-access-token",
            "raw-audit-secret-token"));

        var updated = await Put<BackupTargetDto>(client, $"/api/v1/targets/{created.Id}/s3", new UpsertS3TargetRequest(
            "minio-renamed",
            "http://minio:9000",
            "us-east-1",
            "bucket-b",
            "prefix",
            true,
            null,
            null));

        var user = await Post<CreateUserResponse>(client, "/api/v1/users", new CreateUserRequest("operator"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/users/{user.UserId}")).StatusCode);

        var audits = await client.GetFromJsonAsync<PagedResultDto<AuditEntryDto>>("/api/v1/audit?last=20", JsonOptions) ?? throw new InvalidOperationException("Audit query returned no data.");
        var updateAudit = audits.Items.First(x => x.Action == "update" && x.EntityType == "backup-target" && x.EntityId == created.Id.ToString());
        Assert.Equal("minio", updateAudit.Details.GetProperty("previous").GetProperty("name").GetString());
        Assert.Equal("bucket-a", updateAudit.Details.GetProperty("previous").GetProperty("settings").GetProperty("bucket").GetString());
        Assert.Equal("minio-renamed", updateAudit.Details.GetProperty("current").GetProperty("name").GetString());
        Assert.Equal("bucket-b", updateAudit.Details.GetProperty("current").GetProperty("settings").GetProperty("bucket").GetString());
        Assert.Equal(updated.Id.ToString(), updateAudit.Details.GetProperty("current").GetProperty("id").GetString());
        Assert.DoesNotContain("raw-audit-access-token", updateAudit.Details.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-audit-secret-token", updateAudit.Details.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var deactivateAudit = audits.Items.First(x => x.Action == "deactivate" && x.EntityType == "user" && x.EntityId == user.UserId.ToString());
        Assert.Equal("operator", deactivateAudit.Details.GetProperty("deactivated").GetProperty("userName").GetString());
        Assert.False(deactivateAudit.Details.GetProperty("current").GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Backup_api_summarizes_schema_only_1000_table_backup_without_loading_tables()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        Guid backupId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            var cluster = new ClickHouseClusterEntity
            {
                Name = "large-source",
                Mode = ClusterMode.SingleInstance,
                AccessNodes = [new ClickHouseAccessNodeEntity { Host = "localhost", Port = 9000 }]
            };
            var backup = new BackupEntity
            {
                TriggerType = BackupTriggerType.Manual,
                Status = BackupRunStatus.Succeeded,
                BackupType = BackupType.Full,
                ContentMode = BackupContentMode.SchemaOnly,
                SourceCluster = cluster,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAt = DateTimeOffset.UtcNow
            };
            for (var i = 0; i < 1000; i++)
            {
                var tableName = $"table_{i:0000}";
                backup.Tables.Add(new BackupTableEntity
                {
                    Database = "large_schema",
                    Table = tableName,
                    Engine = "MergeTree",
                    DataBackedUp = false,
                    Status = BackupTableStatus.Succeeded,
                    ClickHouseStatus = "SCHEMA_ONLY",
                    CompletedAt = backup.CompletedAt,
                    StoragePath = $"schema-only/large_schema/{tableName}",
                    SchemaDefinition = new SchemaDefinitionEntity
                    {
                        SchemaHash = $"large_schema.{tableName}.schema",
                        Database = "large_schema",
                        Table = tableName,
                        Engine = "MergeTree",
                        CreateTableSql = $"CREATE TABLE large_schema.{tableName} (id UInt64) ENGINE = MergeTree ORDER BY id",
                        ColumnsJson = "[]"
                    }
                });
            }

            db.Backups.Add(backup);
            await db.SaveChangesAsync();
            backupId = backup.Id;
        }

        var list = await client.GetFromJsonAsync<List<BackupDto>>("/api/v1/backups?includeTables=false", JsonOptions);
        var summary = await client.GetFromJsonAsync<BackupDto>($"/api/v1/backups/{backupId}?includeTables=false", JsonOptions);
        var detail = await client.GetFromJsonAsync<BackupDto>($"/api/v1/backups/{backupId}?includeTables=true", JsonOptions);

        var listed = Assert.Single(list!, x => x.Id == backupId);
        Assert.Equal(1000, listed.TableCount);
        Assert.Empty(listed.Tables);
        Assert.NotNull(summary);
        Assert.Equal(1000, summary!.TableCount);
        Assert.Empty(summary.Tables);
        Assert.NotNull(detail);
        Assert.Equal(1000, detail!.TableCount);
        Assert.Equal(1000, detail.Tables.Count);
    }

    [Fact]
    public async Task Backup_api_filters_by_created_time_window()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var cluster = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "prod",
            ClusterMode.SingleInstance,
            [new UpsertAccessNodeRequest("localhost")],
            null,
            null,
            3));
        var target = await Post<BackupTargetDto>(client, "/api/v1/targets/s3", new UpsertS3TargetRequest(
            "minio",
            "http://minio:9000",
            "us-east-1",
            "bucket",
            null,
            true,
            "raw-audit-access-token",
            "raw-audit-secret-token"));
        var now = DateTimeOffset.UtcNow;
        var recent = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Succeeded,
            BackupType = BackupType.Full,
            SourceClusterId = cluster.Id,
            TargetId = target.Id,
            CreatedAt = now.AddDays(-3),
            StartedAt = now.AddDays(-3).AddMinutes(1),
            CompletedAt = now.AddDays(-3).AddMinutes(2)
        };
        var old = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Succeeded,
            BackupType = BackupType.Full,
            SourceClusterId = cluster.Id,
            TargetId = target.Id,
            CreatedAt = now.AddDays(-30),
            StartedAt = now.AddDays(-30).AddMinutes(1),
            CompletedAt = now.AddDays(-30).AddMinutes(2)
        };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            db.Backups.AddRange(recent, old);
            await db.SaveChangesAsync();
        }

        var from = Uri.EscapeDataString(now.AddDays(-14).ToString("O"));
        var to = Uri.EscapeDataString(now.ToString("O"));
        var list = await client.GetFromJsonAsync<List<BackupDto>>($"/api/v1/backups?includeTables=false&from={from}&to={to}", JsonOptions);

        Assert.Contains(list!, x => x.Id == recent.Id);
        Assert.DoesNotContain(list!, x => x.Id == old.Id);
    }

    [Fact]
    public async Task Policy_schedule_logs_and_clear_paths_work()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var cluster = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "prod",
            ClusterMode.SingleInstance,
            [new UpsertAccessNodeRequest("localhost")],
            null,
            null,
            3));
        var target = await Post<BackupTargetDto>(client, "/api/v1/targets/s3", new UpsertS3TargetRequest(
            "minio",
            "http://minio:9000",
            "us-east-1",
            "bucket",
            null,
            true,
            "raw-audit-access-token",
            "raw-audit-secret-token"));
        var policy = await Post<BackupPolicyDto>(client, "/api/v1/policies", new UpsertPolicyRequest("all", cluster.Id, target.Id, PolicySelector.Empty));
        var eval = await Post<PolicyEvaluationDto>(client, $"/api/v1/policies/{policy.Id}/evaluate", new PolicyEvaluationRequest(new PolicyInventory([new PolicyInventoryTable("sales", "orders")])));
        Assert.Equal(policy.Id, eval.PolicyId);
        Assert.Equal(cluster.Id, eval.SourceClusterId);
        Assert.Equal(1, eval.SelectorJsonVersion);
        Assert.Contains(eval.Tables, x => x.Database == "sales" && x.Table == "orders");

        var schedule = await Post<BackupScheduleDto>(client, "/api/v1/schedules", new UpsertScheduleRequest("nightly", policy.Id, BackupType.Full, "0 0 2 * * ?", "UTC", true, null, "nightly full"));
        Assert.True(schedule.IsEnabled);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/schedules/{schedule.Id}/disable", null)).StatusCode);

        var logs = await client.GetFromJsonAsync<PagedResultDto<ApplicationLogEntryDto>>("/api/v1/logs?last=10", JsonOptions);
        Assert.NotNull(logs);
        var clear = await client.PostAsJsonAsync("/api/v1/logs/clear", new ClearApplicationLogsRequest(DateTimeOffset.UtcNow.AddDays(1)), JsonOptions);
        Assert.True(clear.IsSuccessStatusCode);
    }

    [Fact]
    public async Task List_endpoints_can_include_soft_deleted_configuration_entities()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var cluster = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "deleted-prod",
            ClusterMode.SingleInstance,
            [new UpsertAccessNodeRequest("localhost")],
            null,
            null,
            3));
        var target = await Post<BackupTargetDto>(client, "/api/v1/targets/s3", new UpsertS3TargetRequest(
            "deleted-minio",
            "http://minio:9000",
            "us-east-1",
            "bucket",
            null,
            true,
            "access",
            "secret"));
        var policy = await Post<BackupPolicyDto>(client, "/api/v1/policies", new UpsertPolicyRequest("deleted-all", cluster.Id, target.Id, PolicySelector.Empty));
        var schedule = await Post<BackupScheduleDto>(client, "/api/v1/schedules", new UpsertScheduleRequest("deleted-nightly", policy.Id, BackupType.Full, "0 0 2 * * ?", "UTC", true, null, null));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/policies/{policy.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/targets/{target.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/clusters/{cluster.Id}")).StatusCode);

        var activePolicies = await client.GetFromJsonAsync<List<BackupPolicyDto>>("/api/v1/policies", JsonOptions);
        var allPolicies = await client.GetFromJsonAsync<List<BackupPolicyDto>>("/api/v1/policies?includeDeleted=true", JsonOptions);
        var activeSchedules = await client.GetFromJsonAsync<List<BackupScheduleDto>>("/api/v1/schedules", JsonOptions);
        var allSchedules = await client.GetFromJsonAsync<List<BackupScheduleDto>>("/api/v1/schedules?includeDeleted=true", JsonOptions);
        var activeTargets = await client.GetFromJsonAsync<List<BackupTargetDto>>("/api/v1/targets", JsonOptions);
        var allTargets = await client.GetFromJsonAsync<List<BackupTargetDto>>("/api/v1/targets?includeDeleted=true", JsonOptions);
        var activeClusters = await client.GetFromJsonAsync<List<ClusterDto>>("/api/v1/clusters", JsonOptions);
        var allClusters = await client.GetFromJsonAsync<List<ClusterDto>>("/api/v1/clusters?includeDeleted=true", JsonOptions);

        Assert.DoesNotContain(activePolicies!, x => x.Id == policy.Id);
        Assert.Contains(allPolicies!, x => x.Id == policy.Id && x.IsDeleted);
        Assert.DoesNotContain(activeSchedules!, x => x.Id == schedule.Id);
        Assert.Contains(allSchedules!, x => x.Id == schedule.Id && x.IsDeleted);
        Assert.DoesNotContain(activeTargets!, x => x.Id == target.Id);
        Assert.Contains(allTargets!, x => x.Id == target.Id && x.IsDeleted);
        Assert.DoesNotContain(activeClusters!, x => x.Id == cluster.Id);
        Assert.Contains(allClusters!, x => x.Id == cluster.Id && x.IsDeleted);
    }

    [Fact]
    public async Task Controller_validation_rejects_invalid_request_shapes()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);

        var userResponse = await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(" "), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, userResponse.StatusCode);
        Assert.Contains("Name is required", await userResponse.Content.ReadAsStringAsync());

        var clusterResponse = await client.PostAsJsonAsync(
            "/api/v1/clusters",
            new UpsertClusterRequest("prod", ClusterMode.SingleInstance, [new UpsertAccessNodeRequest("", 70000)], null, null, 1),
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, clusterResponse.StatusCode);
        var clusterError = await clusterResponse.Content.ReadAsStringAsync();
        Assert.Contains("Host", clusterError);
        Assert.Contains("Port", clusterError);

        var scheduleResponse = await client.PostAsJsonAsync(
            "/api/v1/schedules",
            new UpsertScheduleRequest("nightly", Guid.NewGuid(), BackupType.Full, "0 0 99 * * ?", "UTC", true, null, null),
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, scheduleResponse.StatusCode);
        Assert.Contains("CronExpression is invalid", await scheduleResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Application_logging_does_not_delete_chobo_state()
    {
        await using var factory = CreateFactory();
        _ = AuthenticatedClient(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<Serilog.ILogger>().ForContext<ChoboFoundationTests>();
            logger.Information("Regression log entry for application log storage.");
        }

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.True(await db.Users.AnyAsync(x => x.UserName == "admin" && x.IsActive));
        Assert.True(await db.AccessTokens.AnyAsync(x => x.IsActive));
        Assert.True(await db.ApplicationLogEntries.AnyAsync(x => x.RenderedMessage.Contains("Regression log entry")));
    }

    [Fact]
    public async Task Audit_clear_api_deletes_old_records_and_writes_clear_audit()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var cutoff = DateTimeOffset.UtcNow.AddYears(-1);

        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, Details) VALUES ({ToUnixMilliseconds(cutoff.AddMinutes(-10))}, 'system', 'old-audit', 'test', '{{}}');");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, Details) VALUES ({ToUnixMilliseconds(cutoff.AddMinutes(10))}, 'system', 'new-audit', 'test', '{{}}');");
        }

        var response = await client.PostAsJsonAsync("/api/v1/audit/clear", new ClearAuditEntriesRequest(cutoff), JsonOptions);
        Assert.True(response.IsSuccessStatusCode);

        using var scope = factory.Services.CreateScope();
        var dbAfter = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.False(await dbAfter.AuditEntries.AnyAsync(x => x.Action == "old-audit"));
        Assert.True(await dbAfter.AuditEntries.AnyAsync(x => x.Action == "new-audit"));
        var clearAudit = await dbAfter.AuditEntries.SingleAsync(x => x.Action == "clear" && x.EntityType == "audit");
        Assert.Contains("deleted", clearAudit.Details);
    }

    [Fact]
    public async Task Data_retention_background_service_purges_old_logs_and_audits_by_configured_timestamps()
    {
        await using var factory = CreateFactory();
        _ = AuthenticatedClient(factory);
        var cutoff = DateTimeOffset.UtcNow.AddYears(-1);

        using var seedScope = factory.Services.CreateScope();
        var db = seedScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, Properties) VALUES ({ToUnixMilliseconds(cutoff.AddMinutes(-10))}, 'Information', NULL, 'old log', '{{}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, Properties) VALUES ({ToUnixMilliseconds(cutoff.AddMinutes(10))}, 'Information', NULL, 'new log', '{{}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, Details) VALUES ({ToUnixMilliseconds(cutoff.AddMinutes(-10))}, 'system', 'old', 'test', '{{}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, Details) VALUES ({ToUnixMilliseconds(cutoff.AddMinutes(10))}, 'system', 'new', 'test', '{{}}');");

        var service = new DataRetentionBackgroundService(
            factory.Services,
            TestOptionsMonitor.Create(new ChoboDataRetentionOptions
            {
                LogsBefore = cutoff,
                AuditsBefore = cutoff
            }),
            TimeProvider.System,
            Serilog.Core.Logger.None);

        var deleted = await service.PurgeOnceAsync();

        Assert.True(deleted.LogsDeleted >= 1);
        Assert.True(deleted.AuditsDeleted >= 1);
        Assert.False(await db.ApplicationLogEntries.AnyAsync(x => x.RenderedMessage == "old log"));
        Assert.True(await db.ApplicationLogEntries.AnyAsync(x => x.RenderedMessage == "new log"));
        Assert.False(await db.AuditEntries.AnyAsync(x => x.Action == "old"));
        Assert.True(await db.AuditEntries.AnyAsync(x => x.Action == "new"));
        Assert.True(await db.AuditEntries.AnyAsync(x => x.Action == "retention-purge" && x.EntityType == "data-retention"));
    }

    [Fact]
    public async Task Data_retention_background_service_hard_deletes_old_successfully_deleted_backup_and_restore_records()
    {
        await using var factory = CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Chobo:DataRetention:DeletedBackupRestoreRecordRetention"] = "00:00:00"
        });
        _ = AuthenticatedClient(factory);
        var now = DateTimeOffset.Parse("2026-05-15T10:00:00+00:00");
        var oldDeletedAt = now.AddDays(-91);
        var recentDeletedAt = now.AddDays(-10);
        var clusterId = Guid.NewGuid();
        var oldBackupId = Guid.NewGuid();
        var oldBackupTableId = Guid.NewGuid();
        var oldBackupShardId = Guid.NewGuid();
        var restoreId = Guid.NewGuid();
        var restoreTableId = Guid.NewGuid();
        var restoreShardId = Guid.NewGuid();
        var recentBackupId = Guid.NewGuid();
        var failedCleanupBackupId = Guid.NewGuid();

        using var seedScope = factory.Services.CreateScope();
        var db = seedScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        db.ClickHouseClusters.Add(new ClickHouseClusterEntity
        {
            Id = clusterId,
            Name = "retention-source",
            Mode = ClusterMode.SingleInstance,
            BackupRestoreMaxDop = 1,
            AccessNodes = [new ClickHouseAccessNodeEntity { Host = "localhost", Port = 9000 }]
        });
        db.Backups.AddRange(
            new BackupEntity
            {
                Id = oldBackupId,
                TriggerType = BackupTriggerType.Manual,
                Status = BackupRunStatus.BackupExpiredDeleted,
                BackupType = BackupType.Full,
                ContentMode = BackupContentMode.SchemaOnly,
                SourceClusterId = clusterId,
                RequestedByName = "operator",
                CreatedAt = oldDeletedAt.AddDays(-1),
                CompletedAt = oldDeletedAt.AddHours(-1),
                DeletedAt = oldDeletedAt,
                DeletionStartedAt = oldDeletedAt.AddMinutes(-1),
                DeletionAttemptCount = 1
            },
            new BackupEntity
            {
                Id = recentBackupId,
                TriggerType = BackupTriggerType.Manual,
                Status = BackupRunStatus.BackupExpiredDeleted,
                BackupType = BackupType.Full,
                ContentMode = BackupContentMode.SchemaOnly,
                SourceClusterId = clusterId,
                RequestedByName = "operator",
                CreatedAt = recentDeletedAt.AddDays(-1),
                CompletedAt = recentDeletedAt.AddHours(-1),
                DeletedAt = recentDeletedAt,
                DeletionStartedAt = recentDeletedAt.AddMinutes(-1),
                DeletionAttemptCount = 1
            },
            new BackupEntity
            {
                Id = failedCleanupBackupId,
                TriggerType = BackupTriggerType.Manual,
                Status = BackupRunStatus.BackupExpiredDeleted,
                BackupType = BackupType.Full,
                ContentMode = BackupContentMode.SchemaOnly,
                SourceClusterId = clusterId,
                RequestedByName = "operator",
                CreatedAt = oldDeletedAt.AddDays(-1),
                CompletedAt = oldDeletedAt.AddHours(-1),
                DeletedAt = oldDeletedAt,
                DeletionStartedAt = oldDeletedAt.AddMinutes(-1),
                DeletionError = "storage delete failed",
                DeletionAttemptCount = 1
            });
        db.BackupTables.Add(new BackupTableEntity
        {
            Id = oldBackupTableId,
            BackupId = oldBackupId,
            EffectiveBackupType = BackupType.Full,
            Database = "sales",
            Table = "orders",
            Engine = "MergeTree",
            Status = BackupTableStatus.Succeeded,
            StoragePath = "backups/old/orders"
        });
        db.BackupTableShards.Add(new BackupTableShardEntity
        {
            Id = oldBackupShardId,
            BackupTableId = oldBackupTableId,
            EffectiveBackupType = BackupType.Full,
            SourceShardNumber = 1,
            ReplicaNumber = 1,
            Host = "localhost",
            Port = 9000,
            StoragePath = "backups/old/orders/shard-1",
            Status = BackupTableStatus.Succeeded
        });
        db.Restores.Add(new RestoreEntity
        {
            Id = restoreId,
            BackupId = oldBackupId,
            TargetClusterId = clusterId,
            Status = RestoreRunStatus.Succeeded,
            RequestedByName = "operator",
            CreatedAt = oldDeletedAt.AddMinutes(10),
            CompletedAt = oldDeletedAt.AddMinutes(20)
        });
        db.RestoreTables.Add(new RestoreTableEntity
        {
            Id = restoreTableId,
            RestoreId = restoreId,
            BackupTableId = oldBackupTableId,
            SourceDatabase = "sales",
            SourceTable = "orders",
            TargetDatabase = "sales_restore",
            TargetTable = "orders",
            Status = RestoreTableStatus.Succeeded
        });
        db.RestoreTableShards.Add(new RestoreTableShardEntity
        {
            Id = restoreShardId,
            RestoreTableId = restoreTableId,
            BackupTableShardId = oldBackupShardId,
            SourceShardNumber = 1,
            TargetShardNumber = 1,
            TargetReplicaNumber = 1,
            TargetHost = "localhost",
            TargetPort = 9000,
            LayoutRole = "primary",
            RestoreDatabase = "sales_restore",
            RestoreTableName = "orders",
            Status = RestoreTableStatus.Succeeded
        });
        await db.SaveChangesAsync();

        var service = new DataRetentionBackgroundService(
            factory.Services,
            TestOptionsMonitor.Create(new ChoboDataRetentionOptions { DeletedBackupRestoreRecordRetention = TimeSpan.FromDays(90) }),
            new MutableTimeProvider(now),
            Serilog.Core.Logger.None);

        var deleted = await service.PurgeOnceAsync();

        Assert.Equal(1, deleted.BackupRecordsDeleted);
        Assert.Equal(1, deleted.RestoreRecordsDeleted);
        Assert.False(await db.Backups.AnyAsync(x => x.Id == oldBackupId));
        Assert.False(await db.BackupTables.AnyAsync(x => x.Id == oldBackupTableId));
        Assert.False(await db.BackupTableShards.AnyAsync(x => x.Id == oldBackupShardId));
        Assert.False(await db.Restores.AnyAsync(x => x.Id == restoreId));
        Assert.False(await db.RestoreTables.AnyAsync(x => x.Id == restoreTableId));
        Assert.False(await db.RestoreTableShards.AnyAsync(x => x.Id == restoreShardId));
        Assert.True(await db.Backups.AnyAsync(x => x.Id == recentBackupId));
        Assert.True(await db.Backups.AnyAsync(x => x.Id == failedCleanupBackupId));
        var audit = await db.AuditEntries.SingleAsync(x => x.Action == "retention-purge" && x.EntityType == "data-retention");
        Assert.Contains("\"backupRecordsDeleted\":1", audit.Details);
        Assert.Contains("\"restoreRecordsDeleted\":1", audit.Details);
    }

    [Fact]
    public async Task Sqlite_self_backup_background_service_creates_timestamped_copy_and_audit()
    {
        var dataDir = NewTestDataDirectory();
        var backupDir = Path.Combine(dataDir, "self-backups");
        await using var factory = CreateFactory(dataDir);
        _ = AuthenticatedClient(factory);
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-05-15T10:11:12.123+00:00"));
        var service = new SqliteSelfBackupBackgroundService(
            factory.Services,
            TestOptionsMonitor.Create(new ChoboSqliteSelfBackupOptions
            {
                Enabled = true,
                Directory = backupDir,
                BackupInterval = TimeSpan.FromDays(1)
            }),
            Options.Create(new ChoboStorageOptions { DataDirectory = dataDir }),
            time,
            Serilog.Core.Logger.None);

        var backupPath = await service.RunOnceAsync();
        var secondBackupPath = await service.RunOnceAsync();

        Assert.Equal(Path.Combine(backupDir, "chobo-20260515-101112123Z.db"), backupPath);
        Assert.Null(secondBackupPath);
        Assert.True(File.Exists(backupPath));
        Assert.Equal(1, await CountRowsAsync(backupPath!, "Users"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var state = await db.SqliteSelfBackupStates.SingleAsync();
        Assert.Equal(time.GetUtcNow(), state.LastBackupAt);
        Assert.Equal(backupPath, state.LastBackupPath);
        Assert.Null(state.LastError);
        Assert.True(await db.AuditEntries.AnyAsync(x => x.Action == "sqlite-self-backup-created" && x.EntityType == "sqlite-self-backup"));
    }

    [Fact]
    public async Task Sqlite_self_backup_background_service_records_failure_state_and_audit()
    {
        var dataDir = NewTestDataDirectory();
        var invalidBackupDirectory = Path.Combine(dataDir, "not-a-directory");
        await using var factory = CreateFactory(dataDir);
        _ = AuthenticatedClient(factory);
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(invalidBackupDirectory, "this blocks directory creation");
        var service = new SqliteSelfBackupBackgroundService(
            factory.Services,
            TestOptionsMonitor.Create(new ChoboSqliteSelfBackupOptions
            {
                Enabled = true,
                Directory = invalidBackupDirectory,
                BackupInterval = TimeSpan.FromDays(1)
            }),
            Options.Create(new ChoboStorageOptions { DataDirectory = dataDir }),
            new MutableTimeProvider(DateTimeOffset.Parse("2026-05-15T10:11:12.123+00:00")),
            Serilog.Core.Logger.None);

        await Assert.ThrowsAsync<IOException>(() => service.RunOnceAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var state = await db.SqliteSelfBackupStates.SingleAsync();
        Assert.Null(state.LastBackupAt);
        Assert.NotNull(state.LastAttemptAt);
        Assert.Contains("not-a-directory", state.LastError);
        Assert.True(await db.AuditEntries.AnyAsync(x => x.Action == "sqlite-self-backup-failed" && x.EntityType == "sqlite-self-backup" && x.Details.Contains("not-a-directory")));
    }

    [Fact]
    public async Task Sqlite_time_columns_are_stored_as_unix_millisecond_integers()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<Serilog.ILogger>().ForContext<ChoboFoundationTests>();
            logger.Information("Timestamp storage regression log entry.");
        }

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.Equal("INTEGER", await GetColumnTypeAsync(db, "AuditEntries", "Timestamp"));
        Assert.Equal("INTEGER", await GetColumnTypeAsync(db, "ApplicationLogEntries", "Timestamp"));
        Assert.Equal("INTEGER", await GetColumnTypeAsync(db, "Users", "CreatedAt"));
        Assert.Equal("INTEGER", await GetColumnTypeAsync(db, "BackupSchedules", "CreatedAt"));

        var audits = await client.GetFromJsonAsync<PagedResultDto<AuditEntryDto>>("/api/v1/audit?last=5", JsonOptions);
        var logs = await client.GetFromJsonAsync<PagedResultDto<ApplicationLogEntryDto>>("/api/v1/logs?last=20", JsonOptions);
        var rawAuditJson = await client.GetStringAsync("/api/v1/audit?last=5");
        Assert.Contains(audits!.Items, x => x.Action == "initialize" && x.Timestamp > DateTimeOffset.UnixEpoch);
        Assert.Contains(logs!.Items, x => x.Message.Contains("Timestamp storage regression log entry") && x.Timestamp > DateTimeOffset.UnixEpoch);
        Assert.Contains("+00:00", rawAuditJson);
        Assert.DoesNotContain("\\u002B", rawAuditJson);
    }

    [Fact]
    public async Task Runtime_settings_api_lists_non_hidden_settings_and_persists_overlay_edits()
    {
        var dataDir = NewTestDataDirectory();
        await using var factory = CreateFactory(dataDir);
        var client = AuthenticatedClient(factory);

        var list = await client.GetFromJsonAsync<RuntimeSettingsListDto>("/api/v1/settings", JsonOptions);
        Assert.Contains(list!.Items, x => x.Key == "Chobo:BackupRestore:PollInterval" && x.ValueType == RuntimeSettingValueType.TimeSpan && x.ApplyMode == RuntimeSettingApplyMode.Live);
        Assert.Contains(list.Items, x => x.Key == "Chobo:DatabaseLogging:SlowQueryThreshold" && x.EffectiveValue == "00:00:02" && x.ValueType == RuntimeSettingValueType.TimeSpan && x.ApplyMode == RuntimeSettingApplyMode.Live);
        Assert.Contains(list.Items, x => x.Key == "Chobo:Sqlite:JournalMode" && x.EffectiveValue == "WAL" && x.ValueType == RuntimeSettingValueType.String && x.ApplyMode == RuntimeSettingApplyMode.Live);
        Assert.Contains(list.Items, x => x.Key == "Chobo:Sqlite:BusyTimeout" && x.EffectiveValue == "00:00:05" && x.ValueType == RuntimeSettingValueType.TimeSpan && x.ApplyMode == RuntimeSettingApplyMode.Live);
        Assert.Contains(list.Items, x => x.Key == "Chobo:Sqlite:WalAutoCheckpoint" && x.EffectiveValue == "1000" && x.ValueType == RuntimeSettingValueType.Integer && x.ApplyMode == RuntimeSettingApplyMode.Live);
        Assert.Contains(list.Items, x => x.Key == "Chobo:ClusterMetadata:CacheDuration" && x.EffectiveValue == "01:00:00" && x.ValueType == RuntimeSettingValueType.TimeSpan && x.ApplyMode == RuntimeSettingApplyMode.Live);
        Assert.Contains(list.Items, x => x.Key == "Chobo:ClusterMetadata:RefreshInterval" && x.EffectiveValue == "00:30:00" && x.ValueType == RuntimeSettingValueType.TimeSpan && x.ApplyMode == RuntimeSettingApplyMode.Live);
        Assert.DoesNotContain(list.Items, x => x.Key == "Chobo:EncryptionKeyBase64");
        Assert.DoesNotContain(list.Items, x => x.Key == "Chobo:Init:AccessToken");
        Assert.DoesNotContain(list.Items, x => x.Key == "Chobo:Settings:HiddenKeys");

        var response = await client.PutAsJsonAsync("/api/v1/settings/Chobo:BackupRestore:PollInterval", new UpdateRuntimeSettingRequest("00:00:07"), JsonOptions);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var result = JsonSerializer.Deserialize<RuntimeSettingUpdateResult>(text, JsonOptions)!;
        Assert.Equal("00:00:07", result.Setting.EffectiveValue);
        Assert.True(result.Setting.HasOverlayValue);
        Assert.True(result.Setting.IsClientOverrideEffective);
        Assert.False(result.Setting.IsExternallyOverridden);
        Assert.Equal("Client override active", result.Setting.OverrideStatus);

        var overlay = await File.ReadAllTextAsync(Path.Combine(dataDir, ChoboConfiguration.RuntimeSettingsFileName));
        Assert.Contains("PollInterval", overlay);
        Assert.Contains("00:00:07", overlay);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.True(await db.AuditEntries.AnyAsync(x => x.Action == "set" && x.EntityType == "runtime-setting" && x.EntityId == "Chobo:BackupRestore:PollInterval"));
    }


    [Fact]
    public void Slow_sqlite_queries_are_logged_at_information_when_over_configured_threshold()
    {
        var options = new TestOptionsMonitor<ChoboDatabaseLoggingOptions>(new ChoboDatabaseLoggingOptions
        {
            SlowQueryThreshold = TimeSpan.FromSeconds(2)
        });
        var sink = new CollectingSink();
        var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var interceptor = new SlowSqliteQueryLoggingInterceptor(options, logger);

        using var command = new SqliteCommand("SELECT * FROM Users WHERE UserName = $userName");
        interceptor.LogIfSlow(command, TimeSpan.FromMilliseconds(1999));
        interceptor.LogIfSlow(command, TimeSpan.FromMilliseconds(2001));

        var logEvent = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Information, logEvent.Level);
        Assert.Contains("Slow SQLite query completed", logEvent.RenderMessage());
        Assert.Contains("SELECT * FROM Users", logEvent.RenderMessage());
    }

    [Fact]
    public async Task Runtime_settings_api_rejects_hidden_invalid_and_environment_overridden_values()
    {
        var dataDir = NewTestDataDirectory();
        await using var factory = CreateFactory(dataDir, extraConfiguration: new Dictionary<string, string?>
        {
            ["Chobo:BackupRestore:PollInterval"] = "00:00:09"
        });
        var client = AuthenticatedClient(factory);

        var hidden = await client.GetAsync("/api/v1/settings/Chobo:Init:AccessToken");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/Chobo:BackupRestore:PollInterval", new UpdateRuntimeSettingRequest("not-a-timespan"), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var invalidPragma = await client.PutAsJsonAsync("/api/v1/settings/Chobo:Sqlite:JournalMode", new UpdateRuntimeSettingRequest("unsafe; pragma synchronous=off"), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPragma.StatusCode);

        var update = await client.PutAsJsonAsync("/api/v1/settings/Chobo:BackupRestore:PollInterval", new UpdateRuntimeSettingRequest("00:00:05"), JsonOptions);
        var text = await update.Content.ReadAsStringAsync();
        Assert.True(update.IsSuccessStatusCode, text);
        var result = JsonSerializer.Deserialize<RuntimeSettingUpdateResult>(text, JsonOptions)!;
        Assert.Equal("00:00:09", result.Setting.EffectiveValue);
        Assert.Equal("00:00:05", result.Setting.OverlayValue);
        Assert.False(result.Setting.IsClientOverrideEffective);
        Assert.True(result.Setting.IsExternallyOverridden);
        Assert.Equal("Client override masked by external configuration", result.Setting.OverrideStatus);
        Assert.True(result.EffectiveValueUnchanged);

        var unset = await client.DeleteAsync("/api/v1/settings/Chobo:BackupRestore:PollInterval");
        Assert.True(unset.IsSuccessStatusCode, await unset.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Audit_details_and_log_properties_store_enum_values_as_text()
    {
        await using var factory = CreateFactory();
        _ = AuthenticatedClient(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            await audit.RecordAsync("enum-regression", AuditEntityType.Backup, "backup-id", new
            {
                deletionStatus = BackupRunStatus.ManualDeleted,
                statuses = new[] { BackupRunStatus.FailedBackupDeletedByGarbageCollector }
            });

            var logger = scope.ServiceProvider.GetRequiredService<Serilog.ILogger>().ForContext<ChoboFoundationTests>();
            logger.Information("Enum property regression log. deletionStatus={DeletionStatus}", BackupRunStatus.ManualDeleteRequested);
        }

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var auditEntry = await db.AuditEntries.SingleAsync(x => x.Action == "enum-regression");
        var logEntry = await db.ApplicationLogEntries.SingleAsync(x => x.RenderedMessage.Contains("Enum property regression log"));

        Assert.Contains("\"deletionStatus\":\"ManualDeleted\"", auditEntry.Details);
        Assert.Contains("\"FailedBackupDeletedByGarbageCollector\"", auditEntry.Details);
        Assert.DoesNotContain("\"deletionStatus\":7", auditEntry.Details);
        Assert.Contains("\"DeletionStatus\":\"ManualDeleteRequested\"", logEntry.Properties);
        Assert.DoesNotContain("\"DeletionStatus\":6", logEntry.Properties);
    }

    [Fact]
    public async Task Logs_and_audits_can_be_filtered_by_operation_id_column()
    {
        await using var factory = CreateFactory();
        _ = AuthenticatedClient(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var timestamp = ToUnixMilliseconds(DateTimeOffset.Parse("2026-05-15T10:11:12+00:00"));

        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, OperationId, Properties) VALUES ({timestamp}, 'Information', NULL, 'matching log', 'backup-a', '{{\"OperationId\":\"backup-a\"}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, OperationId, Properties) VALUES ({timestamp + 1000}, 'Information', NULL, 'newer matching log', 'backup-a', '{{\"OperationId\":\"backup-a\"}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, OperationId, Properties) VALUES ({timestamp}, 'Information', NULL, 'other log', 'backup-b', '{{\"OperationId\":\"backup-b\"}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, OperationId, Details) VALUES ({timestamp}, 'system', 'matching-audit', 'backup', 'backup-a', '{{\"operationId\":\"backup-a\"}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, OperationId, Details) VALUES ({timestamp + 1000}, 'system', 'newer-matching-audit', 'backup', 'backup-a', '{{\"operationId\":\"backup-a\"}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, OperationId, Details) VALUES ({timestamp}, 'system', 'other-audit', 'backup', 'backup-b', '{{\"operationId\":\"backup-b\"}}');");

        var logs = await scope.ServiceProvider.GetRequiredService<IApplicationLogStore>().QueryAsync(null, null, null, limit: 50, operationId: "backup-a");
        var audits = await scope.ServiceProvider.GetRequiredService<IAuditStore>().QueryAsync(null, null, null, limit: 50, operationId: "backup-a");

        Assert.Contains(logs.Items, x => x.Message == "matching log");
        Assert.DoesNotContain(logs.Items, x => x.Message == "other log");
        Assert.Contains(audits.Items, x => x.Action == "matching-audit");
        Assert.DoesNotContain(audits.Items, x => x.Action == "other-audit");
        Assert.Equal(2, logs.TotalCount);
        Assert.Equal(2, audits.TotalCount);
        var secondLogPage = await scope.ServiceProvider.GetRequiredService<IApplicationLogStore>().QueryAsync(null, null, null, offset: 1, limit: 1, operationId: "backup-a");
        Assert.Single(secondLogPage.Items);
        Assert.Equal("matching log", secondLogPage.Items[0].Message);
    }

    [Fact]
    public async Task Logs_and_audits_time_filters_return_recent_rows()
    {
        await using var factory = CreateFactory();
        _ = AuthenticatedClient(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var now = DateTimeOffset.UtcNow;
        var old = ToUnixMilliseconds(now.AddHours(-2));
        var recent = ToUnixMilliseconds(now.AddMinutes(-10));
        var startTime = now.AddHours(-1);
        var endTime = now.AddMinutes(1);

        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, OperationId, Properties) VALUES ({old}, 'Information', NULL, 'old time-window log', NULL, '{{}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationLogEntries (Timestamp, Level, Exception, RenderedMessage, OperationId, Properties) VALUES ({recent}, 'Information', NULL, 'recent time-window log', NULL, '{{}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, OperationId, Details) VALUES ({old}, 'system', 'old-time-window-audit', 'test', NULL, '{{}}');");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEntries (Timestamp, ActorName, Action, EntityType, OperationId, Details) VALUES ({recent}, 'system', 'recent-time-window-audit', 'test', NULL, '{{}}');");

        var logs = await scope.ServiceProvider.GetRequiredService<IApplicationLogStore>().QueryAsync(startTime, endTime, null, limit: 200);
        var audits = await scope.ServiceProvider.GetRequiredService<IAuditStore>().QueryAsync(startTime, endTime, null, limit: 200);

        Assert.Contains(logs.Items, x => x.Message == "recent time-window log");
        Assert.DoesNotContain(logs.Items, x => x.Message == "old time-window log");
        Assert.Contains(audits.Items, x => x.Action == "recent-time-window-audit");
        Assert.DoesNotContain(audits.Items, x => x.Action == "old-time-window-audit");
    }

    private static async Task<FullExportGraphIds> SeedFullExportGraphAsync(WebApplicationFactory<Program> factory, bool includeCredentials, bool softDeletedConfig = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var user = await db.Users.SingleAsync(x => x.UserName == "admin");
        var now = DateTimeOffset.Parse("2026-05-15T10:00:00+00:00");
        var deletedAt = softDeletedConfig ? now.AddMinutes(5) : (DateTimeOffset?)null;
        var clusterId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var schemaDefinitionId = Guid.NewGuid();
        var backupId = Guid.NewGuid();
        var backupTableId = Guid.NewGuid();
        var backupTableShardId = Guid.NewGuid();
        var restoreId = Guid.NewGuid();
        var restoreTableId = Guid.NewGuid();
        var restoreTableShardId = Guid.NewGuid();

        db.ClickHouseClusters.Add(new ClickHouseClusterEntity
        {
            Id = clusterId,
            Name = "prod",
            Mode = ClusterMode.Cluster,
            ClickHouseClusterName = "prod_cluster",
            BackupRestoreMaxDop = 4,
            EncryptedUserName = includeCredentials ? "encrypted-user" : null,
            EncryptedUserNameKeyId = includeCredentials ? Guid.NewGuid() : null,
            EncryptedPassword = includeCredentials ? "encrypted-password" : null,
            EncryptedPasswordKeyId = includeCredentials ? Guid.NewGuid() : null,
            IsDeleted = softDeletedConfig,
            CreatedAt = now,
            DeletedAt = deletedAt,
            AccessNodes =
            [
                new ClickHouseAccessNodeEntity { Id = Guid.NewGuid(), Host = "clickhouse-1", Port = 9000, UseTls = false },
                new ClickHouseAccessNodeEntity { Id = Guid.NewGuid(), Host = "clickhouse-2", Port = 9440, UseTls = true }
            ]
        });
        var target = CreateS3TargetEntity(targetId, "minio", "http://minio:9000", "us-east-1", "backups", "prod", true, includeCredentials, now);
        target.IsDeleted = softDeletedConfig;
        target.DeletedAt = deletedAt;
        db.BackupTargets.Add(target);
        db.BackupPolicies.Add(new BackupPolicyEntity
        {
            Id = policyId,
            Name = "all-prod",
            SourceClusterId = clusterId,
            TargetId = targetId,
            SelectorJsonVersion = 1,
            SelectorJson = JsonSerializer.Serialize(PolicySelector.Empty, JsonOptions),
            FullRetentionMinutes = 1440,
            IncrementalRetentionMinutes = 240,
            MinBackupsToKeep = 2,
            MinFullBackupsToKeep = 1,
            MaxAgeHoursForBaseBackup = 72,
            IsDeleted = softDeletedConfig,
            CreatedAt = now,
            DeletedAt = deletedAt
        });
        db.BackupSchedules.Add(new BackupScheduleEntity
        {
            Id = scheduleId,
            Name = "nightly",
            PolicyId = policyId,
            BackupType = BackupType.Full,
            CronExpression = "0 0 2 * * ?",
            TimeZoneId = "UTC",
            MissedRunGracePeriod = TimeSpan.FromMinutes(30),
            Description = "Nightly backup",
            IsDeleted = softDeletedConfig,
            CreatedAt = now,
            DeletedAt = deletedAt
        });
        db.SchemaDefinitions.Add(new SchemaDefinitionEntity
        {
            Id = schemaDefinitionId,
            SchemaHash = "schema-hash-1",
            Database = "sales",
            Table = "orders",
            Engine = "MergeTree",
            CreateTableSql = "CREATE TABLE sales.orders (id UInt64) ENGINE = MergeTree ORDER BY id",
            ColumnsJson = "[{\"name\":\"id\",\"type\":\"UInt64\"}]",
            CreatedAt = now
        });
        db.Backups.Add(new BackupEntity
        {
            Id = backupId,
            TriggerType = BackupTriggerType.Scheduled,
            Status = BackupRunStatus.Succeeded,
            BackupType = BackupType.Full,
            SourceClusterId = clusterId,
            TargetId = targetId,
            PolicyId = policyId,
            ScheduleId = scheduleId,
            RequestedByUserId = user.Id,
            RequestedByName = user.UserName,
            CreatedAt = now,
            QueuedAt = now,
            StartedAt = now.AddMinutes(1),
            CompletedAt = now.AddMinutes(2),
            IsPinned = true,
            PinnedAt = now.AddMinutes(3),
            PinnedByUserId = user.Id,
            PinnedByName = user.UserName
        });
        db.BackupTables.Add(new BackupTableEntity
        {
            Id = backupTableId,
            BackupId = backupId,
            EffectiveBackupType = BackupType.Full,
            Database = "sales",
            Table = "orders",
            Engine = "MergeTree",
            DataBackedUp = true,
            SchemaDefinitionId = schemaDefinitionId,
            StoragePath = "s3://backups/sales/orders",
            Status = BackupTableStatus.Succeeded,
            ClickHouseOperationId = "backup-op-table",
            ClickHouseStatus = "success",
            StartedAt = now.AddMinutes(1),
            CompletedAt = now.AddMinutes(2)
        });
        db.BackupTableShards.Add(new BackupTableShardEntity
        {
            Id = backupTableShardId,
            BackupTableId = backupTableId,
            EffectiveBackupType = BackupType.Full,
            SourceShardNumber = 1,
            SourceShardName = "shard1",
            ReplicaNumber = 1,
            Host = "clickhouse-1",
            Port = 9000,
            UseTls = false,
            StoragePath = "s3://backups/sales/orders/shard1",
            Status = BackupTableStatus.Succeeded,
            ClickHouseOperationId = "backup-op-shard",
            ClickHouseStatus = "success",
            StartedAt = now.AddMinutes(1),
            CompletedAt = now.AddMinutes(2)
        });
        db.Restores.Add(new RestoreEntity
        {
            Id = restoreId,
            BackupId = backupId,
            TargetClusterId = clusterId,
            Status = RestoreRunStatus.Succeeded,
            Append = true,
            AllowSchemaMismatch = true,
            Layout = RestoreLayout.Preserve,
            SourceShard = 1,
            TargetShard = 1,
            RequestJson = "{\"mode\":\"test\"}",
            RequestedByUserId = user.Id,
            RequestedByName = user.UserName,
            CreatedAt = now,
            QueuedAt = now,
            StartedAt = now.AddMinutes(3),
            CompletedAt = now.AddMinutes(4)
        });
        db.RestoreTables.Add(new RestoreTableEntity
        {
            Id = restoreTableId,
            RestoreId = restoreId,
            BackupTableId = backupTableId,
            SourceDatabase = "sales",
            SourceTable = "orders",
            TargetDatabase = "sales_restore",
            TargetTable = "orders_restore",
            Append = true,
            AllowSchemaMismatch = true,
            Status = RestoreTableStatus.Succeeded,
            ClickHouseOperationId = "restore-op-table",
            ClickHouseStatus = "success",
            Warning = "none",
            StartedAt = now.AddMinutes(3),
            CompletedAt = now.AddMinutes(4)
        });
        db.RestoreTableShards.Add(new RestoreTableShardEntity
        {
            Id = restoreTableShardId,
            RestoreTableId = restoreTableId,
            BackupTableShardId = backupTableShardId,
            SourceShardNumber = 1,
            TargetShardNumber = 1,
            TargetShardName = "shard1",
            TargetReplicaNumber = 1,
            TargetHost = "clickhouse-1",
            TargetPort = 9000,
            TargetUseTls = false,
            LayoutRole = "primary",
            RestoreDatabase = "sales_restore",
            RestoreTableName = "orders_restore",
            Status = RestoreTableStatus.Succeeded,
            ClickHouseOperationId = "restore-op-shard",
            ClickHouseStatus = "success",
            Warning = "none",
            StartedAt = now.AddMinutes(3),
            CompletedAt = now.AddMinutes(4)
        });

        await db.SaveChangesAsync();
        return new FullExportGraphIds(clusterId, targetId, policyId, scheduleId, schemaDefinitionId, backupId, backupTableId, backupTableShardId, restoreId, restoreTableId, restoreTableShardId);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class TestOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed record FullExportGraphIds(
        Guid ClusterId,
        Guid TargetId,
        Guid PolicyId,
        Guid ScheduleId,
        Guid SchemaDefinitionId,
        Guid BackupId,
        Guid BackupTableId,
        Guid BackupTableShardId,
        Guid RestoreId,
        Guid RestoreTableId,
        Guid RestoreTableShardId);
    private static WebApplicationFactory<Program> CreateFactory(string? dataDir = null, string adminUser = "admin", string accessToken = Token, IReadOnlyDictionary<string, string?>? extraConfiguration = null)
    {
        dataDir ??= NewTestDataDirectory();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
                var values = new Dictionary<string, string?>
                {
                    ["Chobo:DataDirectory"] = dataDir,
                    ["Chobo:Init:AdminUser"] = adminUser,
                    ["Chobo:Init:AccessToken"] = accessToken
                };
                config.AddJsonFile(Path.Combine(dataDir, ChoboConfiguration.RuntimeSettingsFileName), optional: true, reloadOnChange: true);
                if (extraConfiguration is not null)
                {
                    foreach (var entry in extraConfiguration)
                    {
                        values[entry.Key] = entry.Value;
                    }
                }

                config.AddInMemoryCollection(values);
            });
        });
    }

    private static string NewTestDataDirectory() =>
        Path.Combine(Path.GetTempPath(), "chobo-tests", Guid.NewGuid().ToString("N"));

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private static BackupTargetEntity CreateS3TargetEntity(Guid id, string name, string endpoint, string region, string bucket, string? pathPrefix, bool forcePathStyle, bool includeCredentials, DateTimeOffset? createdAt = null) =>
        new()
        {
            Id = id,
            Name = name,
            Type = StorageProviderTypes.S3,
            SettingsJson = JsonSerializer.Serialize(new S3TargetSettingsDto(endpoint, region, bucket, pathPrefix, forcePathStyle), JsonOptions),
            SecretsJson = includeCredentials
                ? JsonSerializer.Serialize(new
                {
                    accessKey = new { ciphertext = "encrypted-access", keyId = Guid.NewGuid() },
                    secretKey = new { ciphertext = "encrypted-secret", keyId = Guid.NewGuid() }
                }, JsonOptions)
                : "{}",
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };
    private static IReadOnlyDictionary<string, JsonElement> Settings(params (string Name, object Value)[] settings)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in settings)
        {
            result[name] = JsonSerializer.SerializeToElement(value, JsonOptions);
        }

        return result;
    }
    private static async Task<T> Post<T>(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body, JsonOptions);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private static async Task<T> Put<T>(HttpClient client, string path, object body)
    {
        var response = await client.PutAsJsonAsync(path, body, JsonOptions);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static long ToUnixMilliseconds(DateTimeOffset value) =>
        value.ToUniversalTime().ToUnixTimeMilliseconds();

    private static async Task<string> GetColumnTypeAsync(ChoboDbContext db, string tableName, string columnName)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return reader.GetString(2).ToUpperInvariant();
                }
            }
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }

        throw new InvalidOperationException($"Column {tableName}.{columnName} was not found.");
    }

    private static async Task<string> GetPragmaStringAsync(ChoboDbContext db, string name)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA {name};";
            return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static async Task<long> GetPragmaInt64Async(ChoboDbContext db, string name)
    {
        var value = await GetPragmaStringAsync(db, name);
        return long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountRowsAsync(string dbPath, string tableName)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static class TestOptionsMonitor
    {
        public static IOptionsMonitor<T> Create<T>(T value) => new TestOptionsMonitorValue<T>(value);
    }

    private sealed class TestOptionsMonitorValue<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;

        public static IOptionsMonitor<T> Create(T value) => new TestOptionsMonitorValue<T>(value);
    }
    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() =>
            UtcNow;
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = [];

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (name, value) in values)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
