# Diário de desenvolvimento

Matéria-prima do Capítulo 5. Cada entrada regista o que foi feito, os problemas encontrados (incluindo as tentativas falhadas) e as decisões tomadas. Os valores medidos são os observados nesta máquina e não substituem os *benchmarks* da Fase 9.

---

## 14-11-2026 — Fase 1: Fundação

### Ambiente

- Windows 11 Pro (10.0.26100)
- .NET SDK 10.0.404
- Docker 29.0.1, Compose v2.40.3
- Imagens fixadas em versões *patch*:
  - `postgres:16.16-alpine`
  - `mongo:7.0.48`
  - `redis:7.4.12-alpine`
  - `prom/prometheus:v3.16.0`

As versões foram lidas das próprias imagens (`PG_VERSION`, `MONGO_VERSION`, `REDIS_VERSION`, `prometheus --version`), em vez de se usarem *tags* móveis como `16` ou `latest`.

### O que foi feito

1. **Solução** `SmartParking.Hybrid.slnx` (formato XML, o padrão no .NET 10) com nove projetos em `src/`:
   - os oito da estrutura do CLAUDE.md;
   - `SmartParking.ServiceDefaults`, um desvio justificado no ADR-0001.
   - `global.json`, `Directory.Build.props` (`TreatWarningsAsErrors`) e `Directory.Packages.props` (versões centralizadas).
2. **Infraestrutura local** (`docker-compose.yml`): PostgreSQL, MongoDB e Redis com *healthchecks* e volumes nomeados.
   - O Redis tem AOF ativo, para que o *read model* sobreviva a um reinício.
   - O Prometheus está no perfil `observability`.
   - As credenciais são só de desenvolvimento e podem ser substituídas por `.env` (há um `.env.example`).
3. ***Health checks*:** `/health/live` (só o processo) e `/health/ready` (os três motores, com um *timeout* de 3 s cada), com resposta JSON própria.
4. **OpenTelemetry:**
   - *traces* e métricas com recursos de serviço;
   - instrumentação de ASP.NET Core, HttpClient e *runtime*;
   - `ActivitySource` e `Meter` com o nome `SmartParking`;
   - exportação Prometheus em `/metrics` (ADR-0002).
5. **API:** OpenAPI em `/openapi/v1.json` e Swagger UI em `/swagger`, ambos só em Development. Porta fixa 5080.
6. **Workers:** anfitrião web mínimo na porta 5090, com `/health/*` e `/metrics`. Ainda não tem nenhum *BackgroundService*.
7. **Testes** (xUnit v3 em Microsoft.Testing.Platform), no fim da fase, já com as correções da revisão:
   - 18 testes de arquitetura (grafo de projetos e NetArchTest);
   - 13 testes de integração com Testcontainers (PostgreSQL, MongoDB e Redis reais, com as mesmas imagens do compose).
8. **Documentação:** ADR-0001 (estrutura e camadas), ADR-0002 (observabilidade) e `RASTREABILIDADE.md`.

### Problemas encontrados e tentativas falhadas

1. **O template `worker` não restaura com gestão central de pacotes.** O `dotnet new worker` gera um `PackageReference` com `Version=`, o que é incompatível com `ManagePackageVersionsCentrally`. A solução foi reescrever os `.csproj` sem versões e manter tudo no `Directory.Packages.props`.

2. **O NuGet escolheu um pacote do .NET 11.** O `dotnet add package Microsoft.AspNetCore.OpenApi` instalou a versão 11.0.0, que só tem `lib/net11.0` (o .NET 11 saiu este mês). A versão foi fixada em 10.0.15, para manter o protótipo no .NET 10 LTS.
   - **Lição:** com o .NET 11 já disponível, os pacotes `Microsoft.*` têm de ser fixados na linha 10.0.x.
   - O `Swashbuckle.AspNetCore.SwaggerUI` 11.0.1 inclui `net10.0` e foi mantido.

3. **Colisão de volumes Docker com outro projeto.** O primeiro `docker compose up` falhou:
   - o PostgreSQL deu «*database files are incompatible with server*», porque os dados tinham sido criados pela versão 17;
   - o MongoDB deu «*Invalid featureCompatibilityVersion document … version: "8.0"*».

   **Causa:** já existiam volumes `smartparking_postgres-data` e `smartparking_mongo-data` de outro projeto, com dados de versões mais recentes. O nome do projeto compose (`smartparking`) coincidia com o desse projeto.

   **Solução:** mudar o nome do projeto compose para `smartparking-hybrid`, o que isola os volumes. Os volumes antigos não foram apagados, porque não pertencem a este repositório.

   **Lição:** o nome do projeto compose também é parte da reprodutibilidade.

4. **O *health check* do Redis não respeitava o *timeout*.** Com o pacote `AspNetCore.HealthChecks.Redis` 9.0.0 e o Redis parado, o `/health/ready` demorou **5,25 s**, apesar de o *timeout* configurado ser de 3 s.
   - **Causa:** o *check* usa um PING síncrono, que ignora o `CancellationToken` e espera pelo *timeout* interno do StackExchange.Redis (5 000 ms). Além disso, o pacote foi compilado contra o StackExchange.Redis 2.x e o projeto usa o 3.4.35.
   - **Solução:** um *check* próprio (`RedisHealthCheck`) com `PingAsync().WaitAsync(cancellationToken)`. Com o Redis parado, o tempo passou para **3,02 s** (503 em `ready`, 200 em `live`). Depois de reiniciar o Redis, o `ready` voltou a 200 sem reiniciar a API.
   - O teste `ReadinessFailureTests` impede a regressão: exige que o `ready` responda em menos de 4,5 s.

5. **Dependências transitivas perdidas ao remover o pacote do Redis.** Ao retirar `AspNetCore.HealthChecks.Redis`, a `Infrastructure.Cache` deixou de compilar, porque `IConfiguration` e `IHealthCheck` vinham por transitividade. A solução foi declarar `Microsoft.Extensions.Configuration.Abstractions` e `Microsoft.Extensions.Diagnostics.HealthChecks` explicitamente na `Cache`. Nas outras duas subcamadas, o `Configuration.Abstractions` também passou a ser declarado; o `HealthChecks` continua a vir dos pacotes `AspNetCore.HealthChecks.*`. (Corrigido depois da revisão: a primeira versão desta entrada dizia «nas três subcamadas», o que não era exato.)
   - Neste episódio, um pedido manual correu ainda com o binário anterior, porque o *build* tinha falhado sem eu reparar. Foi descartado e repetido depois de um *build* limpo.

6. **O `dotnet test` deixou de aceitar o modo VSTest.** Com o SDK 10 e o xUnit v3 (4.0.3), o `dotnet test` falhou com «*Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK*».
   - **Solução:** ativar `"test": { "runner": "Microsoft.Testing.Platform" }` no `global.json` e retirar `Microsoft.NET.Test.Sdk` e `xunit.runner.visualstudio`.
   - Consequência: o comando para correr um único projeto passa a ser `dotnet test --project <caminho>`.

7. **Os testes de arquitetura não detetam dependências através de constantes.** Para confirmar que os testes não passavam em vazio, fiz duas mutações temporárias:
   - **Primeira mutação:** proibir a dependência `ServiceDefaults → Application`. O teste continuou a passar, porque a `ServiceDefaults` só usa constantes (`const string`) do `Application`, e o compilador copia-as para o IL de quem as usa.
   - **Segunda mutação:** proibir `"OpenTelemetry"`. O teste falhou, como devia, e indicou o tipo infrator (`ServiceDefaultsExtensions`).
   - **Conclusão:** as regras funcionam, mas têm este limite, que está registado no ADR-0001.

