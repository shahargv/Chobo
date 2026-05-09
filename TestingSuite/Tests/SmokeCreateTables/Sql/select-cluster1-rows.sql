SELECT Id, PartitionNumber, Name, Amount
FROM {cluster1.DatabaseName}.{cluster1.TableName}
ORDER BY PartitionNumber, Id
FORMAT CSV
