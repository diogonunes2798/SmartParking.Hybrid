# Revisão de código — Fase 4 (*read model*, UC-03)

- **Data:** 15-11-2026
- **Âmbito:** o *diff* da Fase 4 por *commitar* em `main` (`git diff` e ficheiros novos ainda não seguidos pelo git), com foco nos consumidores:
  - `src/SmartParking.Application/Availability/` (`AvailabilityProjector`, `ConfigurationProjector`, `ReadModelRebuilder`, `ReadModelGate`, `ReadModelMetrics`, `ConfigurationEventMessage`, `AvailabilitySearch`, contratos);
  - `src/SmartParking.Workers/Program.cs` e `ReadModel/ReadModelInitializer.cs`;
  - `src/SmartParking.Infrastructure.Cache/Availability/*.cs` (`RedisAvailabilityReadModel`, *scripts* Lua, chaves);
  - `MongoOccupancyEventLog.GetLatestPerSpotAsync`, `EfAvailabilitySource` e registos de DI;
  - `Domain/Facilities` (`FacilityEvents`, `MarkDeleted`, nova assinatura de `Create`/`Update`) e `Application/Configuration/ConfigurationCommands.cs`;
  - `Api/Endpoints/AvailabilityEndpoints.cs`;
  - `tests/SmartParking.Api.IntegrationTests/Availability/*.cs`.

  O `RedisStreamConsumer` (revisto na Fase 3) serviu só de contexto.
- **Revisor:** um agente separado (Claude, método «dotnet-code-reviewer»), sem acesso ao raciocínio da sessão que escreveu o código. Revisão só de leitura: nenhum ficheiro em `src/` ou `tests/` foi editado e nada foi *commitado*. O único ficheiro escrito é este.
- **Referências lidas antes:** `CLAUDE.md`, `docs/ESPECIFICACAO.md`, ADR-0002, ADR-0004, ADR-0006 e ADR-0008.
- **Método e provas:**
  - `dotnet build` (incremental) e `dotnet build --no-incremental src/SmartParking.Workers` (que compila `Application`, `Infrastructure.*` e `Workers`): **0 avisos, 0 erros**. O CA2007 está ativo nas bibliotecas, por isso não falta nenhum `ConfigureAwait(false)` no código novo dessas camadas.
  - `dotnet test --project tests/SmartParking.ArchitectureTests`: 18 testes, todos passam.
  - `dotnet test --project tests/SmartParking.Api.IntegrationTests -- --filter-namespace SmartParking.Api.IntegrationTests.Availability`: 24 testes, todos passam (uma execução).
  - **Programas descartáveis**, na pasta temporária da sessão, fora do repositório:
    1. **OpenTelemetry 1.21.0**, histograma `smartparking.read_model.lag` sem *view*, com as amostras 0,05 · 0,12 · 0,3 · 0,8 · 0,95 · 1,4 s. Os limites exportados foram `0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000`, e **as seis amostras caíram todas no *bucket* `le=5`** (achado 1).
    2. **OpenTelemetry 1.21.0**, dois `ObservableGauge` com o nome `smartparking.bus.consumer.consecutive_failures` e a mesma etiqueta `group=availability`, a devolver 7 e 0. Foi exportado **um único ponto, com o valor 0** (achado 6).
    3. **Redis descartável** (`redis:7.4.12-alpine`, apagado no fim), com os cinco *scripts* Lua extraídos tal e qual de `ReadModelScripts.cs` e executados com `redis-cli --eval`:
       - `DeleteSpot(t=300)` seguido de `UpsertSpot(t=200)` para um lugar que não existia: respostas `3` e depois `1`, e o parque e a zona ficaram com `spots=1` e `free=1` (achado 2);
       - `UpsertFacility` com latitude 89: erro `invalid longitude,latitude pair`, mas o hash do parque ficou gravado (`name`, `changedAt`). A repetição do mesmo evento respondeu `2` (*stale*), e o parque não ficou no conjunto GEO (achado 7).
    4. **PostgreSQL descartável** (`postgres:16.16-alpine`, apagado no fim) com EF Core/Npgsql 10.0.3, o `OutboxSaveChangesInterceptor` real e a entidade `ParkingFacility` real (num modelo mínimo). `Create` → `SaveChanges`, depois `MarkDeleted` + `Remove` → `SaveChanges`. A outbox ficou com **duas linhas, `FacilityChanged` e `FacilityDeleted`**, e o parque foi apagado. O evento de remoção é gravado (ver «Verificado sem problemas»).
  - **Não** foi reproduzida nenhuma corrida entre processos (achados 3 e 4): esses cenários vêm da leitura do código e da semântica documentada do Redis. Os tempos e as contagens acima não são resultados para a tese.

