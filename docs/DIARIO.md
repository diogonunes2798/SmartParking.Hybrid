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
