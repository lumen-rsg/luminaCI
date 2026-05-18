FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["Lumina CI.sln", "./"]
COPY src/Shared/Lumina.Shared/Lumina.Shared.csproj src/Shared/Lumina.Shared/
COPY src/Services/Lumina.SourceService/Lumina.SourceService.csproj src/Services/Lumina.SourceService/

# Restore dependencies
RUN dotnet restore src/Services/Lumina.SourceService/Lumina.SourceService.csproj

# Copy source code
COPY src/Shared/Lumina.Shared/ src/Shared/Lumina.Shared/
COPY src/Services/Lumina.SourceService/ src/Services/Lumina.SourceService/

# Build and publish
RUN dotnet publish src/Services/Lumina.SourceService/Lumina.SourceService.csproj \
    -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# Install tools needed for source fetching (git, curl, wget, rsync, svn, hg)
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    curl \
    wget \
    rsync \
    subversion \
    mercurial \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Create temp directory for source fetching
RUN mkdir -p /tmp/source-fetch && chmod 777 /tmp/source-fetch

EXPOSE 5006
ENTRYPOINT ["dotnet", "Lumina.SourceService.dll"]