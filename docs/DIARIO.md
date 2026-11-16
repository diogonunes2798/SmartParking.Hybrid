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
- Teste manual com o compose, a API (porta 5080) e os Workers (porta 5090), feito pelo Claude Code a 15-11-2026 (a pedido do Diogo, «só desta vez», também a segunda verificação abaixo):

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
- **Segunda verificação, antes do PR** (pedida pelo Diogo, feita pelo Claude Code): `dotnet build` sem erros nem avisos; `dotnet test` com **116/116** (39 s). Teste manual com um `eventId` novo, porque o `1111…` já estava gravado:

  | passo | `XLEN` | documentos | pendentes |
  |---|---|---|---|
  | antes | 2 | 1 | 0 |
  | `curl` com `eventId` `2222…` e `payload {"temperature":19.5}` → `202` | 3 | 2 | 0 |
  | o mesmo `curl` → `202` | 4 | **2** | 0 |
  | lote misto (um válido `3333…`, um inválido `4444…`) → `202`, `rejected` com `index: 1` e os 5 erros | 5 | 3 | 0 |
  | lote só com `{"eventId":"abc"}` → `400` com `rejected` | 5 | 3 | 0 |

  Consulta por intervalo no `mongosh` (`facilityId` + `timestamp` entre 10:00 e 11:00 UTC, ordenada por `timestamp`): os 3 eventos por ordem, com `timestamp` como `ISODate`. A API e os Workers foram parados no fim.
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

### Correção dos achados da revisão (antes de integrar o PR)

O Diogo pediu para resolver os achados baixos antes de integrar o PR. Foram corrigidos os cinco achados baixos (2 a 6) e três informativos de uma linha (7, 9 e 10). O 8 ficou só com o `HashSet` (as cópias para a *dead letter* continuam em série). O achado 1 (médio) continua documentado nos ADR e é medido na Fase 9.

1. **Achado 2 — ligação ao Redis sem bloquear o arranque.** O `Infrastructure.Cache` e o `Infrastructure.Messaging` passaram a abrir a ligação com `ConnectAsync`, numa tarefa partilhada que os respetivos *warm-ups* iniciam **sem** o *host* esperar por ela (`RedisCacheConnection`, `RedisBusConnection`). O `RedisHealthCheck` responde «Connecting.» enquanto a primeira tentativa não termina, por isso nem o arranque nem a sonda esperam. Isto altera uma decisão da Fase 1 (o `RedisConnectionWarmup` esperava pela ligação) sem perder o que ela protegia: a sonda continua a responder dentro do seu *timeout*.
   - **Consequência:** cada subcamada tem a sua ligação ao Redis (duas por processo). Antes, o Messaging e o Cache partilhavam o `IConnectionMultiplexer` por `TryAdd`, um acoplamento implícito entre subcamadas que o ADR-0001 não prevê. Assim, o *polling* do consumidor também não fica na mesma fila que os comandos do *read model* (Fase 4).
   - **Teste:** `RedisUnreachableTests` aponta o Redis para um endereço que nunca responde (192.0.2.1, TEST-NET-1 da RFC 5737; um contentor parado recusa a ligação de imediato e não reproduz o problema, como já se tinha visto na Fase 1). A API arranca e responde a `/health/live` em menos de 2 s, `/health/ready` dá 503 em menos de 2 s e a ingestão dá **503**: a primeira vez que o caminho 503 fica testado. O *host* do consumidor também arranca em menos de 2 s.
   - **Mutação:** com o *warm-up* outra vez a esperar pela ligação, o teste falha («Host start and /health/live took 9,3 s»). O ficheiro foi reposto.
2. **Achado 3.** Um erro de escrita com a categoria `ExecutionTimeout` deixa a exceção subir, e o lote inteiro é repetido (o índice único torna a repetição segura). Já não vai para a *dead letter*. **Não há teste:** não se conseguiu provocar este erro por documento num MongoDB local sem configurar `maxTimeMS`, o que ficaria fora do âmbito.
3. **Achado 4.** Novas métricas `smartparking.bus.consumer.failures` (contador) e `smartparking.bus.consumer.consecutive_failures` (*gauge*, volta a 0 depois de um lote bem-sucedido), por grupo. Teste `Handler_that_always_fails_keeps_the_entry_pending_and_is_counted_as_failures`: um *handler* que lança sempre deixa a entrada pendente, a *dead letter* vazia e pelo menos 3 falhas contadas.
4. **Achado 5.** Regra **CA2007** ativa (como aviso, logo erro com `TreatWarningsAsErrors`) por `.editorconfig` em `Domain`, `Application` e `Infrastructure.*`. Os `await` em falta (Application e Relational, incluindo código das Fases 2a e 2b) foram corrigidos com `dotnet format analyzers --diagnostics CA2007`: 17 ficheiros, só com `.ConfigureAwait(false)` acrescentado (diferença revista). O `await using` do âmbito de DI passou a `await using (scope.ConfigureAwait(false))`.
5. **Achado 6.** O `PublishAsync` já não usa `WaitAsync(ct)` sobre os `XADD`: espera sempre pelas respostas (limitadas pelo `AsyncTimeout`, 5 s). Nenhuma falha fica por observar, e a resposta HTTP corresponde ao que ficou gravado.
6. **Informativos:** o ciclo do `XAUTOCLAIM` verifica o cancelamento entre páginas (7); a *dead letter* usa um `HashSet` de IDs (8, parcial); o histograma de *lag* só conta os eventos gravados nessa altura, e cada `eventId` aparece uma vez por lote (os repetidos na mesma leitura contam como duplicados), o que elimina a contagem dupla de *poison* (9); `MaxBackoff` limitado a 5 minutos na validação (10).
7. **Verificação:** `dotnet build` com 0 avisos; `dotnet test` com **119/119** (mais 3: dois de arranque com o Redis inacessível e um de falhas); teste manual repetido (`curl` duas vezes com o `eventId` `5555…`: `XLEN` 5 → 6 → 7, documentos 3 → 4 → 4, nada pendente; `smartparking_bus_consumer_consecutive_failures{group="occupancy-log"} 0`; `/health/ready` saudável).
8. **Commits:** três *commits* de correção (Documents, Redis, CA2007), cada um a compilar sozinho (verificado num *worktree* temporário).
9. **Correção ao que ficou escrito acima:** no ponto 2 de «O que foi feito», o `Infrastructure.Cache` deixou de registar o `IConnectionMultiplexer` com `TryAdd`; ver o ponto 1 desta secção.

### Pontos a levar para a próxima fase

- **Fase 4:** o *read model* usa `RedisCacheConnection.GetAsync()`, não um `IConnectionMultiplexer` injetado; segundo grupo (`availability`) no mesmo stream; o *read model* tem de comparar `timestamp`, porque com vários consumidores a ordem entre eventos do mesmo lugar não é garantida (ADR-0006).
- **Fase 9:** custo do *polling* de 100 ms na D7; `XREADGROUP BLOCK` com uma ligação dedicada; `appendfsync` e `j:true` (achado 1); *pool* Npgsql (memória da Fase 2b).
- **Fase 10:** aparar o stream e a *dead letter*; `_id` = `eventId` e *time series collections* como variantes de armazenamento (ADR-0007).
- **`TODO(Diogo)`:** comportamento de um lote com inválidos; Redis Streams vs. Kafka (justificação qualitativa ou medição); referências do Cap. 4 para Redis Streams, Kafka e *Event Store*.

## 15-11-2026 — Fase 4: *read model* de disponibilidade (UC-03) em Redis

Âmbito: segundo *consumer group* (`availability`) sobre o stream da Fase 3, que atualiza o estado de cada lugar, os contadores por parque, zona e tipo, e publica em pub/sub; carga inicial e reconstrução a partir do PostgreSQL e do MongoDB; eventos do RF-06 (parques e lugares) para o *read model*; `GET /api/availability`, que lê só do Redis.

### Plano e decisões (antes do código)

- Plano apresentado em texto e aprovado pelo Diogo com as duas opções recomendadas:
  1. **RF-06 → *read model* pela outbox (opção A).** Os comandos de parque e lugar gravam eventos de estado completo na outbox, na mesma transação. Um projetor aplica-os a partir do tópico `smartparking:configuration-events`. O *relay* da outbox continua na Fase 5. Rejeitou-se antecipar o *relay* (misturava fases) e escrever no Redis a partir da API (escrita dupla, contra o ADR-0004).
  2. **Lugar sem eventos começa `FREE`.** `TODO(Diogo):` confirmar no Cap. 3.
- **As três confirmações pedidas:**
  - atomicidade por *script* Lua (e não `MULTI`, porque a decisão depende do estado lido);
  - idempotência por `(timestamp, eventId)`;
  - reconstrução do zero por comando e no arranque.
- **Numeração:** o guião pedia «ADR 0006», número já ocupado pela D2 desde a Fase 3. Ficou **ADR-0008**. É a segunda fase seguida em que o guião traz um número de ADR desatualizado.
- **Revisões:** o *plugin* dotnet-ai-toolkit está instalado, mas não está ativo nesta sessão. A `caching-strategy-setup` e o `dotnet-code-reviewer` foram aplicados por dois subagentes separados, que leram o `SKILL.md` e o `.md` do agente.

### O que foi feito

1. **Domain:**
   - `FacilityChanged`, `FacilityDeleted`, `SpotChanged` e `SpotDeleted` (`Facilities/FacilityEvents.cs`);
   - `ParkingFacility` e `ParkingSpot` passam a `IHasDomainEvents`;
   - `Create`/`Update` recebem o instante (`occurredAt`) e `MarkDeleted` regista a remoção;
   - os comandos do RF-06 recebem `TimeProvider`.
2. **Application/Availability:**
   - contratos `IAvailabilityReadModel`, `IAvailabilityQueries` e `IAvailabilitySource`;
   - `AvailabilityProjector` (UC-01, passo 4) e `ConfigurationProjector` (RF-06);
   - `ReadModelRebuilder`, `ReadModelGate`, `ReadModelMetrics`, `AvailabilitySearch` (validação) e `ConfigurationEventMessage` (formato da linha da outbox no *bus*);
   - `IOccupancyEventLog.GetLatestPerSpotAsync`.
3. **Infrastructure.Cache:** `ReadModelKeys` (prefixo `sp:rm:`), `ReadModelScripts` (cinco *scripts* Lua) e `RedisAvailabilityReadModel` (escrita e consulta).
4. **Documents e Relational:**
   - último evento por lugar com `$group` + `$top`;
   - `EfAvailabilitySource` (leitura em *streaming*, sem *tracking*).
5. **Workers e Api:**
   - nos Workers, os grupos `availability` (no stream de ocupação e no de configuração), o `ReadModelInitializer` e o comando `rebuild-read-model`;
   - na Api, o `AvailabilityEndpoints`.
6. **Testes (24 novos):**
   - `ReadModelConsistencyTests` (7);
   - `AvailabilityQueryTests` (11, contando cada caso da `Theory`);
   - `ReadModelConfigurationAndRebuildTests` (6);
   - com um `AvailabilityFixture` próprio (os três motores).

### Como se testou que os testes apanham os erros (mutações)

- **Troca de latitude e longitude no `GEOADD`:** 4 testes falham.
  - Os parques de teste usam coordenadas reais de Coimbra (aproximadas). A «Portagem» fica a 1 722 m do centro da consulta, mas ficaria a 2 191 m com as coordenadas trocadas, porque a distância este–oeste encolhe com cos(latitude).
  - Num raio de 2 km, a troca tira-a do resultado. O teste também compara a distância devolvida pelo Redis com a *haversine*, com ±1 %.
- **Sem a comparação de `ts` no *script*:** 3 testes falham (evento atrasado, concorrência e reconstrução).
- **Restauro:** os dois ficheiros mutados foram repostos (ver o problema 2).

### Problemas encontrados e tentativas falhadas

1. **Heredoc no Git Bash, quarta fase seguida.** Um comando que escrevia `ReadModelKeys.cs` e `ReadModelScripts.cs` falhou com «unexpected EOF while looking for matching `'`», por causa das plicas do Lua. Nada foi escrito, e os ficheiros foram escritos com as ferramentas de edição. A regra «para código, não usar heredocs» já estava no diário da Fase 3 e não foi seguida.
2. **Mutação mal reposta.** O guião de mutação repunha o ficheiro com `git checkout`, mas o ficheiro ainda não estava no git. A primeira mutação ficou ativa e a segunda correu por cima dela: o resultado dessa execução (7 falhas) misturava as duas. O ficheiro foi reposto a partir de uma cópia e a segunda mutação foi repetida sozinha (3 falhas). **Lição:** em ficheiros novos, guardar uma cópia antes de mutar.
3. **Duas instâncias da API no teste manual.** Lancei a API em segundo plano com `&` dentro de uma tarefa já em segundo plano, e depois outra vez. A segunda falhou ao arrancar porque a porta 5080 estava ocupada (código 127). O teste usou a primeira, e as duas foram paradas no fim pelo PID da porta.
4. **Eventos antigos sem lugar.** Na base de dados de desenvolvimento, os 4 eventos da Fase 3 no MongoDB referem lugares gerados aleatoriamente pelo `curl` dessa fase, que não existem no PostgreSQL. A reconstrução reporta «4 latest events (0 applied)». O comportamento é o esperado (`unknown_spot`), mas convém lembrá-lo nos *benchmarks*: o simulador da Fase 7 tem de usar lugares configurados.
5. **Um parque criado pela API não aparece sozinho no *read model*.** Como o *relay* da outbox é da Fase 5, o teste manual teve de seguir esta ordem: parar os Workers, criar os parques, correr `rebuild-read-model` e voltar a arrancar os Workers. É o limite assumido no ADR-0008, ponto 4.
6. **Precisão dos instantes:** o `ts` dos eventos é truncado ao milissegundo (como no MongoDB), para a reconstrução e o caminho incremental compararem os mesmos valores. O `changedAt` da configuração usa microssegundos: criar e atualizar o mesmo lugar no mesmo milissegundo ignoraria a atualização.

