# AmneziaWG-сайдкар: поднимает полноценный AmneziaWG-туннель (userspace amneziawg-go)
# и отдаёт SOCKS5 (microsocks) на :1080 для других контейнеров compose.
# Конфиг забирается из общего тома (/config/awg0.conf), который пишет сервер.

# --- Сборка amneziawg-go и amneziawg-tools из исходников ---
FROM golang:1.23-bookworm AS build
RUN apt-get update && apt-get install -y --no-install-recommends git make gcc libc6-dev bash \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /b
RUN git clone --depth=1 https://github.com/amnezia-vpn/amneziawg-go.git \
 && cd amneziawg-go && make
RUN git clone --depth=1 https://github.com/amnezia-vpn/amneziawg-tools.git \
 && cd amneziawg-tools/src && make && make install
# awg-quick (bash) — на случай, если install его не положил в /usr/bin
RUN cp -f /b/amneziawg-tools/src/wg-quick/linux.bash /usr/bin/awg-quick 2>/dev/null || true

# --- Runtime ---
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
      iproute2 iptables procps microsocks ca-certificates bash \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /b/amneziawg-go/amneziawg-go /usr/bin/amneziawg-go
COPY --from=build /usr/bin/awg /usr/bin/awg
COPY --from=build /usr/bin/awg-quick /usr/bin/awg-quick
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh /usr/bin/awg-quick /usr/bin/awg /usr/bin/amneziawg-go
ENTRYPOINT ["/entrypoint.sh"]
