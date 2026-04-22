FROM mcr.microsoft.com/dotnet/sdk:10.0 AS base
WORKDIR /src

COPY ["src/Services/Lumina.WebApp/Lumina.WebApp.csproj", "src/Services/Lumina.WebApp/"]
COPY ["src/Shared/Lumina.Shared/Lumina.Shared.csproj", "src/Shared/Lumina.Shared/"]

RUN dotnet restore "src/Services/Lumina.WebApp/Lumina.WebApp.csproj"

COPY src/Services/Lumina.WebApp/ src/Services/Lumina.WebApp/
COPY src/Shared/Lumina.Shared/ src/Shared/Lumina.Shared/

RUN dotnet build "src/Services/Lumina.WebApp/Lumina.WebApp.csproj" -c Release -o /app/build

FROM base AS publish
RUN dotnet publish "src/Services/Lumina.WebApp/Lumina.WebApp.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM nginx:alpine AS final
WORKDIR /usr/share/nginx/html

COPY --from=publish /app/publish/wwwroot .
COPY deploy/nginx/blazor.conf /etc/nginx/conf.d/default.conf

EXPOSE 80