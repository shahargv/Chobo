using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using System.Reflection;

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
    public async Task Version_one_database_schema_is_upgraded_to_version_two()
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

        await new SchemaUpgradeService(fixture.Db).UpgradeAsync(schema);

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.SchemaStates.SingleAsync();
        Assert.Equal(ChoboApi.SchemaVersion, stored.SchemaVersion);
        Assert.Equal("000000000002_PasswordProtectedBackups", stored.AppliedMigrationId);
        Assert.Equal(ChoboApi.ProductVersion, stored.ProductVersion);
    }

    [Fact]
    public void Ef_schema_v2_migration_is_additive_and_defaults_existing_policies_to_unprotected()
    {
        var migration = new ChoboServer.Data.Migrations.PasswordProtectedBackups();
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        typeof(ChoboServer.Data.Migrations.PasswordProtectedBackups)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var columns = builder.Operations.OfType<AddColumnOperation>().ToList();
        Assert.Equal(7, columns.Count);
        var passwordMode = Assert.Single(columns, x => x.Table == "BackupPolicies" && x.Name == "PasswordMode");
        Assert.False(passwordMode.IsNullable);
        Assert.Equal(0, passwordMode.DefaultValue);
        Assert.All(columns.Where(x => x.Name != "PasswordMode"), column => Assert.True(column.IsNullable));
        Assert.DoesNotContain(builder.Operations, operation => operation is DropColumnOperation or DropTableOperation);
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
