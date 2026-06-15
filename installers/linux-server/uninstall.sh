#!/usr/bin/env bash
# Удаление службы YPMon Server (данные в /opt/ypmon/data сохраняются).
set -euo pipefail
SERVICE=ypmon-server

if [[ $EUID -ne 0 ]]; then
  echo "Запустите от root: sudo ./uninstall.sh" >&2
  exit 1
fi

systemctl stop $SERVICE || true
systemctl disable $SERVICE || true
rm -f /etc/systemd/system/$SERVICE.service
systemctl daemon-reload
echo "Служба $SERVICE удалена. Файлы в /opt/ypmon оставлены (удалите вручную при необходимости)."