8. **Tentativa falhada de ferramenta:** um comando de *shell* que chamava `python3` bloqueou a sessão. No Windows, `python3` é o atalho da Microsoft Store e não um interpretador. Não era necessário e foi retirado.

### Revisão independente (passo 6)

A revisão foi feita por um agente com as instruções do `dotnet-architecture-reviewer` (dotnet-ai-toolkit), sobre o código ainda por registar. Corrigi os oito pontos marcados como «corrigir já»:

| # | Achado | Correção | Prova |
|---|---|---|---|
| 1 (alto) | Os testes de arquitetura passavam em vazio. Só viam tipos usados no IL, e cada assembly tinha apenas um `AssemblyMarker`. Uma `ProjectReference` proibida passaria. A API não estava coberta. | Novo `ProjectGraphTests`: lê os `.csproj` e aplica listas de permissões por camada. Nova regra: a API não usa os *drivers*. | Mutação `Messaging → Relational`: 1 falha, com o projeto infrator identificado. Revertida. |
| 2 | As sondas e os *scrapes* entravam no histograma de latência (o filtro só se aplicava aos *traces*). | `DisableHttpMetrics()` nos três *endpoints*. | `Probes_and_scrapes_are_excluded_from_latency_histograms`. |
| 3 | Os *buckets* por omissão (0,1 → 0,25 s) são grosseiros para o alvo de 200 ms. | `AddView` com *buckets* explícitos (ADR-0002). | `Latency_histogram_uses_fine_buckets_around_the_200_ms_target`. |
| 4 | A ligação ao Redis era feita na primeira resolução, possivelmente fora do *timeout* da sonda. | `RedisConnectionWarmup` (ligação no arranque) e caminho rápido `!IsConnected` no *check*. | `Api_starts_and_reports_unready_when_redis_is_down_at_boot`. Ver a nota abaixo. |
| 5 | Só se testava a falha do Redis. | `[Theory]` para os três motores, cada caso com os seus próprios contentores. | 3 casos verdes. O MongoDB respeita o *timeout*: 3 012,9 ms, com `OperationCanceledException`. |
| 6 | Os testes corriam em `Development`, que tem *connection strings* para o compose. Uma substituição falhada passaria contra as bases erradas. | Ambiente `Testing`, sem *connection strings* próprias; o OpenAPI fica disponível em `Development` e em `Testing`. | Todos os testes de integração. |
| 7 | SDK fixado de forma solta (`latestFeature`); *tags* de imagem mutáveis; imagens duplicadas entre o compose e o *fixture*. | `latestPatch`; imagens fixadas por `tag@sha256` no compose e no *fixture*. | `ComposeConsistencyTests` (3 casos). |
| 8 | O `service.version` era sempre `1.0.0.0`, porque vinha da assembly de entrada, que nos testes é o *runner*. | Passou a usar a *InformationalVersion* da `ServiceDefaults`, que inclui o commit. | `/metrics` mostra `service_version="1.0.0+c29dc6fca1f2…"`. |

*Digests* fixados nesta data:
- `postgres:16.16-alpine@sha256:0adbf25a…dbfb060cbdc106`
- `mongo:7.0.48@sha256:9896df58…4e1384997e6503c8c71`
- `redis:7.4.12-alpine@sha256:7f7d30b7…20e0cf287fbe`
- `prom/prometheus:v3.16.0@sha256:e0498764…c38fddd0f06`

Os valores completos estão no `docker-compose.yml`.

**Problemas encontrados durante as correções:**

1. **A primeira mutação do grafo foi mal escolhida.** `Domain → Infrastructure.Relational` cria um ciclo (Relational → Application → Domain → Relational), e o MSBuild recusa-o logo no *restore*, antes de o teste correr. Usei antes `Messaging → Relational`, que não tem ciclo.
2. **O `sed` não inseriu linhas com `\` no *shell* do Windows**, duas vezes, sem dar erro. Numa dessas vezes, os testes «passaram» porque a mutação nem tinha sido aplicada. Passei a confirmar cada mutação (`grep`) antes de ler o resultado, e a usar o `perl`.
3. **Dois testes novos falharam à primeira, por erros nos próprios testes:**
   - `/health/live` aparecia numa métrica de *routing* (`aspnetcore_routing_match_attempts`), e não no histograma de latência. A asserção passou a olhar só para as linhas `http_server_request_duration_seconds`.
   - O `GetConnectionString()` do Testcontainers era avaliado quando a API arrancava, que já era depois de o Redis ter sido parado («*Exposed port 6379/tcp is not mapped*»). As *connection strings* passaram a ser capturadas logo após o arranque dos contentores.
4. **A mutação do ponto 4 não fez o teste falhar.** Primeiro tentei-a com um *switch* inválido (`-warnaserror-`): o *build* falhou e o teste correu com o binário antigo, por isso o resultado não valia. Repeti com `-p:TreatWarningsAsErrors=false`. Mesmo sem o `RedisConnectionWarmup`, o teste passa. A explicação: com o contentor parado, a porta recusa a ligação de imediato, por isso o `Connect` não espera o `ConnectTimeout`. O bloqueio descrito pelo revisor só acontece quando o destino não responde de todo (um nó inacessível, sem recusa), e os Testcontainers não reproduzem isso facilmente.
   - **Decisão:** manter o *warmup*, porque torna a ligação no arranque determinística e não tem custo, mas **não** o apresentar como provado por este teste. O teste continua a cobrir o arranque com o Redis em baixo e o caminho rápido.
   - **A considerar no cenário `failover` (RNF-03):** simular também uma partição de rede (por exemplo, `docker network disconnect`), e não só a paragem do contentor.

### Verificação (resultados reais)

- `dotnet build`: 0 erros e 0 avisos (`TreatWarningsAsErrors` ativo).
- `dotnet test`:
  - antes da revisão: **14/14** testes (9 de arquitetura e 5 de integração), em 17,1 s;
  - depois das correções: **31/31** (18 de arquitetura e 13 de integração), em 38,6 s.
- Pedidos manuais com a API ligada ao compose:
  - `/health/live` → 200;
  - `/health/ready` → 200, com os três motores *Healthy*;
  - `/openapi/v1.json` → 200;
  - `/swagger/index.html` → 200;
  - `/metrics` → contém `http_server_request_duration_seconds` e `target_info` com `service_name="smartparking-api"`.
- Prometheus (perfil `observability`): `up{job="smartparking-api"} = 1` e `up{job="smartparking-workers"} = 1`.

### Decisões tomadas

- ADR-0001: estrutura em camadas, uma subcamada por motor, CQRS sem mediador e o projeto ServiceDefaults.
- ADR-0002: OpenTelemetry com exportação Prometheus, CSV adiado para a Fase 9 e destino dos *traces* adiado; *check* próprio para o Redis.
- Nenhuma decisão «EM ABERTO» (D1–D8) foi alterada.
  - D2: o valor por omissão (Outbox e Redis Streams) dispensa um contentor de broker.
  - D5: sem Cosmos DB.

### Pontos a levar para a próxima fase

Notas da revisão para fases seguintes (ainda não implementadas):

- **Fase 2, um só *pool* Npgsql.** Usar `AddNpgsqlDataSource` (Npgsql.DependencyInjection) e `UseNpgsql(dataSource)` no EF Core, em vez de um segundo *pool*. Registar também a telemetria do Npgsql e do EF Core (fontes e medidores), para decompor a latência por motor.
- **Fases 2–3, onde fica a outbox.** A regra «subcamadas independentes» proíbe `Messaging → Relational`. Proposta: `IOutboxReader` e `IEventBus` (D2) no `Application`, implementados no Relational e no Messaging, com o *dispatcher* alojado nos Workers. **Tem de ser decidido num ADR antes de escrever a outbox.**
- **Fase 3, Redis Streams.** O StackExchange.Redis não suporta `XREADGROUP … BLOCK` no *multiplexer*, por isso os consumidores fazem *polling*. O intervalo de *polling* soma-se diretamente à latência ponta a ponta (D7). Tem de ser configurável e registado em cada experiência.
- **Fase 9:**
  - poder desligar os *traces* por configuração;
  - Workers com porta parametrizável e `file_sd_configs` no Prometheus, para correr 1, 2 e 4 instâncias (RNF-02);
  - `cpus` e `mem_limit` no compose, registados com os resultados;
  - o AOF do Redis tem de ser igual na variante monolítica e nos Testcontainers.
- Os *traces* ainda não têm exportador (ADR-0002, ponto 4).

---

## 14-11-2026 — Fase 2a: Domínio, EF Core e configuração (RF-06)

Âmbito: entidades da §3, `DbContext` e configurações EF Core, migração inicial, CRUD de RF-06, auditoria (RNF-06) e D1 atrás de `IParkingSessionRepository`. Os *endpoints* de UC-02 e a outbox ficam para a Fase 2b, porque a outbox exige primeiro o ADR sobre a sua localização (ver «Pontos a levar», Fase 1).

### Ambiente (diferenças em relação à Fase 1)

- EF Core 10.0.15 com `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 e `EFCore.NamingConventions` 10.0.1 (esquema em *snake_case*).
- `Npgsql.DependencyInjection` 10.0.3: um único *pool* partilhado pelo EF Core e pelo *health check*.
- `dotnet-ef` 10.0.15 num manifesto local (`dotnet-tools.json`). Executar `dotnet tool restore` antes de usar os comandos `dotnet ef`.

