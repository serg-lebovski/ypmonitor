#!/usr/bin/env bash
# Однократная установка хостового воркера самообновления YPMonitor.
# Запускать на сервере из папки репозитория: sudo bash deploy/install-updater.sh
set -euo pipefail

REPO="${YPMON_REPO:-/home/admin_yp/ypmonitor}"
USER_NAME="${YPMON_USER:-admin_yp}"

echo "==> Репозиторий: $REPO, пользователь: $USER_NAME"

# 1) Пользователь должен иметь доступ к docker (для docker compose из воркера)
if ! id -nG "$USER_NAME" | tr ' ' '\n' | grep -qx docker; then
  usermod -aG docker "$USER_NAME"
  echo "    Пользователь $USER_NAME добавлен в группу docker."
fi

# 2) Разрешаем git-операции в репозитории (на случай смены владельца)
sudo -u "$USER_NAME" git config --global --add safe.directory "$REPO" 2>/dev/null || true

# 3) Папка-канал .update (запрос/статус). Открыта на запись, чтобы контейнер мог класть запрос.
mkdir -p "$REPO/.update"
chown "$USER_NAME":"$USER_NAME" "$REPO/.update"
chmod 777 "$REPO/.update"

# 4) Права на запуск скрипта
chmod +x "$REPO/deploy/ypmon-updater.sh"

# 5) Устанавливаем и запускаем systemd-сервис
install -m 644 "$REPO/deploy/ypmon-updater.service" /etc/systemd/system/ypmon-updater.service
systemctl daemon-reload
systemctl enable ypmon-updater.service
systemctl restart ypmon-updater.service
sleep 1
systemctl --no-pager --full status ypmon-updater.service | head -n 10 || true
echo "==> Готово. Воркер самообновления установлен и запущен."