## Resumo

Há **um achado de gravidade alta**, e é de medição, não de correção: o histograma da latência D7 não permite calcular o p95. No caminho principal (um processo Workers, um consumidor por grupo), não foi encontrado nenhum caminho que confirme (`XACK`) antes de o Redis aplicar a alteração, nem nenhuma entrada que bloqueie um grupo para sempre. Os achados médios aparecem quando há **vários processos Workers** (cenários `ingest-scale` e `failover` da Fase 9) ou numa **reconstrução**.

| # | Gravidade | Achado | Quando |
|---|---|---|---|
| 1 | Alta | Os histogramas de *lag* (D7) usam os *buckets* por omissão (0, 5, 10… s): o p95 abaixo de 1 s não é mensurável no Prometheus | Já (antes de qualquer medição) |
| 2 | Média | Sem *tombstone*: com dois consumidores no grupo de configuração, um `SpotChanged` processado depois de `SpotDeleted` cria um lugar fantasma, contado como livre para sempre | Antes da Fase 5 (o *relay* liga a fonte real) |
| 3 | Média | Reconstruções concorrentes (vários Workers a arrancar sem `sp:rm:meta`, ou o comando com Workers a correr) podem corromper os contadores; não há trinco | Antes da Fase 9 |
| 4 | Média | A reconstrução lê o MongoDB, que pode estar atrás do grupo `availability`; eventos já confirmados por este grupo e ainda não gravados no log perdem-se para o *read model* | Antes da Fase 9 |
| 5 | Baixa | Um evento de ocupação que chega antes do `SpotChanged` do lugar é confirmado como `unknown_spot` e perdido até ao evento seguinte do lugar | Fase 5 (latência do *relay*) |
| 6 | Baixa | Os dois projetores usam o mesmo nome de grupo (`availability`): as métricas do *bus* misturam-se, e o *gauge* `consecutive_failures` de um esconde o do outro | Já (uma linha) |
| 7 | Baixa | O `ConfigurationProjector` só trata como *poison* o JSON ilegível; um *payload* semanticamente inválido produz escritas parciais (o Lua não reverte) | Fase 5 |
| 8 | Baixa | Com a *gate* fechada, nada torna visível que os projetores estão parados (nem a prontidão, nem `consecutive_failures`) | Fase 9 |
| 9 | Baixa | Testes: lacunas (inicializador e *gate*, remoções pela outbox, `DeleteFacility`, *poison* de configuração, *buckets*) e duas fragilidades | Com os achados 1–4 |
| 10 | Informativa | Erros de *binding* nos parâmetros de consulta: título «The request body could not be read.» e, fora de *Development*, `400` sem ProblemDetails | Sem urgência |
| 11 | Informativa | Pormenores: tarefas abandonadas em `InChunksAsync`, `CancellationToken.None` no comando, relógios entre instâncias da API, *lag* negativo, cardinalidade da etiqueta `type` | Sem urgência |

## Achados

### 1. Histogramas de *lag* com os *buckets* por omissão: o p95 de D7 não é mensurável (alta, confirmado)