### O que foi feito

1. **Domain** (sem dependências): `ParkingFacility`, `ParkingSpot`, `Sensor`, `Tariff`, `User`, `Vehicle`, `ParkingSession`, `Payment` e `OccupancyEvent`.
   - O `OccupancyEvent` só existe no domínio, porque vai para o MongoDB (Fase 3).
   - *Value objects*: `GeoPoint`, `ValidityPeriod` e `TariffRules`.
   - Os invariantes estão nas fábricas e nos métodos `Update`, e cada violação lança `DomainValidationException`.
   - `[PersonalData]` marca os campos pessoais (RGPD).
   - As datas são normalizadas para UTC à entrada do domínio.
2. **Application:**
   - `IRepository<T>`, `IParkingSessionRepository` (D1), `IUnitOfWork` e `ICurrentActor`;
   - `IConfigurationQueries` (lado das consultas);
   - `FacilityCommands`, `SpotCommands`, `SensorCommands` e `TariffCommands` (lado dos comandos);
   - DTOs, incluindo `GeoJsonPoint`.
   - A camada continua **sem pacotes NuGet**.
3. **Infrastructure.Relational:**
   - `SmartParkingDbContext`, com uma `IEntityTypeConfiguration` por entidade;
   - `xmin` como token de concorrência; UUIDv7 gerado na aplicação; enums como texto;
   - `RESTRICT` em todas as FKs;
   - restrições `CHECK`: coordenadas, capacidade, validade do tarifário, custo e montante;
   - índices únicos: código do lugar por parque, um sensor por lugar, e-mail, matrícula e uma sessão aberta por lugar (índice parcial);
   - `AuditSaveChangesInterceptor` e a tabela `audit_log`;
   - migração `InitialCreate`.
4. **Api:**
   - `/api/facilities`, `/api/facilities/{id}/spots`, `/api/spots/{id}`, `/api/sensors` e `/api/tariffs`, com GET (lista e detalhe), POST, PUT e DELETE;
   - `ProblemDetailsExceptionHandler` faz o mapeamento dos erros: 400 com `errors.<campo>`, 404 e 409;
   - os enums viajam por nome.
5. **ServiceDefaults:** passa a recolher os *traces* do Npgsql e as métricas do Npgsql e do EF Core. São referidos pelo nome, e não por pacote, para o ServiceDefaults não depender de *drivers* (ADR-0001).
6. **Documentação:**
   - ADR-0003 (localização, modelo relacional e auditoria);
   - `RASTREABILIDADE.md` e comandos `dotnet ef` no `CLAUDE.md` atualizados.

### Problemas encontrados e tentativas falhadas

1. **Uma instância antiga da API bloqueava a compilação.**
   - O que aconteceu: o `dotnet run` das 16:11 (da verificação manual da Fase 1) continuava ativo e tinha as DLLs em `bin/` bloqueadas, o que deu erro MSB3027.
   - Como se resolveu: o processo foi parado com autorização do Diogo.
   - Lição: no fim de cada verificação manual, parar a API.
2. **Primeira tentativa de `dotnet ef migrations add` falhou:** «No suitable constructor was found for the type `Tariff.Rules#TariffRules`».
   - Causa: num *complex type* (aqui mapeado para JSON), o EF Core não mapeia por convenção propriedades só com `get`, por isso não consegue ligar os parâmetros do construtor.
   - No `GeoPoint` e no `ValidityPeriod` isto não aconteceu, porque as colunas já estavam configuradas explicitamente com `HasColumnName`.
   - Correção: declarar `rules.Property(...)` para cada propriedade.
3. **A migração gerada não compilava.** Dava o erro CA1861 (arrays constantes em `CreateIndex`), porque o `TreatWarningsAsErrors` está ativo.
   - Correção: um `.editorconfig` em `Migrations/` com `generated_code = true`.
   - Alternativa rejeitada: editar a migração à mão, o que impediria regenerá-la sem diferenças.
4. **Colunas `xmin` no `CreateTable`.** O código da migração declara `xmin` como coluna. Confirmou-se no `\d parking_facilities` que o gerador SQL do Npgsql a omite, porque é uma coluna de sistema. Nenhuma tabela ficou com uma coluna `xmin` física.
5. **Dois testes de arquitetura falharam** (as *fitness functions* da Fase 1 cumpriram o seu papel):
   - **`Microsoft.Extensions.DependencyInjection.Abstractions` no `Application`**, que tinha sido acrescentado para um `AddApplication()`.
     - Em vez de alargar a regra «Application sem pacotes», o registo dos *handlers* passou para a API (`Api/ApplicationRegistration.cs`).
     - Os Workers ainda não usam estes *handlers*.
   - **`EFCore.NamingConventions` no `Relational`.** A lista de permissões foi alargada, com justificação no próprio teste: é um *plugin* do EF Core para o mesmo motor.
6. **Um teste falhou por igualdade de *records*:** `FacilityDto` contém `IReadOnlyList<double>`, e a igualdade gerada compara listas por referência. Corrigido com `Assert.Equivalent`.
7. **A mensagem de validação dependia da cultura do sistema.** No teste manual, a resposta 400 dizia «between -85,05112878 and 85,05112878», com vírgula decimal (cultura pt-PT da máquina). Corrigido com `string.Create(CultureInfo.InvariantCulture, …)` no `GeoPoint`.
   - A considerar: um teste que corra com outra cultura.
8. **Versão da ferramenta:** o `dotnet-ef` global era o 11.0.0, com o EF Core 10. Para a migração ser reprodutível, fixou-se a 10.0.15 num manifesto local.
9. **Comando do pedido:** o `psql -U sp` não funciona. O utilizador no compose é `smartparking` (comando correto mais abaixo).

### Revisão da migração `InitialCreate`

