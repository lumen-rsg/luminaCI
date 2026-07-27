FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Lumina CI.sln", "./"]
COPY ["src/Shared/Lumina.Shared/Lumina.Shared.csproj", "src/Shared/Lumina.Shared/"]
COPY ["src/Shared/Lumina.Web.Shared/Lumina.Web.Shared.csproj", "src/Shared/Lumina.Web.Shared/"]
COPY ["src/Services/Lumina.BuildService/Lumina.BuildService.csproj", "src/Services/Lumina.BuildService/"]

RUN dotnet restore "src/Services/Lumina.BuildService/Lumina.BuildService.csproj"

COPY src/ src/

RUN dotnet build "src/Services/Lumina.BuildService/Lumina.BuildService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/Services/Lumina.BuildService/Lumina.BuildService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5001

# Install Docker CLI for container management and rpm for artifact validation.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl docker.io rpm \
    && rm -rf /var/lib/apt/lists/*

USER app

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Lumina.BuildService.dll"]