- **Local:** `Application/Availability/ReadModelMetrics.cs:34-35` (`smartparking.read_model.lag` e `bus_lag`); `ServiceDefaults/ServiceDefaultsExtensions.cs:82-85` (a única `AddView` é a de `http.server.request.duration`). O `smartparking.occupancy_log.lag` da Fase 3 (`OccupancyIngestionMetrics.cs:28`) tem o mesmo problema.
- **Problema:** sem *view* nem `InstrumentAdvice`, o SDK do OpenTelemetry usa os limites por omissão, pensados para milissegundos. No programa descartável, as amostras entre 0,05 e 1,4 s caíram **todas** no *bucket* `(0, 5]`. Qualquer `histogram_quantile(0.95, …)` interpola dentro desse intervalo e devolve um valor sem significado. O alvo do RNF-01/D7 é «< 1 s no p95», e esse valor fica dentro do primeiro *bucket*.
- **Porque é alta:** é a métrica que o `ReadModelMetrics` (linhas 11-12) e o ADR-0008 (consequência «D7») apresentam como a latência ponta a ponta do UC-01. O ADR-0002 (linha 25) prometeu: «O instrumento de latência ponta a ponta (D7) terá *buckets* próprios, centrados em 1 s.» A promessa não foi cumprida. Uma série recolhida assim na Fase 9 não se aproveita.
- **Proposta:**
  - acrescentar em `AddServiceDefaults` uma `AddView` para `smartparking.read_model.lag`, `smartparking.read_model.bus_lag` e `smartparking.occupancy_log.lag`. Os limites devem ser finos à volta de 1 s (por exemplo, de 10 ms a 10 s, com passos pequenos entre 0,5 e 2 s). `TODO(Diogo):` fixar os limites e registá-los no ADR-0002;
  - em alternativa, usar `InstrumentAdvice<double>` na criação do histograma, para que os limites viajem com o instrumento;
  - acrescentar um teste parecido com `Latency_histogram_uses_fine_buckets_around_the_200_ms_target`, que verifique o `/metrics` dos Workers.
- **Confiança:** confirmado com o SDK na mesma versão do projeto (1.21.0).

### 2. Sem marca de remoção: um lugar fantasma com dois consumidores de configuração (média, confirmado no Redis)

- **Local:** `ReadModelScripts.cs:62-94` (`UpsertSpot`: se o hash não existe, cria-o sem consultar nada) e `:97-118` (`DeleteSpot`: apaga o hash e, com ele, o `changedAt`). O mesmo vale para `UpsertFacility`/`DeleteFacility`.
- **Cenário de falha:** dois processos Workers (RNF-02, ou o cenário `failover`) registam cada um um `ConfigurationProjector` no mesmo grupo. O Redis reparte as entradas entre os dois consumidores, por isso `SpotChanged(t1)` e `SpotDeleted(t2)` do mesmo lugar podem ser processados em paralelo, por ordem trocada:
  - `DeleteSpot(t2)` → `3` (*unknown*), confirmado;
  - `UpsertSpot(t1)` → `1`: o lugar renasce `FREE`, com `spots+1` e `free+1` no parque, na zona e no tipo.

  Foi exatamente isto que se reproduziu no Redis descartável. O fantasma não desaparece com eventos de ocupação (o lugar já não existe, não há sensor). Só uma reconstrução o remove. O mesmo acontece com um `XAUTOCLAIM`, que pode entregar a outro consumidor uma entrada antiga depois de as seguintes já terem sido aplicadas.