A skill `ef-migration-reviewer` (dotnet-ai-toolkit 1.3.0) está instalada, mas o *plugin* não está ativo nesta sessão (`enabledPlugins: {}`). Por isso, a checklist do `SKILL.md` foi aplicada manualmente. Como essa checklist trata riscos de migrações sobre dados existentes, acrescentou-se uma revisão de índices, FKs e tipos segundo a `ef-index-advisor`.

**Checklist `ef-migration-reviewer`. Veredicto: segura.**
- Sem DROP, sem renomeações e sem mudanças de tipo no `Up()`. O `Down()` apaga as nove tabelas, como é esperado numa migração inicial.
- Não há `NOT NULL` sem valor por omissão sobre tabelas com dados, porque todas as tabelas são novas.
- Os `CREATE INDEX` correm sobre tabelas vazias, por isso não há bloqueios longos. Em migrações futuras sobre tabelas grandes (sobretudo `audit_log`), usar `CREATE INDEX CONCURRENTLY` em SQL manual.
- A migração tem `Down()`, portanto é reversível.
- 🟢 Junta mudanças «não relacionadas»: o esquema de sessões, pagamentos, utilizadores e veículos (para a 2b) entra com o de RF-06. É aceitável numa migração inicial.

**Índices, FKs e tipos:**
- Todas as colunas de FK têm índice: `parking_spots.facility_id` (primeira coluna do único `(facility_id, code)`), `sensors.spot_id` (único), `vehicles.user_id`, `parking_sessions.spot_id` (primeira coluna de `(spot_id, started_at)`), `vehicle_id`, `user_id`, `tariff_id` e `payments.session_id`.
- Não há índices redundantes. O índice único parcial `ux_parking_sessions_spot_id_open` e o índice `(spot_id, started_at)` servem predicados diferentes.
- Tipos:
  - `double precision` para as coordenadas;
  - `numeric(10,2)` para os montantes;
  - `timestamptz` para os instantes;
  - `jsonb` para as regras do tarifário e para `changes`;
  - `varchar` com limites alinhados com as constantes do domínio.
- **Achados não corrigidos (registados para decisão):**
  1. Os enums em texto não têm `CHECK` que restrinja os valores. Uma escrita feita fora da aplicação poderia gravar um estado inválido.
  2. O `audit_log` não está protegido contra `UPDATE`/`DELETE`. Para RNF-06, considerar revogar essas permissões ao utilizador da aplicação, ou um *trigger*.
  3. A listagem de lugares por parque ordena por `id` com um filtro em `facility_id`. O índice `(facility_id, code)` serve o filtro, mas a ordenação é feita em memória. Não tem impacto com cardinalidades de configuração (centenas de lugares por parque).
  4. Cosmético: o nome de uma FK gerada é `fk_parking_sessions_spots_spot_id` (usa o nome do `DbSet`, e não o da tabela).

### Verificação (resultados reais)

- `dotnet build`: compilação sem erros nem avisos (`TreatWarningsAsErrors`).
- `dotnet test` antes da revisão independente: **60/60** (eram 31 na Fase 1; mais 29), em 35,9 s:
  - `ConfigurationEndpointsTests`: 19;
  - `MigrationTests`: 3;
  - `AuditTests`: 4;
  - `ParkingSessionRepositoryTests`: 3.

  Antes das correções dos pontos 5 e 6: 57/60.
- `dotnet ef database update` (base de dados do compose): migração `20261114170205_InitialCreate` aplicada. O `\dt` mostra 10 relações: as 9 tabelas e `__ef_migrations_history`.
- Teste manual por HTTP contra o compose (API em `localhost:5080`, `/health/ready` com os três motores *Healthy*, `/swagger/index.html` → 200):
  - `POST /api/facilities` → 201;
  - 2× `POST /api/facilities/{id}/spots` → 201;
  - `POST /api/sensors` → 201;
  - um parque com latitude 86 → 400 com `errors["location.latitude"]`;
  - `audit_log` → **4 linhas** `Created` (ParkingFacility, 2× ParkingSpot, Sensor), ator `anonymous`;
  - `parking_facilities` → `latitude 40.2033`, `longitude -8.4103`.
- `TODO(Diogo):` repetir o teste manual no Swagger (passo 5 do fluxo) e confirmar com:
  `docker compose exec postgres psql -U smartparking -d smartparking -c "select entity_type, operation, actor, occurred_at from audit_log order by occurred_at"`

### Revisão independente (passo 6, 15-11-2026)

Relatório completo: `docs/revisoes/fase-2a-revisao-independente.md`. Foi feita por um agente separado, que só podia ler o código, e a sessão principal confirmou cada achado no código.

- **7 achados:** 0 altos, 2 médios e 5 baixos.
  - **1 (médio).** Um corpo ilegível dava **400 vazio fora do ambiente Development**, porque o `ThrowOnBadRequest` só está ativo por omissão em Development. O teste do enum inválido só verificava o código de estado.
    - Corrigido com `ThrowOnBadRequest = true`.
    - Prova de que os testes novos detetam o problema: com a correção desligada, os 4 testes falharam; com ela, passaram.
  - **2 (médio).** Erros do PostgreSQL que chegavam ao cliente como 500.
    - Instantes abaixo do microssegundo violavam `ck_tariffs_validity`; o carácter NUL no texto era rejeitado pela base de dados.
    - Corrigido: truncagem ao microssegundo em `Guard.Utc`, rejeição de caracteres de controlo, e tradução de `CheckViolation` e dos erros da classe 22 em 400.
  - **3.** Campos obrigatórios em falta recebiam valores por omissão sem aviso. Um tarifário sem `validFrom` ficava válido desde `-infinity`.
    - Corrigido com `RespectRequiredConstructorParameters`; os campos opcionais (`validTo`, `dailyCap`) passam a ter valor por omissão.
  - **4.** O D1 não é «só uma nova implementação»: o contrato depende do *change tracking* do EF.
    - Documentado no ADR-0003 (ponto 5), sem alterar o contrato. `TODO(Diogo)`.
  - **5.** `numeric(10,2)` versus domínio: adiado para a Fase 2b.
  - **6.** Testes:
    - o teste de migração que não podia falhar foi substituído por um que usa uma base de dados nova e testa `Up()` e `Down()`;
    - acrescentou-se a auditoria de propriedades compostas e `jsonb`;
    - a listagem passou a percorrer todas as páginas.
  - **7.** O texto dizia que os UUIDv7 seguiam a ordem de criação, mas isso só vale ao milissegundo. O ADR e o comentário foram corrigidos.
- **Falha minha encontrada ao escrever o relatório.** Dois testes de auditoria ordenavam as linhas por `Id`. Pelo achado 7, isso não garante a ordem entre linhas criadas no mesmo milissegundo. Passaram a ordenar por `OccurredAt`.
- **Efeito secundário da revisão.** O revisor arrancou por engano a API com o perfil de Development, ligada ao compose, durante cerca de 30 s. Não houve pedidos nem escritas.
  - Confirmou-se que não ficou nenhum processo a correr e que nenhum ficheiro foi alterado.
  - Lição: pedir `--no-launch-profile` ao revisor.
- **Depois das correções:** `dotnet build` sem erros nem avisos; `dotnet test` **67/67**, em 41,3 s.
  - Os testes novos da Fase 2a são 36: `ConfigurationEndpointsTests` 25, `AuditTests` 5, `MigrationTests` 3, `ParkingSessionRepositoryTests` 3.

### Decisões tomadas

- **ADR-0003:**
  - P1: localização em duas colunas e GeoJSON na API;
  - P2: regras do tarifário em `jsonb` tipado, com um modelo provisório;
  - P3: eliminação física com `RESTRICT`;
  - P4: um sensor por lugar;
  - `xmin`, UUIDv7 e auditoria por *interceptor*.
