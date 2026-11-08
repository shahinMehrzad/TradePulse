# syntax=docker/dockerfile:1
# One file for the six programs. The build context is the repository root:
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
COPY src/Trader/Trader.csproj src/Trader/
COPY src/Research/Research.csproj src/Research/
COPY src/SignalReceiver/SignalReceiver.csproj src/SignalReceiver/
RUN dotnet restore src/BackgroundWorker/BackgroundWorker.csproj \
 && dotnet restore src/WebApi/WebApi.csproj \
 && dotnet restore src/WebRazor/WebRazor.csproj \
 && dotnet restore src/Trader/Trader.csproj \
 && dotnet restore src/Research/Research.csproj \
 && dotnet restore src/SignalReceiver/SignalReceiver.csproj

FROM restore AS build
COPY src ./src
RUN dotnet publish src/BackgroundWorker/BackgroundWorker.csproj -c Release --no-restore -p:UseAppHost=false -o /out/collector \
 && dotnet publish src/WebApi/WebApi.csproj -c Release --no-restore -p:UseAppHost=false -o /out/api \
 && dotnet publish src/WebRazor/WebRazor.csproj -c Release --no-restore -p:UseAppHost=false -o /out/panel \
 && dotnet publish src/Trader/Trader.csproj -c Release --no-restore -p:UseAppHost=false -o /out/trader \
 && dotnet publish src/Research/Research.csproj -c Release --no-restore -p:UseAppHost=false -o /out/watch \
 && dotnet publish src/SignalReceiver/SignalReceiver.csproj -c Release --no-restore -p:UseAppHost=false -o /out/signals

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Keys of data protection, on a volume (see docker-compose.yml). The folder is made here so that the volume starts out writable for the app user.
RUN mkdir /keys && chown $APP_UID /keys
ENV DataProtection__KeysPath=/keys
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

FROM runtime AS trader
COPY --from=build /out/trader .
ENTRYPOINT ["dotnet", "Trader.dll"]

# The price watch: Research's `watch` command, which follows the symbols switched on in the panel until it is stopped.
FROM runtime AS watch
COPY --from=build /out/watch .
ENTRYPOINT ["dotnet", "Research.dll", "watch"]

FROM runtime AS panel
COPY --from=build /out/panel .
ENTRYPOINT ["dotnet", "WebRazor.dll"]

# The program that takes the signals of the program that makes them (docs/signals.md): the webhook, the paper trades and their messages.
FROM runtime AS signals
COPY --from=build /out/signals .
ENTRYPOINT ["dotnet", "SignalReceiver.dll"]
