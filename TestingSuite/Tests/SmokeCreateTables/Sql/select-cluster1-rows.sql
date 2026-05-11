SELECT Id, PartitionNumber, Name, Amount
FROM smoke_cluster1_db.smoke_cluster1_orders
ORDER BY PartitionNumber, Id
FORMAT CSV