- **D1:** valor por omissão (relacional), atrás de `IParkingSessionRepository`. Nenhuma outra decisão «EM ABERTO» foi tocada.
- **Desvios ao plano aprovado:**
  - Um *handler* por recurso (com criar, alterar e eliminar), em vez de uma classe por operação. A separação entre comandos e consultas mantém-se, e evitam-se 12 classes quase vazias.
  - Sem `AddValidation`/DataAnnotations: o domínio é a única fonte das regras de validação, e as violações chegam como 400 através do *handler* de exceções. Assim não há regras duplicadas que possam divergir.
  - Sem o *value object* `Money`: a moeda é única (EUR), e os montantes são `decimal` com `numeric(10,2)`.
  - Sem o pacote `Npgsql.OpenTelemetry`: o Npgsql emite *traces* e métricas nativamente. Basta registar a fonte `Npgsql` pelo nome.

### Pontos a levar para a próxima fase

- **Fase 2b:**
  - ADR sobre a localização da outbox (`IOutboxReader` e `IEventBus` no `Application`), antes de escrever código;
  - *endpoints* de UC-02, com a outbox na mesma transação;
  - emitir eventos de configuração (RF-06, §4) para o *read model*;
  - usar `ParkingSession.Close` com o tarifário em vigor (`ValidityPeriod.Contains`).
- **TODO(Diogo):**
  - modelo definitivo das regras de tarifário (Cap. 3);
  - lista de `VehicleType`;
  - decidir se o `audit_log` deve ser protegido contra alterações (achado 2 da revisão).
- **Montantes (revisão, achado 5):** arredondar e limitar no domínio antes de `numeric(10,2)` (custo da sessão e valor do pagamento), quando a Fase 2b passar a escrevê-los.
- **Tarifários:** não há regra contra períodos de validade sobrepostos para a mesma zona. Pode ser preciso uma restrição de exclusão (`EXCLUDE USING gist`, extensão `btree_gist`) quando se definir o cálculo.
- **Auditoria em caso de falha:** se um `SaveChanges` falhar, as entradas de auditoria ficam no *change tracker* desse contexto. Hoje isto não tem efeito, porque o contexto é por pedido e não há novas tentativas. Se a Fase 2b introduzir uma estratégia de novas tentativas (`EnableRetryOnFailure`), as entradas têm de ser recriadas em cada tentativa.

## 15-11-2026 — Fase 2b: UC-02 (sessões e pagamento) com outbox transacional

Âmbito: `POST /api/sessions`, `/{id}/close` e `/{id}/pay`; em cada comando, a alteração e um evento (`SessionStarted`, `SessionClosed`, `PaymentRegistered`) gravados na mesma transação EF Core, na tabela `outbox_messages`; concorrência otimista com 409. O *worker* que publica a outbox fica para a Fase 5.

### Plano e decisões do Diogo (antes do código)

- O plano foi aprovado em modo de planeamento. O Diogo respondeu a três perguntas:
  1. **Utilizadores e veículos:** não havia forma de os criar pela API (o RF-06 só cobre parques, lugares, sensores e tarifários). Escolha: *endpoints* mínimos `POST /api/users` e `POST /api/users/{id}/vehicles`, sem eventos, justificados como pré-condição do UC-02.
  2. **Custo da sessão:** usar as três regras do tarifário provisório da Fase 2a (preço por hora, minutos grátis e teto diário), e não só o preço por hora. As regras **não vêm da especificação**: ficam atrás de `ITariffPricing`, com `TODO(Diogo)`.
  3. **Revisão de desempenho de BD:** o agente `db-performance-auditor` pedido no guião não existe no projeto nem em `~/.claude/agents`. Usou-se um subagente genérico com um *brief* de auditoria de BD.
- **Numeração dos ADR:** o guião pedia «ADR 0002» (outbox) e «ADR 0003» (D1), mas esses números já estavam ocupados (observabilidade e modelo relacional). Ficaram **ADR-0004** e **ADR-0005**.
- As skills do dotnet-ai-toolkit não estavam disponíveis nesta sessão; seguiram-se os padrões da Fase 2a.

### O que foi feito

1. **Domain:**
   - `IDomainEvent` e `IHasDomainEvents` (os eventos são expostos por um método, para o EF Core não tentar mapeá-los);
   - `ParkingSession` levanta `SessionStarted`, `SessionClosed` e `PaymentRegistered`. O pagamento passa a ser criado por `ParkingSession.Pay(method, paidAt)` (o `Payment.Register` ficou `internal`), que verifica o estado e usa o custo da sessão como montante;
   - `InvalidStateTransitionException` (fechar duas vezes, pagar uma sessão aberta) → **409**, e não 400 como na Fase 2a: é um conflito com o estado guardado, não um pedido mal formado;
   - `Money.Normalise`: arredonda a cêntimos (metade para longe de zero) e rejeita valores negativos ou ≥ 10⁸ (achado 5 da revisão da Fase 2a);
   - `ITariffPricing` e `ProvisionalTariffPricing` (minutos iniciados; minutos grátis no início; preço por hora, ao minuto; teto por bloco de 24 h).
2. **Application:** `SessionCommands` (*handler* próprio, sem MediatR), `ISessionQueries`, `ITariffLookup`, `NoTariffInForceException` (409) e `UserCommands`. O custo é calculado com o tarifário em vigor **no início** da sessão (`TODO(Diogo)`: confirmar no Cap. 3).
3. **Infrastructure.Relational:**
   - `OutboxMessage` e a tabela `outbox_messages` (`id`, `type`, `aggregate_id`, `payload jsonb`, `occurred_at`, `processed_at` nula), com o índice parcial `ix_outbox_messages_unprocessed`;
   - `OutboxSaveChangesInterceptor`, registado antes do de auditoria; a outbox não é auditada;
   - `EfTariffLookup` (lugar → zona do parque → tarifário, numa só consulta) e `EfSessionQueries`;
   - migração `AddOutbox` (só a tabela e o índice).
4. **Api:** `SessionEndpoints` (sessões e utilizadores); o `ProblemDetailsExceptionHandler` mapeia as duas exceções novas para 409.
5. **Testes:** `SessionEndpointsTests` (9), `OutboxAtomicityTests` (2) e `SessionDomainTests` (12 casos). O `MigrationTests` passa a esperar `outbox_messages`. Novos *helpers* no `RelationalFixture`: zona única por teste, tarifário, utilizador com veículo.
6. **Documentação:** ADR-0004 (outbox em vez de 2PC), ADR-0005 (D1, com a contradição Cap. 3 vs Cap. 4.4.2/4.5) e `RASTREABILIDADE.md`.

### Como se testou a atomicidade («falha a meio»)

- Um *trigger* `BEFORE INSERT` em `outbox_messages`, criado pelo próprio teste e só para o `aggregate_id` dessa sessão, faz `RAISE EXCEPTION`. O fecho devolve 500. Depois disso, a sessão continua `Open`, só existe o evento `SessionStarted` e não há linha de auditoria do fecho. A seguir, sem o *trigger*, o fecho funciona: nada ficou meio gravado nem bloqueado.
- Teste espelho: o mesmo *trigger* no `UPDATE` de `parking_sessions`.
- Porquê dois testes: o EF Core envia o `UPDATE` da sessão e os `INSERT` da outbox e da auditoria num só lote, e a ordem dos comandos no lote não é garantida. Com uma falha de cada lado, o resultado não depende dessa ordem.
- Porquê um *trigger*: a falha acontece **dentro** da transação da base de dados, depois de parte das instruções do lote já ter sido executada. Um *interceptor* que lançasse uma exceção antes de enviar o lote não provaria a atomicidade, porque nada chegaria a ser escrito.