- **Porque importa:** o ADR-0008 (linha 137) diz que esta janela «só existe com reentregas». Com mais de um consumidor no grupo, a janela existe sem reentrega nenhuma. O comentário de `ConfigurationProjector.cs:9-11` («a redelivered or late event is ignored») promete mais do que o código faz depois de uma remoção. A ordem «um de cada vez» de `ConfigurationProjector.cs:25-26` só vale dentro de um consumidor.
- **Proposta (uma de três):**
  - **(a) *Tombstone*:** `DeleteSpot`/`DeleteFacility` deixam `sp:rm:deleted:spot:{id}` com o `changedAt` e um TTL largo (por exemplo, a retenção do stream de configuração). `UpsertSpot`/`UpsertFacility` recusam como `Stale` quando `changedAt <= tombstone`. A chave tem o prefixo `sp:rm:`, por isso a reconstrução também a limpa, o que é coerente;
  - **(b)** manter o hash com `deleted=1` e `changedAt`, e excluí-lo das contagens e da consulta;
  - **(c)** registar o `ConfigurationProjector` num único processo (por configuração) e documentar a restrição. É a opção mais simples, mas o RNF-03 (`failover`) passa a depender desse processo, e o `XAUTOCLAIM` continua a poder reordenar depois de uma falha.

  Acrescentar um teste com a ordem `SpotDeleted` → `SpotChanged` e corrigir o ADR-0008 e o comentário.
- **Confiança:** sequência reproduzida com os *scripts* reais. A repartição entre consumidores é a semântica normal dos *consumer groups*.

### 3. Reconstruções concorrentes corrompem os contadores (média, plausível)

- **Local:** `ReadModelInitializer.cs:33-45`, `ReadModelRebuilder.cs:39-86`, `Workers/Program.cs:74-84`, `RedisAvailabilityReadModel.cs:119-144` (`ClearAsync`: `SCAN` + `UNLINK`, não atómico).
- **Cenário de falha:** dois Workers arrancam com o Redis sem `sp:rm:meta` (primeiro arranque do cenário, ou depois de um `rebuild-read-model` interrompido). Os dois chamam `RebuildAsync`:
  1. A limpa e começa a carregar;
  2. B limpa a meio da carga de A. O `SCAN` apaga as chaves que já percorreu, mas não necessariamente as que A cria depois de o cursor passar;
  3. fica, por exemplo, `sp:rm:spot:X` (criado por A depois do cursor) sem os contadores correspondentes no hash do parque (apagado por B);
  4. quando B carrega X, `UpsertSpot` vê o hash existente com o mesmo `changedAt` e responde `Stale`. Os contadores nunca são incrementados: `spots`/`free` ficam abaixo do real, em silêncio.

  Além disso, A acaba primeiro, abre a sua *gate* e começa a projetar enquanto B ainda limpa. Atualizações confirmadas por A podem ser apagadas por B.
  O comando `rebuild-read-model` com os Workers a correr dá o mesmo resultado. A documentação diz «com os Workers parados» (ADR-0008, linha 117), mas nada o impõe.
- **Proposta:**
  - trinco no Redis: `SET sp:rebuild-lock <id> NX PX <ttl>` (fora do prefixo `sp:rm:`, para a limpeza não o apagar). Quem não obtém o trinco espera até `sp:rm:meta` existir e só depois abre a *gate*;
  - no comando, recusar se o trinco estiver ocupado. Opcionalmente, recusar também se houver consumidores ativos nos grupos `availability` (`XINFO CONSUMERS`, `idle` pequeno).
- **Confiança:** cenário deduzido da leitura do código e da semântica do `SCAN` (garante só as chaves presentes durante toda a iteração). Não foi reproduzido.

### 4. A reconstrução confia no log do MongoDB, que pode estar atrás do grupo `availability` (média, plausível)

