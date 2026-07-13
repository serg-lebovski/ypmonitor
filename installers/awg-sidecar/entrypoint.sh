#!/usr/bin/env bash
# Поднимает AmneziaWG-туннель из /config/awg0.conf и держит SOCKS5 (microsocks) на :1080.
# В unprivileged-LXC wg-quick не может выполнить sysctl src_valid_mark, поэтому маршруты
# настраиваем вручную (Table = off): исключаем endpoint, дефолт заворачиваем в awg0.

set -u
CONF=/config/awg0.conf
WORK=/tmp/awg0.conf
export WG_QUICK_USERSPACE_IMPLEMENTATION=amneziawg-go
export WG_SUDO=1
SOCKS=0

log(){ echo "[awg] $*"; }

prepare(){
  # Добавляем "Table = off" в [Interface] — маршрутами управляем сами.
  awk '/^\[Interface\]/{print; print "Table = off"; next} {print}' "$CONF" > "$WORK"
}

route_up(){
  local ep gw dev
  ep=$(grep -oiE 'Endpoint[[:space:]]*=[[:space:]]*[0-9.]+' "$CONF" | grep -oE '[0-9.]+' | head -1)
  gw=$(ip route show default | awk '/default/{print $3; exit}')
  dev=$(ip route show default | awk '/default/{print $5; exit}')
  if [ -n "$ep" ] && [ -n "$gw" ] && [ -n "$dev" ]; then
    ip route replace "$ep/32" via "$gw" dev "$dev" && log "endpoint $ep напрямую через $gw dev $dev"
  fi
  if ip route replace default dev awg0; then log "дефолтный маршрут через awg0 (туннель)"; else log "не удалось поставить default через awg0"; fi
}

up(){
  prepare
  log "awg-quick up"
  awg-quick up "$WORK" 2>&1 | sed 's/^/[awg-quick] /' || log "awg-quick up: ошибка (см. выше)"
  ip link show awg0 >/dev/null 2>&1 && route_up || log "интерфейс awg0 не поднялся"
}

down(){ awg-quick down "$WORK" 2>&1 | sed 's/^/[awg-quick] /' || true; }

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
  kill -0 "$SOCKS" 2>/dev/null || start_socks
done
