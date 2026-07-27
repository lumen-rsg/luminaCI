FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["Lumina CI.sln", "./"]
COPY ["src/Shared/Lumina.Shared/Lumina.Shared.csproj", "src/Shared/Lumina.Shared/"]
COPY ["src/Shared/Lumina.Web.Shared/Lumina.Web.Shared.csproj", "src/Shared/Lumina.Web.Shared/"]
COPY ["src/Services/Lumina.ApiGateway/Lumina.ApiGateway.csproj", "src/Services/Lumina.ApiGateway/"]

# Restore dependencies
RUN dotnet restore "src/Services/Lumina.ApiGateway/Lumina.ApiGateway.csproj"

# Copy source code
COPY src/ src/

# Build
RUN dotnet build "src/Services/Lumina.ApiGateway/Lumina.ApiGateway.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/Services/Lumina.ApiGateway/Lumina.ApiGateway.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5000

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

USER app

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Lumina.ApiGateway.dll"]