- **Local:** `ReadModelRebuilder.cs:58-80` e o comentário das linhas 15-19; ADR-0008, linha 117.
- **Problema:** os grupos `occupancy-log` e `availability` avançam de forma independente (ADR-0006). A reconstrução substitui o estado de cada lugar pelo último evento **do MongoDB**. Os eventos que o grupo `availability` já confirmou, mas que o grupo `occupancy-log` ainda não gravou (por atraso, ou porque estão pendentes depois de uma falha do MongoDB), não estão no log nem voltam a ser entregues ao grupo `availability`.
- **Cenário de falha:** sob carga (cenário `ingest-scale`), o MongoDB fica atrás. Os Workers param e corre-se `rebuild-read-model` antes do cenário seguinte. Os lugares cujo último evento ainda não estava no log ficam num estado **mais antigo** do que o já publicado. Quando os Workers voltam, o log é completado, mas o grupo `availability` já não revê esses eventos. O estado fica errado até ao evento seguinte de cada lugar. O comentário «they only move a spot forward» só é verdade para os eventos que ainda estão por confirmar no grupo `availability`.
- **Proposta (uma de duas):**
  - antes de reconstruir, exigir que o grupo `occupancy-log` esteja em dia (`XINFO GROUPS`: `lag = 0` e `pending = 0`), ou esperar por isso;
  - ou recuar o grupo `availability` (`XGROUP SETID`) para a posição do `occupancy-log` (o menor entre o primeiro pendente e o `last-delivered-id`) depois da reconstrução. Reaplicar é idempotente e ordenado, por isso só custa tempo.

  Documentar a escolha no ADR-0008.
- **Confiança:** deduzido do modelo de grupos independentes; não reproduzido.

### 5. Evento de ocupação antes da configuração do lugar: `unknown_spot` confirmado e perdido (baixa)

- **Local:** `ReadModelScripts.cs:28`, `AvailabilityProjector.cs:56-66`; ADR-0008, linha 64 («a reconstrução corrige»).
- **Problema:** a *gate* resolve o caso no arranque, mas não durante o funcionamento. Na Fase 5, o `SpotChanged` de um lugar novo chega pelo *relay* da outbox, com o atraso do *polling*. O primeiro evento do sensor desse lugar pode chegar antes. Esse evento é confirmado como `unknown_spot`, e o lugar aparece `FREE` até ao evento seguinte (no cenário D8, em média 5 min depois). Na Fase 4, sem *relay*, todos os eventos de lugares criados depois da última reconstrução têm este destino. Isso está documentado.
- **Proposta (Fase 5):** guardar o último evento de cada lugar desconhecido (`sp:rm:orphan:{spotId}`, com o mesmo critério `(ts, eventId)` e um TTL curto) e aplicá-lo dentro do `UpsertSpot` quando o lugar é criado. Em alternativa, medir a taxa de `unknown_spot` na Fase 9 e aceitar o efeito de forma explícita.

### 6. Os dois projetores partilham o nome de grupo `availability` (baixa, confirmado)

- **Local:** `Workers/Program.cs:68` (`options.Group = AvailabilityConsumers.Occupancy` no `ConfigurationProjector`), `AvailabilityProjector.cs:12-13`, `AvailabilityFixture.cs:113`; `RedisStreamConsumer.cs:65-75` (etiqueta `group`).
- **Problema:** para o Redis isto funciona, porque os grupos são por stream. Já as métricas do `RedisStreamConsumer` só têm a etiqueta `group`, por isso:
  - `smartparking.bus.consumer.messages` e `failures` somam os dois streams;
  - o *gauge* `consecutive_failures` passa a ter duas *callbacks* com as mesmas etiquetas. No programa descartável, um projetor bloqueado (7) foi **escondido** pelo outro (0). É precisamente o instrumento que a revisão da Fase 3 (achado 4) criou para tornar visível um grupo bloqueado.

  O nome de configuração (`availability-configuration`) também difere do nome do grupo, o que confunde quem lê o `XINFO GROUPS`.
- **Proposta:** usar `Group = AvailabilityConsumers.Configuration` (`availability-configuration`), ou acrescentar a etiqueta `stream` às três métricas do consumidor. A primeira opção é uma linha no `Program.cs`, outra na *fixture* e outra no ADR-0008, §4.

### 7. *Poison* de configuração só ao nível do JSON; escritas parciais no Lua (baixa, confirmado no Redis)