### Verificação (resultados reais)

- `dotnet build`: 0 avisos e 0 erros.
- `dotnet test`: **143/143** (eram 119; mais 24). Arquitetura: passou. Integração: 35 s. Depois da correção dos achados: **151/151**.
- **Teste manual** (compose em execução; `dotnet ef database update` sem migrações por aplicar):
  - Arranque dos Workers: «Availability read model rebuilt: 3 facilities, 5 spots, 4 latest events in 1162 ms».
  - Criados pela API o «Parque da Portagem (manual F4)» (40.2089, −8.4292, 2 lugares) e o «Parque Coimbra-B (manual F4)» (40.2228, −8.4357, 1 lugar), com os Workers parados. `rebuild-read-model`: «5 facilities, 8 spots, 4 latest events (0 applied) in 1499 ms».
  - `curl "http://localhost:5080/api/availability?lat=40.2033&lon=-8.4103&radius=2000"`: os três parques das Fases 2a/2b no centro e a Portagem a **1721,9 m**, com `spots` 2 e `free` 2. Coimbra-B não aparece.
  - `redis-cli GEOSEARCH sp:rm:geo:facilities FROMLONLAT -8.4103 40.2033 BYRADIUS 2000 m ASC WITHDIST`: o mesmo resultado. Com 3 500 m, aparece Coimbra-B a **3059,2 m**. Os valores coincidem com a *haversine* calculada à parte (1 722 e 3 059 m).
  - `redis-cli SUBSCRIBE sp:availability` aberto e um evento `OCCUPIED` enviado para um lugar da Portagem (`202`): chegou **uma** mensagem com `"state":"OCCUPIED","free":1,"freeOfType":1`. O hash do parque ficou com `free 1` e `spots 2`.
  - O mesmo evento reenviado (`202`): nenhuma mensagem nova e os contadores iguais. Métricas dos Workers: `outcome="applied"} 1` e `outcome="duplicate"} 1`.
  - `XINFO GROUPS`: os grupos `availability` e `occupancy-log` com `pending 0` e `lag 0`.
  - `smartparking_read_model_lag_seconds`: uma amostra de 0,32 s. É uma única medição local, sem valor estatístico; a D7 mede-se na Fase 9.
  - `INFO commandstats`: `eval` 30, `evalsha` 8. O StackExchange.Redis envia `EVAL` na primeira vez de cada *script* em cada processo (incluindo o processo do comando de reconstrução) e `EVALSHA` a seguir.

### Revisões independentes (passo 5)

Dois relatórios, ambos feitos por subagentes separados que não editaram `src/` nem `tests/` e que usaram contentores descartáveis (já apagados):
- `docs/revisoes/fase-4-revisao-cache.md`: método da `caching-strategy-setup`, adaptado a um *read model* que não é uma cache *cache-aside*;
- `docs/revisoes/fase-4-revisao-codigo.md`: método do `dotnet-code-reviewer`.

Achados consolidados (os dois relatórios coincidem em três):
1. **Alta. Duas reconstruções, ou uma reconstrução com projetores ativos, desviam os contadores para sempre.**
   - Durante o `SCAN`, o `HINCRBY` de um projetor recria o hash do parque com `free = -1`, e esse hash sobrevive à limpeza.
   - O revisor da cache reproduziu o problema com os *scripts* reais: «expected 3/3, got 3/2».
   - Acontece com 2 ou mais Workers a arrancar sem `sp:rm:meta` (o RNF-02 mede 2 e 4 instâncias) ou com o comando a correr com os Workers ligados.
   - A regra «Workers parados» do ADR-0008 não basta, porque nada a impõe.
2. **Alta. Os histogramas de latência usam os *buckets* por omissão (0, 5, 10… s).** Assim, o p95 da D7 (< 1 s) não se calcula no Prometheus. Afeta também o `occupancy_log.lag` da Fase 3. O revisor confirmou-o com o OTel 1.21.0: amostras de 0,05 a 1,4 s caíram todas em `le=5`.
3. **Média. A reconstrução ignora o atraso do grupo `occupancy-log`.** Os eventos que `availability` já confirmou, mas que ainda não estão no MongoDB, desaparecem do *read model*.
4. **Média. Um `timestamp` no futuro congela o lugar.** O RF-01 não tem limite superior, e a reconstrução volta a escolher esse evento. Reproduzido.
5. **Média. Não há marca de remoção (*tombstone*).** Com dois consumidores no grupo de configuração, `SpotChanged` depois de `SpotDeleted` recria o lugar. Reproduzido. O ADR-0008 dizia que isto «só existe com reentregas», o que estava errado.
6. **Baixas:**
   - evento antes do `SpotChanged` → `unknown_spot` perdido;
   - UC-03 responde 200 vazio durante uma reconstrução;
   - `noeviction` só implícito e stream sem limite;
   - `ClearAsync` silencioso sem primário ligado, e o ADR fala em `UNLINK` mas o código usa `DEL`;
   - grupo de configuração com o mesmo nome (`availability`), o que mistura métricas e esconde um projetor bloqueado (confirmado);
   - *payload* desserializável mas inválido (por exemplo, latitude 89) deixa uma escrita a meio (reproduzido);
   - a gate fechada não aparece na prontidão;
   - lacunas nos testes (initializer, remoções pela outbox, `DeleteFacility`, *poison* de configuração) e `Random.Shared` num teste dito reproduzível.
7. **Informativas:**
   - ordem da configuração dependente dos relógios de várias instâncias da API;
   - Redis Cluster (`CROSSSLOT` no `DEL` multi-chave);
   - rajada de pub/sub na reconstrução;
   - hashes de zona nunca removidos;
   - título errado no 400 de um parâmetro de *query*;
   - tarefas abandonadas em `InChunksAsync`;
   - `lag` negativo com sensores adiantados.

Verificado sem problemas pelos revisores:
- `XACK` só depois dos *scripts*;
- atomicidade com vários projetores (50 parques sem diferença);
- desempate `(ts, eventId)` igual no Lua e no `$top` do MongoDB;
- `FacilityDeleted` gravado na mesma transação do `Remove` (PostgreSQL descartável);
- mudança de zona;
- ordem longitude/latitude;
- regras de camadas;
- paragem limpa com a gate fechada.

Estado: corrigidos os achados 1 a 5 e três baixos (ver a secção seguinte).

### Correção dos achados da revisão (antes dos commits)

O Diogo aprovou o conjunto recomendado: os 5 achados altos e médios e três baixos. Todos têm um teste novo, em `ReadModelReviewFixesTests` (8 testes):

1. **Trinco e barreira na reconstrução (alto, achado 1).**
   - **O que mudou:**
     - `SET sp:rebuild-lock <token> NX PX 120000`, fora do prefixo `sp:rm:`;
     - os cinco *scripts* recebem a chave do trinco e um *token*, e devolvem 6 a quem não o tem;
     - o código 6 vira `ReadModelRebuildingException`: falha transitória, sem `XACK`;
     - `IReadModelRebuildSession` é o único escritor durante a reconstrução, e renova e liberta o trinco;
     - `RebuildInProgressException` para uma segunda reconstrução. O `ReadModelInitializer` espera e volta a verificar, e o comando sai com o código 1.
   - **Testes:**
     - `Rebuild_lock_refuses_projector_writes_and_a_second_rebuild`;
     - `Concurrent_rebuilds_and_projector_leave_counters_equal_to_spot_states`: duas reconstruções e um projetor a escrever ao mesmo tempo.
   - **Verificação manual:** dois `rebuild-read-model` em simultâneo; um terminou e o outro saiu com «Another rebuild … in progress» e o código 1.
2. ***Buckets* dos histogramas de latência (alto, achado 1 do código).**
   - **O que mudou:** *buckets* explícitos de 5 ms a 60 s para `smartparking.read_model.lag`, `bus_lag` e `occupancy_log.lag` (este último da Fase 3), e outros para a duração da reconstrução.
   - **Teste:** `Pipeline_lag_histograms_have_sub_second_buckets`.
3. **Reconstrução alinhada com o grupo `occupancy-log` (médio, achado 2).**
   - **O que mudou:**
     - nova abstração `IConsumerGroupPositions` (Application) e `RedisConsumerGroupPositions` (Messaging);
     - a reconstrução regista até onde o log está completo e, no fim, faz `XGROUP SETID availability` para essa posição.
   - **Teste:** `Rebuild_redelivers_to_the_projector_what_the_log_does_not_have_yet`. O grupo `availability` confirma à mão um evento que não está no MongoDB; depois da reconstrução e do arranque dos Workers, o lugar fica `OCCUPIED`.
4. **Timestamps no futuro (médio, achado 3 da cache).**
   - **O que mudou:** o RF-01 recusa um `timestamp` posterior à receção mais `Ingestion:MaxClockSkew` (5 min por omissão). `TODO(Diogo):` confirmar a tolerância.
   - **Teste:** `Sensor_timestamp_in_the_future_is_rejected` (+1 h → 400; +1 min → 202).
5. **Marcas de remoção (médio, achado 2 do código).**
   - **O que mudou:**
     - `DeleteSpot` e `DeleteFacility` deixam a chave só com `deleted` e `changedAt`, com uma expiração de 24 h;
     - `UpsertSpot` e `UpsertFacility` tratam como antigo um evento não mais recente do que a remoção;
     - um lugar novo num parque removido é ignorado.
   - **Testes:** `Late_spot_change_after_its_delete_is_stale_and_does_not_resurrect_it` e `Late_facility_change_after_its_delete_is_stale_and_its_late_spots_are_ignored`.
6. **Baixos:**
   - o grupo de configuração passa a chamar-se `availability-configuration` (verificado em `New_facility_and_spot_reach_the_read_model_through_the_outbox`);
   - payload desserializável mas inválido:
     - o `ConfigurationProjector` aplica as regras do domínio e manda para a *dead letter*;
     - o `GEOADD` passou para a primeira escrita do *script*;
     - teste: `Invalid_facility_event_is_poison_and_writes_nothing`, com latitude 89;
   - `--maxmemory-policy noeviction` explícito no compose (verificado com `CONFIG GET` depois de recriar o contentor);
   - a limpeza usa `UNLINK` de facto e falha se não houver um primário ligado;
   - a semente fixa no teste de concorrência deixou de usar `Random.Shared`.
7. **Não corrigidos (documentados):**
   - `unknown_spot` antes do `SpotChanged` (Fase 5: medir com o *relay*);
   - UC-03 vazio durante uma reconstrução (Fase 9);
   - gate fechada invisível na prontidão;
   - lacunas de testes do *initializer*;
   - informativos (relógios de várias instâncias da API, Redis Cluster, rajada de pub/sub, hashes de zona, título do 400 de *query*).

**Mutações** (com uma cópia de cada ficheiro guardada antes, por causa da lição do problema 2):
- **sem a barreira nos *scripts*:** falha `Rebuild_lock_refuses_projector_writes_and_a_second_rebuild`. O teste de concorrência **não** falhou nessa execução: depende do entrelaçamento, por isso o teste determinista é o que protege a barreira;
- **sem o `SetPosition`:** falha `Rebuild_redelivers_…`. Uma primeira tentativa desta mutação não compilou, e os testes correram sobre o binário anterior; repetiu-se com uma alteração que compila;
- **sem a verificação da marca de remoção do lugar:** falha `Late_spot_change_…`.

**Problemas desta correção:**
1. **Erro real encontrado pelos testes.** O `XGROUP SETID` sobre um stream que ainda não existe devolve «The XGROUP subcommand requires the key to exist» (Redis 7.4), e não `NOGROUP`, por isso não era tratado. Agora cria o grupo com `MKSTREAM`.
2. **`INFO` precisa de `allowAdmin`** na ligação do teste, para contar as chamadas a `UNLINK`.
3. **Heredoc outra vez** ao editar o ADR (plicas no texto em português). O script Python passou a ser escrito num ficheiro do scratchpad.

**Verificação:**
- `dotnet build`: 0 avisos;
- `dotnet test`: **151/151** (mais 8);
- ao recriar o contentor do Redis, os dados mantiveram-se (AOF);
- o comando de reconstrução reporta «5 latest events (1 applied)»: o evento do teste manual, agora reaplicado;
- os grupos `availability` e `occupancy-log` ficaram com o mesmo `last-delivered-id`.


### Pontos a levar para a próxima fase

- **Fase 5:**
  - o *relay* da outbox publica `FacilityChanged`/`FacilityDeleted`/`SpotChanged`/`SpotDeleted` em `smartparking:configuration-events`, com o formato de `ConfigurationEventMessage`: a linha da outbox tal como está;
  - depois disso, um parque criado pela API aparece no *read model* sem reconstrução;
  - os eventos de sessão (UC-02) precisam de outro tópico ou são ignorados pelo `ConfigurationProjector` (já os reconhece como `ignored`).
- **Fase 9:**
  - p95 do UC-03 (`availability-read`);
  - D7 com `smartparking.read_model.lag`;
  - duração da reconstrução com 5 000 lugares e 10⁶ eventos (custo do *collection scan* do `$group`);
  - variante «SET de livres» se os contadores mostrarem problemas.
- **`TODO(Diogo)`:** lugar sem leitura = livre? Raio máximo de 50 km.

## 16-11-2026 — Fase 5: sincronização (*relay* da outbox) e medição

Âmbito:
- *relay* da outbox nos Workers, com `FOR UPDATE SKIP LOCKED` para poder correr em várias instâncias;
- consumidores idempotentes no *read model*;
- janela de consistência (UC-02, Cap. 4.5) e latência ponta a ponta do UC-01 (D7) como histogramas OpenTelemetry;
- *trace* propagado desde o pedido HTTP até ao consumidor;
- alvo D7 em configuração.