### Problemas encontrados e tentativas falhadas

1. **Heredocs falhados no Git Bash (duas vezes).** Comandos que escreviam ficheiros com heredocs falharam com «unexpected EOF while looking for matching `'`» quando o texto tinha plicas ou crases. Nada foi escrito (erro de *parsing*); os ficheiros foram escritos com as ferramentas de edição.
2. **Regra CA1716 com `TreatWarningsAsErrors`.** Os parâmetros `end` e, depois, `to` de `ITariffPricing.Price` colidem com palavras reservadas do VB (`End`, `To`). Ficaram `startedAt` e `endedAt`.
3. **De novo, uma instância antiga da API bloqueava a compilação** (`SmartParking.Api.exe`, iniciada às 13:18, erro MSB3027). Foi parada com autorização do Diogo. É a segunda vez: a lição da Fase 2a («parar a API no fim da verificação manual») não chegou. `TODO(Diogo):` parar sempre a API ao terminar a verificação manual.
4. **Colisão de *namespace* nos testes.** A pasta nova `tests/.../Domain/` criou o *namespace* `SmartParking.Api.IntegrationTests.Domain`, que passou a esconder `SmartParking.Domain` nos testes da Fase 2a (`Domain.Tariffs.Tariff` deixou de resolver). Foi renomeada para `DomainModel`.
5. **Comando do guião:** tal como na Fase 2a, `psql -U sp` falha («role "sp" does not exist»). O utilizador do compose é `smartparking`.
6. **Os *commits* intermédios não compilam todos sozinhos.** O *commit* da subcamada relacional usa `ITariffLookup`, que só entra no *commit* seguinte (Application/Api). O ramo compila no fim, mas um `git bisect` teria de saltar esse *commit*. Nota para as próximas fases: ordenar os *commits* por dependência (Application antes de Infrastructure).

### Verificação (resultados reais)

- `dotnet build`: 0 erros, 0 avisos.
- `dotnet test`: **90/90** em 37,2 s (Api.IntegrationTests e ArchitectureTests). Os testes novos passaram à primeira execução. O facto de os testes de atomicidade receberem 500 confirma que o *trigger* disparou.
- `dotnet ef migrations add AddOutbox` → migração só com `outbox_messages` e o índice parcial (nenhuma alteração inesperada ao resto do esquema).
- `dotnet ef database update` (base de dados do compose): `20261115145043_AddOutbox` aplicada. O `\d outbox_messages` confirma as seis colunas, `processed_at` nula e o índice `ix_outbox_messages_unprocessed ... WHERE processed_at IS NULL`; 0 linhas.
- **Teste manual no Swagger (passo 5).** Desta vez, a pedido do Diogo, foi feito pela sessão, no *browser* integrado, contra o compose (API em `localhost:5080`, `/health/ready` com os três motores *Healthy*):
  1. dados prévios por HTTP (`curl`): tarifário da zona `Centro` (1,20 €/h, sem minutos grátis nem teto), parque «Parque Swagger 2b» na zona `Centro`, lugar `A-01`, utilizador e veículo → todos 201;
  2. no Swagger UI («Try it out»): `POST /api/sessions` → **201**, `status: Open`; `POST /api/sessions/{id}/close` com `{}` → **200**, `status: Closed`, `cost: 0.02` (18 s → 1 minuto iniciado × 1,20 €/60); `POST /api/sessions/{id}/pay` com `{"method":"Card"}` → **201**, `amount: 0.02`, `status: Completed`;
  3. `psql`:

     ```
            type        |          occurred_at          | processed_at
     -------------------+-------------------------------+--------------
      SessionStarted    | 2026-11-15 15:24:27.558158+00 |
      SessionClosed     | 2026-11-15 15:24:45.933095+00 |
      PaymentRegistered | 2026-11-15 15:25:01.136866+00 |
     (3 rows)
     ```

     Os *payloads* só têm identificadores, instantes e montantes (nem utilizador, nem veículo). O `audit_log` tem 4 linhas para esta sessão: `ParkingSession` Created, Updated, Updated e `Payment` Created; nenhuma da outbox.
  4. A API foi parada no fim.
- **Observações do teste manual:**
  - o exemplo gerado pelo Swagger para o `pay` traz `"version": 0`. Enviado tal como está, dá 409 (versão desatualizada), o que pode confundir quem testa. Retirou-se o campo. `TODO(Diogo):` decidir se se documenta o campo como opcional no OpenAPI ou se fica assim;
  - o exemplo do `close` traz o corpo `null`, que também é aceite;
  - o *payload* inclui `aggregateId`, que repete `sessionId` (a propriedade calculada de `IDomainEvent` é serializada). É inofensivo, mas redundante; pode ser retirado na Fase 5, quando o formato dos eventos for fixado com o consumidor.

### Revisão de desempenho de BD (passo 6)

Relatório completo: `docs/revisoes/fase-2b-revisao-desempenho-bd.md`. Foi feita por um subagente separado (o «db-performance-auditor» não existia; ver o plano), só de leitura sobre o código. Para obter o SQL real, o revisor correu Start, Close e Pay num contentor PostgreSQL descartável (removido no fim); na base de dados do compose só fez `EXPLAIN`. A sessão principal confirmou que nenhum ficheiro de código foi alterado, que a base de dados do compose continua sem sessões nem eventos e que `max_connections` = 100 sem `Maximum Pool Size` na *connection string*.

- **Nenhum achado alto.** Nenhum foi corrigido nesta fase; todos ficam registados com a fase em que devem ser tratados.
  1. *Pool* de ligações (média): a API e os Workers podem abrir até 100 ligações cada, contra `max_connections` = 100. É só configuração. `TODO(Diogo):` decidir se se corrige já ou antes da Fase 9.
  2. Outbox (média): crescimento sem limite e `UPDATE processed_at` sem atualização HOT → Fase 5 (e purga na Fase 10). Aviso para o *poller*: não usar um cursor `occurred_at > último`, porque perderia linhas confirmadas mais tarde com um `occurred_at` anterior.
  3. Idas à base de dados por comando: Start 4, Close 4, Pay 3 (as escritas vão num único lote e numa transação). Duas são evitáveis → Fase 9, só se a medição o justificar.
  4. `EfTariffLookup` ordena antes do `LIMIT 1`; um índice `(zone, valid_from DESC)` ou uma cache resolveriam → Fase 9.
  5. As atualizações de `parking_sessions` nunca são HOT (`tariff_id` tem índice; `status` está no predicado do índice parcial) → documentar no Cap. 6.4.
  6. `DetectChanges` várias vezes por `SaveChanges` → sem correção (1 a 3 entidades por pedido).
  7. `AddDbContextPool` → experiência opcional na Fase 9 (deixa de ser compatível quando `ICurrentActor` for por pedido).
- **Verificado sem problemas** pelo revisor: `FindAsync`/`AnyAsync` pela chave primária; dois Start simultâneos no mesmo lugar dão 409 pelo índice único parcial; num conflito de `xmin` no Pay, o `INSERT` do pagamento é revertido no mesmo lote (não fica pagamento órfão).

### Decisões tomadas

- **ADR-0004:** outbox transacional na mesma base de dados, escrita por um *interceptor* a partir de eventos de domínio; sem 2PC, sem *dual write* e sem CDC.
- **ADR-0005:** D1 mantém o valor por omissão (relacional); a contradição entre os Caps. 3 e 4 fica assinalada.
- 409 para transições de estado inválidas e para «sem tarifário em vigor».
- `version` opcional no `close` e no `pay`: a concorrência é sempre verificada pelo `xmin`; a versão no corpo só serve para rejeitar logo uma vista desatualizada do cliente.
- Eventos sem dados do utilizador nem do veículo (minimização, RNF-06).
- Os eventos de configuração (RF-06) **não** foram emitidos: o mecanismo existe, mas não há consumidor antes da Fase 4, e emiti-los agora poria mais de 3 linhas na outbox do teste manual.