- **Local:** `ConfigurationEventMessage.cs:26-54`, `ConfigurationProjector.cs:36-61`, `ReadModelScripts.cs:125-146`.
- **Problema:**
  - `PayloadAs<T>` aceita *payloads* que desserializam, mas que estão errados: campos em falta passam a `null` ou `0`, `Guid.Empty`, latitude fora do intervalo do GEO, um valor numérico de enum não definido;
  - quando um *script* falha a meio, o Redis **não reverte** os comandos já executados. Com latitude 89, o `UpsertFacility` gravou o hash (com `changedAt`), e o `GEOADD` falhou. A exceção faz o lote repetir, mas a repetição responde `Stale`, o lote é confirmado e o parque fica gravado mas invisível na consulta. Também não move os contadores de zona.

  Os eventos são produzidos pelo domínio, que valida, por isso a probabilidade é baixa. O efeito, porém, é silencioso.
- **Proposta:**
  - validar no projetor antes de escrever: `GeoPoint` (reutilizar o construtor), textos não vazios, `Enum.IsDefined`, ids diferentes de `Guid.Empty`, `e.FacilityId`/`e.SpotId == message.AggregateId`. O que falhar vai para *poison* (*dead letter*);
  - nos *scripts*, fazer primeiro as operações que podem falhar (`GEOADD`) e só depois os `HSET`.

### 8. *Gate* fechada sem visibilidade (baixa)

- **Local:** `ReadModelGate.cs`, `AvailabilityProjector.cs:31`, `ConfigurationProjector.cs:21`, `ReadModelInitializer.cs:52-65`.
- **Comportamento verificado na leitura:**
  - o consumidor lê um lote (`XREADGROUP`), que entra na PEL, e fica parado dentro do *handler*;
  - o `XAUTOCLAIM` desse consumidor corre no mesmo ciclo, por isso também fica parado;
  - na paragem, `WaitAsync(ct)` lança `OperationCanceledException` e o ciclo sai sem `XACK`. Nada se perde, e o lote é relido no arranque seguinte;
  - se a *gate* ficar fechada mais de 60 s, outro processo Workers com a *gate* aberta reclama o lote. Isto é inofensivo, porque a aplicação é idempotente.
- **Problema:** se a carga inicial falhar sempre (por exemplo, MongoDB em baixo), os projetores ficam parados indefinidamente. `consecutive_failures` fica a 0 (o *handler* não falha, espera), e `/health/ready` não reflete a *gate*. O único sinal é um aviso no *log* do `ReadModelInitializer`.
- **Proposta:** um *health check* `read-model-loaded` com a etiqueta `ready`, que devolva `Unhealthy` enquanto `!gate.IsOpen`, ou um *gauge* `smartparking.read_model.gate_open`. Na Fase 9, saber se a carga já terminou é condição para começar a medir.

### 9. Testes: o que provam, lacunas e fragilidades (baixa)

- **Bons:** `Concurrent_out_of_order_application_keeps_counters_equal_to_spot_states` é um teste de propriedade com valor real. As coordenadas de Coimbra que distinguem uma troca entre latitude e longitude também são uma boa escolha. A reconstrução é comparada com o estado incremental, e não com valores fixos.
- **Lacunas:**
  - `ReadModelInitializer` e `ReadModelGate`: nenhum teste prova que os projetores esperam pela carga, que a carga é saltada quando existe `sp:rm:meta`, ou que uma falha é repetida;
  - remoções pela outbox: nenhum teste do repositório verifica as linhas `FacilityDeleted`/`SpotDeleted` (confirmei-o num programa descartável, mas isso não protege contra regressões);
  - o *script* `DeleteFacility` nunca é exercitado;
  - `ConfigurationProjector`: os caminhos *poison* (tipo conhecido com *payload* inválido) e `Ignored` (tipo desconhecido) não são testados;
  - a ordem `SpotDeleted` → `SpotChanged` (achado 2);
  - os *buckets* dos histogramas de *lag* (achado 1);
  - o desempate de `GetLatestPerSpotAsync` com o mesmo `timestamp`. A ordem dos bytes do `BinData` (representação *Standard*) coincide com a ordem do texto «D» usada no Lua, por isso o resultado está certo, mas nenhum teste o fixa.
