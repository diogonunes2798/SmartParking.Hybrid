#!/usr/bin/env bash
# Cria ou atualiza o login da Api (smartparking_api), membro do grupo smartparking_app
# (especificação 001, RNF-06). A Api nunca liga como dono das tabelas: assim, a base de dados
# recusa alterar ou apagar a auditoria e não há DDL.
#
# Pré-requisitos:
#   docker compose up -d --wait
#   migrações aplicadas como dono (criam o grupo smartparking_app):
#     ConnectionStrings__PostgresMigrations="Host=localhost;Port=5432;Database=smartparking;Username=smartparking;Password=…" \
#       dotnet ef database update --project src/SmartParking.Infrastructure --startup-project src/SmartParking.Api
#
# Uso: scripts/db-app-user.sh   (idempotente: volta a definir a palavra-passe se o login já existir)
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [[ ! -f .env ]]; then
    echo "Falta o ficheiro .env (cp .env.example .env)." >&2
    exit 1
fi

# CR removido: o .env pode ter fins de linha do Windows.
set -a
# shellcheck disable=SC1090
source <(tr -d '\r' < .env)
set +a

: "${POSTGRES_USER:?defina POSTGRES_USER no .env}"
: "${POSTGRES_DB:?defina POSTGRES_DB no .env}"
: "${POSTGRES_APP_PASSWORD:?defina POSTGRES_APP_PASSWORD no .env (ver .env.example)}"

# A palavra-passe entra como variável do psql e é citada com format('%L'), nunca por interpolação da shell.
# O \gexec executa o comando gerado sem o mostrar.
docker compose exec -T postgres \
    psql --quiet --no-psqlrc -v ON_ERROR_STOP=1 -v app_password="$POSTGRES_APP_PASSWORD" \
    -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<'SQL'
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'smartparking_app') THEN
        RAISE EXCEPTION 'O grupo smartparking_app não existe: aplique primeiro as migrações (dotnet ef database update).';
    END IF;
END
$$;

SELECT format('CREATE ROLE smartparking_api LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'smartparking_api')
\gexec

SELECT format('ALTER ROLE smartparking_api WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', :'app_password')
\gexec

GRANT smartparking_app TO smartparking_api;
SQL

echo "Login smartparking_api pronto (membro de smartparking_app)."
echo "Api: ConnectionStrings__Postgres=\"Host=localhost;Port=${POSTGRES_PORT:-5432};Database=${POSTGRES_DB};Username=smartparking_api;Password=…\""
