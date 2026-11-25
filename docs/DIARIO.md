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
