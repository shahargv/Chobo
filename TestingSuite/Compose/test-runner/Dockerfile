FROM clickhouse/clickhouse-server:25.4 AS clickhouse

FROM mcr.microsoft.com/powershell:7.5-ubuntu-24.04

COPY --from=clickhouse /usr/bin/clickhouse /usr/bin/clickhouse
RUN ln -s /usr/bin/clickhouse /usr/bin/clickhouse-client

WORKDIR /suite
