SELECT Id, EventDate, Name, Amount
FROM {cluster2.DatabaseName}.{cluster2.TableName}
ORDER BY EventDate, Id
FORMAT CSV