- **Fragilidades:**
  - `ReadModelConsistencyTests.cs:118` usa `Random.Shared` para baralhar a ordem de entrega, embora o comentário da linha 109 diga «reproducible». Uma falha deste teste não se consegue reproduzir. Usar o mesmo `Random(42)` (ou uma semente por consumidor) e registar a semente na mensagem da asserção;
  - as consultas dos testes usam o `limit` por omissão (50) sobre um conjunto GEO partilhado pela coleção. Muitos `SeedAsync` colocam parques **no próprio centro da consulta** (as coordenadas por omissão de `AvailabilityFixture.cs:173`). Hoje há bem menos de 50, mas um novo teste pode empurrar para fora do `COUNT` os parques que outro teste procura, e esse teste falha conforme a ordem de execução. Passar `&limit=200` nas consultas dos testes, ou semear longe do centro;
  - as subscrições de pub/sub (`SubscribeAsync`) nunca são canceladas. É inofensivo, mas acumulam-se dentro da coleção.

### 10. Erros de *binding* nos parâmetros da consulta (informativa)

- **Local:** `AvailabilityEndpoints.cs:16-26`; `ProblemDetailsExceptionHandler.cs:27`.
- **Problema:** `type=Bus` ou `lat=abc` falham no *binding* dos Minimal APIs. Em *Development* lança-se `BadHttpRequestException`, e o *handler* responde com o título «The request body could not be read.», errado para um parâmetro de consulta. Fora de *Development* (`ThrowOnBadRequest` desligado), a resposta é um `400` sem corpo, o que contradiz o `.ProducesValidationProblem()`. O teste `Unknown_vehicle_type_is_rejected` só verifica o código de estado.
- **Proposta:** receber `type` como `string?` e validar em `AvailabilitySearch` (`Enum.TryParse` com `ignoreCase`), como já se faz com o intervalo de valores. Em alternativa, mudar o título genérico para «The request could not be read.».

### 11. Pormenores (informativa)

- `ReadModelRebuilder.cs:95-112`: se a enumeração lançar a meio de um bloco, as tarefas em `pending` não são esperadas, e as suas exceções ficam por observar. Usar `try/finally` com `Task.WhenAll`.
- `Workers/Program.cs:79`: o comando usa `CancellationToken.None`, por isso um `Ctrl+C` interrompe sem limpeza. Usar `app.Lifetime.ApplicationStopping` ou um `CancellationTokenSource` ligado a `Console.CancelKeyPress`. Uma reconstrução interrompida deixa o *read model* sem `meta`, e o arranque seguinte reconstrói, por isso o efeito é só cosmético.
- `ConfigurationCommands.cs`: o `OccurredAt` vem do `TimeProvider` da instância da API que tratou o pedido. Com uma única instância, a concorrência otimista (`xmin`) garante que dois eventos do mesmo agregado ficam por ordem crescente. Com várias instâncias e relógios desfasados, a ordem por `changedAt` pode inverter-se. Basta registar a hipótese no ADR-0008.
- `AvailabilityProjector.cs:64`: o validador não recusa `timestamp` no futuro, por isso um sensor adiantado produz *lag* negativo no histograma. Vale a pena decidir na Fase 9 se se filtra ou se se conta à parte.
- `ConfigurationProjector.cs:63`: a etiqueta `type` vem da mensagem. Um produtor com tipos arbitrários aumenta a cardinalidade da métrica. Usar `"other"` para os tipos ignorados.
- `RedisAvailabilityReadModel.cs:11-12`: o comentário afirma que o StackExchange.Redis passa a `EVALSHA` depois do primeiro `EVAL`. Não o verifiquei. Se for para a tese, confirmar com `MONITOR` ou usar `LuaScript.Prepare(...).LoadAsync` de forma explícita.

