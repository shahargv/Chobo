FROM clickhouse/clickhouse-server:26.3 AS clickhouse
FROM minio/mc:RELEASE.2025-08-13T08-35-41Z AS minio-client

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS cli-build
WORKDIR /src
COPY Chobo.Contracts/Chobo.Contracts.csproj Chobo.Contracts/
COPY ChoboCli/ChoboCli.csproj ChoboCli/
RUN dotnet restore ChoboCli/ChoboCli.csproj
COPY . .
RUN dotnet publish ChoboCli/ChoboCli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o /cli

FROM mcr.microsoft.com/powershell:7.5-ubuntu-24.04

COPY --from=clickhouse /usr/bin/clickhouse /usr/bin/clickhouse
COPY --from=minio-client /usr/bin/mc /usr/bin/mc
COPY --from=cli-build /cli /opt/chobo-cli
RUN ln -s /usr/bin/clickhouse /usr/bin/clickhouse-client
RUN ln -s /opt/chobo-cli/ChoboCli /usr/local/bin/ChoboCli

WORKDIR /suite
