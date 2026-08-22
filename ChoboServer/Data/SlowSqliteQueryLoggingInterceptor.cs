using System.Data.Common;
using ChoboServer.Options;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace ChoboServer.Data;

public sealed class SlowSqliteQueryLoggingInterceptor(
    IOptionsMonitor<ChoboDatabaseLoggingOptions> options,
    Serilog.ILogger logger) : DbCommandInterceptor
{
    private readonly Serilog.ILogger _logger = logger.ForContext<SlowSqliteQueryLoggingInterceptor>();

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        SqliteQueryTagging.EnsureTag(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        SqliteQueryTagging.EnsureTag(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        SqliteQueryTagging.EnsureTag(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        SqliteQueryTagging.EnsureTag(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        SqliteQueryTagging.EnsureTag(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        SqliteQueryTagging.EnsureTag(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        LogIfSlow(command, eventData.Duration);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        LogIfSlow(command, eventData.Duration);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        LogIfSlow(command, eventData.Duration);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData.Duration);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData.Duration);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        LogIfSlow(command, eventData.Duration);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public void LogIfSlow(DbCommand command, TimeSpan duration)
    {
        var threshold = options.CurrentValue.SlowQueryThreshold;
        if (threshold < TimeSpan.Zero || duration <= threshold)
        {
            return;
        }

        var tag = SqliteQueryTagging.EnsureTag(command);
        _logger.Information(
            "Slow SQLite query {QueryTag:l} completed in {ElapsedMilliseconds} ms, exceeding threshold {ThresholdMilliseconds} ms. QueryTagMissing={QueryTagMissing}; CommandType={CommandType}; CommandText={CommandText}",
            tag.Name,
            duration.TotalMilliseconds,
            threshold.TotalMilliseconds,
            tag.IsMissing,
            command.CommandType,
            command.CommandText);
    }
}
