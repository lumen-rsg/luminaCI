FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Lumina CI.sln", "./"]
COPY ["src/Shared/Lumina.Shared/Lumina.Shared.csproj", "src/Shared/Lumina.Shared/"]
COPY ["src/Shared/Lumina.Web.Shared/Lumina.Web.Shared.csproj", "src/Shared/Lumina.Web.Shared/"]
COPY ["src/Services/Lumina.ScannerService/Lumina.ScannerService.csproj", "src/Services/Lumina.ScannerService/"]

RUN dotnet restore "src/Services/Lumina.ScannerService/Lumina.ScannerService.csproj"

COPY src/ src/

RUN dotnet build "src/Services/Lumina.ScannerService/Lumina.ScannerService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/Services/Lumina.ScannerService/Lumina.ScannerService.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Pinned to match deploy/docker-compose.yml `trivy` service (0.72.0). Keep both
# in sync so the embedded trivy binary and the server speak the same protocol.
FROM aquasec/trivy:0.72.0 AS trivy-bin

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5003

# Install rpm2cpio and cpio for RPM extraction (needed for trivy rootfs scanning)
RUN apt-get update && apt-get install -y --no-install-recommends curl cpio rpm2cpio && rm -rf /var/lib/apt/lists/*

# Copy trivy binary from official image
COPY --from=trivy-bin /usr/local/bin/trivy /usr/local/bin/trivy

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Lumina.ScannerService.dll"]
