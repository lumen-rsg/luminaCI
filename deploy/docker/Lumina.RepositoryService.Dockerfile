FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Lumina CI.sln", "./"]
COPY ["src/Shared/Lumina.Shared/Lumina.Shared.csproj", "src/Shared/Lumina.Shared/"]
COPY ["src/Services/Lumina.RepositoryService/Lumina.RepositoryService.csproj", "src/Services/Lumina.RepositoryService/"]

RUN dotnet restore "src/Services/Lumina.RepositoryService/Lumina.RepositoryService.csproj"

COPY src/ src/

RUN dotnet build "src/Services/Lumina.RepositoryService/Lumina.RepositoryService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/Services/Lumina.RepositoryService/Lumina.RepositoryService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5004

# Install createrepo for RPM repository management
RUN apt-get update && apt-get install -y createrepo-c && rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .
RUN mkdir -p /app/repos && chown app:app /app/repos
USER app

ENTRYPOINT ["dotnet", "Lumina.RepositoryService.dll"]