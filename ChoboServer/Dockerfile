FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ChoboServer/ChoboServer.csproj", "ChoboServer/"]
COPY ["Chobo.Contracts/Chobo.Contracts.csproj", "Chobo.Contracts/"]
RUN dotnet restore "ChoboServer/ChoboServer.csproj"
COPY . .
WORKDIR "/src/ChoboServer"
RUN dotnet build "./ChoboServer.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ChoboServer.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ChoboServer.dll"]
