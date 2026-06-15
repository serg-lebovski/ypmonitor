#!/usr/bin/env bash
# Установка YPMon Server на Linux как службы systemd.
# Запускать от root:  sudo ./install.sh
set -euo pipefail

INSTALL_DIR=/opt/ypmon
SERVICE=ypmon-server
SRC_DIR="$(cd "$(dirname "$0")" && pwd)"

if [[ $EUID -ne 0 ]]; then
  echo "Запустите от root: sudo ./install.sh" >&2
  exit 1
fi

# Пользователь службы
if ! id ypmon &>/dev/null; then
  useradd --system --no-create-home --shell /usr/sbin/nologin ypmon
fi

mkdir -p "$INSTALL_DIR"
# Копируем всё, кроме служебных файлов установки
for f in "$SRC_DIR"/*; do
  base="$(basename "$f")"
  case "$base" in
    install.sh|uninstall.sh|ypmon-server.service) continue;;
  esac
  cp -r "$f" "$INSTALL_DIR/"
done

chmod +x "$INSTALL_DIR/Ypmon.Server"
mkdir -p "$INSTALL_DIR/data"
chown -R ypmon:ypmon "$INSTALL_DIR"

cp "$SRC_DIR/ypmon-server.service" /etc/systemd/system/$SERVICE.service
systemctl daemon-reload
systemctl enable $SERVICE
systemctl restart $SERVICE

PORT=$(grep -oP '"HttpPort"\s*:\s*\K[0-9]+' "$INSTALL_DIR/appsettings.json" 2>/dev/null || echo 8080)
echo ""
echo "YPMon Server установлен и запущен (служба $SERVICE)."
echo "Веб-интерфейс:  http://<адрес-сервера>:$PORT/"
echo "Логи:           journalctl -u $SERVICE -f"
echo "При первом входе создайте администратора."
