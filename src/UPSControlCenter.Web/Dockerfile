# Dockerfile multi-stage per ups-web (§23).
# Immagini fissate a tag espliciti: :latest renderebbe i deploy non riproducibili.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Si copiano prima i soli file di progetto: il layer di restore viene riusato finché le
# dipendenze non cambiano, risparmiando tempo e banda sul mini PC.
COPY Directory.Build.props Directory.Packages.props NuGet.config ./
COPY src/UPSControlCenter.Core/*.csproj src/UPSControlCenter.Core/
COPY src/UPSControlCenter.Contracts/*.csproj src/UPSControlCenter.Contracts/
COPY src/UPSControlCenter.Nut/*.csproj src/UPSControlCenter.Nut/
COPY src/UPSControlCenter.Infrastructure/*.csproj src/UPSControlCenter.Infrastructure/
COPY src/UPSControlCenter.Notifications/*.csproj src/UPSControlCenter.Notifications/
COPY src/UPSControlCenter.Simulator/*.csproj src/UPSControlCenter.Simulator/
COPY src/UPSControlCenter.Adapters/*.csproj src/UPSControlCenter.Adapters/
COPY src/UPSControlCenter.Adapters.Live/*.csproj src/UPSControlCenter.Adapters.Live/
COPY src/UPSControlCenter.Web/*.csproj src/UPSControlCenter.Web/

RUN dotnet restore src/UPSControlCenter.Web/UPSControlCenter.Web.csproj

COPY src/ src/
RUN dotnet publish src/UPSControlCenter.Web/UPSControlCenter.Web.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Le regole IANA servono alla conversione esplicita Europe/Rome, inclusi i passaggi
# automatici tra ora solare e ora legale.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*

# Utente non privilegiato: §15 vieta i container privilegiati e il minimo privilegio vale
# anche dentro il container.
RUN groupadd -g 64198 upscc && useradd -u 64198 -g 64198 -s /usr/sbin/nologin -M upscc

COPY --from=build /app/publish .

# /keys contiene le chiavi Data Protection (volume persistente), /tmp serve con root-fs in sola lettura.
RUN mkdir -p /keys && chown -R 64198:64198 /keys /app

USER 64198:64198

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "UPSControlCenter.Web.dll"]
