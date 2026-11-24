# Diário do projeto

Registo, por fase, do trabalho no protótipo SmartParking.Hybrid: o que foi feito, problemas (incluindo tentativas falhadas), decisões e ligações aos *commits*. É a matéria-prima das secções 5.9 (implementação) e 7.8 (iterações ao artefacto) da tese.

Formato de cada entrada:

- **Data**
- **Feito**
- **Problemas**
- **Decisões**
- ***Commits***

---

## A1–A2: preparação

- **Data:** 2026-11-24
- **Feito:**
  - Repositório criado, com licença, `.gitignore` para .NET e terminações de linha LF.
  - Instruções do projeto (`CLAUDE.md`) e regras do Claude Code (`.claude/rules/`).
  - *Hooks* de *pre-commit* do dotnet-claude-kit.
  - Esqueleto da documentação: índices de `docs/specs/`, `docs/plans/` e `docs/adr/`, `docs/RASTREABILIDADE.md`, este diário, `results/` e `bench/`.
- **Versões das ferramentas** (recolhidas a 2026-11-24 com `dotnet --version`, `claude --version`, `dotnet tool list -g` e o `plugin.json` do *plugin*):
  - .NET SDK: 10.0.404 (é o SDK por omissão da máquina; o `global.json` ainda não existe);
  - Claude Code: 2.1.325;
  - dotnet-claude-kit: 0.14.0;
  - CWM.RoslynNavigator (`cwm.roslynnavigator`, ferramenta global .NET): 0.8.0.
- **Problemas:** por preencher.
- **Decisões:**
  - EF Core e Npgsql na linha 10.x: os pacotes 11.0.0 de `Microsoft.EntityFrameworkCore` e `Npgsql.EntityFrameworkCore.PostgreSQL` só incluem `net11.0` (verificado nos `.nuspec` do NuGet a 2026-11-24), o que não é compatível com o alvo `net10.0`.
  - `dotnet-ef` 10.0.15 fixado como ferramenta local no manifesto `dotnet-tools.json` (`rollForward: false`), para coincidir com o pacote `Microsoft.EntityFrameworkCore.Design` 10.x. A ferramenta global instalada na máquina é a 11.0.0; dentro do repositório, `dotnet ef --version` resolve para a 10.0.15. Quem clonar o repositório obtém-na com `dotnet tool restore`.
- ***Commits:*** `5fca546`, `2720bab`, `9fb74d3`, `6f8d1b7`, `743000e` (esqueleto da documentação).

## Infraestrutura local (`compose.yaml`)

- **Data:** 2026-11-24
- **Feito:**
  - `compose.yaml` com PostgreSQL+PostGIS, MongoDB em replica set de 1 nó e Redis; `.env.example`; `scripts/db-reset.sh`; ADR 0001 e entrada no índice dos ADR.
  - Verificação: os 3 serviços ficam *healthy*; `postgis_full_version()` responde (PostGIS 3.7.0, PostgreSQL 17); `rs.status()` mostra um PRIMARY; `redis-cli ping` responde PONG. O `db-reset.sh` apagou os volumes do projeto e um novo `docker compose up -d --wait` voltou a deixar tudo *healthy*.
- **Problemas:**
  - Com autenticação ligada, o `mongod` em replica set exige um *keyfile*. Foi gerado no arranque do contentor e guardado no volume de configuração.
  - O replica set é iniciado pelo *healthcheck* na primeira execução, em vez de um serviço de inicialização à parte.
  - Consequência da autenticação: `rs.status()` sem credenciais falha com `requires authentication`; os comandos de verificação do Mongo levam `-u`, `-p` e `--authenticationDatabase admin`.
  - O daemon do Docker estava parado no início da sessão; arrancou-se o Docker Desktop. Sem tentativas falhadas nos serviços.
- **Decisões:**
  - Tags fixadas a 2026-11-24: `postgis/postgis:17-3.7`, `mongo:8.0.38`, `redis:8.12.0`.
  - MongoDB 8.0.38 e não 9.0.x: o CLAUDE.md fixa a linha 8 e mudar de major é uma decisão de stack. A 9.0.8 já existe no Docker Hub.
  - Autenticação mantida nas três bases, apesar de ser só para desenvolvimento.
  - Redis com AOF ligado (não é fonte de verdade, mas evita reconstruir a cada reinício).
  - Detalhe e alternativas: `docs/adr/0001-infraestrutura-local.md`.
  - TODO(Diogo): indicar a fase do plano a que esta entrada pertence e a decisão D e a secção da tese do ADR 0001.
- ***Commits:*** por registar (alterações ainda não commitadas).
