DROP DATABASE IF EXISTS redistribute_subset_replicated_restore ON CLUSTER {restore.ClusterName} SYNC;
CREATE DATABASE redistribute_subset_replicated_restore ON CLUSTER {restore.ClusterName} ENGINE = Replicated('/clickhouse/databases/redistribute_subset_replicated_restore', '{shard}', '{replica}');