### Plano e decisões (antes do código)

- **Plano aprovado pelo Diogo com as quatro opções por omissão:**
  1. a janela ponta a ponta mede-se nos eventos de configuração; para os eventos de sessão e pagamento mede-se só até à publicação (`publish_lag`), porque o consumidor deles chega na Fase 6;
  2. o histograma E2E muda de nome para `smartparking.ingestion.e2e_latency`;
  3. exportador OTLP opcional e Jaeger no compose;
  4. `UPDATE processed_at` (sem `DELETE`), com a retenção na Fase 10.
- **Relógios** (pedido no passo 2 do guião):
  - a janela de consistência começa no `occurred_at`, lido pelo `TimeProvider.System` da API, e acaba no `TimeProvider.System` dos Workers;
  - a latência E2E começa no `timestamp` do sensor e acaba no relógio dos Workers;
  - em local, os processos correm todos no mesmo anfitrião e leem o mesmo relógio, por isso o desvio é zero;
  - está escrito no ADR-0009, ponto 5, com a condição para a Fase 9 e o que muda numa implantação distribuída.
- **Numeração:** o guião pedia «ADR 0007», mas esse número é da D3. Ficou **ADR-0009**. É a terceira fase seguida com o número desatualizado no guião.
- **Ferramentas do guião ausentes:** a *skill* `metrics-dashboard-generator` e o agente `observability-gap-finder` não estão disponíveis nesta sessão. A análise de lacunas foi feita por um subagente genérico com a pergunta do guião. O Grafana (passo 5, opcional) ficou de fora, como estava no plano.
- **Desvio ao plano:** o tópico dos eventos de sessão ficou `smartparking:domain-events` e não `session-events`, porque também leva pagamentos e é o destino por omissão de qualquer tipo novo (ADR-0009, ponto 3).

### O que foi feito

1. **Application:**
   - `Abstractions/Outbox.cs`: `IOutboxStore`, `IOutboxBatch` e `OutboxEntry`;
   - `Outbox/OutboxRelay.cs`: `OutboxRelay`, `OutboxTopics` e `OutboxRelayOptions`;
   - `Outbox/OutboxMetrics.cs`;
   - o `ConfigurationProjector` regista a janela de consistência e abre um span por evento;
   - `ReadModelMetrics`: novo histograma da janela; o histograma D7 muda de nome.
2. **Infrastructure.Relational:**
   - `NpgsqlOutboxStore`, com SQL simples sobre a fonte de dados partilhada;
   - coluna `trace_parent` (migração `AddOutboxTraceParent`), preenchida pelo `OutboxSaveChangesInterceptor`;
   - filtro de traces do Npgsql para os comandos de *polling*.
