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

## F1: verificação e revisão independente

- **Data:** 2026-11-24
- **Feito:**
  - `/verify` do *kit* (7 fases): build PASS (0 erros, 0 avisos); diagnósticos PASS; antipadrões WARN (só código gerado em `obj/` e métodos de teste xUnit); testes PASS; segurança PASS (nenhum pacote vulnerável, incluindo transitivos); format PASS; diff PASS. Veredicto: pronto para revisão.
  - Revisão independente do diff por um subagente (`code-reviewer`), sem acesso de escrita. Sem achados críticos nem violações das regras do projeto; dois achados médios e quatro de precisão dos testes, corrigidos abaixo.
  - Depois das correções: 41 testes a passar, build e format limpos.
- **Problemas (achados da revisão):**
  - O *health check* do Redis (pacote AspNetCore.HealthChecks.Redis) criava a ligação com `ConnectionMultiplexer.Connect` síncrono dentro da verificação, e o `PingAsync` não observava o *token*. O *timeout* configurado não limitava a resposta: o limite real era o `connectTimeout`/`asyncTimeout` do cliente (5 s por omissão).
  - `AddApplication()` na Api só varria Application: um validador escrito na Api nunca seria registado e o erro só apareceria no pedido (500).
  - O caso «`Persistence:Mode` ausente» do AC-9 testava, na prática, um valor vazio: o WebApplicationFactory passa `UseSetting(null)` como argumento vazio e o `appsettings.json` traz sempre um modo.
  - O AC-7 só cobria «Redis parado antes do arranque», não «Redis para com o *host* a correr».
- **Decisões:**
  - `Cache/RedisConnection` (multiplexador ligado com `ConnectAsync` no primeiro uso) e `Cache/RedisHealthCheck` (cada espera com `WaitAsync(cancellationToken)`). O pacote AspNetCore.HealthChecks.Redis saiu.
  - A Api passa a chamar `AddApplication(typeof(Program).Assembly)`.
  - AC-9 «ausente» testado num *host* sem secção `Persistence` (`Host.CreateEmptyApplicationBuilder` + `AddInfrastructure` + `StartAsync`); o caso `null` saiu das *theories*. Workers.Tests com os mesmos valores inválidos da Api (`hybrid`, `0`).
  - Teste novo do AC-7 com o Redis parado a meio: 503 com `redis` em `Unhealthy` e resposta abaixo do dobro do *timeout* configurado. Não foi confirmado que este teste falharia com a implementação anterior (o limite de 5 s do cliente também ficava abaixo do dobro de 3 s); garante só que a resposta não fica pendurada.
  - ADR 0002: o espaço de nomes raiz e `Messaging/` ficam de fora das regras de isolamento, por desenho; a regra de `Messaging/` define-se na F3/F4.
  - Não alterados: a personalização do `traceId` (redundante com o *default* do ASP.NET Core, mas explícita); a igualdade por referência do dicionário em `Error` (sem uso que dependa dela); o exportador CSV (F12, fora da 000).
- ***Commits:*** `902678c` (correções e testes), `1b7ce9a` (documentação).

## F1: integração na `main`

