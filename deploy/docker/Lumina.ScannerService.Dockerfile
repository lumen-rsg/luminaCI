
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Lumina CI.sln", "./"]
COPY ["src/Shared/Lumina.Shared/Lumina.Shared.csproj", "src/Shared/Lumina.Shared/"]
COPY ["src/Services/Lumina.ScannerService/Lumina.ScannerService.csproj", "src/Services/Lumina.ScannerService/"]

RUN dotnet restore "src/Services/Lumina.ScannerService/Lumina.ScannerService.csproj"

COPY src/ src/

RUN dotnet build "src/Services/Lumina.ScannerService/Lumina.ScannerService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/Services/Lumina.ScannerService/Lumina.ScannerService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5003

USER app

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Lumina.ScannerService.dll"]