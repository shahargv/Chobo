SELECT Id, EventDate, Name, Amount
FROM smoke_cluster2_db.smoke_cluster2_events
ORDER BY EventDate, Id
FORMAT CSV