- **Data:** 2026-11-24
- **Feito:**
  - *Push* do ramo `feat/000-fundacao` (8 *commits*) e [PR #1](https://github.com/diogonunes2798/SmartParking.Hybrid/pull/1), com a correspondência AC → teste, o relatório do `/verify` e os achados da revisão independente na descrição.
  - *Merge* pedido pelo Diogo, com *merge commit* (`0edbb45`), para manter na `main` os *commits* atómicos da F1. Depois, o ramo foi apagado no `origin` e localmente, e confirmou-se antes que já estava integrado.
  - Plano 000 marcado como integrado na `main`.
- **Problemas:**
  - O primeiro pedido de *push* foi interrompido pelo Diogo antes de chegar ao `origin` (confirmado com `git ls-remote`); foi repetido a seguir, sem alterações.
  - O repositório não tem integração contínua (fora da F1, especificação 000), por isso o PR não teve verificações automáticas: a garantia são os resultados locais (41 testes, build e format limpos) registados na descrição.
- **Decisões:**
  - *Merge commit* em vez de *squash*: os *commits* da F1 referidos neste diário continuam a existir na `main`.
- ***Commits:*** `0edbb45` (*merge* do PR #1); registo desta entrada no commit seguinte.

## F2a: domínio, esquema e parques (especificação 001)

- **Data:** 2026-11-24 a 2026-11-25
- **Feito:**
  - Plano aprovado pelo Diogo ([`docs/plans/001a-dominio-esquema-parques.md`](plans/001a-dominio-esquema-parques.md)), ramo `feat/001-configuracao`, 15 passos.
  - Domain dos três agregados da Tabela 15 (estrutura e invariantes de construção), `AggregateRoot` com eventos de domínio e os eventos `FacilityCreated.v1`, `FacilityUpdated.v1` e `FacilityDeleted.v1`.
  - Subcamada relacional:
    - modelo EF Core com nomes em `snake_case` (convenção escrita à mão), enums como texto com `CHECK` dos valores permitidos, FK compostas que mantêm zonas e lugares no mesmo parque, índices únicos parciais e `geography(Point, 4326)` com índice GiST;
    - migração `Initial` com PostGIS, `btree_gist`, os dois `EXCLUDE` (versões de tarifário e reservas ativas), o operador de referência por `HasData` e o script versionado do grupo `smartparking_app` ([ADR 0004](adr/0004-papel-da-aplicacao-e-auditoria.md));
    - o `DbContext` é o `IUnitOfWork`: uma auditoria e uma mensagem da *outbox* por evento, na mesma transação; violações traduzidas para 409, 400 e 412.
  - Fundação: `Error.PreconditionFailed` (412) e `Error.PreconditionRequired` (428), a porta `ICurrentActor` (`anonymous`), paginação partilhada e o `TimeProvider` registado (não estava registado desde a F1).
  - Parques: comandos e consultas em Application, repositório por raiz de agregado (D10) e porta de leitura, *endpoints* `/api/facilities` com ETag forte sobre o `xmin` e `If-Match` nas alterações; `tests/http/configuration.http` com os pedidos de parques.
  - Testes: os hosts de teste ligam com o *login* da aplicação (`smartparking_api`), nunca como superutilizador. AC-1 a AC-6, AC-10, AC-12 e AC-17 a AC-22 cobertos por testes com o número no nome; 131 testes a passar no fim (52 antes da F2a).
  - Desenvolvimento local: `POSTGRES_APP_PASSWORD` no `.env.example`, `scripts/db-app-user.sh` e a Api a ligar como `smartparking_api`.
  - Verificação manual (AC-1 documental): `dotnet ef database update`, como dono, aplicou a `Initial` à base do `compose.yaml`; o `db-app-user.sh` criou o *login*. Com a Api ligada como `smartparking_api`: `/health/ready` 200; POST 201 com `Location` e ETag; GET 200; PUT sem `If-Match` 428, com o ETag 200 e ETag novo, com o ETag antigo 412; DELETE 204; na base, três auditorias `anonymous` (`Created`, `Updated`, `Deleted`), cada uma com o seu evento por publicar. A Api parou sem deixar processo órfão na porta 5080.
- **Versões dos pacotes** (resolvidas a 2026-11-24 com `dotnet package search`): Npgsql.EntityFrameworkCore.PostgreSQL e Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite 10.0.4; Microsoft.EntityFrameworkCore.Design 10.0.15 (EF Core 10.0.15 por dependência).
- **Problemas:**
  - O `dotnet add package` sem versão escolheria o EF Core 11.0.0, que segue o .NET 11. As versões foram fixadas na linha 10.x, contra a regra «sem `--version`» mas de acordo com a regra «Microsoft.* na linha 10.x».
  - A convenção `snake_case` mudava o nome da coluna `xmin` para `version`, o que criaria uma coluna verdadeira `version xid` em cinco tabelas. Detetado ao rever o DDL gerado por `dotnet ef dbcontext script`; a convenção passou a ignorar as propriedades de versão.
  - O código gerado das migrações dava CA1861 (avisos tratados como erros). Resolvido com um `.editorconfig` em `Relational/Migrations` que marca só os ficheiros gerados como `generated_code`.
  - O tipo `geography(Point, 4326)` converte em silêncio uma latitude fora do intervalo (95 passa a 90, só com um `NOTICE`). Os `CHECK` de latitude e longitude da especificação nunca falhariam e saíram da `Initial` (ver «Decisões»).
  - `WithName` no FluentValidation não muda a chave do `ValidationProblemDetails`; passou-se para `OverridePropertyName`.
  - O registo dos validadores (F1) ignorava tipos `internal`, que é o omisso do `AssemblyScanner`; passou a incluí-los, porque os validadores da Api são internos.
  - No Git Bash, os caminhos `/tmp/…` passados ao `docker exec` eram convertidos em caminhos do Windows; resolvido com `MSYS_NO_PATHCONV=1`.
  - Nenhuma abordagem falhou duas vezes; não foi preciso voltar a planear.
- **Decisões:**
  - Pacotes EF Core da stack do `CLAUDE.md` autorizados, apesar de a especificação pôr «dependências NuGet novas» fora do âmbito: o «Out» lê-se como «fora da stack» (Diogo, 2026-11-24).
  - AC-17 e AC-18 cobertos com parques na F2a; os casos de tarifários e de lugares juntam-se aos mesmos testes na F2b (Diogo, 2026-11-24).
  - Um `operatorId` inexistente dá 409 pela FK, sem porta nova (Diogo, 2026-11-24).
  - `CHECK` de latitude e longitude retirados, com a garantia no `GeoCoordinate` do Domain e no validador (Diogo, 2026-11-24; ADR 0004).
  - `IUnitOfWork.SaveChangesAsync` passa a devolver `Result`, para a Infrastructure devolver as violações sem EF Core em Application.
  - `AuditEntry` e `OutboxMessage` na Infrastructure, por serem persistência e não domínio; auditoria e *outbox* no `SaveChangesAsync` do `DbContext`, sem *interceptor* (ADR 0004).
  - O `before` e o `after` da auditoria guardam a raiz e as linhas do horário, lidas do ChangeTracker, sem o `xmin`.
  - O tradutor de FK distingue «ainda referenciado» de «referência inexistente» pelo comando que falhou, e não pelo texto da mensagem, que depende do `lc_messages` do servidor. Nunca copia o DETAIL, que traz valores das linhas.
  - Invariantes do Domain com `ArgumentException`, sem `DomainException`: são erros de programação, porque a entrada é validada na fronteira.
  - `Version` (o `xmin`) também no `Tariff`, porque o DELETE de tarifários da F2b pede `If-Match`; o `xmin` é uma coluna de sistema, por isso o esquema não muda.
  - Validadores dos parques na Api, sobre os DTO do pedido (o `ValidationFilter` da F1 valida o argumento do *endpoint* e o `CLAUDE.md` pede DTO próprios na fronteira), e não em Application como dizia o plano.
  - Uma classe estática por operação (`CreateFacility`, `UpdateFacility`…), com `Command` ou `Query` e `Handler` aninhados.
  - Sem `SetExpectedVersion` no repositório (estava no plano): o *handler* compara o `If-Match` com o `xmin` carregado, e uma corrida entre a leitura e a gravação é apanhada pelo `UPDATE … WHERE xmin` do EF Core.
  - Um PUT que não muda nada não grava auditoria nem evento e mantém o ETag.
  - Sem `ConnectionStrings:PostgresMigrations` no `appsettings.json` (estava no plano): o `dotnet ef` lê a ligação do dono da variável de ambiente `ConnectionStrings__PostgresMigrations`, e uma entrada no ficheiro nunca seria lida.
  - Os Workers continuam a ligar como dono; revê-se na F3/F4, com o leitor da *outbox*.
- ***Commits:*** `1b01d96` (plano), `4dd0d2a` (pacotes), `018653e` (fundação), `05319fb` (Domain), `9dd0343` (modelo EF), `51d4182` (migração `Initial`), `e7475de` (unidade de trabalho), `5218fc2` (testes de esquema), `455676b` (operações de parques), `a3064fa` (*endpoints*), `a7627a6` (testes de arquitetura e de modos), `dc0b4d8` (desenvolvimento local), `9aeeb27` (ADR 0004).

## F2a: verificação e revisão independente

- **Data:** 2026-11-25
- **Feito:**
  - `/verify` do *kit* (7 fases): build PASS (0 erros, 0 avisos); diagnósticos PASS (0); antipadrões WARN; testes PASS (130); segurança PASS (nenhum pacote vulnerável, nenhuma palavra-passe no `src`); format PASS; diff PASS. Os avisos de antipadrões são todos de código gerado em `obj/`, de métodos de teste xUnit sem parâmetro `CancellationToken`, ou dois falsos positivos em `src/`: o repositório de escrita mantém o *tracking* de propósito, e o `ChangeTracker.Entries()` não é uma consulta.
  - `get_project_graph`: dependências só para dentro. `get_di_registrations`: sem duplicados nem dependências cativas.
  - Teste de mutação do AC-22: sem o registo do `IFacilityReader`, o teste falha nos dois modos e nomeia a porta.
  - Revisão independente do diff por um subagente (`code-reviewer`), sem acesso de escrita: um achado importante e seis menores, todos tratados com o acordo do Diogo. Depois das correções: 131 testes a passar, build e format limpos.
- **Problemas (achados da revisão):**
  - **Importante:** um PUT que só mudava o horário (uma tabela *owned* própria) deixava a linha do parque intacta, e o EF Core não emitia o `UPDATE … WHERE xmin`. O ETag não mudava, e um ETag antigo podia sobrescrever o horário sem 412 (atualização perdida, contra o AC-12). Confirmado primeiro com um teste que falhou.
  - Depois de uma gravação falhada, as linhas de auditoria e de *outbox* acrescentadas ficavam no ChangeTracker e podiam ser gravadas mais tarde no mesmo âmbito.
  - Havia dois códigos diferentes para o mesmo 412.
  - Um `If-Match` malformado num id inexistente dá 412 e não 404.
  - No `db-app-user.sh`, a palavra-passe ia na linha de comandos (visível no `ps`) e o `ALTER ROLE` podia ir para o registo do servidor, se o `log_statement` estivesse ligado.
  - O AC-5 só via as mensagens da *outbox* através da auditoria, e o teste do nome repetido não verificava a auditoria nem a *outbox*.
- **Decisões:**
  - Um agregado com eventos pendentes passa a `Modified`, mesmo que só tenham mudado as linhas *owned*; teste novo `Put_OnlyOpeningHoursChanged_Returns200WithNewETagAndOldETagIsStale_AC12`.
  - `ChangeTracker.Clear()` depois de uma gravação falhada, documentado no `IUnitOfWork`.
  - Um só código para o 412: `Concurrency.StaleVersion`.
  - O 412 do `If-Match` malformado fica como está (o RFC 9110 permite-o) e documentado no `FacilityEndpoints`.
  - A palavra-passe do `db-app-user.sh` passa pelo stdin do `psql` (`\set`, com plicas e barras escapadas; verificado com uma palavra-passe com plicas, barras e `;`) e o `log_statement` fica desligado nessa sessão.
  - O AC-5 conta também as mensagens da *outbox* desse parque, e o teste do nome repetido confirma que não há auditoria nem *outbox* nova.
- ***Commits:*** `d7ac285` (correções da revisão); registo desta entrada no commit seguinte.

## F2b: zonas, lugares, sensores e tarifários (especificação 001)

- **Data:** 2026-11-25
- **Feito:**
  - Plano aprovado pelo Diogo, tal como estava, com os 8 pontos de interpretação da especificação ([`docs/plans/001b-zonas-lugares-sensores-tarifarios.md`](plans/001b-zonas-lugares-sensores-tarifarios.md)), no mesmo ramo da F2a (`feat/001-configuracao`).
  - Domain: comportamento e eventos de `Zone`, `ParkingSpot`, `Sensor` e `Tariff` (13 eventos `.v1`, todos com `id` e `facilityId`); regras das versões de tarifário (criar fecha a última; só se apaga a última versão futura, e isso reabre a anterior); `Timestamps.Normalize` (UTC, truncado ao microssegundo).
  - Quatro repositórios por raiz de agregado (`IZoneRepository`, `IParkingSpotRepository`, `ISensorRepository`, `ITariffRepository`), `ExistsAsync` no repositório dos parques e quatro portas de leitura (D10, ADR 0003), registados da mesma forma nos dois modos.
  - *Endpoints* de zonas, lugares (individualmente e em lote, de 1 a 1 000, tudo ou nada), sensores (com `/{id}/spot`) e versões de tarifário (com `/current`), com ETag e `If-Match`. Sem migrações nem pacotes novos.
  - [ADR 0005](adr/0005-versoes-de-tarifario-com-vigencia.md) (versões de tarifário com vigência); `tests/http/configuration.http` com um pedido por *endpoint*.
  - Testes: AC-7, AC-8, AC-9, AC-11, AC-13, AC-14, AC-15, AC-16, os casos de tarifário do AC-17, os de lugares do AC-18 e o AC-23 (este também com um teste que compara as rotas `/api` com os pedidos do `.http`, nos dois sentidos). No fim: 201 testes a passar (131 antes da F2b).
  - Verificação manual contra o `compose.yaml`, com a Api ligada como `smartparking_api`: os 34 pedidos do percurso do `.http` (incluindo 409, 412, 428 e 404 provocados) deram o código esperado. Na base ficaram 19 pares auditoria/*outbox* (ator `anonymous`, por publicar) para as 19 alterações bem-sucedidas, sem mensagens órfãs; o lote de 2 lugares com 1 sensor deixou 3 pares.
- **Problemas:**
  - Na primeira compilação, uma expressão condicional entre `Error` e `List<T>` não convertia para `Result<IReadOnlyList<T>>`: as conversões definidas pelo utilizador não se aplicam a tipos interface. Resolvido com `Result.Success<IReadOnlyList<…>>(…)`.
  - O Npgsql só grava `timestamptz` com *offset* zero, e o `timestamptz` guarda microssegundos. Um `validFrom` com mais precisão seria gravado diferente do `validTo` da versão que fecha. Primeiro truncou-se no Domain (`188a898`); a revisão mostrou que o *handler* ainda comparava o valor por truncar (achado 2).
  - Na verificação manual, o *script* de arranque não passava a palavra-passe do Redis, e o `/health/ready` ficou à espera (o Redis exige autenticação). Os *endpoints* de configuração só usam o PostgreSQL, por isso a verificação seguiu sem o Redis; fica a nota para o *script* da F3/F5.
  - O `TaskStop` voltou a deixar o processo `SmartParking.Api` órfão na porta 5080, como na F1 (o processo filho do `dotnet run` sobrevive). Foi identificado pelo nome e terminado.
  - O `hooks/post-test-analyze.sh` referido em `.claude/rules/hooks.md` não existe no repositório; os testes correram sem ele.
  - Nenhuma abordagem falhou duas vezes; não foi preciso voltar a planear.
- **Decisões:**
  - Os 8 pontos do plano 001b (Diogo, 2026-11-25): o AC-7 prevalece sobre «um par por entidade alterada» (o fecho e a reabertura da versão vizinha vão no evento da operação); a corrida na criação de versões dá 409; zona ou lugar de outro parque (ou inexistente) dão 400, e um parque inexistente dá 404; `hardwareId` repetido no lote dá 400; o `validFrom` no passado é verificado no *handler*; as operações sem efeito dão 200 sem auditoria; o lote responde 201 sem `Location`; as versões em vigor dos testes são inseridas por SQL.
  - Desvio ao plano: os testes ficaram agrupados por recurso (`ZoneTests`, `SpotTests`, `SpotBatchTests`, `SensorTests`, `TariffTests`, `TariffAtomicityTests`) e não por operação; os nomes dos métodos seguem o mapa AC → teste do plano.
  - Os validadores ficaram na Api, sobre os DTO, como nos parques. O sensor ambiental com lugar é recusado no validador e também no *handler*.
  - `EnumRules` (Api/Common): os enums aceitam só o nome exato, como o `status` dos parques; o `Enum.Parse` dos *endpoints* só corre depois disso.
  - A listagem de lugares usa duas validações encadeadas no mesmo *endpoint* (`PageRequest` e `SpotFilterRequest`), em vez de um DTO único que repetisse as regras de paginação.
- ***Commits:*** `fb1901c` (plano), `9c959f5` (início), `5b1b830` (Domain), `43a4ad8` (repositórios e leitores), `8943da2` (zonas), `5fdc459` (lugares e lote), `330eed2` (sensores), `b66bd11` (tarifários), `188a898` (truncatura do `validFrom`), `c9efd71` (`.http` e AC-23), `28ef7d0` (ADR 0005), `9a593c6` (correções da revisão).

## F2b: verificação e revisão independente

- **Data:** 2026-11-25
- **Feito:**
  - Build com `-warnaserror` (0 avisos), `dotnet format` sem alterações e `dotnet test`: 201 testes a passar.
  - `get_diagnostics`: 0. `get_project_graph`: dependências só para dentro. `get_di_registrations`: as 8 portas novas registadas, sem duplicados nem dependências cativas. `detect_antipatterns`: só avisos de código gerado em `obj/` e falsos positivos AP010 (o *tracking* dos repositórios de escrita é intencional; o `AnyAsync` não faz *tracking*).
  - Testes de mutação: sem o fecho da versão anterior no Domain, o AC-7 falha (e o AC-9 e a corrida também, pelo `EXCLUDE`); sem um pedido no `.http`, o teste do AC-23 falha e nomeia a rota. Os ficheiros foram repostos a partir de cópias.
  - Revisão independente do diff (subagente `code-reviewer`, só leitura): nenhum defeito na atomicidade da auditoria e da *outbox*, no `xmin`, nos *payloads* dos eventos nem nas operações sem efeito; 6 achados, tratados com o acordo do Diogo.
- **Problemas (achados da revisão):**
  1. O validador do lote rebentava (500) com `spots` em falta, nulo ou com um item nulo, porque as regras continuavam depois da primeira falha.
  2. O *handler* dos tarifários comparava o `validFrom` por truncar e o Domain o truncado: um início menos de 1 µs depois da última versão (ou igual, sem versão anterior) chegava à guarda do Domain (500).
  3. O `InstalledAt` dos sensores não era truncado ao microssegundo: o evento e a auditoria diferiam da linha gravada.
  4. Um `If-Match` malformado num recurso inexistente dá 412 e não 404 (herdado da F2a).
  5. Quando um lugar muda de zona, o sensor desse lugar mantém a zona antiga; essa zona não se apaga enquanto o sensor a referir.
  6. O teste de concorrência dos tarifários podia passar sem chegar a haver corrida.
- **Decisões (Diogo, 2026-11-25):**
  - Corrigidos 1, 2, 3 e 6, cada um com teste novo. Os 5 testes novos falham no código anterior e passam com a correção (verificado). `Timestamps.Normalize` no Domain é usado pelo `Tariff`, pelo `Sensor` e pelo *handler*. O teste de corrida usa o mesmo início em 4 pedidos e exige exatamente um 201, com e sem versão anterior.
  - O achado 4 fica como na F2a (o RFC 9110 permite o 412).
  - O achado 5 fica documentado, sem mudar o comportamento: a zona do sensor é configuração própria (`PUT /sensors/{id}`) e não segue o lugar. Revê-se na F5, quando os contadores por zona precisarem dela.
- ***Commits:*** `9a593c6` (correções da revisão); registo desta entrada no commit seguinte.

## F2: integração na `main`

- **Data:** 2026-11-25
- **Feito:**
  - *Push* do ramo `feat/001-configuracao` (29 *commits*, F2a e F2b) e [PR #2](https://github.com/diogonunes2798/SmartParking.Hybrid/pull/2), pronto para revisão, com o resumo das duas partes, a localização do mapa AC → teste, as decisões que um revisor deve conhecer e os resultados da verificação na descrição.
  - *Merge* pedido pelo Diogo, com *merge commit* (`f123c86`), como na F1. O GitHub indicava o PR como integrável e sem conflitos.
  - Planos 001a e 001b e especificação 001 marcados como integrados na `main`.
  - Este registo foi feito numa *worktree* temporária sobre `origin/main`, para não mudar o ramo da cópia de trabalho principal.
- **Problemas:**
  - O repositório continua sem integração contínua, por isso o PR não teve verificações automáticas: a garantia são os resultados locais (201 testes, build e format limpos) registados na descrição.
- **Decisões:**
  - *Merge commit* em vez de *squash*: os *commits* da F2a e da F2b referidos neste diário continuam a existir na `main`.
  - O ramo `feat/001-configuracao` foi apagado no `origin` e localmente, a pedido do Diogo, depois de se confirmar que estava integrado na `main` (`git merge-base --is-ancestor`), como na F1. A cópia de trabalho principal passou para a `main`.
- ***Commits:*** `f123c86` (*merge* do PR #2); registo desta entrada no commit seguinte.

## F3: especificação 002 (sessões, reservas e pagamentos)

- **Data:** 2026-11-25
- **Feito:**
  - Especificação [002-sessoes-pagamentos](specs/002-sessoes-pagamentos.md) escrita com o `/spec` do *kit*, em 10 rondas de perguntas, e aprovada pelo Diogo a 2026-11-25. Tem 30 AC: AC-1 a AC-21 na F3a (utilizadores, sessões, cálculo do valor, pagamento, falha a meio e concorrência) e AC-22 a AC-30 na F3b (reservas e *worker* de expiração).
  - Índice das especificações e matriz de rastreabilidade (RF-02) atualizados.
- **Problemas:**
  - O pedido da F3 dava a especificação 002 como aprovada, mas o ficheiro não existia: o índice marcava a F3 como «Por especificar». A implementação parou antes do plano, e a especificação foi feita primeiro.
  - Duas contradições resolvidas durante as perguntas:
    - a chave de idempotência do gateway só com o `sessionId` impediria tentar de novo com outro meio depois de uma recusa; passou a ser (`sessionId`, `paymentMethodId`);
    - duas auditorias para um só evento (ronda 8) contrariam o mecanismo da F2, em que cada evento gera uma auditoria, e o ADR 0005; o Diogo manteve as duas auditorias, e a versão de tarifário fechada da F2 fica como exceção registada.
- **Decisões:**
  - Regra de cálculo do valor:
    - minutos grátis como limiar;
    - incremento arredondado sempre para cima;
    - máximo diário por períodos de 24 h a contar da entrada;
    - 2 casas decimais com arredondamento do meio para cima, aplicado uma só vez no fim.
  - A sessão usa a versão de tarifário em vigor na abertura (ADR 0005).
  - Pagamento:
    - o gateway é chamado fora da transação, e depois uma só transação grava tudo;
    - uma recusa grava um `Payment` `Failed` e responde 402 (`Error.PaymentDeclined`, extensão da fundação).
  - Comandos de transição sem `If-Match`: um conflito de `xmin` dá 409.
  - Exatamente um evento por transição; uma auditoria por entidade alterada.
  - Uma única alteração ao esquema, aditiva: o índice único parcial das sessões `Open` por veículo.
  - Os Workers passam a ligar como papel da aplicação, o que fecha o ponto em aberto da F2.
  - Implementação dividida em F3a e F3b.
- ***Commits:*** `60238a0` (especificação aprovada).

## F3a: utilizadores, sessões e pagamento (especificação 002)

- **Data:** 2026-11-25
- **Feito:**
  - Plano aprovado pelo Diogo ([002a](plans/002a-utilizadores-sessoes-pagamento.md)), com as decisões 1 a 4 (2026-11-25) e as propostas 5 a 12. Ramo `feat/002-sessoes-pagamentos`.
  - Auditoria por entidade alterada na unidade de trabalho ([ADR 0006](adr/0006-auditoria-por-entidade-alterada.md)): `AggregateRoot.Raise(evento, entidades alteradas)`, uma `OutboxMessage` por evento e uma `AuditEntry` por entidade, com o mesmo `outboxMessageId`. Os 201 testes da F2 passaram sem alterações depois da mudança. O `providerToken` fica fora da auditoria (`AuditExclusions`).
  - Migração aditiva `OpenSessionPerVehicle` (índice único parcial `ux_parking_sessions_vehicle_open`), aplicada também à base do `compose.yaml`.
  - Domain: `User` com veículos e meios de pagamento (cinco eventos); `ParkingSession` com `Open`, `Close` e `RecordPayment` (quatro eventos), sem nenhum método que saia de `Paid`; `SessionPricing`; `ParkingFacility.IsOpenAt`.
  - Application e Infrastructure: `IUserRepository`, `IParkingSessionRepository`, `IUserReader`, `ISessionReader`, `GetInForceAsync` no `ITariffRepository`; duas consultas compiladas (sessão aberta de um veículo, tarifário em vigor); `IPaymentGateway` e `FakePaymentGateway` em `Infrastructure/Payments`; `Error.PaymentDeclined` (402, com `paymentId`).
  - Api: `UserEndpoints` e `SessionEndpoints`; `tests/http/sessions.http`.
  - Testes: AC-1 a AC-21 da F3a; 303 testes a passar (201 antes da F3a), em duas corridas seguidas; 304 depois das correções da revisão. Testes de mutação: sem o pagamento nas entidades do `SessionPaid`, o AC-12 falha; sem a conversão 412 → 409, o AC-11 falha; sem o `Lazy` no gateway, o AC-16 e o teste unitário do gateway falham.
  - Verificação manual contra o `compose.yaml`, com a Api ligada como `smartparking_api`: o percurso do `sessions.http` deu os 27 códigos esperados (incluindo 409 antes de o tarifário entrar em vigor, 402 com `paymentId` e 409 ao apagar um meio com pagamento). Na base, a sessão deixou quatro mensagens por publicar (`SessionOpened`, `SessionClosed`, `PaymentFailed`, `SessionPaid`), com ator `anonymous`, e duas auditorias no `SessionPaid`; o `providerToken` não aparece na auditoria nem na *outbox*; nenhuma mensagem órfã.
  - Roslyn: dependências só para dentro (`get_project_graph`); portas e gateway registados sem duplicados nem dependências cativas (`get_di_registrations`); 0 diagnósticos. O `detect_antipatterns` só assinalou ficheiros gerados, métodos de teste e repositórios de escrita com *tracking* intencional, como na F2.
- **Problemas:**
  - O teste do AC-23 da F2 exigia que o `configuration.http` cobrisse todas as rotas `/api`. Os *endpoints* novos fá-lo-iam falhar; passou a verificar cada ficheiro `.http` contra os seus prefixos (proposta 12).
  - Com o relógio de teste parado, duas transições seguidas ficam com o mesmo `occurred_at`, e a ordem das auditorias e mensagens por `occurred_at, id` deixa de ser fiável (UUID v7 do mesmo milissegundo). Falharam três testes, um deles intermitente (o AC-13 passou por acaso na primeira corrida). Os testes passaram a avançar o relógio entre transições.
  - Um *heredoc* falhou no Git Bash (já registado na memória do projeto); os ficheiros passaram a ser escritos diretamente.
  - O `hooks/post-test-analyze.sh` referido em `.claude/rules/hooks.md` não existe no repositório; os resultados foram lidos diretamente da saída do `dotnet test`.
  - O `TaskStop` voltou a deixar o processo `SmartParking.Api` órfão na porta 5080, como na F1 e na F2; foi identificado pelo nome e terminado.
  - O `dotnet ef migrations add` exige a variável `ConnectionStrings__PostgresMigrations` mesmo sem ligar à base; foi passada uma cadeia sem palavra-passe.
- **Decisões:**
  - Decisões 1 a 4 do plano 002a, tomadas com o Diogo a 2026-11-25: `FakePaymentGateway` em `Infrastructure/Payments`; `reservationId` dá 409 até à F3b; blocos arredondados por período de 24 h, com ou sem máximo diário; horário `[opensAt, closesAt)`.
  - Propostas 5 a 12 do plano, aprovadas com ele, implementadas sem desvios.
  - O índice da FK `ix_parking_sessions_vehicle_id` foi declarado de forma explícita, para a migração ficar aditiva; o `Up` gerado só tem o `CreateIndex`.
  - Desvios ao plano: a sessão e o utilizador tiveram *commits* de Api juntos com as *fixtures* (o `SessionApi` usa os DTO das sessões); o teste do 402 ficou em `ErrorHandlingTests` (integração) além do `ErrorTests` (unitário).
- **Revisão independente** (`code-reviewer`, só leitura, sobre `git diff main...HEAD -- src tests`): sem erros contra a especificação 002 nem violações das regras do projeto. O Diogo pediu a correção do achado 1 e dos *nits* 3 e 4 (2026-11-25); os restantes ficam registados. Achados:
  1. **Recusa concorrente com um pagamento aprovado (confirmado).** Na recusa, a sessão não está entre as entidades alteradas e não é «tocada» (ADR 0006), por isso o seu `xmin` não é verificado. Se um pagamento aprovado com o meio X e uma recusa com o meio Y correrem em simultâneo, a recusa grava um `Payment` `Failed` e um `PaymentFailed.v1` numa sessão já `Paid` e responde 402. A secção «Edge Cases» da especificação pede 200 e 409 para dois pagamentos concorrentes. **Corrigido:** a raiz que levanta um evento passa a ter sempre o `xmin` verificado (como na F2), mesmo quando só outras entidades mudaram, sem ganhar auditoria por isso; o ADR 0006 foi atualizado e regista a primeira versão como alternativa abandonada. Novo teste `Pay_ApprovedAndDeclinedConcurrently_OneReturns409AndNoDeclineOnPaidSession` (barreira): quem grava primeiro ganha (200 ou 402) e o outro dá 409; com a regra antiga, falha nas duas corridas de mutação. Custo: um `UPDATE` sem alterações na linha da raiz (por exemplo, `users` ao registar um veículo). Desvio ao plano 002a (propostas 5 e 8 diziam que a linha da raiz não era tocada).
  2. **Dois pagamentos concorrentes com meios diferentes cobram duas vezes**, e a cobrança perdedora fica sem `Payment`. A especificação aceita-o («outro meio é uma tentativa nova»; reembolsos fora do âmbito), mas é um limite de cobrar fora da transação, a registar para as secções 5.9 e 7.8.
  3. *Nit* de teste: a barreira de concorrência podia ficar armada depois de um teste falhar e contaminar o seguinte. **Corrigido:** `Disarm` num `finally` (`RaceAsync`) e `Arm` recusa armar sobre uma barreira pendente.
  4. *Nit* de teste: o AC-14 aceitava qualquer 500. **Corrigido:** o *trigger* conta as suas execuções numa sequência (`nextval` não é desfeito pelo *rollback*), e o teste confirma que correu.
  5. *Nit*: `FindSystemTimeZoneById` em cada abertura (o .NET guarda-o em cache).
  6. *Nit*: com `[opensAt, closesAt)` e `opensAt < closesAt`, um parque aberto 24 h não se representa (o último segundo do dia fica fechado); é coerente com a especificação 001. Ponto a considerar numa revisão futura da especificação.
- ***Commits:*** `f8f8d21`, `b39a6b3`, `ef4b139`, `ca0e141`, `7a4d524`, `6706c4b`, `a5c9a32`.

## F3b: reservas e *worker* de expiração (especificação 002)

- **Data:** 2026-11-25
- **Feito:**
  - Plano aprovado pelo Diogo ([002b](plans/002b-reservas-worker.md)), com os pontos 1 a 3 e as propostas 4 a 15. Mesmo ramo da F3a (`feat/002-sessoes-pagamentos`).
  - Ciclo da reserva no Domain (`Reserve`, `Cancel`, `Fulfil`, `Expire`) e `ReservationPolicy`, com a janela de cumprimento fechada nos dois extremos e a expiração estritamente depois dela; `FitsInOneOpeningInterval` sobre o `IsOpenAt`, com `endsAt − 1 µs`.
  - Cumprimento na transação da abertura (um `SessionOpened.v1`, duas auditorias com o mesmo `outboxMessageId` e o mesmo `xmin`) e proteção do lugar numa consulta compilada; sai o 409 temporário `Session.ReservationNotSupported` da F3a ([ADR 0007](adr/0007-reservas-cumprimento-e-expiracao.md)).
  - *Endpoints* `/api/reservations` (criar, consultar, cancelar), com o `23P01` traduzido para `Reservation.Overlaps`; configuração `Reservations:*` validada no arranque nos dois modos.
  - *Worker* de expiração, o primeiro *background service* do projeto: uma passagem ao arrancar e depois a cada `ExpiryScanSeconds`, uma transação por reserva, actor `system`, conflito de `xmin` contado e ignorado. Os Workers passam a ligar como `smartparking_workers`, membro de `smartparking_app`, o que fecha o ponto em aberto da F2; o `scripts/db-app-user.sh` cria os dois logins.
  - Testes: AC-22 a AC-30 e as quatro transições de reserva do AC-21 (criar, cancelar e abrir com reserva na Api; expirar nos Workers); 385 testes a passar em duas corridas seguidas depois das correções da revisão (304 antes da F3b).
  - Verificação manual contra o `compose.yaml`, com a Api como `smartparking_api` e os Workers como `smartparking_workers` (tolerância 0 e uma passagem a cada 10 s, só nesta corrida): os 18 pedidos do percurso deram os códigos esperados (incluindo 201 a abrir com reserva, 409 a cancelar de novo, 400 no passado e 409 na sobreposição). Na base: a reserva expirada tem auditoria com actor `system` e um `ReservationExpired.v1` por publicar; o cumprimento deixou duas auditorias no `SessionOpened.v1`; nenhuma mensagem órfã; o `pg_stat_activity` mostra `smartparking_api` e `smartparking_workers` (além da sessão do `psql`).
  - Roslyn: dependências só para dentro; `ICurrentActor` = `SystemCurrentActor` nos Workers e `AnonymousCurrentActor` na Api; portas novas iguais nos dois modos, sem duplicados nem dependências cativas; 0 diagnósticos. O `detect_antipatterns` só assinalou métodos de teste e o repositório de escrita com *tracking* intencional.
  - Testes de mutação (com cópia de segurança do ficheiro não versionado antes de começar):
    - sem a reserva nas entidades alteradas do `SessionOpened`, o AC-25 falha (uma só auditoria da reserva);
    - com a janela aberta no fim (`<` em vez de `≤`), o caso «no extremo exato da tolerância» falha;
    - parar a passagem no primeiro conflito (`break` em vez de `continue`) faz falhar o caso de conflito do AC-28;
    - sem a conversão 412 → 409 no `CancelReservation`, o teste novo do cancelamento que perde falha (ver «Problemas»);
    - sem o `catch` da `DbUpdateException` no *worker* (achado 1 da revisão), o *worker* para depois da primeira gravação falhada e o teste `Worker_SaveFails_HostKeepsRunningAndExpiresOnALaterPass` falha.
  - Documentação: ADR 0007 e índice; RASTREABILIDADE (RF-02 com o AC-1 a AC-30, RNF-06 com o AC-28 e o AC-29, e a linha do RF-02 na Tabela 13 reposta); estado do plano 002b e dos índices; `CLAUDE.md` (os Workers com a expiração de reservas e os comandos com os dois logins da aplicação); `.env.example` com `POSTGRES_WORKERS_PASSWORD`.
- **Problemas:**
  - O teste do *host* com a base inacessível falhou à primeira: o EF Core embrulha a falha de ligação numa `InvalidOperationException` («transient failure») cuja causa é a `NpgsqlException`, e o *worker* só apanhava `DbException`, por isso parava. Passou a apanhar também esse caso, com um filtro sobre a exceção interna (sem `catch (Exception)`).
  - Duas mutações do plano sobreviveram à primeira:
    - «uma só unidade de trabalho para toda a passagem» não faz falhar o AC-28, porque a unidade de trabalho já limpa o *change tracker* depois de uma gravação falhada (F2). A premissa do plano estava errada; a mutação que de facto viola o AC-28 é parar a passagem no conflito, e essa é apanhada. As transações por *scope* ficam como defesa;
    - «sem a conversão 412 → 409 no cancelamento» passou quatro vezes: na corrida do AC-27 o `UPDATE` do cancelamento chega sempre primeiro ao bloqueio da linha, por isso o ramo «o cancelamento perde» nunca corria. Acrescentou-se um teste determinístico (o gancho pós-leitura altera a linha entre a leitura e a gravação do cancelamento), que apanha a mutação.
  - Um *heredoc* voltou a ficar pendurado no Git Bash (lição já registada); foi parado e os ficheiros foram escritos diretamente.
  - Desde o fecho da F3a (`7539a19`), a linha do RF-02 na Tabela 13 da RASTREABILIDADE tinha as colunas da matriz (AC, especificação, código, testes) em vez do texto do requisito. Foi reposta com a redação da tese dada pelo Diogo («Ciclo de vida das sessões», UC-02, prioridade Alta); a rastreabilidade fica na linha da matriz.
  - Desta vez o `TaskStop` não deixou processos órfãos da Api nem dos Workers (confirmado pelo nome).
  - Para a verificação manual, o `.env` local (não versionado) ganhou `POSTGRES_WORKERS_PASSWORD`; sem ela, o `scripts/db-app-user.sh` recusa correr. Quem já tinha o ambiente da F3a tem de fazer o mesmo e voltar a correr o script.
  - A sessão foi interrompida pelo limite de utilização no passo 14: a revisão independente, já lançada, foi parada antes de acabar e voltou a ser lançada do início na sessão seguinte, sobre o mesmo `HEAD`; os testes de mutação e a documentação foram feitos depois da retoma.
- **Decisões:**
  - Pontos 1 a 3 e propostas 4 a 15 do plano 002b, aprovados com ele; ADR 0007.
  - Desvios ao plano, com a razão:
    - o conflito de `xmin` do AC-28 é provocado por um decorador do `IReservationRepository` (`AfterReadReservationRepository`, que corre uma ação logo depois de ler a reserva escolhida) e não por um decorador da unidade de trabalho, que não sabe que reserva está a gravar;
    - `Reservation.Cancel()` sem `now`, que não era usado; `Reservation.CanBeFulfilledBy` público, para o *handler* e o Domain verificarem a mesma regra;
    - o `Fulfil` é testado através do `ParkingSession.Open`, porque o Domain não expõe os internos aos testes;
    - os pedidos de reserva do `sessions.http` e o prefixo `/api/reservations` do `HttpFileTests` entraram no passo 6 e não no 13, porque o AC-20 falhava sem eles;
    - o *host* `Hooked` da Api, com o mesmo gancho, para o teste do cancelamento que perde.
- **Revisão independente** (`code-reviewer`, só leitura, sobre `git diff 9f41f33...HEAD`): sem erros contra a especificação nem contra o plano nos pontos verificados. O Diogo pediu a correção dos achados 1 e 5 (2026-11-25); os restantes ficam registados. Achados:
  1. Uma falha da base de dados durante a gravação (por exemplo, o PostgreSQL a reiniciar) chega como `DbUpdateException` com a `NpgsqlException` dentro, que o *worker* não apanha: o *host* dos Workers pararia, ao contrário do que o comentário promete. O teste só cobria a falha de ligação na listagem. **Corrigido:** o *worker* apanha também a `DbUpdateException` cuja causa é uma `DbException`. Novo teste `Worker_SaveFails_HostKeepsRunningAndExpiresOnALaterPass`: um *trigger* faz falhar a gravação da expiração e conta as execuções numa sequência; o *worker* sobrevive a duas passagens falhadas e, sem o *trigger*, a reserva expira. Sem a correção, o *worker* para depois da primeira falha e o teste falha (confirmado por mutação).
  2. `FitsInOneOpeningInterval` só verifica os extremos: na mudança para a hora de inverno a hora 01:00–02:00 repete-se em Lisboa, e um parque com `opensAt` ou `closesAt` dentro dessa hora pode aceitar uma reserva que atravessa um período fechado. Impacto baixo.
  3. A proteção do lugar é «verificar e depois agir», sem bloqueio: uma abertura sem reserva e uma criação de reserva concorrentes podem gravar as duas; e criar uma reserva não verifica se o lugar tem uma sessão aberta. A especificação não o pede; fica como limite conhecido ou bloqueio da linha do lugar.
  4. O mesmo veículo a abrir sem `reservationId` dentro da sua janela (ponto 2 do plano) deixa a reserva `Active`, que depois expira: conta como não comparência nos relatórios do UC-04.
  5. Testes: o caso negativo do AC-25 só verificava o 409, não o código do erro (**corrigido:** cada caso verifica `Session.OutsideFulfilmentWindow` ou `Session.ReservationMismatch`); os *hosts* criados por `CreateFactory` nos testes dos Workers correm o *worker* real com o relógio do sistema (inofensivo enquanto os testes correm em série e verificam por id).
  6. *Nits*: o validador compara os instantes antes da normalização ao microssegundo; o `ListDueIdsAsync` não tem limite de lote; a mensagem de `Reservation.NotActive`.
- ***Commits:*** `4f1b61b`, `3bf1248`, `fd1bf5f`, `9b84572`, `27040a3`, `6d2d73c`, `25b5489`, `41cf95d`, `b9fcac2`, `ed7debd`, `e09a33f`, `97eaf2d`, `eeb5847`, `a850710`, `d8d35cd`, `ea71e7c`, `7c306f8`, `0bce65c`, `209c7b7` e o desta atualização do diário. PR [#3](https://github.com/diogonunes2798/SmartParking.Hybrid/pull/3) (F3a e F3b para a `main`), aberto com o OK do Diogo a 2026-11-25; por integrar.

## F3: integração na `main`

- **Data:** 2026-11-25
- **Feito:**
  - *Push* do ramo `feat/002-sessoes-pagamentos` (32 *commits*, F3a e F3b) e [PR #3](https://github.com/diogonunes2798/SmartParking.Hybrid/pull/3), aberto com o OK do Diogo, com o resumo das duas partes, os resultados da verificação e os limites conhecidos na descrição.
  - *Merge* pedido pelo Diogo, com *merge commit* (`b1d7e6e`), como na F1 e na F2. O GitHub indicava o PR como integrável e sem conflitos, e o `HEAD` do PR era o mesmo da cópia local (`4e6b015`, verificado com `--match-head-commit`).
  - Planos 002a e 002b e especificação 002 marcados como integrados na `main`.
  - Este registo foi feito numa *worktree* temporária sobre `origin/main`, para não mudar o ramo da cópia de trabalho principal.
- **Problemas:**
  - O repositório continua sem integração contínua, por isso o PR não teve verificações automáticas: a garantia são os resultados locais (385 testes em duas corridas seguidas, build e format limpos) registados na descrição.
- **Decisões:**
  - *Merge commit* em vez de *squash*: os *commits* da F3a e da F3b referidos neste diário continuam a existir na `main`.
  - O ramo `feat/002-sessoes-pagamentos` foi apagado no `origin` e localmente, a pedido do Diogo, depois de se confirmar que estava integrado na `main` (`git merge-base --is-ancestor`), como na F1 e na F2. A cópia de trabalho principal passou para a `main`.
- ***Commits:*** `b1d7e6e` (*merge* do PR #3); registo desta entrada no commit seguinte.

## F4a: ingestão de eventos de ocupação (especificação 003)

- **Data:** 2026-11-25 a 2026-11-26
- **Feito:**
  - Plano aprovado pelo Diogo na sessão (UC-01, RF-01; prepara o UC-05; RNF-03), no ramo `feat/003-ingestao`. Cobre só a F4a (AC-1 a AC-18); o simulador (F4b, AC-19 a AC-24) fica para outra sessão, como a especificação prevê.
  - Domain: `OccupancyEvent`, um facto imutável que não é raiz de agregado, e `OccupancyEventType`.
  - Application:
    - porta só por acréscimo `IOccupancyEventLog` (`AppendAsync`);
    - porta de leitura `ISensorRegistryReader`;
    - comando `IngestOccupancyBatch`, que retira os repetidos dentro do lote, faz uma consulta ao registo por lote, aplica as regras 1 a 8 pela ordem da especificação e faz uma escrita por lote;
    - `ErrorType.Unavailable` (503);
    - `IngestionMetrics`, sobre o `Meter` `SmartParking.Ingestion`.
  - Infrastructure:
    - `SensorRegistryReader`: consulta compilada com `AsNoTracking` e `= ANY`;
    - `MongoOccupancyEventLog`: `insertMany` sem ordem; o E11000 conta como duplicado e as outras falhas dão 503;
    - coleção `occupancy_events` com `_id = eventId` e *write concern* `majority` com *journal*;
    - `BsonPayload`, conversão estrutural do `payload`;
    - opções `Ingestion:*` e `Mongo:Database`;
    - no modo monolítico, um *log* provisório que nunca é chamado ([ADR 0008](adr/0008-registo-de-eventos-de-ocupacao.md)).
  - Api: `POST /api/ingest/occupancy-events`:
    - o filtro de modo vem primeiro e dá 501 no monolítico;
    - FluentValidation com chaves `events[i].campo`;
    - 200 com `{ accepted, duplicates, rejected, issues }`;
    - 503 com `Retry-After`;
    - uma linha de *log* `Information` por lote e os rejeitados em `Debug`.
  - Testes de AC-1 a AC-18:
    - integração com PostgreSQL e MongoDB reais (Testcontainers);
    - o AC-12 usa um MongoDB próprio, parado e arrancado de novo na mesma porta;
    - o AC-15 é uma regra de arquitetura que lê o IL da subcamada `Documents/` com Mono.Cecil (dependência do NetArchTest; sem pacote novo);
    - o AC-17 usa um `MeterListener` da BCL;
    - o AC-18 tem um runner de ficheiros `.http` que envia os pedidos à Api de teste e compara o estado com `# @expect` (ficheiro novo `tests/http/ingest.http`).
  - Contagem de testes:
    - antes da revisão: 422 a passar em duas corridas seguidas (385 antes da F4a);
    - depois das correções da revisão: 431 a passar em duas corridas seguidas.
  - Testes de mutação, todos detetados:
    - uma chamada `DeleteOneAsync` em `Documents/` faz falhar o AC-15;
    - sem a remoção dos repetidos no lote, o AC-5 falha;
    - um `@expect` errado no `ingest.http` faz falhar o AC-18;
    - desfazer as três correções da revisão faz falhar oito testes (ver «Revisão independente»).
  - Verificação manual contra o `compose.yaml`, com a Api como `smartparking_api`:
    - o primeiro envio deu `accepted: 1`;
    - o mesmo lote outra vez deu `duplicates: 1`, e ficou um só documento com esse `_id`;
    - um evento sem `timestamp` deu 400;
    - no `mongosh`, o documento tem UUID padrão nos identificadores, as datas em UTC e o `payload` com o campo novo `temperature`; o único índice é `_id_`.
  - Roslyn: 0 diagnósticos. O `detect_antipatterns` só assinalou os métodos de teste (sem `CancellationToken`, que vem do `TestContext`).
  - Documentação: ADR 0008 e o índice; RASTREABILIDADE (linha do RF-01 na matriz).
- **Problemas:**
  - A primeira execução manual da Api deu 500 em `POST /api/facilities`: o `appsettings.json` não tem palavras-passe e a Api arrancou sem as variáveis `ConnectionStrings__*`. Repetiu-se com as cadeias de ligação montadas a partir do `.env` local, só no processo e sem as mostrar.
  - Um commit falhou porque as aspas duplas dentro da mensagem quebram a passagem de argumentos do PowerShell 5.1 ao `git`. A mensagem foi reescrita sem elas.
  - A primeira versão gravava o `payload` com `BsonDocument.Parse`, que lê JSON estendido do MongoDB. A revisão independente encontrou o problema (achado 1).
  - Depois da mutação das três correções, a primeira bateria completa deu 8 falhas, os mesmos testes que a mutação faz falhar. A causa estava na reposição dos ficheiros:
    - o `Copy-Item` repôs os ficheiros a partir da cópia com a data antiga;
    - o MSBuild incremental considerou atualizados os binários compilados com a mutação;
    - os testes correram, portanto, sobre o código mutado.

    A fonte estava correta (confirmado). Atualizou-se a data dos ficheiros e recompilou-se com `--no-incremental` antes de voltar a correr os testes.
  - A sessão foi interrompida durante as correções da revisão. As edições já feitas confirmaram-se com `git diff` antes de continuar.
- **Decisões:**
  - **A especificação 003 prevaleceu sobre o briefing da sessão** (decisão do Diogo, 2026-11-25). Ficaram de fora:
    - Redis Streams entre a Api e um *worker* consumidor;
    - resposta 202;
    - índices `{facilityId, timestamp}`, um índice único em `eventId` e um índice `2dsphere`;
    - normalização de câmara LPR e parquímetro.

    A ingestão grava diretamente no MongoDB antes de responder (RNF-03), não publica no `IEventBus` e não toca em D2. Por isso, o critério do briefing «parar o *worker* não perde eventos» não se aplica. Justificação no ADR 0008.
  - O leitor de validação fica em `Infrastructure/Relational/` (autorizado pelo Diogo), sem migrações nem alterações às entidades.
  - A rota é `/api/ingest/occupancy-events`, porque as especificações omitem o prefixo `/api`, como nas F2 e F3.
  - O *log* de cada lote é escrito no *endpoint*, porque Application não tem pacote de *logging*; nenhuma dependência nova.
  - No modo monolítico há um `IOccupancyEventLog` provisório só para o *handler* poder ser construído, porque o *binding* dos parâmetros corre antes dos filtros.
  - O `payload` passa a seguir I-JSON (RFC 7493), sem nomes começados por `$` nem com o carácter nulo (correção do achado 1, pedida pelo Diogo). É uma regra de esquema nova sobre o conteúdo do `payload`, e não uma mudança ao formato comum do evento. Deve ser acrescentada à especificação 003 na próxima revisão: `TODO(Diogo):` confirmar a redação.
- **Revisão independente** (`code-reviewer`, só leitura, sobre `git diff main...HEAD`): não encontrou erros nas contagens, na ordem das regras, no mapeamento dos erros do `insertMany` nem no 503 com `Retry-After`. O Diogo pediu a correção dos achados 1, 2 e 3 e o reforço dos testes (2026-11-26); os restantes ficam registados. Achados:
  1. **Um `payload` aceite pelo validador podia dar 500, e voltava a dar 500 em cada reenvio.** O `BsonDocument.Parse` falha com nomes repetidos, números enormes e formas `$date`/`$oid` inválidas, e transforma as formas válidas (por exemplo, um `{"$date": …}` passava a data). **Corrigido:**
     - conversão estrutural `BsonPayload`, sem JSON estendido;
     - regra `PayloadRules` no validador, com 400 em `events[i].payload`;
     - testes novos para nome repetido, JSON estendido, número não finito, nome com `$` e substituto (*surrogate*) isolado, e um teste que mostra que um valor `"$date"`, nomes com ponto e um `int64` ficam gravados como chegaram.
  2. **Qualquer `PostgresException` dava 503**, incluindo um *login* recusado ou uma tabela em falta, e um *gateway* repetiria o pedido sem fim. **Corrigido:** só `NpgsqlException { IsTransient: true }` e `TimeoutException` dão 503; um erro de configuração vai para o tratamento global (500). Teste novo com uma palavra-passe errada: 500 e sem `Retry-After`.
  3. **As métricas de resultado contavam um lote que acabava em 503**, e esse lote volta a ser enviado. **Corrigido:** os contadores e o tamanho do lote só se registam quando a resposta é 200; as durações registam-se sempre, porque medem as bases. Teste novo com um *log* de eventos indisponível.
  4. Um tipo JSON errado (por exemplo `"eventId": 123`) dá um 400 de *binding* sem a chave `events[i].campo`; e, no modo monolítico, um corpo mal formado dá 400 e não 501. Fica registado.
  5. O `timestamp` exige `Z` maiúsculo ou `±hh:mm` e até 7 casas decimais: os nanossegundos (9 casas) são rejeitados, e o MongoDB guarda só milissegundos. O simulador da F4b tem de ficar dentro deste formato.
  6. Um `"payload": null` explícito é tratado como ausente e aceite.
  7. Um repetido no mesmo lote é `duplicate` mesmo quando o primeiro foi rejeitado; nesse caso nada fica gravado com esse `eventId`. Está de acordo com a especificação (o `eventId` decide sozinho).
  - Testes reforçados a pedido do Diogo:
    - o AC-3 verifica a chave de `errors` em cada caso e aceita um `payload` de exatamente 4 096 *bytes*;
    - o intercetor do AC-11 conta também os comandos sem leitura e os escalares;
    - o runner de `.http` ignora comentários entre os cabeçalhos e dá uma mensagem clara quando falta `:`.
- ***Commits:*** `8d56540`, `8154895`, `638e397`, `f656d6a`, `6e31d37`, `ae80df9`, `0a155b1`, `cb628b9` e o desta atualização do diário. Por integrar na `main`; nada enviado para o `origin`.

## F4a: integração na `main`

- **Data:** 2026-11-26
- **Feito:**
  - *Push* do ramo `feat/003-ingestao` (9 *commits*) e abertura do [PR #4](https://github.com/diogonunes2798/SmartParking.Hybrid/pull/4) a pedido do Diogo. A descrição inclui o resumo, a decisão de seguir a especificação 003 e não o briefing, os achados corrigidos da revisão, os pontos em aberto e a verificação.
  - *Merge* pedido pelo Diogo, com *merge commit* (`75e0d9f`), como na F1, na F2 e na F3. O GitHub indicava o PR como integrável e sem conflitos, e o `HEAD` do PR era o mesmo da cópia local (`bbf5737`, verificado com `--match-head-commit`).
  - Especificação 003 marcada no índice como «F4a integrada na `main`; F4b por fazer»; linha do RF-01 da RASTREABILIDADE atualizada.
- **Problemas:**
  - O repositório continua sem integração contínua, por isso o PR não teve verificações automáticas. A garantia são os resultados locais registados na descrição: 431 testes em duas corridas seguidas, build e format limpos.
- **Decisões:**
  - *Merge commit* em vez de *squash*, para que os *commits* da F4a referidos neste diário continuem a existir na `main`.
  - O ramo `feat/003-ingestao` foi apagado no `origin` e localmente, a pedido do Diogo, depois de se confirmar que estava integrado na `main` (`git merge-base --is-ancestor`), como nas fases anteriores.
- ***Commits:*** `75e0d9f` (*merge* do PR #4); registo desta entrada no commit seguinte.

## F4b: simulador de sensores (especificação 003)

- **Data:** 2026-11-26
- **Feito:**
  - Plano aprovado pelo Diogo na sessão (UC-01, RF-01; prepara o UC-05; AC-19 a AC-24; D8 com os valores por omissão; D2, D6 e D7 não tocadas), no ramo `feat/003-simulador`. O Diogo escolheu:
    - os testes em `tests/SmartParking.Api.Tests`, que reutilizam o `ApiFixture` e os contentores, em vez de um projeto de testes novo;
    - 5 zonas por omissão (`--zones`).
  - `tools/SensorSimulator`, aplicação de consola na `SmartParking.slnx` (pasta `/tools/`):
    - cliente HTTP de caixa preta da Api, com os seus próprios DTO de fio; não referencia nenhum projeto de `src/` nem bibliotecas de bases de dados (regra de arquitetura nova, parte do AC-19);
    - pacotes novos: `Microsoft.Extensions.Hosting` e `Microsoft.Extensions.Http` 10.0.15, `System.CommandLine` 2.0.15;
    - `--provision`: um parque, `--zones` zonas e os lugares com sensor de ocupação, só pelos *endpoints* da F2 (`spots/batch` em pedidos de até 1 000), e o ficheiro de mapeamento `hardwareId`–`spotId` (por omissão `sensor-map.json`, ignorado pelo Git). Os nomes, os códigos e os `hardwareId` são função da *seed*; só os identificadores mudam entre duas provisões;
    - gerador puro: um processo de Poisson por lugar, com taxa `1/mean` fora e `factor/mean` dentro das janelas `--peak-at`/`--peak-duration`. Quando um sorteio passa uma fronteira de janela, sorteia-se de novo a partir dela (exato pela ausência de memória da exponencial). O estado inicial é sorteado com a *seed* e os tipos alternam. Cada lugar tem o seu `Random`, semeado com SHA-256 da *seed* e do `hardwareId`;
    - `eventId`: UUID versão 8 a partir de SHA-256 da *seed*, do instante de arranque, do `hardwareId` e do número de sequência. A mesma *seed* e o mesmo relógio dão os mesmos identificadores (AC-20); outra corrida com a mesma *seed* não colide no registo idempotente;
    - `timestamp` em UTC com milissegundos e `Z` (dentro do formato da F4a);
    - *gateway* virtual por parque: lote aos 500 eventos ou 1 s depois do evento mais antigo do *buffer* (`--batch-size`, `--flush-interval`). Cada *gateway* envia os seus lotes por ordem, numa tarefa própria, para que a espera de um 503 não pare a geração;
    - envio por `IHttpClientFactory`. Só o 503 é repetido: o mesmo corpo serializado, depois do `Retry-After` (segundos ou data HTTP), até `--max-retries`. As outras respostas ficam registadas e contadas;
    - resumo da corrida no *log* da consola (*stderr*); nunca escreve em `results/` (o `--mapping` dentro de uma pasta `results` é recusado).
  - Testes em `tests/SmartParking.Api.Tests/Features/Simulator/` e `ArchitectureTests/Features/SimulatorIsolationTests.cs`:
    - AC-19: a linha de comandos da especificação (`--provision --spots 20 --seed 42`) contra a Api de teste; o teste confirma por `GET` da F2 o parque, as 5 zonas, os 20 lugares, os 20 sensores de ocupação ativos e o mapeamento;
    - AC-20 e AC-21: sequências iguais com a mesma *seed* e o mesmo `FakeTimeProvider`, diferentes com outra *seed* ou outro instante; tipos alternados por lugar;
    - AC-22: o intervalo é calculado no teste, sem valores escritos à mão. A contagem fora da janela tem de ficar no intervalo central de uma Poisson(N·T_out/mean). Dado o total n, a contagem dentro segue uma Binomial(n, p), com p = factor·T_in/(factor·T_in + T_out), e a razão das taxas tem de ficar no intervalo daí derivado. Os quantis vêm da soma do log-pmf, com α = 10⁻⁶ declarado no teste;
    - AC-23: regra do acumulador com o relógio falso (500 eventos; 1 s contado desde o mais antigo; valores configuráveis) e uma corrida em tempo real contra a Api de teste, com um *handler* de registo, em que nenhum pedido passa de 500 e todos os eventos são aceites;
    - AC-24: um registo de eventos que falha só na primeira escrita dá 503 com `Retry-After: 1`. O simulador reenvia os mesmos `eventId` depois da espera, e o MongoDB fica com cada evento uma vez. Testes extra: 503 permanente, que para ao fim de `--max-retries`, e o `Retry-After` em segundos, em data ou ausente.
  - Contagem de testes:
    - antes da revisão: 446 a passar em duas corridas seguidas da solução (431 antes da F4b);
    - depois das correções da revisão: 448 a passar em duas corridas seguidas.
  - Testes de mutação, todos detetados:
    - sem alternância, falha o AC-21;
    - sem o fator de pico, falha o AC-22;
    - `eventId` novos no reenvio, falha o AC-24;
    - lote de 501, falha o AC-23;
    - `Retry-After` ignorado, falha o AC-24;
    - desfazer cada uma das três correções da revisão, ou esperar 10 % menos do que o `Retry-After`, faz falhar o teste novo correspondente.
  - Verificação manual contra o `compose.yaml`, com a Api como `smartparking_api` e as cadeias de ligação montadas a partir do `.env` só no processo:
    - `--provision --spots 20 --seed 42` criou o parque, 5 zonas e 20 lugares com sensor;
    - uma corrida de 30 s com uma janela de pico terminou com todos os eventos aceites, sem duplicados nem rejeitados;
    - no `mongosh`, o número de documentos do parque é igual ao número de aceites, os `_id` são todos distintos (versão 8) e os tipos aparecem em número igual ±1.

    São contagens de verificação, não medições: as medições são da F12.
  - Roslyn: 0 diagnósticos. O `detect_antipatterns` assinalou:
    - os dois `catch (OperationCanceledException)` com comentário, que são o fim esperado da corrida e do escoamento;
    - o `Main` sem `CancellationToken`, que vem do `System.CommandLine`.
  - Documentação: RASTREABILIDADE (linha do RF-01), índice das especificações, comandos do simulador no `CLAUDE.md`.
- **Problemas:**
  - O `System.CommandLine` 2.0 não converte `Uri`: o AC-19 falhou com `Cannot parse argument … as expected type 'System.Uri'`. O `--api` passou a texto, validado com `Uri.TryCreate`.
  - Os validadores do comando raiz não veem os valores por omissão. Na primeira versão, `--help` funcionava, mas qualquer corrida dava erros como «`--spots` must be at least 1» com os valores por omissão. A validação passou para dentro da ação, sobre o `ParseResult`.
  - Durante essa correção, o formatador automático (*hook* pós-edição) apagou o bloco de validação inteiro, porque a primeira de duas edições deixou o ficheiro com sintaxe inválida. O bloco foi reescrito numa só edição.
  - O `Program` com instruções de nível superior criaria um segundo tipo global `Program`, visível nos testes ao lado do `Program` da Api (`InternalsVisibleTo`). Passou a `SmartParking.SensorSimulator.Program` com `Main` explícito.
  - A primeira bateria completa passou a saída por `hooks/post-test-analyze.sh`, que não existe no repositório (problema já registado na F1, na F2b e na F3a). A saída perdeu-se e a bateria foi repetida com o *script* do *plugin* do *kit*. Esse *script* não lê o resumo do xUnit v3 («All 0 test(s) passed»), por isso a contagem vem da linha `Test run summary` do `dotnet test`.
  - Na correção do achado 1, o formatador automático voltou a reescrever o ficheiro a meio de uma edição (chavetas em excesso) e apagou o método `RetryAfter`. O `git diff` mostrou a perda antes de compilar; o método foi reposto a partir do *commit* `52dcab8`.
  - Apesar da memória do projeto, dois comandos usaram *heredoc* no Git Bash (o `.csproj` inicial e a mensagem do primeiro *commit*). Desta vez não falharam; os *scripts* seguintes ficaram em ficheiros.
- **Decisões:**
  - `System.CommandLine` fica na linha 2.0.x, distribuída com o .NET 10 (a 3.0.0 acompanha o .NET 11), pelo mesmo critério dos pacotes `Microsoft.*` 10.x.
  - Sem Polly nem `AddStandardResilienceHandler`: também repetiriam outras respostas 5xx e *timeouts*, o que muda a semântica da especificação (só o 503, o mesmo lote, depois do `Retry-After`).
  - O instante de arranque entra no `eventId`. Assim, as várias execuções da F12 com a mesma *seed* não chegam ao registo como duplicados. A especificação pede identificadores determinísticos «para a mesma *seed* e o mesmo relógio», e isto cumpre-o.
  - O `--provision` grava o mapeamento e termina; a emissão é uma segunda invocação sobre o ficheiro. Leitura da especificação: «depois emite sobre esse mapeamento». `TODO(Diogo):` confirmar, ou pedir que o `--provision` emita logo a seguir. *(Resolvido a 2026-11-26: mantém-se separado; ver «F4b: fecho dos achados da revisão».)*
  - `TODO(Diogo):` decidir se o `eventId` determinístico e o *flush* contado desde o evento mais antigo justificam um ADR 0009 ou ficam só neste diário. *(Resolvido a 2026-11-26: ADR 0009.)*
- **Revisão independente** (`code-reviewer`, só leitura, sobre `git diff main...HEAD`): não encontrou erros na matemática do gerador, na construção do `eventId`, no acumulador, na repartição por zonas e lotes de 1 000, nem na repetição após 503. Também não encontrou violações das convenções do `CLAUDE.md`. O Diogo pediu a correção dos achados 1, 3 e 6 (2026-11-26); os restantes ficam registados. Achados, por gravidade:
  1. **Uma resposta 200 com corpo mal formado derruba o *gateway*.** O `ReadFromJsonAsync` está fora do `try`. A tarefa do *gateway* falha, os lotes seguintes não são enviados e a corrida termina com uma exceção (código 1) sem resumo. **Corrigido:**
     - um 200 cujas contagens não se leem (`JsonException`, `HttpRequestException`, `IOException` ou corpo vazio) fica registado em `Warning` e conta como não entregue, porque nada confirma a gravação;
     - o *gateway* continua a enviar os lotes seguintes;
     - teste novo: um *handler* que trunca o corpo de cada 200 da Api de teste. A corrida termina com resumo, todos os lotes são enviados e nenhum conta como entregue.
  2. **Uma provisão interrompida a meio impede repetir com a mesma *seed*.** O parque e os lotes já gravados ficam, e o nome do parque e os `hardwareId` são únicos, por isso a repetição dá 409. Um `HttpRequestException` no `--provision` também escapa como exceção (código 1 em vez de 3).
  3. **O AC-24 mede a espera a partir do início do primeiro pedido, e não da chegada do 503.** Uma espera um pouco mais curta do que o `Retry-After` passaria; inversamente, os *timers* do Windows podem disparar alguns milissegundos antes do `Stopwatch`. **Corrigido:**
     - o *handler* de registo guarda também o instante da resposta;
     - o teste mede da chegada do 503 até ao reenvio, com uma tolerância explícita de um *tick* do *timer* do Windows (16 ms). É uma margem de correção, não um valor de desempenho.
  4. Um envio interrompido pelo fim do prazo de escoamento conta como não entregue, embora a Api o possa ter gravado.
  5. Uma corrida interrompida com Ctrl+C sai com 0, como uma corrida completa; `ExitCodes.Cancelled` não é usado.
  6. **A *seed* e o `eventId` dependem da cultura atual.** As cadeias passadas ao SHA-256 são formatadas na cultura corrente, e uma *seed* negativa pode mudar entre máquinas (sinal menos U+2212). **Corrigido:** as duas cadeias são formatadas com `FormattableString.Invariant`. Teste novo: com a *seed* −1 e uma cultura cujo sinal menos é U+2212, a sequência é igual à da cultura invariante.
  7. Validação incompleta:
     - `--mean-interval` infinito ou enorme dá `OverflowException`;
     - `zone * spots` pode exceder `int`;
     - um mapeamento mal formado dá uma exceção não tratada.
  8. Nenhum teste exercita o *flush* por tempo dentro do `GatewayRunner`. O acumulador está provado com o relógio falso, mas a corrida em tempo real só usa lotes cheios e o *flush* final.
  9. A correção de +1 ms por lugar pode deixar a sequência global fora de ordem por 1 ms; é inofensivo, mas o comentário diz «ordenada».
- ***Commits:*** `52dcab8` (código e testes), `88ded9c` (documentação), `e56a160` (correções da revisão) e `b953717` (esta atualização do diário).

## F4b: integração na `main`

- **Data:** 2026-11-26
- **Feito:**
  - *Push* do ramo `feat/003-simulador` (4 *commits*) e abertura do [PR #5](https://github.com/diogonunes2798/SmartParking.Hybrid/pull/5) a pedido do Diogo. A descrição inclui:
    - o resumo e a tabela dos testes AC-19 a AC-24;
    - a verificação;
    - os achados corrigidos e os que ficam em aberto da revisão;
    - os pontos por decidir.
  - *Merge* pedido pelo Diogo, com *merge commit* (`0fe6fb1`), como nas fases anteriores. O GitHub indicava o PR como integrável e sem conflitos, e o `HEAD` do PR era o mesmo da cópia local (`b953717`, verificado com `--match-head-commit`).
  - Especificação 003 marcada no índice como implementada e integrada (F4a e F4b); linha do RF-01 da RASTREABILIDADE atualizada.
- **Problemas:**
  - O repositório continua sem integração contínua, por isso o PR não teve verificações automáticas. A garantia são os resultados locais registados na descrição: 448 testes em duas corridas seguidas, build e format limpos.
  - Depois do *merge*, o *hook* de *pre-commit* recusou o *commit* desta entrada: o `dotnet format` falhou em `GatewayRunner.cs`. A causa:
    - no ramo, o ficheiro estava em CRLF, como o Write o criou no Windows, e o formato passava;
    - ao mudar para a `main`, o Git reescreveu-o em LF (`.gitattributes` com `eol=lf`);
    - sem `.editorconfig`, o `dotnet format` no Windows pede CRLF nas linhas de um comentário metido numa lista de argumentos.

    Correção: o valor passou para uma variável local, com o comentário por cima, sem mudar o comportamento. O format ficou limpo e os 16 testes do simulador passaram. É um *commit* `style` próprio na `main`. Fica a observação de que a falta de `.editorconfig` torna o resultado do `dotnet format` dependente dos finais de linha da cópia local.
- **Decisões:**
  - *Merge commit* em vez de *squash*, para que os *commits* da F4b referidos neste diário continuem a existir na `main`.
  - O ramo `feat/003-simulador` foi apagado no `origin` e localmente, a pedido do Diogo, depois de se confirmar que estava integrado na `main` (`git merge-base --is-ancestor`).
  - Continuam em aberto da F4b: os achados 2, 4, 5, 7, 8 e 9 da revisão e os dois `TODO(Diogo)` (emitir logo a seguir ao `--provision`; ADR 0009 para o `eventId` determinístico).
- ***Commits:*** `0fe6fb1` (*merge* do PR #5); registo desta entrada no commit seguinte.

## F4b: fecho dos achados da revisão

- **Data:** 2026-11-26
- **Âmbito:** especificação 003 (UC-01, UC-05, RF-01; AC-19 a AC-24). Sem AC novos e sem mudança a D1–D10. Ramo `fix/003-simulador-revisao`, a partir da `main` (`2b78399`). O plano foi aprovado pelo Diogo antes do código.
- **Decisões do Diogo (2026-11-26):**
  - corrigir os achados 2, 5, 7, 8 e 9; o achado 4 fica sem alteração;
  - o `--provision` não emite logo a seguir e continua a ser uma invocação separada, porque as execuções da F12 reutilizam a mesma provisão;
  - escrever o ADR 0009 sobre o `eventId` determinístico e o *flush* contado desde o evento mais antigo.
- **Feito:**
  - Achado 2: uma falha de transporte, um *timeout* ou um corpo 2xx ilegível no `--provision` deixam de escapar como exceção (código 1) e saem com o código 3. A mensagem diz que o parque pode ter ficado registado em parte e que a repetição com a mesma `--seed` precisa de uma base limpa. A provisão não passou a ser retomável.
  - Achado 5: o Ctrl+C sai com 130 (`ExitCodes.Cancelled`), tanto durante a emissão como durante a provisão ou a leitura do mapeamento. O 130 tem precedência sobre o 5 (`NotAllDelivered`): uma corrida cancelada não é uma medição. O resumo continua no *log*.
  - Achado 7:
    - o `--mean-interval` tem de ser maior do que 0 e no máximo 86 400 s; NaN e infinito são rejeitados;
    - o `--peak-factor` tem de ser finito;
    - a repartição dos lugares por zonas passa a ser calculada em `long` (`Provisioner.ZoneRange`);
    - um mapeamento mal formado, sem `sensors` ou com entradas nulas sai com o código 4.
  - Achado 8: teste em tempo real do *flush* por tempo no `GatewayRunner`. São cerca de 20 eventos/s, com lotes de 500 e *flush* de 200 ms, e o teste exige mais de um pedido.
  - Achado 9: o comentário do gerador diz agora que a sequência só está quase ordenada (o acerto de +1 ms por lugar).
  - ADR 0009, com o índice dos ADR e a linha do RF-01 da RASTREABILIDADE atualizados. Os dois `TODO(Diogo)` da entrada F4b ficaram marcados como resolvidos.
  - Testes novos: 13. O total passou de 448 para 461.
- **Verificação:**
  - `dotnet build`: 0 avisos, 0 erros;
  - `dotnet format --verify-no-changes`: limpo;
  - `dotnet test`: 461 testes passaram nas duas corridas seguidas depois da segunda ronda de correções. Antes dessa ronda, com 459 testes, tinham passado três corridas;
  - mutações, todas apanhadas pelos testes novos:
    - desligar o `FlushIfDue`;
    - devolver 0 no cancelamento da emissão;
    - retirar o `catch` do cancelamento na ação;
    - voltar a comparar em `TimeSpan` no gerador;
  - manual, sem a Api a correr:
    - `--provision` deu o código 3;
    - `--mean-interval 1e309` deu o código 2;
    - um mapeamento truncado deu o código 4;
    - o código 130 com Ctrl+C real não foi experimentado à mão. Cobrem-no os dois testes de cancelamento.
- **Problemas:**
  - O primeiro teste de cancelamento usava `--mean-interval 0.1` e saiu com o código 1. O System.CommandLine lê os números com a cultura atual, e em pt-PT o separador decimal é a vírgula. O teste passou a usar `1`. Fica um ponto em aberto: na linha de comandos, `0.5` é rejeitado numa máquina em português. `TODO(Diogo):` decidir se as opções numéricas devem passar a ler-se com a cultura invariante. *(Resolvido a 2026-11-26: cultura invariante; ver «Simulador: opções lidas com a cultura invariante».)*
  - A mutação do gerador sobreviveu à primeira versão do teste. Com `--peak-factor 1e-9` a espera média é cerca de 6e10 s, abaixo do máximo do `TimeSpan` (cerca de 9,2e11 s), por isso o transbordo não acontecia. O teste passou a usar 1e-12.
  - O plano previa um *commit* por achado, mas o `SimulatorCommand.cs` e o `SimulatorApiTests.cs` têm alterações de vários achados no mesmo ficheiro, e aqui não é possível separá-las por blocos de forma interativa. Ficou um *commit* para as correções e os testes, como o `e56a160` da revisão anterior.
- **Revisão independente** (`code-reviewer`, só leitura, sobre `git diff 2b78399...HEAD`): confirmou o `ZoneRange`, as exceções apanhadas no `PostAsync`, a validação, o mapeamento mal formado, o comentário do gerador e a correspondência do ADR 0009 com o código. Achados, por gravidade:
  1. **Um Ctrl+C fora do ciclo de emissão** (provisão, leitura do mapeamento) **ainda saía com 1.** Um `SaveAsync` cancelado deixava o mapeamento truncado. O teste de cancelamento dependia de um *timer* de 1 s. **Corrigido:**
     - a ação devolve 130 para um cancelamento;
     - o mapeamento é escrito num ficheiro temporário e depois movido;
     - o teste cancela no primeiro pedido de ingestão;
     - teste novo para o cancelamento a meio da provisão.
  2. **Um `--peak-factor` finito mas minúsculo ainda transbordava o `TimeSpan` dentro da janela.** **Corrigido** no gerador: a espera é comparada com a fronteira em segundos antes de se construir o `TimeSpan`. Impor `--peak-factor >= 1` mudaria o significado da opção, por isso não se fez sem decisão do Diogo.
  3. **O ADR exagerava a espera máxima.** Um lote vencido pode esperar na fila atrás de um envio a repetir depois de um 503. **Corrigido** no texto.
  4. Menores:
     - a precedência do 130 sobre o 5 está agora documentada em `ExitCodes`;
     - o ADR já não diz que o teste em tempo real prova a contagem desde o evento mais antigo, o que só o relógio falso prova;
     - os testes de `SimulatorCommandTests` não têm `AC-n` no nome, porque validam opções que nenhum AC da especificação 003 descreve. Fica registado.
- **Decisões:**
  - Achado 4 sem alteração: um envio cortado pelo prazo de escoamento conta como não entregue. A contagem é conservadora, e um reenvio seria ignorado pela idempotência da ingestão.
  - O limite de 86 400 s do `--mean-interval` é um limite de correção, não um valor de desempenho. `TODO(Diogo):` confirmar o limite. *(Resolvido a 2026-11-26: confirmado; ver «Simulador: confirmação do limite do `--mean-interval` e das secções do ADR 0009».)*
  - Secções da tese do ADR 0009: proposta 5.9.4 e 7.3. `TODO(Diogo):` confirmar. *(Resolvido a 2026-11-26: confirmadas.)*
- ***Commits:***
  - `9518685`: correções dos achados 2, 5, 7 e 8;
  - `a4eee36`: achado 9;
  - `e172419`: ADR 0009;
  - `e044897`: segunda ronda de correções;
  - `392280d`: texto do ADR;
  - esta entrada fica no *commit* seguinte.

## F4b: integração do fecho da revisão na `main`

- **Data:** 2026-11-26
- **Feito:**
  - *Push* do ramo `fix/003-simulador-revisao` (6 *commits*) e abertura do [PR #6](https://github.com/diogonunes2798/SmartParking.Hybrid/pull/6) a pedido do Diogo. A descrição inclui:
    - as decisões do Diogo;
    - as correções por achado e o ADR 0009;
    - a revisão independente;
    - a verificação;
    - os três `TODO(Diogo)` em aberto.
  - *Merge* pedido pelo Diogo, com *merge commit* (`52db1e5`). O GitHub indicava o PR como integrável e sem conflitos, e o `HEAD` do PR era o mesmo da cópia local (`a922b4f`, verificado com `--match-head-commit`).
  - Linha do RF-01 da RASTREABILIDADE atualizada com o PR #6.
- **Problemas:**
  - O repositório continua sem integração contínua, por isso o PR não teve verificações automáticas. A garantia são os resultados locais registados na descrição: 461 testes em duas corridas seguidas, *build* e *format* limpos.
  - Depois de mudar para a `main`, o `dotnet format --verify-no-changes` continuou limpo. O problema dos finais de linha da integração anterior não se repetiu.
- **Decisões:**
  - *Merge commit* em vez de *squash*, para que os *commits* referidos na entrada anterior continuem a existir na `main`.
  - Continuam em aberto: a leitura dos números na linha de comandos com a cultura atual, o limite de 86 400 s do `--mean-interval` e as secções da tese do ADR 0009 (`TODO(Diogo)` na entrada anterior).
- ***Commits:*** `52db1e5` (*merge* do PR #6); registo desta entrada no *commit* seguinte.

## Simulador: opções lidas com a cultura invariante

- **Data:** 2026-11-26
- **Âmbito:** especificação 003 (F4b, simulador). Sem AC novos e sem mudança a D1–D10. Ramo `fix/003-cultura-invariante`, a partir da `main` (`45ad861`).
- **Decisão do Diogo (2026-11-26):** a linha de comandos do simulador lê os números com a cultura invariante. Resolve o `TODO(Diogo)` da entrada «F4b: fecho dos achados da revisão».
- **Feito:**
  - `InvariantParsers`: *parsers* com a cultura invariante para as opções `int`, `double` e `TimeSpan`. As 11 opções numéricas e de duração do `SimulatorCommand` usam-nos como `CustomParser`. O System.CommandLine só chama o `CustomParser` quando a opção vem na linha de comandos, por isso os valores por omissão não mudam.
  - As durações (`--peak-at`, `--peak-duration`, `--duration`, `--flush-interval`) também entram, embora a decisão falasse de números: o `TimeSpan` depende da cultura no separador das frações de segundo (`00:00:00.5`).
  - Um número com vírgula passa a ser um erro de *parse* (código 1), com uma mensagem que diz a opção e o formato esperado.
  - Testes novos, em cultura pt-PT: um que lê ponto decimal, *seed* negativa e durações com frações, incluindo as opções repetidas; e três casos com vírgula, que dão erro de *parse*.
- **Verificação:**
  - `dotnet build`: 0 avisos, 0 erros;
  - `dotnet format --verify-no-changes`: limpo;
  - `dotnet test`: 465 testes passaram em duas corridas seguidas (eram 461);
  - mutação: sem o *parser* no `--mean-interval`, os dois testes em pt-PT falham;
  - manual nesta máquina (pt-PT): `--mean-interval 0,5` dá o erro de *parse*; `--mean-interval 0.5` é aceite e o simulador segue para a verificação do mapeamento.
- **Problemas:**
  - A primeira compilação falhou: o C# não inferiu o tipo genérico a partir de um grupo de métodos com parâmetro `out` (CS0411). Ficou explícito (`Single<TimeSpan>`).
  - O `GetValue<TimeSpan[]>` é anotado como anulável, e com os avisos tratados como erros os testes não compilavam (CS8604). Ficou `?? []`.
- **Decisões:**
  - Uma linha de comandos com vírgula deixa de funcionar numa máquina em português. É intencional: o mesmo texto passa a significar o mesmo valor em qualquer máquina, como pedem as medições da F12.
  - Continuam em aberto: o limite de 86 400 s do `--mean-interval` e as secções da tese do ADR 0009.
- ***Commits:*** `4bda043` (código e testes); esta entrada fica no *commit* seguinte.

## Simulador: integração da cultura invariante na `main`

- **Data:** 2026-11-26
- **Feito:**
  - *Push* do ramo `fix/003-cultura-invariante` (2 *commits*) e abertura do [PR #7](https://github.com/diogonunes2798/SmartParking.Hybrid/pull/7) a pedido do Diogo. A descrição inclui o problema, a alteração às 11 opções, a verificação e os dois `TODO(Diogo)` em aberto.
  - *Merge* pedido pelo Diogo, com *merge commit* (`2601999`). O GitHub indicava o PR como integrável e sem conflitos, e o `HEAD` do PR era o mesmo da cópia local (`c67a7fa`, verificado com `--match-head-commit`).
  - Linha do RF-01 da RASTREABILIDADE atualizada com o PR #7.
- **Problemas:**
  - O repositório continua sem integração contínua, por isso o PR não teve verificações automáticas. A garantia são os resultados locais registados na descrição: 465 testes em duas corridas seguidas, *build* e *format* limpos.
  - Não houve revisão independente desta alteração: o Diogo escolheu abrir o PR diretamente.
  - Depois de mudar para a `main`, o `dotnet format --verify-no-changes` continuou limpo.
- **Decisões:**
  - *Merge commit* em vez de *squash*, como nos PR anteriores.
  - Continuam em aberto: o limite de 86 400 s do `--mean-interval` e as secções da tese do ADR 0009 (`TODO(Diogo)` na entrada «F4b: fecho dos achados da revisão»).
- ***Commits:*** `2601999` (*merge* do PR #7); registo desta entrada no *commit* seguinte.

## Simulador: confirmação do limite do `--mean-interval` e das secções do ADR 0009

- **Data:** 2026-11-26
- **Decisões do Diogo (2026-11-26):**
  - confirma o limite de 86 400 s (um dia) do `--mean-interval`. É um limite de correção, que evita o transbordo do `TimeSpan` no gerador, e não um valor de desempenho;
  - confirma as secções da tese do ADR 0009: 5.9.4 e 7.3.
- **Feito:**
  - ADR 0009: o campo «Secções da tese» deixa de ser uma proposta;
  - os dois `TODO(Diogo)` da entrada «F4b: fecho dos achados da revisão» ficam marcados como resolvidos, sem reescrever a entrada.
- **Problemas:** nenhum. Só muda documentação; não há código nem testes alterados.
- **Decisões:** o simulador deixa de ter `TODO(Diogo)` em aberto. Na especificação 003 mantém-se só o da redação da regra I-JSON do `payload` (entrada da F4a), que fica para a próxima revisão da especificação.
- ***Commits:*** esta entrada e as duas alterações acima, num só *commit* na `main`.
