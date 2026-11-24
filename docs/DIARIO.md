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
  - Fase F1. O ADR 0001 não aplica nenhuma decisão D1–D10 e responde à secção 5.9.1 (confirmado pelo Diogo a 2026-11-24).
- ***Commits:*** `115254c`.

## Ponto 7b: ordem das fases e especificações

- **Data:** 2026-11-24
- **Feito:**
  - Ordem das 13 fases (F1 a F13) fixada em `docs/specs/README.md`, com os princípios da divisão, os requisitos e objetivos de cada fase, a secção da tese e as dependências entre fases.
  - Reservados os números das especificações 000 a 012 (estado «Por especificar»): o `/spec` do *kit* é interativo e só o Diogo aprova, por isso nenhuma especificação foi escrita nesta sessão.
  - `docs/RASTREABILIDADE.md`: colunas «UC» e «Especificação» preenchidas a partir da tabela de fases.
- **Problemas:**
  - O texto de origem (plano da tese) chegou cortado no princípio sobre as medições («quando os dois modos (híbrido e monolítico) estão»); completou-se com «implementados, isto é, depois de F9». Confirmado pelo Diogo a 2026-11-24.
  - Com as Tabelas 12 a 14 (resumo acrescentado a `docs/RASTREABILIDADE.md`), a coluna «UC» passou a seguir a tese: RF-01 liga a UC-01 e UC-05, RNF-01 a UC-01, UC-02 e UC-03. RNF-02, RNF-03, RNF-05 e RNF-06 não têm UC na Tabela 14 e ficam com «—».
- **Decisões:**
  - A numeração das especificações segue a ordem das fases (F*n* ↔ especificação *n*−1), e não a ordem de criação prevista pelo *kit*.
  - Estado novo «Por especificar» antes de Rascunho, para distinguir um número reservado de uma especificação em curso.
  - Dependências entre fases propostas (coluna «Depende de»); confirmam-se no `/spec` de cada fase.
  - A entrada «Infraestrutura local» acima pertence a F1 (fundação da solução). Confirmado pelo Diogo a 2026-11-24.
- ***Commits:*** `96feec4`.

## F1: especificação 000 (visão geral e fundação)

- **Data:** 2026-11-24
- **Feito:**
  - `/spec 000` em cinco rondas de perguntas; especificação `docs/specs/000-visao-geral.md` aprovada pelo Diogo a 2026-11-24, com AC-1 a AC-16 (AC-15 e AC-16 documentais).
  - Decisões D1–D10 registadas a partir da secção 5 do plano da tese, colada pelo Diogo.
  - Índice das especificações e `docs/RASTREABILIDADE.md` atualizados (critérios transversais fora da matriz RF/RNF).
- **Problemas:**
  - O repositório não tinha o enunciado de D1, D4, D5, D7 e D8; foi pedido ao Diogo em vez de deduzido.
  - Uma pergunta da ronda 4 afirmou, por engano, que o repositório não tinha remoto (tem `origin` no GitHub). A resposta (CI fora da F1) não dependia disso.
- **Decisões:**
  - Âmbito da 000: visão geral curta, D1–D10, esqueleto executável e ADR 0003.
  - D1–D9 diferidas com o valor da tese v16 por omissão; gatilho de revisão: confirmação de quem decide.
  - Fora da F1: exportador CSV (F12), `tools/` (nas suas fases), autenticação e HTTPS (F11), integração contínua.
  - Testes: ArchitectureTests, Api.Tests, Workers.Tests e um único UnitTests; Testcontainers com as imagens do `compose.yaml`; avisos tratados como erros.
- ***Commits:*** `c3de3e1`.

## F1: implementação da fundação (especificação 000)

- **Data:** 2026-11-24
- **Feito:**
  - Plano aprovado pelo Diogo ([`docs/plans/000-fundacao.md`](plans/000-fundacao.md)), ramo `feat/000-fundacao`.
  - `SmartParking.slnx`, `global.json` (SDK 10.0.404, `rollForward: latestPatch`, *runner* Microsoft.Testing.Platform), `Directory.Build.props` (`TreatWarningsAsErrors`, `Nullable`, `AnalysisLevel latest-recommended`, `EnforceCodeStyleInBuild`) e `Directory.Packages.props`.
  - Domain vazio; Application com `Result`, `Result<T>`, `Error` tipado, `ICommandHandler<,>`, `IQueryHandler<,>`, `IEventBus` (D2), `IUnitOfWork` (D10) e `AddApplication` por varrimento do *assembly*.
  - Infrastructure: `PersistenceOptions` validado no arranque; um método de registo por modo; ligação e *health check* por subcamada; `Hosting/` com `IEndpointGroup`, `/health/live`, `/health/ready`, `GlobalExceptionHandler`, Serilog e OpenTelemetry.
  - Api e Workers com `Program.cs` mínimo; OpenAPI e Scalar só em `Development`.
  - Testes: 39, todos a passar (`dotnet test`: ArchitectureTests, UnitTests, Api.Tests, Workers.Tests). AC-1 a AC-14 cobertos por testes com o número no nome.
  - ADR 0002 (estrutura) e ADR 0003 (D10, AC-15); rastreabilidade atualizada.
  - Verificação manual: `docker compose up -d --wait`, Api com as credenciais do `.env` em variáveis de ambiente; `GET /health/ready` devolveu 200 com `postgres`, `mongo` e `redis` em `Healthy`; `/openapi/v1.json` e `/scalar/v1` devolveram 200 em `Development`.
