namespace ChoboServer.Options;

public sealed class ChoboMetricsOptions
{
    /// <summary>
    /// Emit one set of freshness metrics per (database, table, source shard).
    /// Off by default: the series count is the product of tables and shards, so a cluster with a few
    /// thousand tables across 24 shards produces hundreds of thousands of series on every scrape,
    /// which is more than a Prometheus instance will usefully hold. Policy-level and aggregate
    /// metrics are always emitted regardless of this setting.
    /// </summary>
    public bool IncludeTableShardMetrics { get; set; }
}
