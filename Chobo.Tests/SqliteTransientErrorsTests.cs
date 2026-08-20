using Chobo.Contracts;
using ChoboServer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Chobo.Tests;

public sealed class SqliteTransientErrorsTests
{
    [Fact]
    public void Bare_busy_and_locked_errors_are_transient()
    {
        Assert.True(SqliteTransientErrors.IsTransientLock(new SqliteException("busy", 5)));
        Assert.True(SqliteTransientErrors.IsTransientLock(new SqliteException("locked", 6)));
    }

    [Fact]
    public void Wrapped_busy_error_is_transient()
    {
        var wrapped = new DbUpdateException("An error occurred while saving the entity changes.", new SqliteException("database is locked", 5));

        Assert.True(SqliteTransientErrors.IsTransientLock(wrapped));
    }

    [Fact]
    public void Unrelated_and_null_errors_are_not_transient()
    {
        Assert.False(SqliteTransientErrors.IsTransientLock(null));
        Assert.False(SqliteTransientErrors.IsTransientLock(new InvalidOperationException("boom")));
        Assert.False(SqliteTransientErrors.IsTransientLock(new SqliteException("constraint", 19)));
    }

    [Fact]
    public void Retry_delay_grows_and_stays_bounded()
    {
        Assert.True(SqliteTransientErrors.RetryDelay(0) < SqliteTransientErrors.RetryDelay(4));
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var delay = SqliteTransientErrors.RetryDelay(attempt);
            Assert.True(delay > TimeSpan.Zero);
            Assert.True(delay <= TimeSpan.FromSeconds(3), $"attempt {attempt} produced {delay}");
        }
    }

    [Fact]
    public async Task A_contended_save_surfaces_the_lock_wrapped_in_DbUpdateException()
    {
        await WithLockedDatabaseAsync(async (writer, _) =>
        {
            var thrown = await Record.ExceptionAsync(() => writer.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.NotNull(thrown);
            Assert.IsNotType<TimeoutException>(thrown);
            Assert.IsNotType<SqliteException>(thrown);
            Assert.True(SqliteTransientErrors.IsTransientLock(thrown), $"expected a transient lock, got {thrown}");
        });
    }

    [Fact]
    public async Task A_contended_save_recovers_once_the_lock_clears()
    {
        await WithLockedDatabaseAsync(async (writer, releaseLockAsync) =>
        {
            var release = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500));
                await releaseLockAsync();
            });

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var saved = await writer.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(30));
            elapsed.Stop();
            await release;

            Assert.Equal(1, saved);
            Assert.True(
                elapsed.Elapsed > TimeSpan.FromSeconds(1),
                $"expected the ChoboDbContext retry loop to be exercised, but the save settled in {elapsed.ElapsedMilliseconds} ms - a single command budget would have sufficed.");
        });
    }

    private static async Task WithLockedDatabaseAsync(Func<ChoboDbContext, Func<Task>, Task> body)
    {
        var directory = Directory.CreateTempSubdirectory("chobo-lock-test");
        try
        {
            var databasePath = Path.Combine(directory.FullName, "chobo.db");
            var connectionString = $"Data Source={databasePath}";
            var impatientConnectionString = $"Data Source={databasePath};Default Timeout=1";

            await using (var seed = CreateContext(connectionString))
            {
                await seed.Database.EnsureCreatedAsync();
            }

            await using (var blocker = new SqliteConnection(impatientConnectionString))
            {
                await blocker.OpenAsync();
                await ExecuteAsync(blocker, "BEGIN IMMEDIATE;");
                var released = false;

                await using (var writer = CreateContext(impatientConnectionString))
                {
                    writer.SchemaStates.Add(new SchemaStateEntity
                    {
                        SchemaVersion = ChoboApi.SchemaVersion,
                        AppliedMigrationId = "lock-test",
                        AppliedAt = DateTimeOffset.UnixEpoch,
                        ProductVersion = "lock-test"
                    });

                    async Task ReleaseAsync()
                    {
                        if (!released)
                        {
                            released = true;
                            await ExecuteAsync(blocker, "ROLLBACK;");
                        }
                    }

                    try
                    {
                        await body(writer, ReleaseAsync);
                    }
                    finally
                    {
                        await ReleaseAsync();
                    }
                }
            }

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(directory);
        }
    }

    private static void TryDelete(DirectoryInfo directory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                directory.Delete(true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static ChoboDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ChoboDbContext>().UseSqlite(connectionString).Options);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
