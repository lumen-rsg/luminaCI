FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Lumina CI.sln", "./"]
COPY ["src/Shared/Lumina.Shared/Lumina.Shared.csproj", "src/Shared/Lumina.Shared/"]
COPY ["src/Shared/Lumina.Web.Shared/Lumina.Web.Shared.csproj", "src/Shared/Lumina.Web.Shared/"]
COPY ["src/Services/Lumina.SecurityService/Lumina.SecurityService.csproj", "src/Services/Lumina.SecurityService/"]

RUN dotnet restore "src/Services/Lumina.SecurityService/Lumina.SecurityService.csproj"

COPY src/ src/

RUN dotnet build "src/Services/Lumina.SecurityService/Lumina.SecurityService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/Services/Lumina.SecurityService/Lumina.SecurityService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5002

# GnuPG owns the private key; rpm/rpmsign embeds and verifies package signatures.
RUN apt-get update && apt-get install -y gnupg2 pinentry-tty rpm && rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .
RUN mkdir -p /app/keys /app/.gnupg && chmod 700 /app/.gnupg

ENV GNUPGHOME=/app/.gnupg

ENTRYPOINT ["dotnet", "Lumina.SecurityService.dll"]
