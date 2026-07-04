FROM mcr.microsoft.com/dotnet/sdk:10.0 AS base
WORKDIR /src

# .NET 10 Blazor WASM needs the wasm-tools workload to populate the import map
# in index.html at publish time. Without it, dotnet publish emits the hashed
# dotnet.<hash>.js runtime but leaves the bootstrapper's bare `import("./dotnet.js")`
# unmapped — the import map stays empty, dotnet.js 404s, and Blazor fails to start
# with "error loading dynamically imported module" / "disallowed MIME type text/html".
#
# The wasm-tools workload drives emscripten to (re)build the JS runtime + import
# map, and emscripten shells out to python — which the base SDK image lacks
# (dotnet/sdk#52332). Install python3 first so the workload can run.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/* \
    && dotnet workload install wasm-tools

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