### Pontos a levar para a próxima fase

- **Fase 5:** *worker* da outbox (`SELECT ... WHERE processed_at IS NULL ORDER BY occurred_at ... FOR UPDATE SKIP LOCKED`), publicação no `IEventBus` (D2), idempotência pelo `id` e medição da janela `occurred_at` → atualização do *read model*.
- **Fase 4/5:** eventos de configuração (RF-06), quando o *read model* precisar deles.
- **Fase 10:** purga ou arquivo das linhas processadas da outbox (RNF-05).
- **`TODO(Diogo)`:** modelo definitivo do tarifário e regra de cálculo (início da sessão vs. repartição por períodos); referência bibliográfica do padrão outbox para o Cap. 4.5; decisão D1 com o orientador.

## 15-11-2026 — Fase 3: ingestão (UC-01) até ao MongoDB

Âmbito: `POST /api/ingest/occupancy` (lotes, validação RF-01, `202`), `IEventBus` em Redis Streams (D2), consumidor nos Workers que grava num log append-only no MongoDB (D3), sem duplicados. O Redis como *read model* (passo 4 do UC-01) fica para a Fase 4.

### Plano e decisões (antes do código)

- Plano aprovado em modo de planeamento, sem alterações.
- **Onde fica a idempotência:** a garantia final é o índice único em `eventId` no MongoDB, porque o *bus* entrega pelo menos uma vez. A API só remove duplicados dentro do mesmo lote; não filtra entre pedidos (seria uma ida ao Redis ou ao MongoDB por evento, e não dispensaria o índice).
- **Lote com um evento inválido:** a especificação (§4) não define o comportamento. Valor por omissão: aceitação parcial (`202` com a lista `rejected`, por índice); `400` só quando nenhum evento é válido. A alternativa fica numa opção (`Ingestion:RejectWholeBatchOnInvalid`). `TODO(Diogo):` confirmar com o orientador.
- **Numeração dos ADR:** o guião pedia «ADR 0004» (D2) e «ADR 0005» (D3), mas esses números já estavam ocupados desde a Fase 2b (outbox e D1). Ficaram **ADR-0006** e **ADR-0007**.
- **Nomes escolhidos:** stream `smartparking:occupancy-events`, grupo `occupancy-log`, *dead letter* `smartparking:occupancy-events:dead`, coleção `occupancy_events` na base de dados `smartparking`.
- A skill `async-await-auditor` está instalada no dotnet-ai-toolkit, mas não estava ativa nesta sessão. A revisão foi feita por um subagente separado que leu o `SKILL.md` e o aplicou.

### O que foi feito

1. **Application:**
   - `IEventBus` e `IMessageBatchHandler` (o *bus* só confirma um lote depois de o *handler* retornar; as mensagens impossíveis de processar voltam como *poison*); `IOccupancyEventLog`; `EventBusUnavailableException` (→ 503);
   - `OccupancyEventValidator` (RF-01): trabalha sobre o `JsonElement` de cada evento, para que um `eventId` mal formado seja reportado pelo índice e não torne o pedido inteiro ilegível. `timestamp` sem fuso é rejeitado (seria interpretado na hora local do servidor);
   - `IngestOccupancyBatchHandler`, `OccupancyEventMessage` (formato no *bus*, com o `payload` em JSON bruto e `receivedAt`), `OccupancyLogWriter` (consumidor) e `OccupancyIngestionMetrics`.
2. **Infrastructure.Messaging:** `RedisStreamsEventBus` (um `XADD` por evento, em *pipeline*) e `RedisStreamConsumer<THandler>` (`BackgroundService`): cria o grupo, relê as suas pendentes antes das novas, `XACK` depois do *handler*, `XAUTOCLAIM` periódico, *dead letter* para *poison*, *back-off* exponencial até 5 s. O `Infrastructure.Cache` passou a registar o `IConnectionMultiplexer` com `TryAdd`, para partilhar a mesma ligação.
3. **Infrastructure.Documents:** `MongoOccupancyEventLog` (só `insertMany` não ordenado; chave duplicada conta como «já gravado»; erros de documento são devolvidos como recusados); índices `ux_eventId` (único) e `ix_facilityId_timestamp`; `timestamp`, `receivedAt` e `storedAt` como BSON Date; GUID como UUID padrão (subtipo 4).
4. **Api e Workers:** `IngestionEndpoints`; o `ProblemDetailsExceptionHandler` mapeia `EventBusUnavailableException` para 503; os Workers registam o consumidor `occupancy-log`.
5. **Testes (26 novos):** `IngestionEndpointTests` (8 casos), `OccupancyEventValidatorTests` (12), `StreamConsumerRecoveryTests` (4) e `OccupancyEventLogIndexTests` (2), com um `IngestionFixture` próprio (Redis e MongoDB; o PostgreSQL não é arrancado porque a ingestão não lhe toca).
6. **Documentação:** ADR-0006, ADR-0007 e `RASTREABILIDADE.md`.

### Como se testou «parado antes do XACK»

- Um *handler* de teste (`StopBeforeAckHandler`) chama o `OccupancyLogWriter` real (o documento fica no MongoDB), sinaliza e fica à espera até ser cancelado. O teste confirma 1 documento e `XPENDING` = 1, e para o *host* (`StopAsync`): o *handler* é cancelado e o `XACK` nunca é enviado; `XPENDING` continua 1.
- Arranca-se um consumidor normal **com o mesmo nome**. Ao arrancar, lê as suas pendentes (`XREADGROUP … 0`), volta a gravar (chave duplicada, sem efeito) e confirma: `XPENDING` = 0 e continua a haver 1 documento.
- Variante: o consumidor que ficou com a entrada nunca volta, e outro consumidor reclama-a com `XAUTOCLAIM` (intervalos curtos no teste).
- Cada teste de recuperação usa um stream próprio, para que as pendentes de um teste não interfiram nos outros.

### Problemas encontrados e tentativas falhadas

