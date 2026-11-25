#!/usr/bin/env bash
# Cria ou atualiza os logins da Api (smartparking_api) e dos Workers (smartparking_workers), membros
# do grupo smartparking_app (especificação 001, RNF-06; especificação 002, AC-29). Nem a Api nem os
# Workers ligam como dono das tabelas: assim, a base de dados recusa alterar ou apagar a auditoria e
# não há DDL. Dois logins distinguem os processos no pg_stat_activity (plano 002b, ponto 1).
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
: "${POSTGRES_WORKERS_PASSWORD:?defina POSTGRES_WORKERS_PASSWORD no .env (ver .env.example)}"

# As palavras-passe seguem pelo stdin do psql (nunca na linha de comandos, visível no ps): uma linha
# \set por valor, entre plicas, com \ e ' escapados, e depois format('%L') no SQL. O \gexec executa o
# comando gerado sem o mostrar, e o log_statement fica desligado nesta sessão para o ALTER ROLE não ir
# para o registo do servidor (o dono do compose é superutilizador e pode fazê-lo).
quote_for_psql() {
    local escaped=${1//\\/\\\\}
    printf '%s' "${escaped//\'/\'\'}"
}

{
    printf "\\\\set app_password '%s'\n" "$(quote_for_psql "$POSTGRES_APP_PASSWORD")"
    printf "\\\\set workers_password '%s'\n" "$(quote_for_psql "$POSTGRES_WORKERS_PASSWORD")"
    cat <<'SQL'
SET log_statement = 'none';

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

SELECT format('CREATE ROLE smartparking_workers LOGIN PASSWORD %L', :'workers_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'smartparking_workers')
\gexec

SELECT format('ALTER ROLE smartparking_workers WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', :'workers_password')
\gexec

GRANT smartparking_app TO smartparking_workers;
SQL
} | docker compose exec -T postgres psql --quiet --no-psqlrc -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"

echo "Logins smartparking_api e smartparking_workers prontos (membros de smartparking_app)."
echo "Api: ConnectionStrings__Postgres=\"Host=localhost;Port=${POSTGRES_PORT:-5432};Database=${POSTGRES_DB};Username=smartparking_api;Password=…\""
echo "Workers: ConnectionStrings__Postgres=\"Host=localhost;Port=${POSTGRES_PORT:-5432};Database=${POSTGRES_DB};Username=smartparking_workers;Password=…\""
