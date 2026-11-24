# ADR 0001 — Infraestrutura local de desenvolvimento

- **Estado:** Proposto
- **Data:** 2026-11-24
- **Requisitos:** RF-04 (PostGIS), RNF-03 (falha de nó, replica set)
- **Decisão (D1–D10):** TODO(Diogo): indicar a decisão D aplicável, se existir.
- **Secção da tese:** TODO(Diogo): indicar a secção do Cap. 5.

## Contexto

O protótipo usa três tecnologias de persistência (PostgreSQL com PostGIS, MongoDB e Redis). É preciso um ambiente local reprodutível, igual para o desenvolvimento, os testes manuais e as medições do Cap. 7. O registo de eventos tem de correr em replica set para o cenário de falha de nó (RNF-03).

## Decisão

Um único `compose.yaml` na raiz, com o projeto `smartparking`:

- `postgres`: `postgis/postgis:17-3.7`. A imagem ativa o PostGIS na base de dados por omissão.
- `mongo`: `mongo:8.0.38`, replica set `rs0` de 1 nó. Com autenticação, o `mongod` exige um *keyfile*; é gerado no arranque e guardado no volume de configuração. O replica set é iniciado pelo próprio *healthcheck* na primeira execução, e o serviço só fica *healthy* quando o nó é PRIMARY.
- `redis`: `redis:8.12.0`, com palavra-passe e *append-only file* (AOF) ligado (o Redis não é fonte de verdade, mas evita reconstruir a cada reinício).
- Tags fixadas, nunca `latest`. Healthcheck em todos os serviços, volumes com nome, portas só em `127.0.0.1`.
- Credenciais em `.env` (ignorado pelo Git); `.env.example` versionado com valores de desenvolvimento.
- `scripts/db-reset.sh` para parar e apagar os volumes do projeto.

## Alternativas consideradas

- **Mongo 9.0.x ou 8.3.x:** a 9.0 é a mais recente, mas o CLAUDE.md fixa a linha 8; mudar de major é uma decisão de stack. A 8.3 é de lançamento rápido.
- **Mongo sem replica set:** mais simples, mas impede testar RNF-03 e transações.
- **Sem autenticação:** menos configuração, mas afasta o ambiente de um uso realista e deixa as bases abertas a qualquer processo local.
- **Imagens `latest`:** resultados das medições não reprodutíveis.
- **Serviço `mongo-init` separado:** mais um contentor; o *healthcheck* de arranque evita-o.

## Consequências

- O ambiente arranca com `docker compose up -d --wait` e repõe-se com `scripts/db-reset.sh`.
- A ligação ao Mongo a partir do anfitrião usa o *host* `localhost:27017` anunciado pelo replica set; clientes noutros contentores precisarão de outra configuração.
- O keyfile e as credenciais são só para desenvolvimento. Não servem para produção.
- Atualizar uma tag é uma alteração explícita ao `compose.yaml`, registada no diário.
