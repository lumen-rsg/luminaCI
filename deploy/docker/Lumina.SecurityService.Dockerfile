FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Lumina CI.sln", "./"]
COPY ["src/Shared/Lumina.Shared/Lumina.Shared.csproj", "src/Shared/Lumina.Shared/"]
COPY ["src/Services/Lumina.SecurityService/Lumina.SecurityService.csproj", "src/Services/Lumina.SecurityService/"]

RUN dotnet restore "src/Services/Lumina.SecurityService/Lumina.SecurityService.csproj"

COPY src/ src/

RUN dotnet build "src/Services/Lumina.SecurityService/Lumina.SecurityService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/Services/Lumina.SecurityService/Lumina.SecurityService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5002

# Install GnuPG for PGP signing
RUN apt-get update && apt-get install -y gnupg2 pinentry-tty && rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .
RUN mkdir -p /app/keys && chown app:app /app/keys
USER app

ENTRYPOINT ["dotnet", "Lumina.SecurityService.dll"]