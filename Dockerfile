# --- Сборка ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Сначала только проекты — для кэширования restore
COPY src/Ypmon.Shared/Ypmon.Shared.csproj src/Ypmon.Shared/
COPY src/Ypmon.Server/Ypmon.Server.csproj src/Ypmon.Server/
RUN dotnet restore src/Ypmon.Server/Ypmon.Server.csproj

# Остальной код
COPY src/Ypmon.Shared/ src/Ypmon.Shared/
COPY src/Ypmon.Server/ src/Ypmon.Server/
RUN dotnet publish src/Ypmon.Server/Ypmon.Server.csproj -c Release -o /app /p:UseAppHost=false

# --- Среда выполнения ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
# curl нужен для healthcheck; wireproxy — userspace-WireGuard для доступа бота к Telegram
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && ( curl -fsSL https://github.com/whyvl/wireproxy/releases/download/v1.0.9/wireproxy_linux_amd64.tar.gz -o /tmp/wp.tgz \
         && tar -xzf /tmp/wp.tgz -C /usr/local/bin wireproxy \
         && chmod +x /usr/local/bin/wireproxy \
         && rm -f /tmp/wp.tgz ) || echo "ВНИМАНИЕ: wireproxy не установлен — WireGuard будет недоступен"
COPY --from=build /app .

# Папка для данных (SQLite/логи), если используется sqlite; для postgres не нужна, но пусть будет
RUN mkdir -p /app/data /app/agent-updates
VOLUME ["/app/data", "/app/agent-updates"]

ENV Server__HttpPort=8080 \
    Database__Provider=postgres \
    DOTNET_gcServer=1

EXPOSE 8080
ENTRYPOINT ["dotnet", "Ypmon.Server.dll"]
