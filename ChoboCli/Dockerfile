FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_VERSION=0.0.0-dev
ARG INFORMATIONAL_VERSION=0.0.0-dev
WORKDIR /src
COPY ["Chobo.Contracts/Chobo.Contracts.csproj", "Chobo.Contracts/"]
COPY ["ChoboCli/ChoboCli.csproj", "ChoboCli/"]
RUN dotnet restore "ChoboCli/ChoboCli.csproj"
COPY . .
RUN dotnet publish "ChoboCli/ChoboCli.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:Version=$BUILD_VERSION /p:InformationalVersion=$INFORMATIONAL_VERSION

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ChoboCli.dll"]

