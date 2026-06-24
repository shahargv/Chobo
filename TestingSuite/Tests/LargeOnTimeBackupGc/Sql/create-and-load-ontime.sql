CREATE DATABASE IF NOT EXISTS large_ontime_source ENGINE = Atomic;

DROP TABLE IF EXISTS large_ontime_source.ontime SYNC;

CREATE TABLE large_ontime_source.ontime
ENGINE = MergeTree
ORDER BY (Year, Quarter, Month, DayofMonth, FlightDate, IATA_CODE_Reporting_Airline)
AS
SELECT *
FROM s3('https://clickhouse-public-datasets.s3.amazonaws.com/ontime/csv_by_year/{2000..2010}.csv.gz', 'CSVWithNames')
SETTINGS
    input_format_csv_empty_as_default = 1,
    schema_inference_make_columns_nullable = 0,
    max_insert_threads = 8;
