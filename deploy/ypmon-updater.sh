#!/usr/bin/env bash
# Хостовый воркер самообновления сервера YPMonitor.
#
# Слушает файл-запрос .update/request, который пишет веб-контейнер из настроек администратора,
# и выполняет проверку/установку обновления: git pull + docker compose up -d --build.
#
# Работает на ХОСТЕ (не в контейнере), поэтому переживает пересоздание контейнера ypmon-server.
# Веб-контейнеру НЕ нужен доступ к docker-сокету — только к папке-каналу .update (безопасно).
#
# Запускается systemd-сервисом ypmon-updater (см. ypmon-updater.service), от пользователя,
# входящего в группу docker. Устанавливается через deploy/install-updater.sh.
set -u

REPO="${YPMON_REPO:-/home/admin_yp/ypmonitor}"
BRANCH="${YPMON_BRANCH:-main}"
UPD="$REPO/.update"
REQ="$UPD/request"
STATUS="$UPD/status.json"
CHLOG="$UPD/changelog.txt"
LOG="$UPD/updater.log"

mkdir -p "$UPD"

log() { echo "$(date '+%F %T') $*" >> "$LOG" 2>/dev/null; }

json_escape() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

# Аргументы: state updateAvailable(true/false) behind currentShort remoteShort message
write_status() {
  local now; now="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
  cat > "$STATUS.tmp" <<EOF
{
  "state": "$(json_escape "$1")",
  "updateAvailable": $2,
  "behind": ${3:-0},
  "currentShort": "$(json_escape "$4")",
  "remoteShort": "$(json_escape "$5")",
  "lastChecked": "$now",
  "message": "$(json_escape "$6")"
}
EOF
  mv -f "$STATUS.tmp" "$STATUS"
}

do_check() {
  cd "$REPO" 2>/dev/null || { write_status error false 0 "" "" "Репозиторий не найден: $REPO"; return; }
  git fetch --quiet origin "$BRANCH" 2>>"$LOG"
  local cur rem behind avail
  cur="$(git rev-parse --short HEAD 2>/dev/null)"
  rem="$(git rev-parse --short "origin/$BRANCH" 2>/dev/null)"
  behind="$(git rev-list --count "HEAD..origin/$BRANCH" 2>/dev/null || echo 0)"
  git log --pretty=format:'%h %s' "HEAD..origin/$BRANCH" > "$CHLOG" 2>/dev/null || : > "$CHLOG"
  avail=false; [ "${behind:-0}" -gt 0 ] && avail=true
  write_status idle "$avail" "${behind:-0}" "$cur" "$rem" "Проверка выполнена"
  log "check: cur=$cur rem=$rem behind=$behind"
}

do_apply() {
  cd "$REPO" 2>/dev/null || { write_status error false 0 "" "" "Репозиторий не найден"; return; }
  local cur rem behind avail
  cur="$(git rev-parse --short HEAD 2>/dev/null)"
  rem="$(git rev-parse --short "origin/$BRANCH" 2>/dev/null)"
  behind="$(git rev-list --count "HEAD..origin/$BRANCH" 2>/dev/null || echo 0)"
  avail=false; [ "${behind:-0}" -gt 0 ] && avail=true
  write_status updating "$avail" "${behind:-0}" "$cur" "$rem" "Обновление запущено…"
  log "apply: git pull"
  if ! git pull --ff-only origin "$BRANCH" >>"$LOG" 2>&1; then
    write_status error false 0 "$cur" "$rem" "Ошибка git pull (см. .update/updater.log)"; return
  fi
  log "apply: docker compose up -d --build"
  if ! docker compose up -d --build >>"$LOG" 2>&1; then
    write_status error false 0 "$(git rev-parse --short HEAD 2>/dev/null)" "$rem" "Ошибка сборки (см. .update/updater.log)"; return
  fi
  log "apply: done"
  do_check
}

log "updater запущен: репозиторий=$REPO ветка=$BRANCH"
do_check
while true; do
  if [ -f "$REQ" ]; then
    req="$(cat "$REQ" 2>/dev/null)"; rm -f "$REQ"
    case "$req" in
      check) do_check ;;
      apply) do_apply ;;
      *)     log "неизвестный запрос: $req" ;;
    esac
  fi
  sleep 4
done
