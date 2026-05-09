using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.BackgroundServices;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
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
        var users = await client.GetFromJsonAsync<List<UserDto>>("/api/v1/users", JsonOptions);
        Assert.Single(users!);
        Assert.Equal("admin", users![0].UserName);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        Assert.Equal(ChoboApi.SchemaVersion, (await db.SchemaStates.SingleAsync()).SchemaVersion);
        Assert.DoesNotContain(Token, string.Join(' ', await db.AccessTokens.Select(x => x.TokenHash).ToListAsync()));
    }

    [Fact]
    public async Task Cluster_credentials_are_encrypted_and_hidden_from_api()
    {
        await using var factory = CreateFactory();
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
        Assert.Empty(config!.Data.Audits);
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

        var schedule = await Post<BackupScheduleDto>(client, "/api/v1/schedules", new UpsertScheduleRequest("nightly", policy.Id, BackupType.Full, "0 0 2 * * ?", "UTC", true, "nightly full"));
        Assert.True(schedule.IsEnabled);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/schedules/{schedule.Id}/disable", null)).StatusCode);

        var logs = await client.GetFromJsonAsync<List<LogEntryDto>>("/api/v1/logs?last=10", JsonOptions);
        Assert.NotNull(logs);
        var clear = await client.PostAsJsonAsync("/api/v1/logs/clear", new ClearBeforeRequest(DateTimeOffset.UtcNow.AddDays(1)), JsonOptions);
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

        var response = await client.PostAsJsonAsync("/api/v1/audit/clear", new ClearBeforeRequest(cutoff), JsonOptions);
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
        var logs = await client.GetFromJsonAsync<List<LogEntryDto>>("/api/v1/logs?last=5", JsonOptions);
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
}