- **Versões dos pacotes** (resolvidas a 2026-11-24 com `dotnet package search` e o índice do NuGet): FluentValidation 12.2.0; Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.AspNetCore.OpenApi e Microsoft.AspNetCore.Mvc.Testing 10.0.15; Npgsql 10.0.4; MongoDB.Driver 3.14.1; StackExchange.Redis 3.4.35; AspNetCore.HealthChecks.NpgSql/MongoDb/Redis 9.0.0; Serilog.AspNetCore 10.0.0; OpenTelemetry 1.21.0; Scalar.AspNetCore 3.2.0; xunit.v3 4.0.3; Testcontainers 4.17.0; NetArchTest.Rules 1.3.2.
- **Problemas:**
  - A especificação 000 ainda não existia quando o pedido da F1 chegou; a implementação parou até o `/spec 000` ser aprovado.
  - A regra de permissões `Edit(results/**)` (para os CSV de `results/`) também bloqueou a pasta `Application/Abstractions/Results/`, porque o Windows não distingue maiúsculas nos caminhos. Os tipos `Result` e `Error` ficaram diretamente em `Application/Abstractions/`.
  - Analisadores com avisos como erros: CA1716 (`Error` é palavra reservada noutras linguagens; suprimido com justificação, porque o nome vem da especificação) e CA1000 (métodos estáticos em `Result<T>`; as fábricas passaram para `Result`). Nos testes, CA1716 obrigou a renomear `tests/Shared` para `tests/Common` e CA1711 a renomear as classes de coleção para `*CollectionDefinition`.
  - Conflitos de nomes em C#: `TestResult` (NetArchTest e xUnit) e propriedades `Domain`/`Application` que colidiam com os espaços de nomes `SmartParking.*`.
  - O SDK 10 recusa o VSTest com o xUnit v3 4.x («Testing with VSTest target is no longer supported»). Passou-se para o Microsoft.Testing.Platform no `global.json` e retiraram-se `Microsoft.NET.Test.Sdk` e `xunit.runner.visualstudio`, que deixaram de ser precisos. O comando passa a ser `dotnet test --solution SmartParking.slnx` (ou `dotnet test` na raiz).
  - `hooks/post-test-analyze.sh`, referido em `.claude/rules/hooks.md`, não existe no repositório; os testes correram sem ele.
  - `detect_antipatterns` (Roslyn) reportou 24 ocorrências, todas em código gerado em `obj/` (ponto de entrada do xUnit, gerador do OpenAPI) ou em métodos de teste xUnit sem parâmetro `CancellationToken` (usam `TestContext.Current.CancellationToken`). Nenhuma em código de `src/`.
  - Depois de parar a Api com `TaskStop`, o processo `SmartParking.Api` ficou órfão na porta 5080 e foi terminado à mão.
- **Decisões:**
  - Prevalece a especificação 000 sobre o pedido inicial; o ADR 0002 foi acrescentado (Diogo, 2026-11-24).
  - `Infrastructure/Hosting/` para a fundação web partilhada (Diogo, 2026-11-24; ADR 0002).
  - Pacotes Microsoft na linha 10.x (`Serilog.AspNetCore` 10.0.0 e não 11.0.0, que segue a linha do .NET 11).
  - Npgsql 10.0.4 referenciado diretamente para não herdar o 8.x mínimo exigido pelo AspNetCore.HealthChecks.NpgSql.
  - `Persistence:Mode` guardado como texto e validado com uma mensagem que nomeia a chave; comparação exata (`hybrid` e `0` são rejeitados).
  - O JSON de `/health/ready` leva só o estado e a duração de cada verificação, sem as mensagens das exceções (podem conter nomes de máquinas e credenciais).
  - `appsettings.json` sem palavras-passe; as credenciais chegam por variáveis de ambiente (`ConnectionStrings__Postgres`, `__Mongo`, `__Redis`) ou `dotnet user-secrets`.
  - EF Core fica para a F2, com o primeiro `DbContext`.
- ***Commits:*** `81878b7` (Application), `a964587` (Infrastructure), `5ed209f` (Api e Workers), `6e9043e` (documentação).
