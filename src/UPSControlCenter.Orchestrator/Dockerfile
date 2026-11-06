# Dockerfile multi-stage per ups-orchestrator, ups-notifications e ups-simulator.
# Un'unica immagine con entrypoint diversi: i tre servizi condividono il motore decisionale e
# moltiplicare le immagini su un mini PC a risorse limitate non porterebbe vantaggi (§2.1).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props NuGet.config ./
COPY src/UPSControlCenter.Core/*.csproj src/UPSControlCenter.Core/
COPY src/UPSControlCenter.Contracts/*.csproj src/UPSControlCenter.Contracts/
COPY src/UPSControlCenter.Nut/*.csproj src/UPSControlCenter.Nut/
COPY src/UPSControlCenter.Infrastructure/*.csproj src/UPSControlCenter.Infrastructure/
COPY src/UPSControlCenter.Notifications/*.csproj src/UPSControlCenter.Notifications/
COPY src/UPSControlCenter.Adapters/*.csproj src/UPSControlCenter.Adapters/
COPY src/UPSControlCenter.Adapters.Live/*.csproj src/UPSControlCenter.Adapters.Live/
COPY src/UPSControlCenter.Orchestrator/*.csproj src/UPSControlCenter.Orchestrator/

RUN dotnet restore src/UPSControlCenter.Orchestrator/UPSControlCenter.Orchestrator.csproj

COPY src/ src/
RUN dotnet publish src/UPSControlCenter.Orchestrator/UPSControlCenter.Orchestrator.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# L'immagine è aspnet e non runtime perché UPSControlCenter.Infrastructure usa Data Protection,
# che vive nel framework condiviso ASP.NET Core (docs/06).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN groupadd -g 64198 upscc && useradd -u 64198 -g 64198 -s /usr/sbin/nologin -M upscc

COPY --from=build /app/publish .
RUN chown -R 64198:64198 /app

USER 64198:64198

ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "UPSControlCenter.Orchestrator.dll"]
