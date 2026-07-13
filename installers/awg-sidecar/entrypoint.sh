#!/usr/bin/env bash
# Поднимает AmneziaWG-туннель из /config/awg0.conf и держит SOCKS5 (microsocks) на :1080.
# Следит за изменениями конфига и переподнимает туннель.
set -u

CONF=/config/awg0.conf
export WG_QUICK_USERSPACE_IMPLEMENTATION=amneziawg-go
export WG_SUDO=1
SOCKS=0

log(){ echo "[awg] $*"; }

up(){   log "awg-quick up"; awg-quick up "$CONF" 2>&1 | sed 's/^/[awg-quick] /' || log "awg-quick up: ошибка (см. выше)"; }
down(){ awg-quick down "$CONF" 2>&1 | sed 's/^/[awg-quick] /' || true; }
start_socks(){ microsocks -i 0.0.0.0 -p 1080 & SOCKS=$!; log "microsocks запущен (pid $SOCKS, порт 1080)"; }

cleanup(){ down; kill "$SOCKS" 2>/dev/null || true; exit 0; }
trap cleanup TERM INT

log "ожидание конфигурации $CONF ..."
while [ ! -s "$CONF" ]; do sleep 5; done

up
start_socks
LAST=$(md5sum "$CONF" | awk '{print $1}')

while true; do
  sleep 10
  [ -s "$CONF" ] || continue
  NOW=$(md5sum "$CONF" | awk '{print $1}')
  if [ "$NOW" != "$LAST" ]; then
    log "конфиг изменился — переподнимаю туннель"
    down; up; LAST="$NOW"
  fi
  # microsocks мог умереть — перезапускаем
  kill -0 "$SOCKS" 2>/dev/null || start_socks
done