## Verificado sem problemas

- **Eventos de remoção na outbox:** `ChangeTracker.Entries<IHasDomainEvents>()` inclui as entradas `Deleted`. `MarkDeleted` antes de `Remove` grava `FacilityDeleted` na mesma transação (programa descartável com PostgreSQL: duas linhas, parque apagado). Se a FK impedir a remoção, a transação reverte a linha da outbox com a remoção.
- **Eventos de domínio:** `Create` chama `Update`, que levanta `FacilityChanged`/`SpotChanged` já com `Id` e `FacilityId` preenchidos. O *payload* não tem o operador nem dados pessoais (RNF-06). A serialização da outbox (`Web` + enums por nome) e a desserialização do projetor (`ConfigurationEventMessage.SerializerOptions`) usam as mesmas opções.
- **`XACK` só depois do Redis:** os dois projetores só retornam depois de esperar todas as respostas dos *scripts*. Uma exceção em qualquer `ScriptEvaluateAsync` propaga-se por `Task.WhenAll`, nada é confirmado e o lote repete-se. Os *scripts* já aplicados respondem `duplicate`/`stale`.
- **Classificação *poison* vs. transitório no caminho de ocupação:** só o JSON ilegível ou incompleto (`OccupancyEventMessage.TryParse`) vai para a *dead letter*. Não encontrei nenhuma entrada válida que faça o `ApplyOccupancy` falhar de forma determinística: o *script* sai logo quando o hash não existe, e todos os hashes de lugar são criados com `facilityId` e `vehicleType`. Por isso, nenhuma entrada bloqueia o grupo para sempre.
- **Ordem e idempotência da ocupação:** `(ts, eventId)` com comparação lexicográfica do GUID em minúsculas é determinista. O `$top` do MongoDB ordena por `{timestamp: -1, eventId: -1}`, e o `BinData` *Standard* compara bytes pela mesma ordem do texto «D». A reconstrução e o caminho incremental escolhem portanto o mesmo evento. O *timestamp* em milissegundos e o `changedAt` em microssegundos são exatos num número Lua (2^53 µs ≈ 285 anos desde 1970, o que bate com o «2255» do comentário).
- **Métrica de *lag*:** só regista `applied`/`unchanged` (não regista as reentregas `duplicate`/`stale`), o que corrige para a Fase 4 o problema apontado no achado 9 da Fase 3. Os índices de `outcomes` e `parsed` estão alinhados.
- **Contadores com a zona por chegar e com mudança de zona:** `UpsertFacility` move todos os campos `spots*`/`free*` do hash do parque da zona antiga para a nova, e trata o caso de a zona ainda não existir (lugares antes do parque).
- **Consulta:** `GEOSEARCH` recebe a longitude primeiro (há teste). Um parque apagado entre o `GEOSEARCH` e o `HMGET` é saltado. Os valores são escritos com `"R"` e `InvariantCulture`. `AvailabilitySearch` rejeita `NaN`, valores infinitos e valores fora do intervalo do GEO, porque `GeoPoint` testa `NaN` e o raio é limitado.
- **Camadas (ADR-0001):** os testes de arquitetura passam (18). O `Application` só depende do `Domain`; o `Infrastructure.Cache` implementa interfaces do `Application`; o `ReadModelInitializer` está no *host*. O `EfAvailabilitySource` usa `AsNoTracking` e uma projeção (não expõe entidades), e o *token* chega ao enumerador pelo `WithCancellation` do chamador.
- **`ConfigureAwait`:** a compilação completa com o CA2007 ativo não deu avisos. O `await foreach` de `ClearAsync` e o de `InChunksAsync` têm `ConfigureAwait(false)`.
- **Paragem:** a espera na *gate* respeita o *token*. O `ReadModelInitializer` sai sem abrir a *gate* quando o *host* para, e o *back-off* é limitado a 30 s.
