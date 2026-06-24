SELECT
    count() AS rows,
    formatReadableSize(sum(data_compressed_bytes)) AS compressed_on_disk,
    formatReadableSize(sum(data_uncompressed_bytes)) AS uncompressed_on_disk
FROM system.parts
WHERE active AND database = 'large_ontime_source' AND table = 'ontime';
