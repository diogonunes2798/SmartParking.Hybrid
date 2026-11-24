#!/usr/bin/env bash
# Para os contentores do projeto «smartparking» e apaga os seus volumes
# (pgdata, mongodata, mongoconfig, redisdata). Não toca em recursos de outros projetos.
# Uso: scripts/db-reset.sh [-y]
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [[ "${1:-}" != "-y" ]]; then
    read -r -p "Apagar contentores e volumes do projeto smartparking? [y/N] " answer
    [[ "$answer" =~ ^[yY]$ ]] || { echo "Cancelado."; exit 1; }
fi

docker compose down --volumes --remove-orphans
echo "Volumes do projeto apagados. Para recriar: docker compose up -d --wait"
