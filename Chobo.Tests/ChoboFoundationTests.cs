using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer;
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
            ["CHOBO_SQLITE_SELF_BACKUP_ENABLED"] = "true",
            ["CHOBO_SQLITE_SELF_BACKUP_DIRECTORY"] = "env-sqlite-backups",
            ["CHOBO_SQLITE_SELF_BACKUP_INTERVAL"] = "12:00:00",
            ["CHOBO_SQLITE_SELF_BACKUP_POLL_INTERVAL"] = "00:04:00",
            ["Chobo__BackupRestore__MaxDop"] = "7",
            ["CHOBO_BACKUP_RESTORE_QUEUE_CAPACITY"] = "33",
            ["CHOBO_BACKUP_RESTORE_SCHEDULER_INTERVAL"] = "00:02:00",
            ["Chobo__BackupRestore__SchedulerMissedRunGracePeriod"] = "00:07:00",
            ["CHOBO_BACKUP_RESTORE_POLL_INTERVAL"] = "00:00:03",
            ["CHOBO_TEST_HOOKS_ENABLED"] = "true",
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
        var selfBackup = configuration.GetSection("Chobo:SqliteSelfBackup").Get<ChoboSqliteSelfBackupOptions>()!;
        var backupRestore = configuration.GetSection("Chobo:BackupRestore").Get<ChoboBackupRestoreOptions>()!;
        var testHooks = configuration.GetSection("Chobo:TestHooks").Get<ChoboTestHooksOptions>()!;

        Assert.Equal("env-data", storage.DataDirectory);
        Assert.Equal("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=", security.EncryptionKeyBase64);
        Assert.Equal("env-admin", init.AdminUser);
        Assert.Equal("env-token", init.AccessToken);
        Assert.Equal(TimeSpan.FromMinutes(10), retention.Interval);
        Assert.Equal(DateTimeOffset.Parse("2026-05-14T10:00:00+00:00"), retention.LogsBefore);
        Assert.Equal(DateTimeOffset.Parse("2026-05-14T11:00:00+00:00"), retention.AuditsBefore);
        Assert.True(selfBackup.Enabled);
        Assert.Equal("env-sqlite-backups", selfBackup.Directory);
        Assert.Equal(TimeSpan.FromHours(12), selfBackup.BackupInterval);
        Assert.Equal(TimeSpan.FromMinutes(4), selfBackup.PollInterval);
        Assert.Equal(7, backupRestore.MaxDop);
        Assert.Equal(33, backupRestore.QueueCapacity);
        Assert.Equal(TimeSpan.FromMinutes(2), backupRestore.SchedulerInterval);
        Assert.Equal(TimeSpan.FromMinutes(7), backupRestore.SchedulerMissedRunGracePeriod);
        Assert.Equal(TimeSpan.FromSeconds(3), backupRestore.PollInterval);
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
    public async Task First_startup_uses_configured_values_and_writes_initial_token_to_stdout()
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
            Assert.Contains(Token, writer.ToString());
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
        Assert.Equal(ChoboApi.ProductVersion, version.ServerVersion);
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
    public void S3_target_address_uses_configured_path_style_mode()
    {
        var target = new BackupTargetEntity
        {
            Endpoint = "https://s3.example.com",
            Bucket = "backup-bucket",
            PathPrefix = "prod/backups",
            ForcePathStyle = true
        };

        var pathStyle = S3TargetUrlBuilder.BuildObjectUrl(target, "db/table/file name.bin");
        target.ForcePathStyle = false;
        var virtualHost = S3TargetUrlBuilder.BuildObjectUrl(target, "db/table/file name.bin");

        Assert.Equal("https://s3.example.com/backup-bucket/prod/backups/db/table/file%20name.bin", pathStyle.AbsoluteUri);
        Assert.Equal("https://backup-bucket.s3.example.com/prod/backups/db/table/file%20name.bin", virtualHost.AbsoluteUri);
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
            "secret"));

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
    public async Task Cluster_update_replaces_nodes_without_concurrency_errors()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var created = await Post<ClusterDto>(client, "/api/v1/clusters", new UpsertClusterRequest(
            "prod",
            ClusterMode.Cluster,
            [new UpsertAccessNodeRequest("clickhouse-1"), new UpsertAccessNodeRequest("clickhouse-2")],
            null,
            null));

        var updated = await Put<ClusterDto>(client, $"/api/v1/clusters/{created.Id}", new UpsertClusterRequest(
            "prod-updated",
            ClusterMode.Cluster,
            [new UpsertAccessNodeRequest("clickhouse-3", 9440, true)],
            null,
            null));

        Assert.Equal("prod-updated", updated.Name);
        Assert.Single(updated.AccessNodes);
        Assert.Equal("clickhouse-3", updated.AccessNodes[0].Host);
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
    public async Task Mutating_actions_create_audit_records_and_config_export_excludes_logs_and_audits()
    {
        await using var factory = CreateFactory();
        var client = AuthenticatedClient(factory);
        var user = await Post<CreateUserResponse>(client, "/api/v1/users", new CreateUserRequest("operator"));
        Assert.False(string.IsNullOrWhiteSpace(user.AccessToken));

        var audits = await client.GetFromJsonAsync<List<AuditEntryDto>>("/api/v1/audit?last=20", JsonOptions);
        Assert.Contains(audits!, x => x.Action == "create" && x.EntityType == "user");

        var config = await client.GetFromJsonAsync<ExportEnvelope>("/api/v1/config/export", JsonOptions);
        Assert.NotNull(config);
        Assert.Equal(ChoboApi.ProductVersion, config!.ProductVersion);
        Assert.Empty(config.Data.Audits);
        Assert.Empty(config.Data.Logs);
        Assert.Contains(config.Data.Users, x => x.UserName == "operator");
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
            "access",
            "secret"));

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

        var audits = await client.GetFromJsonAsync<List<AuditEntryDto>>("/api/v1/audit?last=20", JsonOptions) ?? throw new InvalidOperationException("Audit query returned no data.");
        var updateAudit = audits.First(x => x.Action == "update" && x.EntityType == "backup-target" && x.EntityId == created.Id.ToString());
        Assert.Equal("minio", updateAudit.Details.GetProperty("previous").GetProperty("name").GetString());
        Assert.Equal("bucket-a", updateAudit.Details.GetProperty("previous").GetProperty("s3").GetProperty("bucket").GetString());
        Assert.Equal("minio-renamed", updateAudit.Details.GetProperty("current").GetProperty("name").GetString());
        Assert.Equal("bucket-b", updateAudit.Details.GetProperty("current").GetProperty("s3").GetProperty("bucket").GetString());
        Assert.Equal(updated.Id.ToString(), updateAudit.Details.GetProperty("current").GetProperty("id").GetString());
        Assert.False(updateAudit.Details.GetRawText().Contains("secret", StringComparison.OrdinalIgnoreCase));

        var deactivateAudit = audits.First(x => x.Action == "deactivate" && x.EntityType == "user" && x.EntityId == user.UserId.ToString());
        Assert.Equal("operator", deactivateAudit.Details.GetProperty("deactivated").GetProperty("userName").GetString());
        Assert.False(deactivateAudit.Details.GetProperty("current").GetProperty("isActive").GetBoolean());
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
            null));
        var target = await Post<BackupTargetDto>(client, "/api/v1/targets/s3", new UpsertS3TargetRequest(
            "minio",
            "http://minio:9000",
            "us-east-1",
            "bucket",
            null,
            true,
            "access",
            "secret"));
        var policy = await Post<BackupPolicyDto>(client, "/api/v1/policies", new UpsertPolicyRequest("all", cluster.Id, target.Id, PolicySelector.Empty));
        var eval = await Post<PolicyEvaluationDto>(client, $"/api/v1/policies/{policy.Id}/evaluate", new PolicyEvaluationRequest(new PolicyInventory([new PolicyInventoryTable("sales", "orders")])));
        Assert.Equal(policy.Id, eval.PolicyId);
        Assert.Equal(cluster.Id, eval.SourceClusterId);
        Assert.Equal(1, eval.SelectorJsonVersion);
        Assert.Contains(eval.Tables, x => x.Database == "sales" && x.Table == "orders");

        var schedule = await Post<BackupScheduleDto>(client, "/api/v1/schedules", new UpsertScheduleRequest("nightly", policy.Id, BackupType.Full, "0 0 2 * * ?", "UTC", true, null, "nightly full"));
        Assert.True(schedule.IsEnabled);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/schedules/{schedule.Id}/disable", null)).StatusCode);

        var logs = await client.GetFromJsonAsync<List<ApplicationLogEntryDto>>("/api/v1/logs?last=10", JsonOptions);
        Assert.NotNull(logs);
        var clear = await client.PostAsJsonAsync("/api/v1/logs/clear", new ClearApplicationLogsRequest(DateTimeOffset.UtcNow.AddDays(1)), JsonOptions);
        Assert.True(clear.IsSuccessStatusCode);
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
            Options.Create(new ChoboDataRetentionOptions
            {
                LogsBefore = cutoff,
                AuditsBefore = cutoff
            }),
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
    public async Task Sqlite_self_backup_background_service_creates_timestamped_copy_and_audit()
    {
        var dataDir = NewTestDataDirectory();
        var backupDir = Path.Combine(dataDir, "self-backups");
        await using var factory = CreateFactory(dataDir);
        _ = AuthenticatedClient(factory);
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-05-15T10:11:12.123+00:00"));
        var service = new SqliteSelfBackupBackgroundService(
            factory.Services,
            Options.Create(new ChoboSqliteSelfBackupOptions
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
            Options.Create(new ChoboSqliteSelfBackupOptions
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

        var audits = await client.GetFromJsonAsync<List<AuditEntryDto>>("/api/v1/audit?last=5", JsonOptions);
        var logs = await client.GetFromJsonAsync<List<ApplicationLogEntryDto>>("/api/v1/logs?last=5", JsonOptions);
        var rawAuditJson = await client.GetStringAsync("/api/v1/audit?last=5");
        Assert.Contains(audits!, x => x.Action == "initialize" && x.Timestamp > DateTimeOffset.UnixEpoch);
        Assert.Contains(logs!, x => x.Message.Contains("Timestamp storage regression log entry") && x.Timestamp > DateTimeOffset.UnixEpoch);
        Assert.Contains("+00:00", rawAuditJson);
        Assert.DoesNotContain("\\u002B", rawAuditJson);
    }

    private static WebApplicationFactory<Program> CreateFactory(string? dataDir = null, string adminUser = "admin", string accessToken = Token)
    {
        dataDir ??= NewTestDataDirectory();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Chobo:DataDirectory"] = dataDir,
                    ["Chobo:Init:AdminUser"] = adminUser,
                    ["Chobo:Init:AccessToken"] = accessToken
                });
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