1. **Heredocs no Git Bash, outra vez.** Um comando que escrevia dois ficheiros C# com heredocs falhou com «unexpected EOF while looking for matching `'`» (o texto tinha plicas dentro de expressões regulares e mensagens). Nada foi escrito; os ficheiros foram escritos com as ferramentas de edição. Terceira fase seguida com este problema: para código, não usar heredocs.
2. **`LoggerMessage.Define` sem o argumento da exceção** (CS7036 em duas chamadas) e **CA1001** (`SemaphoreSlim` num tipo não descartável); corrigidos (passar `null`; `MongoOccupancyEventLog` passou a `IDisposable`).
3. **Ambiguidade nos testes:** `SmartParking.Infrastructure.Documents.DependencyInjection` e `SmartParking.Infrastructure.Messaging.DependencyInjection` com o mesmo nome; resolvido com *aliases*, como já se fazia no `ApiFixture`.
4. **`IHost` não é `IAsyncDisposable`**, por isso `await using` não compilava. Criou-se `RunningConsumer`, que faz `StopAsync` (paragem ordenada) antes de `Dispose`.
5. **Tipos numéricos no `payload` (falha de teste com interesse para a tese).** O teste enviava `"noise": [41.2, 43.0]` e esperava dois `Double`. O `System.Text.Json` escreveu `43.0` como `43`, e o MongoDB gravou-o como **Int32**. O tipo BSON segue o texto JSON que chega. O teste passou a fixar este comportamento. Consequência registada no ADR-0007: quem agregar campos do `payload` (Fases 6 e 7) não pode assumir um único tipo numérico.
6. **Desvio ao plano: *dead letter* depois de N entregas.** O plano previa mandar para a *dead letter* as mensagens com mais de 5 entregas. Ao escrever o ciclo, percebeu-se que, com o MongoDB em baixo, **todas** as mensagens ultrapassariam esse número e seriam desviadas: uma perda de eventos no cenário `failover` (RNF-03). Ficou só a *dead letter* para o que o *handler* classifica como impossível de processar (JSON inválido, documento recusado pelo MongoDB). Justificação no ADR-0006.
7. **Desvio ao plano: criação dos índices.** O plano previa um `IHostedService` no arranque. Ficou uma criação preguiçosa antes da primeira gravação (com `SemaphoreSlim`): se o MongoDB estiver em baixo no arranque, os Workers arrancam na mesma, e nenhuma gravação acontece sem o índice único.
8. **Comando do guião:** `mongosh smartparking` sem credenciais falha com «Command aggregate requires authentication». O compose cria o utilizador `smartparking` na base de dados `admin`; é preciso `-u smartparking -p smartparking --authenticationDatabase admin`.
9. **O ADR-0007 tinha um exemplo errado** (ordenação de «…T9…» vs. «…T10…»): no ISO 8601 a hora tem sempre dois dígitos. Foi substituído pelo problema real dos *timestamps* como texto (fusos e precisões diferentes ordenam mal).

### Verificação (resultados reais)

- `dotnet build`: sem erros nem avisos (`TreatWarningsAsErrors`).
- `dotnet test` (solução inteira): **116 testes, 0 falhados** (eram 90 no fim da Fase 2b; mais 26 da Fase 3). Os 26 testes de ingestão foram repetidos mais 3 vezes, sem falhas (cerca de 11 s por execução).
- Teste manual com o compose, a API (porta 5080) e os Workers (porta 5090), feito pelo Claude Code a 15-11-2026 (`TODO(Diogo):` repetir):

  | passo | resultado |
  |---|---|
  | 1.º `curl` | `202` `{"accepted":1,"duplicatesInBatch":0,"rejected":[]}` |
  | `XLEN smartparking:occupancy-events` | 1 |
  | `db.occupancy_events.countDocuments()` | 1 |
  | 2.º `curl` (igual) | `202` com o mesmo corpo |
  | `XLEN` | **2** (a API volta a publicar: não filtra entre pedidos) |
  | `countDocuments()` | **1** (o índice único absorveu a repetição) |
  | `XPENDING … occupancy-log` | 0 |

  Documento gravado: `eventId: UUID('11111111-…')`, `timestamp: ISODate('2026-11-14T10:00:00.000Z')`, `payload: {}`, `receivedAt` e `storedAt` com 148 ms de diferença (inclui a criação dos índices na primeira gravação). Métricas em `/metrics`: API `smartparking_ingest_events_total{outcome="accepted"} 2`; Workers `smartparking_occupancy_log_events_total{outcome="inserted"} 1` e `{outcome="duplicate"} 1`, `smartparking_bus_consumer_messages_total{outcome="acked"} 2`.
- A API e os Workers foram parados no fim (lição das Fases 2a e 2b).
- Cada um dos 5 *commits* de código compila sozinho (verificado num *worktree* temporário, *commit* a *commit*): os *commits* foram ordenados por dependência (Application → Messaging → Documents → Api/Workers → testes), como ficou combinado na Fase 2b.

### Revisão de código assíncrono (passo 5)

Relatório completo: `docs/revisoes/fase-3-revisao-async.md`. Foi feita por um subagente separado, que aplicou o método do `SKILL.md` do `async-await-auditor` (dotnet-ai-toolkit 1.3.0), sem editar `src/` nem `tests/`. Usou um contentor Redis descartável e um programa de teste fora do repositório para medir o comportamento real.

- **Nenhum achado alto.** O revisor não encontrou nenhum caminho com `XACK` antes da escrita, perda de mensagem, ciclo sem espera, paragem pendurada ou *deadlock*.
- **Achados** (nenhum corrigido ainda; `TODO(Diogo):` decidir quais se corrigem nesta fase):
  1. **Média.** Há uma janela de perda num *crash* abrupto **dos motores**, não do processo .NET. O Redis usa `appendfsync everysec`, por isso o `202` pode sair até cerca de 1 s antes de o evento estar em disco. O MongoDB é *standalone* com `w:1` sem `j:true`, e o `XACK` segue logo a seguir. A afirmação «sem perda» (RNF-03) só vale para falhas do processo e paragens ordenadas. Já ficou documentado no ADR-0006 e no ADR-0007; a medição com `appendfsync always` / `j:true` fica para a Fase 9.
  2. **Baixa.** Com o Redis em baixo, a primeira resposta de `/health/live` dos Workers demorou 8,8 s (0,9 s com o Redis disponível). A causa é o `ConnectionMultiplexer.Connect` síncrono, chamado também pelo `RedisConnectionWarmup` da Fase 1. O comentário do `Task.Yield` no consumidor promete mais do que o código faz.
  3. **Baixa.** Um erro de documento com a categoria `ExecutionTimeout` é transitório, mas hoje vai para a *dead letter*.
  4. **Baixa.** Uma exceção determinística do *handler* bloqueia o grupo, e só fica um aviso no *log*. Proposta: uma métrica de falhas ou de contagem de entregas. O revisor confirmou que o Redis 7.4 incrementa essa contagem ao reler com `XREADGROUP 0`.
  5. **Baixa.** Falta `ConfigureAwait(false)` em `IngestOccupancyBatchHandler` e no código anterior de `Application` e `Relational`. Proposta: ativar a regra CA2007 só nas bibliotecas.
  6. **Baixa.** Quando o pedido é cancelado, o `WaitAsync(ct)` abandona as tarefas `XADD`, e uma falha posterior fica por observar.
  7. a 10. **Informativos:** o `XAUTOCLAIM` percorre a PEL inteira sem consultar o *token*; as cópias para a *dead letter* são em série e há um `List.Contains` O(n²); o histograma de *lag* inclui duplicados e o contador `poison` pode contar a dobrar; um `MaxBackoff` sem limite superior pode fazer `Task.Delay` lançar fora do `catch`.
- **Verificado sem problemas** pelo revisor: o `XACK` só depois da escrita; as entradas de um `XREADGROUP >` cuja resposta se perdeu são relidas; o cancelamento na paragem sai sem `XACK`; a paragem fica limitada a cerca de 5 s (`AsyncTimeout`); o *back-off* vai de 200 ms a 5 s; o duplo teste do `SemaphoreSlim` está correto; o âmbito de DI de cada lote é sempre libertado.

### Pontos a levar para a próxima fase

- **Fase 4:** segundo grupo (`availability`) no mesmo stream; o *read model* tem de comparar `timestamp`, porque com vários consumidores a ordem entre eventos do mesmo lugar não é garantida (ADR-0006).
- **Fase 9:** custo do *polling* de 100 ms na D7; `XREADGROUP BLOCK` com uma ligação dedicada; `appendfsync` e `j:true` (achado 1); *pool* Npgsql (memória da Fase 2b).
- **Fase 10:** aparar o stream e a *dead letter*; `_id` = `eventId` e *time series collections* como variantes de armazenamento (ADR-0007).
- **`TODO(Diogo)`:** comportamento de um lote com inválidos; Redis Streams vs. Kafka (justificação qualitativa ou medição); referências do Cap. 4 para Redis Streams, Kafka e *Event Store*; repetir o teste manual.
