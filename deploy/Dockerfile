# syntax=docker/dockerfile:1
# One file for the three programs. The build context is the repository root:
#   docker build -f deploy/Dockerfile --target collector -t tradepulse-collector .
# (docker compose does this; see deploy/docker-compose.yml)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
# Only the project files first, so that the packages are restored again only when they change.
COPY src/Domain/Domain.csproj src/Domain/
COPY src/Application/Application.csproj src/Application/
COPY src/Infrastructure/Infrastructure.csproj src/Infrastructure/
COPY src/BackgroundWorker/BackgroundWorker.csproj src/BackgroundWorker/
COPY src/WebApi/WebApi.csproj src/WebApi/
COPY src/WebRazor/WebRazor.csproj src/WebRazor/
RUN dotnet restore src/BackgroundWorker/BackgroundWorker.csproj \
 && dotnet restore src/WebApi/WebApi.csproj \
 && dotnet restore src/WebRazor/WebRazor.csproj

FROM restore AS build
COPY src ./src
RUN dotnet publish src/BackgroundWorker/BackgroundWorker.csproj -c Release --no-restore -p:UseAppHost=false -o /out/collector \
 && dotnet publish src/WebApi/WebApi.csproj -c Release --no-restore -p:UseAppHost=false -o /out/api \
 && dotnet publish src/WebRazor/WebRazor.csproj -c Release --no-restore -p:UseAppHost=false -o /out/panel

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Not root.
USER $APP_UID

FROM runtime AS collector
COPY --from=build /out/collector .
# Serves /health for the stack and for the panel's Collector health page.
ENV Health__Url=http://0.0.0.0:8080
ENTRYPOINT ["dotnet", "BackgroundWorker.dll"]

FROM runtime AS api
COPY --from=build /out/api .
ENTRYPOINT ["dotnet", "WebApi.dll"]

FROM runtime AS panel
COPY --from=build /out/panel .
ENTRYPOINT ["dotnet", "WebRazor.dll"]