3. **Workers:** `OutboxRelayService` (BackgroundService), com a secção `Outbox` em `appsettings.json`.
4. **ServiceDefaults:**
   - `LatencyTargets.cs` (`Observability:Targets` e o *gauge* `smartparking.target`);
   - exportador OTLP ativado por `OTEL_EXPORTER_OTLP_ENDPOINT` (pacote `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.21.0);
   - *buckets* abaixo de 1 s para os dois histogramas novos.
5. **Infra:** Jaeger 2.11.0 no perfil `observability`, fixado por *digest*.
6. **Testes:** `OutboxRelayTests` (8). O `AvailabilityFixture` passa a partilhar o `IMeterFactory` da API com os Workers de teste (ver o problema 3).

### Nomes das métricas (os *scripts* da Fase 9 dependem deles)

| Instrumento OpenTelemetry | Prometheus | Processo | O que mede |
|---|---|---|---|
| `smartparking.consistency.window` (`type`) | `smartparking_consistency_window_seconds_{bucket,sum,count}` | Workers | `occurred_at` na outbox → *read model* atualizado (só `Applied`) |
| `smartparking.outbox.publish_lag` (`type`) | `smartparking_outbox_publish_lag_seconds_{bucket,sum,count}` | Workers | `occurred_at` → publicado no *bus* (todos os tipos) |
| `smartparking.outbox.published` (`type`) | `smartparking_outbox_published_total` | Workers | linhas publicadas |
| `smartparking.outbox.batches` (`outcome`) | `smartparking_outbox_batches_total` | Workers | lotes `published` ou `failed` |
| `smartparking.outbox.pending` | `smartparking_outbox_pending` | Workers | *backlog* (a cada 5 s; ausente se a contagem falhar) |
| `smartparking.ingestion.e2e_latency` | `smartparking_ingestion_e2e_latency_seconds_{bucket,sum,count}` | Workers | sensor → Redis (D7); era `smartparking_read_model_lag_seconds` |
| `smartparking.read_model.bus_lag` | `smartparking_read_model_bus_lag_seconds_*` | Workers | receção na API → Redis (sem o relógio do sensor) |
| `smartparking.target` (`objective`) | `smartparking_target_seconds` | API e Workers | alvos da §6: `uc01_ingestion_e2e_p95`, `uc03_availability_query_p95`, `uc02_session_write_p95` (`consistency_window_p95` vazio) |

Consulta de exemplo: `histogram_quantile(0.95, sum by (le) (rate(smartparking_consistency_window_seconds_bucket[5m])))`. O `grep -i -E "consistency|e2e|latency"` do guião encontra as métricas da janela e da E2E. Há uma ressalva: um histograma só aparece no `/metrics` depois da primeira amostra. Antes de criar ou alterar um parque, e antes de enviar um evento de sensor, o `grep` não devolve essas linhas.

### Como se testou que os testes apanham os erros (mutações)

| Mutação | Resultado |
|---|---|
| Sem `FOR UPDATE SKIP LOCKED` (sem bloqueio nenhum) | O teste das duas instâncias falha (duplicados). |
| Só `FOR UPDATE`, sem `SKIP LOCKED` | Depois da correção do achado 4: `TimeoutException` (a barreira nunca abre). Antes da correção, o teste passava. |
| *Commit* antes de publicar («no máximo uma vez») | Os dois casos do teste de paragem falham. |
| Amostra da janela registada sempre com 5 s | O teste de métricas falha. Antes da correção do achado 5, passava. |
| Sem limpar nem repor o `Activity.Current` | 2 de 3 spans encadeados; o teste falha. |

Todas as mutações foram repostas a partir de cópias e não com `git checkout` (ver o problema 2).

### Problemas encontrados e tentativas falhadas

1. **Heredoc no Git Bash, quinta fase seguida.** Um script Python passado por heredoc (`<<'EOF'`) falhou com «unexpected EOF while looking for matching `'`», e nada foi escrito. Os scripts de edição passaram a ser ficheiros na pasta temporária, executados com `python <ficheiro>`. Noutra edição, um `'\\n'` dentro do heredoc chegou ao C# como uma quebra de linha real e partiu a compilação de um teste. **Regra reforçada:** código e texto com aspas vão sempre por ficheiro, nunca por heredoc.
2. **Mutação mal reposta, outra vez.** A primeira mutação (sem `SKIP LOCKED`) foi reposta com `git checkout`, mas o ficheiro ainda não estava no git, por isso a mutação ficou ativa. Detetei-o ao ver o SQL e repus a partir da cópia. As mutações seguintes guardaram sempre uma cópia antes. É a mesma lição da Fase 4, que não foi aplicada à primeira.
3. **O teste de métricas dependia da ordem dos testes.** Passava sozinho e falhava na suite completa. O primeiro diagnóstico estava **errado**: pensei que fosse a *cache* de 300 ms do exportador Prometheus e acrescentei repetições, mas o teste falhou na mesma (15 s de espera). Com uma listagem dos nomes `smartparking_*` exportados, vi que faltavam **todos** os instrumentos de `ReadModelMetrics`, e não só o novo.
   - **Causa:** o `MeterProvider` da API de teste ouve todos os medidores `SmartParking` do processo, incluindo os dos *hosts* de Workers que os testes criam e descartam. Quando um *host* é descartado, o seu medidor também é. O OpenTelemetry passa a tratar o fluxo com esse nome como terminado e deixa de exportar instrumentos novos com o mesmo nome.
   - O teste da Fase 4 (`Pipeline_lag_histograms_have_sub_second_buckets`) tinha a mesma fragilidade e só passava pela ordem dos testes.
   - **Correção:** os Workers de teste usam o `IMeterFactory` da API, como num processo real com uma só fábrica.
   - **Efeito colateral:** a mensagem de diagnóstico temporária, escrita por heredoc, partiu a compilação (problema 1) e foi removida.
4. **A primeira mutação do achado 2 ficou incompleta.** Tirei só o `Activity.Current = null`, mas o `finally` que repõe o valor anterior já impedia o encadeamento, e o teste passou. A mutação correta tira as duas linhas e faz o teste falhar.
5. **A primeira janela medida é muito maior.** No ensaio com a aplicação real, a primeira janela foi de 1,32 s (`FacilityChanged`) e a segunda de 0,08 s (`SpotChanged`). A primeira inclui o primeiro *commit* a frio da API (arranque do EF Core e primeira ligação), porque `occurred_at` é fixado antes do *commit*. Nos *benchmarks* é preciso aquecimento.
6. **O *backlog* antigo entra nos histogramas.** As 8 linhas por processar das Fases 2b a 4 foram publicadas no arranque dos Workers e entraram no `publish_lag` com cerca de 17 h cada (por exemplo, `FacilityChanged` 3 amostras, soma 96 835 s). A medição está correta, mas na Fase 9 é preciso começar com a outbox vazia e os Workers reiniciados.

### Verificação (resultados reais)

- `dotnet build`: 0 avisos e 0 erros.
- `dotnet test`: **157/157** antes da revisão (eram 151; mais 6) e **158/158** depois das correções (mais 1: `Rows_without_trace_context_start_independent_traces`). Arquitetura: passou. Integração: cerca de 32 s. A suite completa correu duas vezes seguidas sem falhas.
- **Ensaio com a aplicação real** (o meu, não o teste manual do Diogo):
  - compose em execução, `dotnet ef database update` com `AddOutboxTraceParent`, API e Workers com `dotnet run`;
  - no arranque dos Workers, as 8 linhas antigas ficaram processadas (`pending 0 | total 8`);
  - parque «Ensaio Fase 5» e um lugar criados por `curl`. Linhas na outbox com `processed_at` preenchido: `SpotChanged` 0,060 s depois do `occurred_at`, `FacilityChanged` 1,315 s (problema 5); `trace_parent` preenchido nas duas;
  - `/metrics` dos Workers: `smartparking_consistency_window_seconds_count` 1 por tipo (somas 1,322 s e 0,076 s), `smartparking_outbox_pending 0` e `smartparking_target_seconds` 1, 0,2 e 2;
  - um evento `OCCUPIED` para o lugar novo (`202`): `smartparking_ingestion_e2e_latency_seconds_count 1`, soma 0,285 s;
  - `GET /api/availability?lat=40.20&lon=-8.41&radius=500`: o parque novo aparece com `spots 1` e `free 0`, **sem reconstrução** (na Fase 4 era preciso o `rebuild-read-model`).

  São amostras isoladas e locais, sem valor estatístico. A D7 e a janela medem-se na Fase 9.
- **Teste manual (passo 4 do guião):** feito a 16-11-2026, ver o ponto «Resultado» abaixo. Fechar uma sessão no Swagger, repetir a consulta à outbox da Fase 2b (`processed_at` preenchido em poucos segundos) e correr `curl http://localhost:5090/metrics | grep -i -E "consistency|e2e|latency"`. A porta é a dos **Workers** (5090), onde estão o *relay* e os projetores. O `/metrics` da API (5080) não tem a janela nem a E2E: só os alvos, a latência HTTP e as métricas de ingestão.
  - **Primeira tentativa falhou (16-11-2026).** O comando do guião é para bash e o terminal é o Windows PowerShell 5.1: não existe `grep`, e `curl` é um alias de `Invoke-WebRequest`. Além disso, nem a API nem os Workers estavam a correr, porque foram parados no fim do meu ensaio. **Pré-condição:** os dois processos arrancados (`dotnet run --project src/SmartParking.Api` e `dotnet run --project src/SmartParking.Workers`, em dois terminais). Equivalente em PowerShell: `(Invoke-WebRequest -UseBasicParsing http://localhost:5090/metrics).Content -split "`n" | Select-String 'consistency|e2e|latency'`, ou `curl.exe -s http://localhost:5090/metrics | findstr /I "consistency e2e latency"`.
  - **Primeira tentativa: registo recusado.** Pediram-me para registar o teste no diário, mas no estado real não havia nenhum `SessionClosed` novo na outbox, e o `/metrics` dos Workers, já arrancados, não tinha amostras. Não registei nenhum resultado e pedi os valores. O Diogo pediu então ao Claude Code que fizesse o teste.
  - **Resultado** (feito pelo Claude Code, a pedido do Diogo, com a API e os Workers arrancados pelo Diogo; por HTTP, com um script, e não pela interface do Swagger, mas com os mesmos *endpoints* e corpos):
    - `POST /api/users` → 201; `POST /api/users/{id}/vehicles` → 201; `POST /api/sessions` (lugar da zona Centro) → **201** `Open`; 3 s depois, `POST /api/sessions/{id}/close` com `{}` → **200** `Closed`, `cost 0.02`; `POST /api/sessions/{id}/pay` com `{"method":"Card"}` → **201** `Completed` (sessão `01a1e3bb-9407-7e65-8f8d-99c40dc8efb2`).
    - A consulta à outbox da Fase 2b, 0,5 s depois do fecho, já mostrava `processed_at` preenchido em todas as linhas:

      | Evento | `processed_at − occurred_at` | `trace_parent` |
      |---|---|---|
      | `SessionStarted` | 0,204 s | preenchido |
      | `SessionClosed` | 0,056 s | preenchido |
      | `PaymentRegistered` | 0,050 s | preenchido |

    - `XLEN smartparking:domain-events` = 6: estes 3 eventos e os 3 da sessão da Fase 2b, publicados no primeiro arranque do *relay*.
    - **Com o padrão exato do guião** (`consistency|e2e|latency`, versão PowerShell), depois do fecho da sessão só apareceu `smartparking_target_seconds{objective="uc01_ingestion_e2e_p95"} 1`. É o esperado: os eventos de sessão ainda não têm consumidor no *read model* (Fase 6), por isso medem-se em `smartparking_outbox_publish_lag_seconds` (contagem 1 por tipo, somas 0,204 s, 0,056 s e 0,050 s).
    - Para completar o teste, criei um lugar no parque «Ensaio Fase 5» (`POST /api/facilities/{id}/spots`) e enviei um evento `OCCUPIED` para esse lugar (`202`). O mesmo comando PowerShell passou a mostrar:
      - `smartparking_consistency_window_seconds_count{type="SpotChanged"} 1`, com soma 0,139 s;
      - `smartparking_ingestion_e2e_latency_seconds_count 1`, com soma 0,356 s;
      - o alvo D7, `smartparking_target_seconds` 1.
    - São amostras únicas, em local e sem carga, sem valor estatístico.

### Revisões independentes (passo 6)

- **Revisão de código** (`docs/revisoes/fase-5-revisao-codigo.md`): 11 achados (0 altos, 3 médios). Corrigidos antes dos commits:
  - **1:** *payload* copiado sem nova análise, para não haver falhas por linha;
  - **2:** spans independentes por linha e *polling* fora dos traces;
  - **4:** barreira no teste das duas instâncias;
  - **5:** contagem antes e depois no teste de métricas;
  - **6:** contagem do *backlog* separada do lote.

  O 3 ficou documentado no teste; do 7 ao 11, no ADR-0009.
- **Lacunas de observabilidade para as secções 6.2 e 6.5** (`docs/revisoes/fase-5-lacunas-observabilidade.md`):
  - faltam os *scripts* k6 com taxa de chegada constante e a exportação para CSV (Fase 9);
  - faltam os *exporters* dos motores e o cAdvisor (CPU, memória, disco);
  - falta um *gauge* do atraso dos streams por grupo;
  - correr 2 ou 4 processos de Workers (RNF-02) não é possível sem porta configurável e *label* `consumer`;
  - falta a reconciliação «enviados vs. persistidos» para o RNF-03;
  - falta um ponto final comparável com o monolítico. A proposta é a «frescura observável»: do sensor até a consulta refletir o evento.

### O que ficou por medir

- **Alvo da janela de consistência:** não há (`TODO(Diogo)`). Fica vazio em `Observability:Targets:ConsistencyWindowP95`.
- **Janela ponta a ponta dos eventos de sessão e pagamento:** só se mede até à publicação. O resto chega com o consumidor da Fase 6.
- **Medições sob carga:** p95 da janela, D7 e UC-02/UC-03, todos com 5 execuções e CSV (Fase 9).
- **Escalamento:** a janela com 2 e 4 instâncias do *relay* (RNF-02), que precisa da porta configurável.
- **Contador de amostras negativas** da janela (relógios desalinhados), proposto pela revisão (achado 7).
- **Recursos por motor e espaço ocupado:** necessários para a secção 6.5.

### Pontos a levar para a próxima fase

- **Fase 6:**
  - consumidor de `smartparking:domain-events`;
  - deduplicar pelo `id` da linha da outbox, porque, ao contrário da configuração, os agregados não são de estado completo;
  - medir a janela até às tabelas de resumo com a mesma definição.
- **Fase 9:**
  - outbox vazia, Workers reiniciados e streams `domain-events` esvaziados antes de cada cenário;
  - API e Workers do mesmo lado quanto ao relógio;
  - `max by (objective)` nos alvos;
  - OTLP desligado nas execuções cronometradas;
  - a ligação do *relay* conta no dimensionamento do *pool* (lembrete já registado);
  - ações da análise de lacunas.
- **`TODO(Diogo)`:** alvo da janela de consistência; D7 com um valor único na tese (1 s ou 2 s), que agora é só uma alteração de configuração.

## 16-11-2026 — Fase 6: analítica (UC-04, RF-05) com tabelas de resumo (D4)

Objetivo: relatórios de ocupação que leem só tabelas de resumo, isolados das tabelas operacionais. A D4 fica no valor por omissão (agregados periódicos em PostgreSQL). Ramo `phase-6-analytics`.

### Plano e decisões (antes do código)

O plano foi aprovado pelo Diogo com as quatro recomendações:

1. **Taxa de ocupação:** Σ minutos-lugar ocupados ÷ Σ minutos-lugar disponíveis. Os minutos disponíveis são o n.º de lugares × a duração do período. Dia, semana e mês são a razão das somas das horas, nunca a média das taxas. `TODO(Diogo):` confirmar no Cap. 3, que não está no repositório.
2. **Fuso horário:** tudo é guardado em UTC, uma linha por hora UTC. Dia, semana ISO e mês são cortados no calendário de Europe/Lisbon (`Reports:TimeZone`).
3. **Denominador:** os lugares registados no momento da agregação, guardados na linha (`spot_count`). O campo `Capacity` não é usado.
4. **Âmbito:** o consumidor de `smartparking:domain-events` (sessões e pagamentos), deixado pela Fase 5 «para a Fase 6», **fica fora**. O UC-04 da especificação é sobre a ocupação.

O que encontrei no código antes de começar:
- **ADR 0008 ocupado:** o guião pedia o ADR 0008, mas esse número já é o do *read model*. O registo desta fase é o **ADR-0010**. É a quarta fase seguida com este desencontro.
- **Zona fora dos eventos:** os eventos no MongoDB não têm a zona, que está no parque, em PostgreSQL. O *worker* copia-a para cada linha de resumo, para que o relatório não precise de *joins*.
- **Estado no início da hora:** um carro que chega às 9h50 conta na hora das 10h. Por isso, o *worker* procura o estado de cada lugar no início do intervalo. Na primeira versão, a procura ia só até 24 h para trás (`StateLookback`); a revisão de código mostrou que isso tornava a reagregação dependente da janela, e o limite foi retirado (ver as correções).
- **Skill não ativa:** a `ef-index-advisor` está instalada (plugin `dotnet-ai-toolkit`), mas não estava ativa na sessão. Li o `SKILL.md` e apliquei-a à mão.

### O que foi feito

- **Application (`Analytics/`):**
  - `OccupancyIntervalCalculator`: cálculo puro por hora, com as regras do ADR-0010, ponto 4;
  - `OccupancyAggregator`: janelas fechadas (`AllowedLateness` de 2 min), reagregação das últimas 2 horas, checkpoint, máximo de 24 horas por execução e o comando manual;
  - `OccupancyReport` (validação do pedido, `OccupancyReportService`, fim de cada período em `TimeZoneInfo`);
  - `AnalyticsMetrics` (`smartparking.analytics.runs`, `.rows`, `.duration` e `.staleness`);
  - `IsoInstant` (datas ISO 8601 estritas, partilhado com o comando).
  - O `IOccupancyEventLog` ganhou duas leituras por parque: o estado antes de um instante (sem limite inferior, com `DISTINCT_SCAN` num índice novo) e os eventos de um intervalo (índice `(facilityId, timestamp)`).
- **Relational:**
  - schema `analytics` com `occupancy_hourly` (chave `(hour_start, facility_id)`, índice `(zone, hour_start) INCLUDE (somas)`, restrições `CHECK`, sem chaves estrangeiras) e `aggregation_checkpoints`;
  - *upsert* com `unnest` numa só instrução por bloco;
  - o relatório numa consulta SQL com `date_trunc(unit, hour_start, 'Europe/Lisbon')` para o início e `date_add(…)` para o fim de cada período;
  - migração `AddOccupancyHourlySummary`, com `DropSchema` no `Down()`.
- **Documents:** terceiro índice no log, `ix_facilityId_spotId_timestamp_eventId` (nota no ADR-0007).
- **Workers:** `OccupancyAggregationService` (a cada minuto, mas só lê quando fecha uma hora) e o comando `aggregate-occupancy --from --to` (só horas fechadas, em fatias de 24 h).
- **Api:** `GET /api/reports/occupancy`.
- **Configuração:**
  - secção `Analytics` nos Workers e `Reports` na API;
  - `Microsoft.EntityFrameworkCore.Database.Command` em `Information` no `appsettings.Development.json` da API, para o teste manual.
- **Bench:** `bench/analytics/explain-report.sql`, que produz os planos da revisão de índices sobre um ano sintético e termina com `ROLLBACK`.

### Como se testou que os testes apanham os erros (mutações)

- **Isolamento:** acrescentei `AND EXISTS (SELECT 1 FROM parking_facilities)` à consulta com zona → `The_report_reads_only_the_summary_tables` falhou. Repeti com `parking_spots` na consulta sem zona, já com a captura nova (ver os problemas) → falhou.
- **Depois das correções da revisão** (um *script* que copia o ficheiro, aplica a mutação, compila, corre o *namespace* `Analytics` e repõe a cópia). Todas as mutações foram apanhadas:

  | Mutação | Testes que falharam |
  |---|---|
  | M1: `GREATEST(…)` → `EXCLUDED.position` | `The_store_keeps_the_newest_checkpoint_and_the_newest_row` |
  | M2: arredondamento → truncatura | `Sub_second_occupations_are_rounded_in_the_stored_row` e 2 casos de `Occupied_milliseconds_are_rounded_to_the_nearest_second` |
  | M3: `eventId` ascendente no `$sort` | `A_tie_before_the_range_is_broken_by_the_larger_event_id` e `The_state_before_a_range_is_read_with_a_distinct_scan` |
  | F11: sem o `WHERE aggregated_at` no *upsert* | `The_store_keeps_the_newest_checkpoint_and_the_newest_row` |
  | F1: sem a paragem antecipada | o teste do *worker* |
  | F2: o limite de 24 h de volta ao estado inicial | `A_stay_longer_than_a_day_gives_the_same_rows_for_any_range` |

  Em todos os casos, o ficheiro foi copiado para o *scratchpad* antes da mutação e reposto a partir da cópia (lição das Fases 4 e 5).

### Problemas encontrados e tentativas falhadas

1. **Migração vazia.** A primeira `dotnet ef migrations add … --no-build` gerou uma migração vazia: o projeto da API ainda tinha a DLL antiga do Relational (só tinha compilado os Workers). Removi a migração e voltei a gerá-la com compilação.
2. **Analisador (CA1716).** Um parâmetro chamado `to` numa interface quebra a compilação (palavra reservada noutras linguagens .NET). Mudei para `since`/`until`.
3. **Teste de isolamento com um segundo *host*.** Na primeira versão, o teste criava uma segunda instância da API (`WithWebHostBuilder`) com um *logger* que captava o SQL. A suite completa falhou num teste da Fase 5 (`OutboxRelayTests.Consistency_window_publish_lag_and_targets_are_exposed_on_metrics`): o *gauge* `smartparking.target` deixou de aparecer no `/metrics` do outro *fixture*. É o mesmo problema registado na Fase 5: o *meter* «SmartParking» de um *host* descartado afeta a exportação dos outros. Troquei o *logger* por uma subscrição dos eventos de diagnóstico do EF Core (`DiagnosticListener`, evento `CommandExecuted`), filtrada pela porta do PostgreSQL deste *fixture*. Com isto, a suite passou duas vezes seguidas (201/201).
4. **Mutações mal montadas.** Na primeira ronda, a M3 não foi aplicada (o padrão de texto não coincidia com a linha) e a F1 usava `if (false)`, que não compila com os avisos tratados como erros (CS0162). Nesse caso, o «apanhada» que apareceu vinha de binários antigos e não valia nada. Repeti as duas com um padrão correto e com uma condição que compila; os resultados da tabela acima são os da segunda ronda.
5. **Heredoc outra vez.** Ao corrigir os documentos, usei um heredoc no Git Bash, contra a regra registada nas Fases 3 a 5. O Python abriu uma consola interativa, ficou preso 2 min e teve de ser parado. Antes disso, o *script* tinha aplicado só a primeira parte (o ADR-0007) e falhado na segunda; tornei-o idempotente e corri-o de novo a partir do ficheiro.
6. **Índices.** A chave inicial `(facility_id, hour_start)` não servia o relatório sem zona: o plano mostrou um *Parallel Seq Scan*. A chave foi reordenada (ver a revisão de índices). Como a migração já estava aplicada na base de dados local, tive de a desfazer (`database update AddOutboxTraceParent`), removê-la, gerá-la de novo e voltar a aplicá-la.

### Verificação (resultados reais)

- **`dotnet build`:** sem avisos nem erros (`TreatWarningsAsErrors`).
- **`dotnet test`:** antes da revisão de código, 201 testes, 0 falhas, em duas execuções seguidas. **Depois das correções, 211 testes, 0 falhas, em duas execuções seguidas.** Dos 211, 52 são da Fase 6 (*namespace* `Analytics`):
  - 11 do cálculo;
  - 17 da validação;
  - 12 de ponta a ponta;
  - 1 do *worker*;
  - 11 das correções da revisão.

  Há ainda 1 teste novo em `MigrationTests`.
- **Os três testes pedidos no guião:**
  - lugar ocupado das 10h00 às 10h30 → a hora das 10h dá `occupancyRate` 0,5 (`One_spot_occupied_from_10_00_to_10_30_gives_50_percent_in_the_10_hour`);
  - a mesma janela agregada duas vezes dá as mesmas linhas e o mesmo número de linhas (`Aggregating_the_same_window_twice_gives_the_same_rows`);
  - um intervalo que atravessa dias soma corretamente (`A_range_across_days_sums_the_hours_of_each_day`, com uma ocupação das 23h30 às 0h30 repartida pelos dois dias).
- **Mudança de hora:** 29-03-2026 dá 23 horas e 25-10-2026 dá 25 horas, ambos com taxa 1,0 para um lugar sempre ocupado.
- **Verificação rápida do Claude Code, na base de dados do `docker compose`** (não substitui o teste manual do Diogo):
  - `dotnet ef database update` aplicou `AddOccupancyHourlySummary`;
  - `aggregate-occupancy --from 2026-11-15T00:00Z --to 2026-11-16T10:00Z`: 6 parques, 34 horas, 204 linhas e 3 eventos (os das fases anteriores), em 1 519 ms;
  - `GET /api/reports/occupancy?from=…&to=…&granularity=day` respondeu 200 com 8 períodos (4 zonas × 2 dias). No log só apareceu `FROM analytics.occupancy_hourly` (EF Core e Npgsql).
  - Depois das correções, o mesmo comando deu outra vez 204 linhas e 3 eventos (1 627 ms). `--from 11/15/2026` foi recusado («must be a whole hour in ISO 8601»), e `--to 2026-11-17T00:00Z` também («must end at a closed hour»); em ambos os casos o código de saída foi 1.

### Teste manual (passo 4 do guião)

**Feito pelo Claude Code a pedido do Diogo** (16-11-2026, cerca das 13h55 UTC, depois dos commits do código). O relatório foi chamado por HTTP com um *script* (`curl`), não pela interface do Swagger, mas com o mesmo *endpoint*. Base de dados do `docker compose`, com os dados das fases anteriores.

1. **Configuração:** `Microsoft.EntityFrameworkCore.Database.Command` em `Information` no `appsettings.Development.json` da API (já versionado nesta fase).
2. **Agregação:** `aggregate-occupancy --from 2026-11-15T00:00:00Z --to 2026-11-16T13:00:00Z` → 6 parques, 37 horas, 222 linhas, 3 eventos, em 1 449 ms.
3. **API em Development** (`--launch-profile http`), com o log para um ficheiro. Contei as linhas do log antes das chamadas e analisei só as que vieram depois.
4. **Chamadas**, todas com resposta **200**:
   - zona `Baixa`: `hour` (16-11, 0h–13h), `day` (15 e 16-11), `week` (semanas de 9 e 16-11) e `month` (novembro);
   - todas as zonas: `day`.

   Uma sexta chamada, com `from=2026-11-15T00:30Z`, deu **400** com «from: must be a whole hour» e não executou SQL.
5. **Resultado no log:**
   - **5 comandos** `Microsoft.EntityFrameworkCore.Database.Command[20101]`, um por chamada válida, e outros 5 de `Npgsql.Command[2001]`;
   - as únicas cláusulas `FROM`/`JOIN` foram **`FROM analytics.occupancy_hourly`** (10 ocorrências: EF Core e Npgsql);
   - nenhuma linha menciona `parking_*`, `outbox`, `audit`, `sensors`, `tariffs`, `users`, `vehicles`, `payments` nem o MongoDB.

   Exemplo (variante com zona):
   ```
   SELECT zone, period_start, date_add(period_start, @p0::interval, @p1) AS period_end, …
   FROM (
       SELECT zone, date_trunc(@p2, hour_start, @p3) AS period_start, count(DISTINCT hour_start)::int AS hours, …
       FROM analytics.occupancy_hourly
       WHERE zone = @p4 AND hour_start >= @p5 AND hour_start < @p6
       GROUP BY zone, 2
   ) AS periods
   ORDER BY period_start, zone
   ```
6. **Coerência das respostas:**
   - a semana de 9-11 tem `hours` 24 e `expectedHours` 168, porque só um dia estava agregado; o mês de novembro tem `hours` 37 e `expectedHours` 720. O sinal de período incompleto funciona;
   - o dia 15-11 da zona Baixa dá 258,08 min ocupados de 2 880 (taxa 0,0896), o mesmo valor da verificação feita antes da revisão;
   - os tempos (29 ms e 40 ms no primeiro comando) são amostras únicas, locais e sem carga, sem valor estatístico.

### Revisões independentes (passo 6)

- **Índices** (`docs/revisoes/fase-6-revisao-indices.md`, skill `ef-index-advisor`): a consulta sem zona passou de *Parallel Seq Scan* (28,3 ms, 437 004 linhas descartadas) para *Index Scan* na chave reordenada (2,4 ms), sobre 438 000 linhas sintéticas. A consulta de um ano inteiro com todas as zonas continua a ler a tabela toda (≈ 0,9 s, ordenação em disco por causa do `count(DISTINCT)`). Fica como ponto a medir na Fase 9.
- **Código** (`docs/revisoes/fase-6-revisao-codigo.md`, subagente separado): 13 achados (1 alto, 3 médios, 1 baixo-médio, 6 baixos, 2 informativos). Segundo a revisão, o cálculo por hora e o SQL estavam corretos; os problemas estavam no *worker*. Das 5 mutações da revisão, 4 sobreviviam aos testes.

### Correção dos achados da revisão (antes dos commits)

- **1 (alto), custo:** o *worker* relia as últimas 2 h a cada minuto, mesmo sem hora nova. Com a D8 seriam cerca de 94 milhões de documentos lidos por hora, a competir com a ingestão.
  - Correção: a execução termina sem ler nada enquanto não fecha uma hora nova.
  - O estado inicial passa a ser lido com `$sort` + `$group`/`$first` num índice novo do log (`DISTINCT_SCAN`, cerca de uma entrada por lugar; verificado por `explain`).
  - **Custo:** um terceiro índice em cada inserção. Fica para medir na Fase 9.
- **2 (médio), idempotência:** com o limite de 24 h, a mesma hora podia ficar ocupada numa agregação e livre noutra, conforme o início da janela.
  - Correção: o estado inicial deixou de ter limite inferior, e o `StateLookback` (e o seu `TODO`) desapareceu.
  - Fica uma limitação nova: um sensor avariado deixa o último estado para sempre (`TODO(Diogo)`: os sensores emitem *heartbeats*?).
- **3 (médio), memória:** corrigido em parte. O comando divide o intervalo em fatias de 24 h, cada uma com a sua transação; não há *streaming*.
- **4 (médio), eventos muito atrasados:** documentado. A justificação do `AllowedLateness` foi corrigida (sensor → MongoDB, não a D7). O contador de eventos atrasados fica para a Fase 9.
- **5, testes:** M1 a M4 passam a ser apanhadas (ver as mutações).
- **6, fusos:** o fim de cada período vem do PostgreSQL (`date_add`), tal como o início, e o C# deixou de usar o `TimeZoneInfo`.
- **7, validação:** a granularidade tem um `switch` estrito; as datas passam por `IsoInstant` (`TryParseExact`).
- **9, comando:** recusa horas que ainda não fecharam.
- **11, concorrência:** o *upsert* só substitui uma linha por outra agregada depois (`WHERE aggregated_at <= EXCLUDED.aggregated_at`).
- **12, *gauge*:** a frescura usa o checkpoint guardado, e o *fixture* tem uma só instância de métricas.
- **13, premissas:** as premissas sem fonte foram retiradas ou marcadas.
- **8:** documentado no ADR-0010.
- **10:** fica como `TODO(Diogo)`.

### O que ficou por medir

- O custo da agregação sob a carga da D8 (`smartparking.analytics.duration`) e a frescura (`.staleness`), com 5 execuções e CSV (Fase 9).
- O tempo do relatório (p95) por granularidade, e se Q1 passa a *index-only* (`Heap Fetches: 0`) depois do `VACUUM`.
- A comparação com a variante monolítica (secção 6.5): o mesmo relatório calculado diretamente sobre a tabela particionada dos eventos.

### Pontos a levar para a próxima fase

- **`TODO(Diogo)`:**
  - definição da taxa de ocupação no Cap. 3;
  - denominador: todos os lugares registados ou só os que têm sensor `Active` (achado 10);
  - os sensores emitem só mudanças de estado ou também *heartbeats*? Isto decide como tratar um sensor avariado;
  - `AllowedLateness` (2 min);
  - limites do intervalo do relatório (31 dias com `hour`, cerca de 3 anos nas outras);
  - se a D4 se mantém (*data lake* só se o orientador quiser manter a 2.4.5).
- **Consumidor de `smartparking:domain-events`:** continua sem consumidor e o stream continua a crescer. Decidir em que fase entra (o candidato natural é um relatório de sessões e receitas). Na Fase 9, esvaziar o stream antes de cada cenário.
- **Fase 9:**
  - cenário com `aggregate-occupancy` sobre um dia de eventos da D8;
  - custo do terceiro índice do log na ingestão (`ingest-scale`);
  - contador de eventos muito atrasados (achado 4), e reagregar o período afetado no cenário `failover`;
  - `work_mem` do relatório anual; *index-only scan*;
  - testes do *hosted service* e das métricas `smartparking.analytics.*`, que ficaram sem teste.
- **Fase 10 (RNF-05):** retenção do log de eventos. O estado inicial do *worker* é o último evento de cada lugar, por mais antigo que seja. Apagar eventos antigos por TTL pode apagar o único evento de um lugar parado há muito tempo. A retenção tem de manter o último evento de cada lugar, ou passar esse estado para uma tabela (`analytics.spot_state`).

## 16-11-2026 — Fase 7: simulador e evolução de esquema (UC-05, RNF-04)

Objetivo: um gerador de carga reprodutível e a demonstração de que uma nova geração de sensores entra sem paragem. Ramo `phase-7-simulator`.

### Plano e decisões (antes do código)

**D8 fechada.** O Diogo fechou a D8 com o valor por omissão:
- 5 000 lugares, 1 evento por lugar a cada 5 min em média (5 000 / 300 = 16,67 eventos/s);
- picos de 10× (166,7 eventos/s);
- cerca de 1,44 milhões de eventos por dia.

Fica em `bench/scenarios/d8-reference.json`. Estes são os valores a usar no Cap. 3. `TODO(Diogo):` o n.º de parques (20, isto é, 250 lugares por parque) não faz parte da D8 e foi escolhido só para repartir os lugares.

Duas decisões do Diogo sobre o plano, ambas com a opção recomendada:
- **Picos:** são janelas explícitas no ficheiro de cenário (`peakWindows`). Se o fator for maior que 1 e não houver janelas, usam-se os 10 % centrais da execução.
- **Provisão:** o simulador cria os parques, os lugares e os sensores pela API de configuração (RF-06). Não há *seed* SQL, para que o caminho exercitado seja o real (outbox → *relay* → projetor de configuração).

O que encontrei no código antes de começar:
- **O `payload` já era opaco** em todo o caminho do UC-01 desde a Fase 3: validador → `OccupancyEventMessage` (`JsonElement`) → `BsonDocument.Parse`. O projetor do Redis e a agregação da Fase 6 leem só os campos de topo. A previsão era que o UC-05 não precisasse de nenhuma alteração em `src/`, e assim foi.
- **Lugares desconhecidos são ignorados:** o read model descarta eventos de lugares que não conhece (`ApplyOutcome.UnknownSpot`). Por isso, o simulador tem de provisionar primeiro e esperar até o read model conhecer todos os lugares, o que verifica com `GET /api/availability`.
- **ADR 0009 ocupado:** o guião pedia o «ADR 0009», mas esse número é o do *relay* da outbox. O registo desta fase é o **ADR-0011**. É a quinta fase seguida com este desencontro.
- **Skill indisponível:** a skill `memory-allocation-analyzer` (passo 6 do guião) não está instalada nesta sessão. A revisão foi feita por um subagente separado (ver as revisões).

### O que foi feito

- **`tools/SensorSimulator`** (consola .NET 10, sem referências a `src/`):
  - `ScenarioOptions`: as opções `--facilities`, `--spots` (total), `--rate` (taxa base fora de pico, em ev/s para a cidade toda), `--peak-factor`, `--duration`, `--gen2-ratio`, `--gen2-initial-ratio`, `--gen2-ramp-start`, `--gen2-ramp-duration`, `--seed`, `--run`, mais as de envio. O cenário JSON aceita comentários, e a linha de comandos sobrepõe-se ao ficheiro.
  - `OccupancyEventGenerator`: chegadas de Poisson geradas por *thinning*, para que a mudança de taxa nas janelas de pico seja exata.
    - Cada evento escolhe um lugar ao acaso e alterna o seu estado. Todos os lugares começam livres.
    - Cada sensor tem um limiar fixo e passa a gen2 quando a proporção corrente o ultrapassa. Uma vez gen2, nunca volta atrás.
    - Os fluxos aleatórios são independentes: mudar a proporção gen2 não muda a carga, e `--run` só muda os `eventId`.
  - `Xoshiro256`: xoshiro256** em vez de `System.Random` (ver as decisões).
  - `EventJsonWriter`: `Utf8JsonWriter` direto num *buffer* reutilizado. O `payload` leva `sensorGeneration` e, na gen2, `temperature`, `airQuality` e `noise`.
  - `Provisioner`: idempotente, encontra o que já existe pelos nomes determinísticos.
  - `LoadRunner`: ritmo em tempo real, lotes, canal limitado e concorrência.
  - `RunSummary`: enviados, aceites, rejeitados, erros, latência, atraso de despacho, CPU e memória do próprio simulador.
  - `--dry-run`: escreve a sequência em NDJSON, sem HTTP.
- **Cenários:** `bench/scenarios/smoke.json`, `d8-reference.json` e `schema-evolution-pilot.json`.
- **Script do piloto:** `bench/schema-evolution/pilot_latency.py` regista o histograma D7 dos Workers e escreve o CSV.
- **Testes:**
  - `tests/SmartParking.SensorSimulator.Tests` (30, unitários);
  - `SchemaEvolutionTests` (integração, Testcontainers): o próprio simulador provisiona por RF-06, envia gen1 e depois gen2, e o teste verifica o MongoDB (os 3 campos com valor e tipo BSON) e o Redis (o último evento de cada lugar aplicado, todos FREE).
- **Sem alterações em `src/`** (UC-05: confirmado pelo teste de ponta a ponta).

Outras decisões tomadas durante a implementação (ADR-0011):
- **`System.Random` não serve para a reprodutibilidade:** a documentação do .NET não garante a mesma sequência para a mesma semente entre versões do *runtime*. Foi escolhido o xoshiro256** com uma semente SplitMix64, numa implementação de cerca de 60 linhas.
- **Uma casa decimal sempre:** `temperature` e `noise` são escritos sempre com uma casa decimal (`21.0`, não `21`). Assim, o MongoDB guarda sempre Double e não mistura Int32 com Double no mesmo campo (o problema de tipos registado na Fase 3).
- **`--run`:** a Fase 9 repete cada cenário 5 vezes contra a mesma base de dados. Com a mesma semente, os `eventId` repetir-se-iam, e o log e o read model descartá-los-iam como duplicados, distorcendo a medição. `--run` muda só os ids.
- **`--free-at-end` (por omissão ligado):** no fim, envia FREED para cada lugar ocupado. Sem isto, a execução seguinte começaria com metade dos lugares ocupados no read model e o simulador a pensar que estavam livres. Contrapartida: um lote de cerca de 2 500 eventos no mesmo instante (ver o piloto).

### Como se testou que os testes apanham os erros (mutações)

Os ficheiros foram copiados para o *scratchpad* antes de cada mutação e restaurados a partir da cópia. Confirmou-se que cada mutação compilava (`mutate.py`).

| Mutação | Resultado |
|---|---|
| M1: o lugar não alterna (`occupied[spot] = true`) | apanhada (`A_spot_is_never_occupied_twice_in_a_row`) |
| M2: chegadas com semente do relógio (`Environment.TickCount64`) | **sobreviveu na primeira versão**, apanhada depois de acrescentar `Seed_42_produces_the_recorded_sequence` |
| M3: `noise` retirado do `payload` | apanhada (`Event_json_has_the_minimal_schema_and_the_gen2_payload` e `SchemaEvolutionTests`) |

### Problemas encontrados e tentativas falhadas

1. **O teste «mesma semente → mesma sequência» não apanhava uma semente do relógio (M2).** As duas execuções do teste correm no mesmo *tick* do `TickCount64` (cerca de 15 ms no Windows) e recebem a mesma semente. Correção: um teste com valores fixos («golden») para a semente 42: os 3 primeiros eventos, o último `eventId` e o total de 308. Este teste também fixa a sequência entre máquinas e versões do .NET.
2. **Conflito de `Program`:** o teste de integração referencia a API e o simulador, e ambos tinham um `Program` global (*top-level statements*), o que deu `CS0433`. O simulador passou a ter `SimulatorProgram.Main` no seu *namespace*.
3. **Nome em conflito no `Topology`:** um método estático e uma propriedade do *record* com o mesmo nome (`CS0102`). O método passou a chamar-se `MapSpotsToFacilities`.
4. **Um teste que comparava o JSON gerado byte a byte** usava `Guid.NewGuid()` na topologia falsa e teria sempre falhado. Foi corrigido antes da primeira execução, com uma topologia determinística.
5. **O comando `mongosh` do guião volta a falhar sem credenciais** («Command aggregate requires authentication»), como já tinha acontecido na Fase 3. É preciso `-u smartparking -p smartparking --authenticationDatabase admin`.
6. **`sed` com barras invertidas no `.csproj`** não alterou nada, em silêncio. A edição foi feita com a ferramenta de edição.
7. **Afirmação provisória corrigida:** a rastreabilidade do RNF-04 foi escrita enquanto o piloto corria, já com «0 erros durante a rampa». Confirmou-se depois com o resumo real (abaixo). Fica o registo de que o texto foi escrito antes do dado.

### Verificação (resultados reais)

- `dotnet build`: sem avisos nem erros.
- `dotnet test` (antes da revisão): **239 testes, 0 falhas** (35 s).
- Depois da revisão:
  - `SmartParking.SensorSimulator.Tests`: 30/30;
  - `SchemaEvolutionTests`: 1/1.

**Teste manual (passo 4 do guião)**, com a API e os Workers a correr e o comando do guião (`--facilities 2 --spots 50 --rate 5 --duration 60 --seed 42`):
- 308 eventos enviados, 308 aceites (202), 0 rejeitados, 0 erros, em 231 pedidos;
- pedidos: p50 1,4 ms, p95 2,6 ms;
- CPU do simulador: 0,92 s (1,5 % de um núcleo);
- `db.occupancy_events.countDocuments()` passou de 7 para **315** (+308).
- A provisão (2 parques, 50 lugares, 50 sensores) levou 1,8 s; o read model conhecia todos os lugares 1,1 s depois.

**Ensaio piloto do UC-05 (passo 5 do guião)**, `bench/scenarios/schema-evolution-pilot.json`:
- **Carga:** 5 000 lugares, 16,67 ev/s, 600 s.
- **Rampa gen2:** de 0 % a 50 % entre os 300 s e os 360 s.
- **Provisão:** 20 parques, 5 000 lugares e 5 000 sensores criados em 10,3 s.
- **Simulador:**
  - 12 472 eventos enviados (gen1 8 896, gen2 3 576), 12 472 aceites, **0 rejeitados, 0 erros, 0 pedidos sem resposta** (4 864 pedidos, todos 202);
  - pedidos: p50 1,3 ms, p95 2,0 ms, máx. 23,4 ms;
  - atraso máximo de despacho 111,4 ms (inclui a janela de 100 ms); 0 ms de espera no canal;
  - CPU do simulador: 2,62 s em 600 s (0,4 % de um núcleo); 20,6 MiB alocados; GC 2/0/0.
- **MongoDB:** 12 472 documentos do período. Dos 3 576 gen2, os 3 576 têm `temperature` (double), `airQuality` (int) e `noise` (double).
- **Latência D7 por fase** (histograma dos Workers, amostrado a cada 15 s, `results/schema-evolution-pilot/2026-11-16_pilot.csv`). Os valores são o limite superior do *bucket*:

  | Fase | n | p50 | p95 | p99 |
  |---|---|---|---|---|
  | antes da rampa (0–300 s) | 4 745 | ≤ 0,15 s | ≤ 0,20 s | ≤ 0,25 s |
  | rampa (300–360 s) | 986 | ≤ 0,15 s | ≤ 0,20 s | ≤ 0,20 s |
  | depois da rampa (360–630 s) | 6 741 | ≤ 0,15 s | ≤ 0,25 s | ≤ 0,30 s |
  | execução completa | 12 472 | ≤ 0,15 s | ≤ 0,25 s | ≤ 0,30 s |

- **Leitura honesta:**
  - Não houve erros nem pedidos sem resposta durante a transição. A latência D7 ficou muito abaixo de 1 s (RNF-01) em todas as fases.
  - O p95 «depois da rampa» subiu um *bucket* (0,20 → 0,25 s), mas essa fase inclui os 2 449 FREED do `--free-at-end` (número corrigido na revisão de código; antes estava «cerca de 2 470»), todos enviados no mesmo instante (achado 6 da revisão). Com estes dados, **não é possível atribuir a diferença à geração 2**.
  - A precisão também é limitada: *buckets* de 50 ms e fronteiras das fases com cerca de 15 s de incerteza.
  - É uma execução única (n = 1), sem valor estatístico. A medição oficial (5 execuções, `--free-at-end false`, comparação com a migração monolítica) é o cenário `schema-evolution` da Fase 9.

**Verificação da revisão:** 60 s a 5 000 ev/s, gen2 em rampa, `--free-at-end false`, contra a mesma API:
- 300 162 eventos, todos aceites, 0 erros, 1 272 pedidos;
- carga mantida em ciclo aberto, com 0 ms de espera no canal;
- «Scheduled → answer»: p95 117,5 ms;
- CPU do simulador: 1,89 s, ou seja, **3,1 % de um núcleo** (0,26 % das 12 lógicas); 6,6 MiB alocados.
- Este valor de CPU é **mais alto** do que a estimativa do revisor (0,5–1,5 %) e do que o limiar que ele sugeriu (< 2 %). Continua a ser desprezável face à máquina.
- Não se verificou se os Workers acompanharam esta carga (fora do âmbito desta verificação; é a Fase 9, `ingest-scale`).

### Revisões independentes (passo 6)

- **Alocações e CPU** (`docs/revisoes/fase-7-revisao-alocacoes.md`, subagente só de leitura, sem executar nada durante o piloto): 11 achados (2 altos, 3 médios, 3 baixos, 3 informativos).
  - **Conclusão:** o CPU e as alocações não eram o problema. O problema era a **forma de medir**.
  - **Corrigidos:**
    - 1, omissão coordenada: a nova métrica «Scheduled → answer» parte do instante previsto;
    - 2, execução saturada marcada «NOT KEPT»;
    - 3, atraso de despacho subestimado depois de um bloqueio;
    - 4, um *sender* que falhava deixava o despacho bloqueado para sempre;
    - 5, Ctrl+C perdia o resumo.
  - **Documentados:** 6 a 11.
  - **Testes novos:** `Run_is_marked_saturated_when_the_offered_load_was_not_kept` e `Cancelled_run_is_never_clean`. As correções 1, 3 e 4 não têm teste unitário próprio (dependem de tempo e de falhas de rede); foram verificadas no resumo da execução a 5 000 ev/s.
- **Revisão de código do *diff*** (passo 6 do fluxo por fase): feita depois dos primeiros commits; ver «Revisão independente do código e correções», no fim desta fase.

### O que ficou por medir

- RNF-04 oficial: cenário `schema-evolution` com 5 execuções (`--run 1..5`), `--free-at-end false` e a mesma rampa, comparado com a migração da variante monolítica (Fase 8).
- A capacidade dos Workers a 5 000 ev/s (atraso dos *streams*, D7 sob carga).

### Pontos a levar para a próxima fase

- **`TODO(Diogo)`:**
  - n.º de parques do cenário D8;
  - contador de propriedades de topo desconhecidas na API (ADR-0011, consequências);
  - os sensores reais terão *heartbeats*? (vem da Fase 6).
- **Fase 8 (variante monolítica):** o UC-05 faz-se com uma migração (`ALTER TABLE … ADD COLUMN` na tabela particionada). O simulador e o cenário servem para as duas variantes; só muda a API alvo.
- **Fase 9:**
  - correr com `--concurrency` suficiente e descartar as execuções marcadas «NOT KEPT»;
  - registar o CPU do simulador em cada execução (já vem no resumo);
  - medir a latência D7 com amostragem mais fina ou com *traces*, e não com *buckets* de 50 ms;
  - esvaziar `smartparking:domain-events` antes de cada cenário (da Fase 6).
- **Dados de teste locais:** o MongoDB e o PostgreSQL de desenvolvimento ficaram com cerca de 313 000 eventos e com os parques `sim-adhoc-*` e `sim-schema-evolution-pilot-*`. Limpar antes da Fase 9 (`docker compose down -v`), se se quiser partir do zero.

### Revisão independente do código e correções (antes do PR)

Revisão do *diff* `af2725a..1912308` por um subagente separado (`docs/revisoes/fase-7-revisao-codigo.md`). Encontrou 0 achados altos, 4 médios, 7 baixos e 4 afirmações erradas nos documentos. M1, M2 e M3 foram reproduzidos de forma independente (execução em seco e procura no código). O Diogo pediu para corrigir todos.

- **M1, dois eventos do mesmo lugar no mesmo milissegundo:** no `d8-reference.json` (semente 42) havia dois pares a 0,29 ms e a 0,43 ms. O read model desempata pelo `eventId`, que é aleatório, e podia deixar o lugar ocupado. Correção: intervalo mínimo de 1 ms por lugar (`MinGapSeconds`); os FREED de fecho passam a ficar 1 ms depois do fim. Tem teste, e a mutação sem o intervalo é apanhada.
- **M2, fases do primeiro piloto desfasadas:** o script usava a última amostra antes de cada fronteira (de 15 em 15 s). As fases reais foram cerca de 0–285, 285–345 e 345–630 s, e não 0–300, 300–360 e 360–630 s como estava escrito acima. As contagens do CSV (4 745 / 986 / 6 741) coincidem com esses cortes na execução em seco. O CSV passou a chamar-se `2026-11-16_1416.csv` e os rótulos foram corrigidos; os valores não mudaram. **A tabela «Latência D7 por fase» acima tem por isso os rótulos errados:** onde diz «antes da rampa (0–300 s)», «rampa (300–360 s)» e «depois da rampa (360–630 s)», deve ler-se 0–285 s, 285–345 s e 345–630 s. O script passou a ler o histograma exatamente nas fronteiras.
- **M3:** `--provision false` não fazia nada. Agora só procura, e termina com o código 3 se faltar algo.
- **M4:** todos os cenários punham o primeiro parque no ponto dos testes de integração. Agora há um bloco do mapa por cenário (FNV-1a do nome), e a espera pelo read model usa a localização real devolvida pela API.
- **L1 a L7:**
  - tratamento de argumentos e erros (`peakWindows` nulo, semente fora do intervalo de `long`, `--scenario` sem valor, Ctrl+C na provisão);
  - rampa a descer recusada;
  - código de saída 4 para execuções saturadas;
  - «aceite» explicado no resumo e no README;
  - o teste *golden* passou a fixar também um evento da geração 2;
  - `--summary-csv` escreve o resumo no formato da §6.
- **Documentos:**
  - os FREED de fecho do primeiro piloto foram **2 449**, não «cerca de 2 470»;
  - o RNF-04 na rastreabilidade deixou de dizer que a medição é desta fase;
  - «0 s de indisponibilidade» foi substituído por «0 erros e 0 pedidos sem resposta», porque nenhuma medição está em segundos;
  - os 1,44 milhões de eventos por dia da D8 são «sem contar os picos».

### Problemas encontrados durante a correção

1. **Ids repetidos entre execuções com parâmetros diferentes.** A verificação a 5 000 ev/s e o segundo piloto (14:46) usaram a mesma semente e o mesmo `--run 2`. O fluxo dos ids dependia só da semente e de `--run`, e os dois geraram os mesmos `eventId`. A API aceitou os 12 472 eventos do piloto com 202, mas o MongoDB guardou **0**, porque o índice único descartou-os todos como duplicados. O read model aceitou-os (deduplica por lugar), por isso a latência D7 até foi medida. O resumo do simulador não podia detetar isto: «aceite» é o 202 da API (achado L5). Correção: os ids dependem também de todos os parâmetros (`ScenarioOptions.Describe()`), com um teste de regressão. A mutação que retira essa dependência faz falhar 2 testes. O CSV desse piloto foi apagado.
2. **Duas sessões do Claude a trabalhar na mesma fase ao mesmo tempo.** Depois de um reinício da sessão, ficaram duas sessões a corrigir a revisão. Cada uma correu o seu piloto contra a mesma API: 14:58–15:09 (`--run 2`, `--free-at-end false`) e 15:00–15:11 (`--run 3`). Nos dois casos, todos os eventos ficaram guardados por `eventId` (10 023 e 12 472). Mas o histograma D7 contou os eventos das duas execuções (20 709 no segundo), por isso a **latência desses dois pilotos está contaminada**. Os CSV ficaram em `results/` (`2026-11-16_1458.csv`, `2026-11-16_1500.csv`) com esta nota. Também foi interrompido aos ~60 s um piloto `--run 4` (15:11–15:12), sem CSV. O Diogo decidiu qual das sessões terminava a fase, e a outra parou.
3. **Parar a tarefa não para o processo.** A primeira sessão julgou «órfão» o piloto que a outra estava a correr. Ao parar uma tarefa de fundo, é preciso confirmar na lista de processos que o `dotnet` filho também parou.
4. **Bateria completa com a API ligada:** a compilação falha (DLL bloqueadas: `MSB3021`) e o `RedisUnreachableTests` falhou uma vez. Com a API e os Workers parados, a bateria passa (255/255).

### Ensaio piloto de referência (15:14, `results/schema-evolution-pilot/2026-11-16_1514.csv`)

Cenário `schema-evolution-pilot` com `--run 5 --free-at-end false`, sem outra carga na API (confirmado: o histograma contou exatamente os eventos enviados).

- **Simulador:**
  - 10 023 eventos enviados (gen1 7 660, gen2 2 363) em 4 842 pedidos; 10 023 aceites, **0 rejeitados, 0 erros, 0 sem resposta**;
  - carga mantida (0 ms de espera no canal);
  - pedidos: p50 1,2 ms, p95 1,7 ms; instante previsto → resposta: p95 104,3 ms;
  - CPU do simulador: 2,83 s em 600 s (0,5 % de um núcleo).
- **MongoDB:** contagem pelos `eventId` gerados em seco: **10 023 de 10 023** guardados; **2 363 de 2 363** gen2 com `temperature` (double), `airQuality` (int) e `noise` (double).
- **Latência D7** (fronteiras exatas, limite superior do *bucket*):

  | Fase | n | p50 | p95 | p99 |
  |---|---|---|---|---|
  | antes da rampa (0–300 s) | 4 978 | ≤ 0,15 s | ≤ 0,20 s | ≤ 0,25 s |
  | rampa (300–360 s) | 991 | ≤ 0,15 s | ≤ 0,20 s | ≤ 0,25 s |
  | depois da rampa (360–630 s) | 4 054 | ≤ 0,15 s | ≤ 0,20 s | ≤ 0,25 s |

- **Leitura:**
  - sem o lote de FREED de fecho e sem sobreposição, o p95 é igual nas três fases. A subida de um *bucket* vista no primeiro piloto não reapareceu, o que está de acordo com a explicação dada então (o lote final), mas não a prova;
  - continua a ser uma execução única, com *buckets* de 50 ms;
  - o que este ensaio mostra é: a transição para a geração 2 não causou erros, pedidos sem resposta nem perda de campos, e a latência D7 não mudou de forma visível. A medição do RNF-04 continua a ser a Fase 9.

### Verificação final

- `dotnet build`: sem avisos.
- `dotnet test` (com a API e os Workers parados): **255 testes, 0 falhas** (35 s), dos quais 43 do simulador.
- Mutações desta ronda: sem o intervalo mínimo → 1 teste falha; ids sem os parâmetros → 2 testes falham.


## 16-11-2026 — Fase 8: variante monolítica (§7, Cap. 6.5)

Objetivo: a mesma API a correr só com PostgreSQL, para a comparação da secção 6.5, numa baseline bem feita e não propositadamente fraca. Ramo `phase-8-monolithic`, PR #9.

### Plano e decisões (antes do código)

O Diogo aprovou o plano e escolheu, nos quatro pontos, a opção recomendada:

| Ponto | Escolha |
|---|---|
| Consulta geográfica | **PostGIS** com índice espacial (não a fórmula de Haversine em SQL) |
| `IEventBus` em PostgreSQL | **escrita síncrona**: o evento e o estado do lugar são gravados numa transação antes do 202 (não uma fila com consumidores) |
| Disponibilidade | **tabela `spot_state`** mantida na mesma transação (não o último evento calculado do log a cada pedido) |
| UC-05 | **só colunas tipadas**, sem `jsonb`, e migração que acrescenta `temperature`, `air_quality` e `noise` |

O que encontrei antes de começar:
- **ADR 0010 ocupado.** O guião pedia o «ADR 0010», mas esse número é o das tabelas de resumo (Fase 6). O registo desta fase é o **ADR-0012**. É a sexta fase seguida com este desencontro.
- **Ferramentas indisponíveis.** O agente `dotnet-architecture-reviewer` e a skill `ef-migration-reviewer` (passo 6) não estão instalados nesta sessão. As revisões foram feitas por dois subagentes gerais com o mesmo enunciado.
- **Os testes estavam muito ligados ao híbrido.** Dos 144 testes de integração, 37 testam internos do Redis, dos *streams* ou do MongoDB (chaves, *consumer groups*, planos do MongoDB). Não fazem sentido no modo monolítico e passaram a ser `[HybridFact]`. Os outros correm nos dois modos.

### O que foi feito

- **`Persistence:Mode`** (`Hybrid` por omissão; um valor desconhecido falha no arranque), lido só nos dois *composition roots*. No modo monolítico, a API e os Workers não registam MongoDB, Redis, *streams*, projetores nem reconstrução. A *readiness* verifica só o PostgreSQL, e `rebuild-read-model` termina com o código 1.
- **`Infrastructure.Relational/Monolithic/`:**
  - `PostgresEventBus`, `MonolithicOccupancyStore`, `PostgresAvailabilityQueries`;
  - `OccupancyPartitionMaintenance`, `EnvironmentalColumnsProbe`, `MonolithicOptions`, `MonolithicRegistration`;
  - nos Workers, `PartitionMaintenanceService`.
- **Migrações:**
  - `AddMonolithicSchema`: PostGIS no *schema* `postgis`; `monolith.occupancy_events` particionada por mês, com DEFAULT e os mesmos índices do log MongoDB; a função de partições; `spot_state` com `fillfactor = 70`; o índice GiST de expressão em `parking_facilities`;
  - `AddEnvironmentalColumns` (UC-05), com `lock_timeout = 5s`.
- **Imagem:** `postgis/postgis:16-3.5-alpine`, fixada por digest, nas duas variantes. Um `tmpfs` vazio em `/docker-entrypoint-initdb.d` esconde o script da imagem, no `docker compose` e nos testes.
- **Testes:**
  - `TestMode` lê `Persistence__Mode`; `HybridFact` e `HybridTheory` marcam os testes só híbridos;
  - `ApiFixture`, `IngestionFixture` e `AvailabilityFixture` sabem correr em modo monolítico, sem arrancar o MongoDB nem o Redis e sem lhes passar *connection strings*. Se algum serviço híbrido ainda estivesse registado, a API falharia no arranque;
  - `MonolithicVariantTests` (12 testes) corre sempre em modo monolítico, com o seu próprio contentor;
  - `MigrationTests` verifica os *schemas* novos e o `Down()`.
- **Ensaio:** `bench/schema-evolution/monolithic_migration.py` (aplica a migração a meio da carga e amostra os *locks*); `pilot_latency.py` ganhou `--variant`; o SQL da migração foi gerado para `bench/schema-evolution/monolithic-add-environmental-columns.sql`.
- **ADR-0012**, `CLAUDE.md` (comandos), README do simulador, `appsettings.json` da API e dos Workers.

### Como se testou que os testes apanham os erros (mutações)

Cada ficheiro foi copiado para o *scratchpad* antes da mutação e reposto a partir da cópia. Em cada mutação, confirmei que o padrão existia uma única vez e que o projeto compilava.

| Mutação | Teste que falhou |
|---|---|
| Sem a guarda `WHERE (s."timestamp", s.event_id) < …` no *upsert* do `spot_state` | `A_late_event_does_not_overwrite_a_newer_state_and_a_repeat_is_a_duplicate` |
| Expressão geográfica da consulta sem `ST_SetSRID` (deixa de coincidir com o índice) | `The_availability_query_can_use_the_gist_index_on_the_facility_location` |
| Um serviço do StackExchange.Redis registado no modo monolítico | `The_container_has_no_hybrid_service_and_readiness_checks_only_postgres` |
| Ordem ascendente na sonda do *skip scan* | `The_skip_scan_state_before_an_instant_equals_the_direct_distinct_on` |

### Problemas encontrados e tentativas falhadas

- **Voltei a usar um *heredoc* com `python -`** para um *script* de edição, contra a regra registada desde a Fase 6. O comando ficou pendurado 2 min até ser parado. A edição foi feita depois com o Edit e com *scripts* em ficheiro (`< /dev/null`). É a quarta vez que este erro aparece no diário.
- **Uma constante `short` dentro de um *raw string* interpolado `const`** não compila (CS0133: só *strings* são constantes nesse contexto). O valor `2` (FREED) ficou literal no SQL, com referência a `MonolithicSchema.Freed` no comentário.
- **A extensão PostGIS em `public` partia o teste das migrações.** O script de arranque da imagem cria `postgis`, `topology` e `tiger` em `public`, e com eles a tabela `spatial_ref_sys`. O teste que espera em `public` só a tabela de histórico, depois de um `Down()` completo, falharia. Solução: esconder o script com `tmpfs`, criar o PostGIS no *schema* `postgis` e qualificar todas as chamadas.
- **Uma falha que só aparecia com a suite completa no modo híbrido.** `OutboxRelayTests.Consistency_window_publish_lag_and_targets_are_exposed_on_metrics` passava sozinho e falhava (2 vezes em 2) com todos os testes. A causa foi o novo `MonolithicFixture`: o seu *host* cria os mesmos instrumentos «SmartParking» e, ao ser descartado com as suas métricas, o OpenTelemetry deixava de exportar os instrumentos com o mesmo nome nos outros *hosts*. É o mesmo efeito da Fase 5. Solução: dar a esse *fixture* uma `IMeterFactory` que vive tanto quanto o processo.
- **A imagem PostGIS traz o PostgreSQL 16.15**, uma versão abaixo do 16.16 usado até à Fase 7. O volume de dados existente arrancou sem problemas (mesma versão maior). A comparação usa a mesma imagem nas duas variantes, mas os resultados das Fases 1 a 7 foram obtidos com o 16.16.
- **A migração `AddMonolithicSchema` foi alterada depois de aplicada** à base de desenvolvimento (correções da revisão). A base foi levada de volta a `AddOccupancyHourlySummary` e atualizada de novo. Os dados do ensaio perderam-se, mas o CSV já estava escrito.
- **A primeira versão do ensaio não registava as linhas na DEFAULT.** O achado 3 da revisão chegou durante a execução. A contagem (0) foi feita à mão depois do ensaio, e o *script* passa a registá-la.

### Verificação (resultados reais)

- `dotnet build`: sem erros nem avisos.
- `Persistence__Mode=Monolithic dotnet test`: **264 testes, 226 passam, 38 ignorados, 0 falhas.** Os 38 ignorados são internos do Redis/MongoDB (`HybridFact`/`HybridTheory`).
- `Persistence__Mode=Hybrid dotnet test`: **267 testes, 267 passam, 0 falhas.** O total é diferente porque a teoria da *readiness* tem 3 linhas no modo híbrido e 1 no monolítico.
- **Ensaio manual** com a API e os Workers em modo monolítico: as duas *readiness* com o `postgres` como única verificação. Os Workers criaram as partições no arranque.

### Ensaio do UC-05 no modo monolítico (passo 5; 20:12, `results/schema-evolution-pilot/2026-11-16_2011.csv`)

A mesma carga do ensaio híbrido de referência da Fase 7 (`2026-11-16_1514.csv`): cenário `schema-evolution-pilot`, `--run 5`, `--free-at-end false`, 5 000 lugares, 16,67 ev/s, rampa da geração 2 de 0 % a 50 % entre os 300 s e os 360 s. A base estava em `AddMonolithicSchema`. A migração `AddEnvironmentalColumns` foi aplicada aos **t0 + 305,0 s**, com `psql` dentro do contentor.

| | Híbrido (Fase 7, 15:14) | Monolítico (Fase 8, 20:11) |
|---|---|---|
| Eventos enviados / aceites | 10 023 / 10 023 | 10 023 / 10 023 |
| Rejeitados, erros, sem resposta | 0, 0, 0 | 0, 0, 0 |
| Guardados | 10 023 (MongoDB) | 10 023 (`monolith.occupancy_events`) |
| Leituras da geração 2 guardadas | 2 363 de 2 363 | **2 360 de 2 363** |
| Pedidos HTTP: p50 / p95 / máx. | 1,2 ms / 1,7 ms / — | 3,9 ms / 5,9 ms / 114,2 ms |
| Instante previsto → resposta, p95 | 104,3 ms | 107,8 ms |
| D7 p95 antes / durante / depois da rampa | ≤ 0,20 / ≤ 0,20 / ≤ 0,20 s | ≤ 0,15 / ≤ 0,15 / ≤ 0,15 s |
| Migração | nenhuma | 0,468 s de parede (código 0) |

- **Bloqueios:** 1 909 amostras de `pg_stat_activity` (cerca de uma a cada 10 ms) durante 20 s à volta da migração, **sem nenhuma sessão à espera de *lock***. A esta taxa (16,7 ev/s, inserções de poucos ms), a tabela está quase sempre livre, por isso o `ACCESS EXCLUSIVE` foi obtido de imediato. Os 0,468 s incluem o arranque do `docker compose exec` e do `psql`; o `ALTER` em si é só de catálogo.
- **Erros e indisponibilidade:** 0 pedidos com erro e 0 sem resposta durante toda a execução, incluindo a migração.
- **Perda de dados:** 3 leituras da geração 2 ficaram sem colunas. Chegaram entre o início da rampa (300 s) e o primeiro evento gravado com `temperature` (t0 + 310,8 s: migração aos 305 s, mais até 5 s até a escrita detetar as colunas novas). No híbrido não se perdeu nenhuma. Antes da migração, o esquema explícito não tem onde as guardar.
- **Partição DEFAULT:** 0 linhas. 10 023 eventos ocupam 3,5 MB na partição de novembro.
- **Leitura, com cuidado:**
  - é uma execução única, com *buckets* de 50 ms;
  - a latência D7 dos dois lados não mede exatamente a mesma coisa. No monolítico mede-se até ao *commit*; no híbrido, até à atualização do Redis depois do bus;
  - esta execução usou a regra antiga de amostragem do D7 (todos os eventos inseridos), antes da correção M1. Como o simulador não gera eventos atrasados, o efeito deve ser pequeno, mas não foi medido;
  - **a esta carga, a migração do monolítico não causou uma indisponibilidade observável.** O custo que o RNF-04 compara é, por agora, a perda de 3 leituras e o passo operacional, e não uma paragem. Falta ver se o mesmo acontece no pico de 10× e com a tabela grande (Fase 9). Com inserções concorrentes, o `ACCESS EXCLUSIVE` pode ficar na fila e parar a ingestão até ao `lock_timeout` de 5 s.

### Revisões independentes (passo 6)

`docs/revisoes/fase-8-revisao-arquitetura-e-migracoes.md`:
- **Arquitetura:** só a infraestrutura muda, e não há achados altos. Foram corrigidos M1 (regra de amostragem do D7), M3 (três testes que não precisavam de estar marcados como só híbridos, mais o teste do 503), L1 (verificação das colunas depois de um `Down()`), L2 (lugares desconhecidos) e L6 (comentários). M2 (o log monolítico guarda menos) ficou documentado, com um `TODO(Diogo)`.
- **Migrações:** um achado **alto**. O estado antes de um instante usava `DISTINCT ON`, que no PostgreSQL 16 lê todo o histórico do parque, e passou a usar um *skip scan* recursivo, com teste e mutação. Também foram corrigidos o `lock_timeout` na criação de partições (2), o aviso da DEFAULT (3), a ordem na inserção (6) e o 42703 (7). Os restantes ficaram documentados no ADR-0012.

### Pontos a levar para a próxima fase

- **`TODO(Diogo)`:**
  - coluna `sensor_generation` na tabela monolítica (M2)?
  - confirmar com o orientador a ameaça à validade «PostgreSQL com `jsonb` também dispensaria a migração» (ADR-0012).
- **Fase 9:**
  - repetir o ensaio do UC-05 nas duas variantes, 5 vezes, também no pico de 10× e com a tabela já grande;
  - repetir os resultados híbridos na imagem nova (PostgreSQL 16.15);
  - dimensionar o *pool* do Npgsql face às `max_connections` (revisão da Fase 2b). No monolítico, toda a ingestão passa pelo *pool* da API, por isso o problema aparece mais cedo;
  - métrica de repetições por *deadlock* no `PostgresEventBus`;
  - um *bucket* mais fino ou *traces* para o D7.
- **Dados locais:** a base de desenvolvimento ficou em `AddEnvironmentalColumns`, com as tabelas `monolith.*` vazias. O MongoDB e o Redis mantêm os dados das fases anteriores.

## 16-11-2026 — Fase 9a: infraestrutura de medição (§6, §9 passo 9)

Objetivo: um comando por cenário que gere CSV comparáveis entre a variante híbrida e a monolítica. Os *scripts* só recolhem dados e não interpretam resultados. Ramo `phase-9a-bench-infra`, PR #10.

### Preparação da máquina (passo 1 do guião)

TODO(Diogo): registar aqui o estado da máquina durante a aceitação (21:23–21:34): browser e IDE fechados? carregador ligado? atualizações automáticas desligadas? A aceitação foi lançada a partir do Claude Code, com a aplicação de ambiente de trabalho aberta. O `meta.json` regista a máquina detetável: Windows 11 Pro 10.0.26100, 12 CPU lógicos (AMD Family 23 Model 113), 24 GiB de RAM; Docker Desktop com 12 CPU e cerca de 11,7 GiB.

### Plano e decisões (antes do código)

O Diogo aprovou o plano com as duas opções recomendadas:

| Ponto | Escolha |
|---|---|
| k6 (D6, valor por omissão) | **contentor `grafana/k6:1.4.2`**, fixado por *tag* e *digest* no `docker-compose.yml` (perfil `bench`), e não uma instalação no anfitrião. Custo: os pedidos passam pela rede do Docker Desktop (`host.docker.internal`), o que soma esse salto a cada latência medida pelo k6. |
| *Pool* do Npgsql (achado 1 da revisão da Fase 2b) | **`Maximum Pool Size=40` por processo**, na configuração versionada: API 40 + Workers 40 = 80, abaixo de `max_connections` = 100. |

Os achados 3, 4 e 7 da revisão da Fase 2b (idas e voltas à BD por comando, índice `(zone, valid_from)`, `AddDbContextPool`) ficam para depois das primeiras medições. Só se tratam se um alvo do RNF-01 falhar (passo 6 do guião).

### O que foi feito

- `bench/run.sh <cenario> [--variant] [--runs 5] [--warmup 60] [--allow-dirty]`. Em cada execução:
  - `down -v`/`up -d --wait`;
  - migrações;
  - API e Workers a partir das DLL de Release, com `ASPNETCORE_ENVIRONMENT=Benchmark` e o `Persistence__Mode` da variante;
  - dados base D8 (20 parques e 5 000 lugares e sensores, através do simulador);
  - aquecimento;
  - parte medida;
  - linhas no CSV da §6.
- **Ambiente `Benchmark`** (`appsettings.Benchmark.json` na API e nos Workers): as mesmas credenciais do Development, mas sem o registo de cada instrução SQL, que o Development tem desde a Fase 6 e que somaria I/O a cada pedido medido.
- **Recolha** (`bench/lib`): todos os recolhedores escrevem as colunas da §6, no formato `0.###` do simulador.
  - `k6_to_csv.py`: todas as métricas do `--summary-export`, com sub-métricas por etiqueta.
  - `e2e_latency.py`: janela do histograma D7, somando a API e os Workers, porque o histograma é registado nos Workers no híbrido e na API no monolítico.
  - `relabel_csv.py`: o resumo do simulador, com o cenário e a execução do *bench*.
  - `csv_row.py`: os códigos de saída.
  - `write_meta.py`: *commit*, *diff*, ambiente, imagens, ferramentas e máquina.
- **Cenário `smoke`**: 30 s de leituras do UC-03 com o k6 (20/s), em simultâneo com eventos do UC-01 do simulador (5/s), mais a janela D7. Serve para provar a cadeia; não é uma medição para a tese.
- `.gitattributes` com `*.sh text eol=lf`, porque com `core.autocrlf=true` um `run.sh` em CRLF parte no bash.
- Testes: `python -m unittest discover -s bench/tests` (12).

### Problemas encontrados e tentativas falhadas

- **Primeira execução: a API não arrancou** (`DirectoryNotFoundException`). Um `--contentRoot` relativo é resolvido a partir da pasta da DLL, não do diretório atual. Passou a absoluto, na forma nativa (`pwd -W`).
- **O resumo do k6 exporta sozinho `http_req_duration{expected_response:true}`.** Com só o valor da etiqueta no nome, ficava `k6_http_req_duration_true_*`, que é ambíguo. Os nomes passaram a levar a chave e o valor: `..._expected_response_true_...` e `..._name_availability_...`.
- **Um *gauge* do k6 (`vus`) era tratado como *trend***, porque também tem `min` e `max`. Um teste apanhou o erro antes da primeira medição.
- **O primeiro *commit* deixou o `run.sh` sem bit de execução e sujeito a CRLF.** Corrigido com `git update-index --chmod=+x` e com o `.gitattributes`.
- **A primeira aceitação foi deitada fora.** A revisão independente chegou durante essa execução e mostrou que a janela D7 estava contaminada (achados 1 e 2). Ao pará-la, o `TaskStop` terminou só o processo pai: o `run.sh` continuou vivo, e a API, os Workers e um contentor do k6 ficaram a ocupar as portas. Era exatamente o cenário dos achados 4 e 9, que foram corrigidos a seguir. Os processos foram identificados pelo PID (`dotnet.exe`) e terminados à mão. Os resultados parciais foram retirados de `results/` e não foram usados.
- **Um ficheiro novo, ainda sem *commit* (o relatório da revisão), teria feito a sessão monolítica recusar-se a correr**, por árvore suja. Foi tirado do repositório até a aceitação terminar. Consequência prática: durante uma sessão de medição não se escreve no repositório, exceto em `results/`.
- **No Windows, o `os.environ` põe em maiúsculas os nomes das variáveis**, por isso o `meta.json` regista `PERSISTENCE__MODE` e não `Persistence__Mode`. Não tem efeito: o Windows e a configuração do .NET ignoram maiúsculas e minúsculas nos nomes. O teste passou a comparar em maiúsculas.

### Verificação (resultados reais)

- `dotnet build`: 0 erros. `dotnet test`: **267 testes, 267 passam, 0 falhas** (antes das correções da revisão, que só tocaram em `bench/`).
- `python -m unittest discover -s bench/tests`: 12 testes, OK.
- Recusa com a porta 5080 ocupada (um `http.server` de Python): mensagem «something is already listening» e código de saída 2.
- **Aceitação, *commit* `271212d`, árvore limpa:**
  - comandos: `./bench/run.sh smoke --variant hybrid --runs 2` (21:23) e `./bench/run.sh smoke --variant monolithic --runs 2` (21:29);
  - ficheiros: `results/smoke/2026-11-16_2123.csv` (híbrido) e `results/smoke/2026-11-16_2129.csv` (monolítico);
  - cada um tem 242 linhas de dados com as 7 colunas, o resumo bruto do k6 por execução e um `meta.json` (`status: ok`, sem alterações por fazer *commit*, nenhuma variável de ambiente relevante herdada);
  - todos os códigos de saída são 0, e não houve pedidos falhados no k6;
  - em todas as execuções, `sim_sent` = `sim_accepted_202` = `e2e_events` = 140. Este resultado confirma que a janela D7 já não apanha eventos do aquecimento nem a rajada final;
  - unidades usadas: `ms`, `events`, `requests`, `requests/s`, `ratio`, `samples`, `bytes`, `bytes/s`, `iterations`, `iterations/s`, `vus`, `code`, `bool`, `s`, `MiB`.
- Os números do `smoke` não são para a tese: cada sessão tem só 2 execuções de 30 s, a taxas muito abaixo do D8. Não se interpretam aqui.

### Revisões independentes (passo 6)

`docs/revisoes/fase-9a-revisao-scripts-medicao.md`: 9 achados, sem nenhum alto.
- Corrigidos em `327b34b`:
  - a rajada final do simulador e os eventos do aquecimento dentro da janela D7;
  - o desfasamento entre o k6 e o simulador (em parte);
  - processos esquecidos nas portas;
  - CSV de uma sessão falhada com aspeto de completo, e códigos de saída no CSV;
  - lacunas do `meta.json`;
  - `results/` a contar como alteração;
  - contentor do k6 órfão.
- O *pool* ficou registado na coluna `configuracao`. O dimensionamento para o `ingest-scale` fica para esse cenário.

### Pontos a levar para os cenários seguintes

- **`ingest-scale`:** com 4 instâncias de Workers, o *pool* de 40 por processo soma 200 ligações, acima de `max_connections` = 100. É preciso fixar o *pool* por processo no `run.sh` ou subir `max_connections` no *compose*, e registar o valor.
- **`TODO(Diogo)`:**
  - no monolítico toda a ingestão passa pelo *pool* da API (40). Isto é uma ameaça à validade da comparação (Cap. 6.5) ou faz parte do desenho da variante?
  - confirmar a duração e a carga do aquecimento de cada cenário (60 s por omissão, com a mesma carga da parte medida).
- O D7 continua a ser medido por limites superiores de *bucket* (50, 75, 100, 150, 200, 250 ms…). Para o `ingest-scale` pode ser preciso um *bucket* mais fino (ponto já levantado na Fase 8).
- A latência do k6 inclui o salto da rede do Docker Desktop. Para separar este efeito, pode-se correr uma vez o mesmo *script* com um k6 instalado no anfitrião e comparar.
