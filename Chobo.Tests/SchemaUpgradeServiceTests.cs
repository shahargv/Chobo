using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Chobo.Tests;

public sealed class SchemaUpgradeServiceTests
{
    [Fact]
    public async Task Current_schema_updates_product_version_without_changing_schema_version()
    {
        await using var fixture = await SchemaFixture.CreateAsync();
        var schema = new SchemaStateEntity
        {
            SchemaVersion = ChoboApi.SchemaVersion,
            AppliedMigrationId = "baseline",
            AppliedAt = DateTimeOffset.Parse("2026-05-15T10:00:00+00:00"),
            ProductVersion = "old-version"
        };
        fixture.Db.SchemaStates.Add(schema);
        await fixture.Db.SaveChangesAsync();

        await new SchemaUpgradeService(fixture.Db).UpgradeAsync(schema);

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.SchemaStates.SingleAsync();
        Assert.Equal(ChoboApi.SchemaVersion, stored.SchemaVersion);
        Assert.Equal("baseline", stored.AppliedMigrationId);
        Assert.Equal(ChoboApi.ProductVersion, stored.ProductVersion);
    }

    [Fact]
    public async Task Newer_database_schema_is_rejected_without_persisting_product_version()
    {
        await using var fixture = await SchemaFixture.CreateAsync();
        var schema = new SchemaStateEntity
        {
            SchemaVersion = ChoboApi.SchemaVersion + 1,
            AppliedMigrationId = "future",
            AppliedAt = DateTimeOffset.Parse("2026-05-15T10:00:00+00:00"),
            ProductVersion = "future-product"
        };
        fixture.Db.SchemaStates.Add(schema);
        await fixture.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SchemaUpgradeService(fixture.Db).UpgradeAsync(schema));

        Assert.Contains("newer than server-supported schema version", ex.Message);
        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.SchemaStates.SingleAsync();
        Assert.Equal(ChoboApi.SchemaVersion + 1, stored.SchemaVersion);
        Assert.Equal("future-product", stored.ProductVersion);
    }

    [Fact]
    public async Task Older_database_schema_is_rejected_when_no_upgrade_path_is_registered()
    {
        await using var fixture = await SchemaFixture.CreateAsync();
        var schema = new SchemaStateEntity
        {
            SchemaVersion = ChoboApi.SchemaVersion - 1,
            AppliedMigrationId = "legacy",
            AppliedAt = DateTimeOffset.Parse("2026-05-15T10:00:00+00:00"),
            ProductVersion = "legacy-product"
        };
        fixture.Db.SchemaStates.Add(schema);
        await fixture.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SchemaUpgradeService(fixture.Db).UpgradeAsync(schema));

        Assert.Contains("No upgrade path is registered", ex.Message);
        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.SchemaStates.SingleAsync();
        Assert.Equal(ChoboApi.SchemaVersion - 1, stored.SchemaVersion);
        Assert.Equal("legacy-product", stored.ProductVersion);
    }

    private sealed class SchemaFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SchemaFixture(SqliteConnection connection, ChoboDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public ChoboDbContext Db { get; }

        public static async Task<SchemaFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ChoboDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ChoboDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new SchemaFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
