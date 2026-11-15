# Revisão de código assíncrono — Fase 3 (UC-01, ingestão)

- **Data:** 15-11-2026
- **Âmbito:** o *diff* da Fase 3 no ramo `phase-3-ingestion` (commits `80624d0..d314bfb`, `git diff 6fcc758..HEAD -- src`), com foco em:
  - `src/SmartParking.Infrastructure.Messaging/RedisStreamConsumer.cs` (ciclo de leitura, cancelamento, *back-off*, paragem, `ConfigureAwait`);
  - `src/SmartParking.Infrastructure.Messaging/RedisStreamsEventBus.cs` e `DependencyInjection.cs`;
  - `src/SmartParking.Application/Ingestion/OccupancyLogWriter.cs` e `IngestOccupancyBatchHandler.cs`;
  - `src/SmartParking.Infrastructure.Documents/OccupancyEvents/MongoOccupancyEventLog.cs` (criação preguiçosa dos índices com `SemaphoreSlim`);
  - `tests/SmartParking.Api.IntegrationTests/Ingestion/StreamConsumerRecoveryTests.cs`, só quanto à forma de parar o consumidor.
- **Revisor:** um agente separado (Claude, método «async-await-auditor»), sem acesso ao raciocínio da sessão que escreveu o código. Revisão só de leitura: nenhum ficheiro em `src/` ou `tests/` foi editado e nada foi *commitado*.
- **Adaptação acordada do método ao projeto:**
  - os *hosts* (API e Workers) são ASP.NET Core, sem `SynchronizationContext`. Por isso, `ConfigureAwait(false)` só é exigido nas bibliotecas `Infrastructure.*` e `Application`, não nos *hosts* nem nos testes;
  - a semântica de entrega é intencional: `XACK` só depois de o *handler* retornar; uma exceção do *handler* significa «sem `XACK`, entregar de novo»; uma falha transitória nunca vai para a *dead letter* (ADR-0006);
  - o StackExchange.Redis não permite `XREADGROUP … BLOCK` no *multiplexer* partilhado, daí o *polling*.

  O código foi avaliado contra estas intenções. Procurou-se tudo o que pudesse perder uma mensagem, confirmar antes da escrita, pendurar a paragem, rodar em ciclo sem espera, engolir mal um cancelamento, deixar recursos por libertar ou causar um *deadlock*.
- **Método e provas:**
  - **Varrimento** dos padrões da lista de verificação (`async void`, `.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Task.Run`, `Thread.Sleep`, `await` sem `ConfigureAwait`) em `src/`, excluindo `bin/` e `obj/`. Depois, leitura da cadeia completa: `IngestionEndpoints` → `IngestOccupancyBatchHandler` → `RedisStreamsEventBus`, e `RedisStreamConsumer` → `OccupancyLogWriter` → `MongoOccupancyEventLog`.
  - **Comportamento do Redis 7.4.12 com `XREADGROUP … 0`:** contentor Redis **descartável** (`redis:7.4.12-alpine`, apagado no fim). Uma entrada lida com `>`, 2 s de espera, depois relida com `0`. O `XPENDING` mostrou o tempo inativo a passar de 2 579 ms para 474 ms e o contador de entregas de 1 para 2. Ou seja, reler as pendentes próprias **reinicia** o tempo inativo e **incrementa** o contador de entregas. O mesmo contentor mostrou `appendfsync = everysec` (valor por omissão).
  - **`ConnectionMultiplexer.Connect` com o Redis inacessível:** um programa descartável (na pasta temporária da sessão, fora do repositório) com StackExchange.Redis 3.4.35, `AbortOnConnectFail = false`, contra uma porta sem servidor. `Connect` só retornou ao fim de **8 227 ms** (`ConnectTimeout` = 5 000 ms). Um `XREADGROUP` a seguir falhou ao fim de 5 019 ms com `RedisConnectionException`.
  - **Arranque dos Workers:** `dotnet build src/SmartParking.Workers` (0 erros) e execução do binário com `ConnectionStrings__Redis` a apontar para uma porta sem servidor e depois para o Redis de desenvolvimento. Mediu-se o tempo até à primeira resposta de `/health/live`: **8,8 s** com o Redis inacessível (duas execuções), contra **0,8–0,9 s** com o Redis disponível. O consumidor não chegou a ler nem a escrever nada no MongoDB, porque não havia Redis.
  - **MongoDB de desenvolvimento**, só leitura: `getParameter journalCommitInterval` = 100 ms; o servidor é um nó isolado (*standalone*; `getDefaultRWConcern` não é suportado nesse modo). A API pública do MongoDB.Driver 3.14.0 (documentação XML do pacote) expõe `WriteError.Category` com os valores `Uncategorized`, `DuplicateKey` e `ExecutionTimeout`.
  - Os testes de integração **não** foram executados. Nenhuma perda de mensagem foi reproduzida. Os tempos acima são de uma máquina local sem carga e **não** servem de resultado para a tese.

## Resumo

Não há achados de gravidade alta. Não foi encontrado nenhum caminho em que o código confirme (`XACK`) antes da escrita no MongoDB, perca uma mensagem por erro de programação assíncrona, rode em ciclo sem espera ou pendure a paragem. Também não há *sync-over-async* no caminho dos pedidos nem `async void`.

| # | Gravidade | Achado | Quando |
|---|---|---|---|
| 1 | Média | Janela de perda por durabilidade: o `202` e o `XACK` seguem antes de o Redis (`everysec`) e o MongoDB (`w:1`, sem *journal*) terem os dados em disco | Já (documentar no ADR-0006/0007); medir o custo na Fase 9 |
| 2 | Baixa | `ConnectionMultiplexer.Connect` síncrono no construtor do consumidor atrasa o arranque dos Workers ~8 s com o Redis em baixo; o `Task.Yield` não cumpre o que o comentário promete | Fase 9 (cenário `failover`); o comentário pode ser corrigido já |
| 3 | Baixa | Qualquer erro de escrita por documento diferente de 11000 é tratado como permanente; `ExecutionTimeout` é transitório | Fase 3 (pequeno) ou antes da Fase 9 |
| 4 | Baixa | Uma exceção determinística do *handler* bloqueia o grupo para sempre, só com um aviso no *log* | Fase 9 (métrica); Fase 10 (ferramenta de reprocessamento) |
| 5 | Baixa | `ConfigureAwait(false)` em falta em `IngestOccupancyBatchHandler` (e em código `Application`/`Relational` de fases anteriores) | Já (mecânico), com uma regra de análise para o manter |
| 6 | Baixa | `PublishAsync` abandona tarefas em curso quando o pedido é cancelado | Fase 9, se aparecer nos *logs* |
| 7 | Informativa | `ClaimAbandonedAsync` percorre toda a PEL sem consultar o *token* | Fase 9 |
| 8 | Informativa | Cópias para a *dead letter* em série e `List.Contains` sobre `StreamEntry` | Sem correção, salvo medição |
| 9 | Informativa | Métricas: o *lag* inclui duplicados e recusados; o contador `poison` pode contar a dobrar | Antes das medições da Fase 9 |
| 10 | Informativa | `MaxBackoff` sem limite superior: um valor enorme faz `Task.Delay` lançar fora do `catch` e para o *host* | Sem correção, ou validação de uma linha |

## Achados

### 1. Janela de perda por durabilidade dos dois motores (média, plausível)

- **Problema:** a ordem das operações está certa, mas a confirmação de cada motor não garante que os dados estão em disco.
  - **Entrada (API → Redis):** `RedisStreamsEventBus.cs:32` espera pela resposta de cada `XADD`, e a API responde `202` (`IngestionEndpoints.cs:24-27`). O Redis do `docker-compose.yml:48` usa AOF com `appendfsync everysec` (confirmado no contentor descartável com a mesma imagem). Um `XADD` confirmado pode ainda não estar no AOF durante cerca de 1 s.
  - **Escrita no log (Workers → MongoDB):** `MongoOccupancyEventLog.cs:69` usa a *write concern* por omissão. Num nó isolado, isto é `w:1` sem `j:true`: pelo que se sabe do MongoDB, a confirmação chega antes de o *journal* ser escrito (intervalo de 100 ms, confirmado com `getParameter`). Logo a seguir, `RedisStreamConsumer.cs:230` envia o `XACK`.
- **Cenário de falha:** um *crash* abrupto (`docker kill`, falta de energia), não uma paragem ordenada:
  - do Redis, até ~1 s depois de um `202`: o sensor recebeu `202`, não volta a tentar, e o evento desaparece do stream;
  - do MongoDB, até ~100 ms depois de um `insertMany` confirmado e já com `XACK`: o evento sai da PEL e não chega ao log.

  Uma paragem ordenada (`docker stop`, SIGTERM) escreve os dados em disco nos dois motores, por isso esta janela não aparece nos testes atuais.
- **Porque importa:** o RNF-03 e o ADR-0006 afirmam «sem perda de eventos». A afirmação é verdadeira para falhas do processo .NET e para paragens ordenadas dos motores, mas não para *crashes* abruptos dos motores com esta configuração. O cenário `failover` da Fase 9 tem de dizer qual dos dois casos mede.
- **Proposta:**
  - **Já:** acrescentar esta limitação às «Consequências» do ADR-0006 e do ADR-0007. Definir no cenário `failover` se a falha é `docker stop` ou `docker kill`.
  - **Fase 9, como variável da experiência:** `WriteConcern.Acknowledged.With(journal: true)` na coleção do log (por exemplo, `collection.WithWriteConcern(...)` no construtor de `MongoOccupancyEventLog`) e `appendfsync always` no Redis. Medir o custo em débito e no p95 do UC-01 contra o valor por omissão. `TODO(Diogo):` decidir se a tese assume a janela (e a quantifica) ou se paga o custo.
- **Confiança:** a configuração está confirmada; a perda **não** foi reproduzida.

### 2. Ligação síncrona ao Redis no arranque dos Workers (baixa, confirmado)

- **Problema:**
  - `DependencyInjection.cs:101-106` (Messaging) e `Infrastructure.Cache/DependencyInjection.cs` registam o *multiplexer* com `ConnectionMultiplexer.Connect(...)`, uma chamada **síncrona** que bloqueia a *thread* até ao `ConnectTimeout`.
  - `DependencyInjection.cs:134-140` constrói o `RedisStreamConsumer` numa *factory* que resolve logo o `IConnectionMultiplexer`. O `Host.StartAsync` resolve todos os `IHostedService` antes de chamar o primeiro `StartAsync`. Por isso, a ligação é feita de forma síncrona na *thread* de arranque, antes de o Kestrel abrir a porta.
  - O comentário de `RedisStreamConsumer.cs:67-69` diz que o `Task.Yield` evita que um Redis lento ou inacessível atrase o arranque do *host* e do `/health`. O `Task.Yield` só cobre o `ExecuteAsync`; o atraso acontece antes, no construtor.
- **Medição:** com o Redis inacessível, a primeira resposta de `/health/live` dos Workers chegou ao fim de 8,8 s; com o Redis disponível, ao fim de 0,8–0,9 s. O `Connect` isolado demorou 8 227 ms.
- **Nota:** o `RedisConnectionWarmup` da Fase 1 também espera pela ligação durante o arranque (devolve a tarefa do `Task.Run` como resultado de `StartAsync`). Por isso, corrigir só o consumidor **não** elimina os ~8 s. Este atraso foi aceite na Fase 1 (`docs/DIARIO.md`, segundo o comentário do `RedisConnectionWarmup`); o que falta é coerência entre o código e o comentário.
- **Não há *deadlock*:** os *hosts* não têm `SynchronizationContext`, e o bloqueio acontece uma vez, no arranque.
- **Proposta:**
  - **Já:** corrigir o comentário de `RedisStreamConsumer.cs:67-69` para dizer que o `Task.Yield` apenas evita que o primeiro comando bloqueie o `StartAsync`, e que a ligação inicial é feita na construção.
  - **Fase 9, se o cenário `failover` medir o tempo até o processo ficar pronto:** resolver o *multiplexer* dentro do `ExecuteAsync` (depois do `Task.Yield`), ou criá-lo com `ConnectionMultiplexer.ConnectAsync` num serviço de ligação partilhado. Em qualquer dos casos, decidir se o `RedisConnectionWarmup` deve continuar a esperar pela ligação.

### 3. Classificação demasiado larga dos erros de escrita por documento (baixa, plausível)

- **Local:** `MongoOccupancyEventLog.cs:73-80`.
- **Problema:** qualquer `WriteError` com código diferente de 11000 é tratado como recusa permanente. O evento vai para a *dead letter* e é confirmado (`XACK`). A API do driver distingue `WriteError.Category == ServerErrorCategory.ExecutionTimeout`, que é transitório por natureza.
- **Cenário de falha:** se um dia for configurado um limite de tempo nas escritas (`maxTimeMS`/`timeoutMS`, por exemplo na Fase 9), um `insertMany` lento pode devolver erros por documento de categoria `ExecutionTimeout`. Esses eventos iriam para `<stream>:dead` e sairiam do caminho normal. Não se perdem (ficam na *dead letter*), mas não chegam ao log sem intervenção manual, e ainda não há ferramenta de reprocessamento. Isto contraria a regra «uma falha transitória nunca vai para a *dead letter*». Hoje não há limite de tempo configurado, por isso o risco atual é baixo.
- **Proposta:** relançar a exceção (repetir o lote) quando algum erro tiver `Category == ServerErrorCategory.ExecutionTimeout`, e só tratar como recusados os restantes. Em alternativa, usar uma lista explícita de códigos determinísticos. Os códigos concretos ficam `TODO(Diogo):`, a confirmar na documentação oficial do MongoDB, e não foram inventados aqui.

### 4. Uma exceção determinística bloqueia o grupo indefinidamente (baixa, confirmado por leitura)

- **Local:** `RedisStreamConsumer.cs:121-141` e `:212`.
- **Problema:** isto é intencional (ADR-0006, alternativa «*dead letter* depois de N entregas»). Ainda assim, um defeito no *handler* que lance sempre a mesma exceção para uma mensagem concreta (por exemplo, um `InvalidCastException` em `documents[e.Index]["eventId"].AsGuid`) faz repetir o mesmo lote de até 100 entradas para sempre, a cada 5 s (`MaxBackoff`). Todas as entradas seguintes do grupo ficam à espera. O único sinal é o aviso `StreamConsumerFailure` no *log*. Não há métrica de falhas consecutivas nem de idade da mensagem mais antiga.
- **Facto útil verificado:** no Redis 7.4.12, reler as pendentes com `XREADGROUP … 0` **incrementa** o contador de entregas (ver «Método»). O contador do `XPENDING` serve, por isso, como medida de tentativas sem mudar a política de *dead letter*.
- **Proposta (Fase 9):**
  - uma métrica `smartparking.bus.consumer.failures` (com a etiqueta `group`) e/ou um *gauge* com o maior contador de entregas da PEL própria. Assim, um bloqueio aparece no Prometheus durante as experiências;
  - na Fase 10, uma ferramenta para mover manualmente uma entrada para a *dead letter* ou para a reprocessar a partir dela.

### 5. `ConfigureAwait(false)` aplicado de forma inconsistente nas bibliotecas (baixa, confirmado)

- **Local:** `IngestOccupancyBatchHandler.cs:102` (`await bus.PublishAsync(...)` sem `ConfigureAwait(false)`), num projeto `Application` em que `OccupancyLogWriter.cs:240` já o usa.
- **Fora do âmbito, mas a mesma regra:** o varrimento encontrou o mesmo padrão no código de fases anteriores, em `Application/Configuration/ConfigurationCommands.cs`, `Application/Sessions/SessionCommands.cs`, `Application/Users/UserCommands.cs`, `Infrastructure.Relational/Persistence/*.cs` e `Infrastructure.Cache/RedisHealthCheck.cs:25`. Em `RedisStreamConsumer.cs:210`, o `await using var scope = …` também espera o `DisposeAsync` com captura de contexto.
- **Risco real:** nenhum hoje (sem `SynchronizationContext`). É só coerência com a regra acordada.
- **Proposta (já, mecânico):**
  - acrescentar `.ConfigureAwait(false)` nestes `await`. Para o `await using`: `var scope = scopes.CreateAsyncScope(); await using (scope.ConfigureAwait(false)) { … }`;
  - para que a regra não volte a falhar, ativar a regra `CA2007` (como aviso, que o `TreatWarningsAsErrors` torna erro) num `.editorconfig` que cubra só `src/SmartParking.Application` e `src/SmartParking.Infrastructure.*`. Ativá-la **depois** de corrigir o código antigo, senão a compilação falha.

### 6. Tarefas abandonadas em `PublishAsync` quando o pedido é cancelado (baixa, confirmado por leitura)

- **Local:** `RedisStreamsEventBus.cs:23-32`.
- **Problema:** os `XADD` são lançados todos e depois esperados com `Task.WhenAll(pending).WaitAsync(cancellationToken)`. Se o cliente HTTP cancelar o pedido, o `WaitAsync` lança `OperationCanceledException` e o método sai sem observar as tarefas. Se alguma falhar depois (por exemplo, `RedisTimeoutException` ao fim de 5 s), a exceção nunca é observada e acaba em `TaskScheduler.UnobservedTaskException`. Em .NET isto não termina o processo, mas é ruído e esconde falhas. Alguns `XADD` podem ficar gravados sem resposta ao sensor; isso é inofensivo, porque o log é idempotente.
- **Proposta:** como os comandos não são canceláveis e o `AsyncTimeout` já os limita a 5 s, esperar sem o `WaitAsync` (só `await Task.WhenAll(pending).ConfigureAwait(false)` depois do `ThrowIfCancellationRequested`). Em alternativa, manter o `WaitAsync` e anexar a cada tarefa de `pending` uma continuação `OnlyOnFaulted` que observe a exceção. A primeira opção é mais simples e torna a resposta coerente com o que ficou gravado.

### 7. `ClaimAbandonedAsync` percorre toda a PEL sem consultar o *token* (informativa)

- **Local:** `RedisStreamConsumer.cs:161-178`.
- **Problema:** o ciclo `do … while (start != "0-0")` reclama tudo o que estiver inativo há mais de 60 s, em páginas de `BatchSize`, sem ver o `stoppingToken`. Com uma PEL grande de um consumidor morto (por exemplo, depois de uma falha longa com vários consumidores), são muitas idas ao Redis antes da primeira leitura e antes de a paragem ser atendida. Cada ida é rápida e o ciclo termina sempre, por isso não é um bloqueio.
- **Proposta:** passar o `stoppingToken` e chamar `ThrowIfCancellationRequested()` em cada página, ou limitar o número de páginas por ronda (o resto fica para a ronda seguinte, 30 s depois).

### 8. Cópias para a *dead letter* em série; `List.Contains` sobre `StreamEntry` (informativa)

- **Local:** `RedisStreamConsumer.cs:218-227`.
- **Problema:** cada entrada *poison* é um `XADD` esperado antes do seguinte, ou seja, uma ida ao servidor por entrada. O `RedisStreamsEventBus` já mostra a alternativa: lançar os comandos todos e esperar com `Task.WhenAll`, que o *multiplexer* junta numa só ida. O filtro `unreadable.Contains(e)` usa a igualdade por omissão de um `struct` (O(n²) com n ≤ 100).
- **Peso:** desprezável, salvo se a *dead letter* passar a ter volume.
- **Proposta:** nenhuma agora. Se for preciso, usar um `HashSet<RedisValue>` de IDs e pipelining. A ordem «*dead letter* antes do `XACK`» deve manter-se.

### 9. Métricas do log: *lag* com duplicados e contagem dupla de *poison* (informativa)

- **Local:** `OccupancyLogWriter.cs:243-251`.
- **Problema (fora do tema assíncrono, mas afeta as medições):**
  - `LogLag` é registado para **todos** os registos do lote, incluindo duplicados e recusados. Depois de uma recuperação (consumidor reiniciado, entradas reclamadas), as reentregas entram no histograma `smartparking.occupancy_log.lag` com atrasos de segundos ou minutos, embora não tenham sido gravadas nessa altura. Isto distorce o p95 do segmento do *bus* na latência D7, precisamente no cenário `failover`.
  - Se o mesmo `eventId` aparecer duas vezes numa leitura e for recusado, `result.Refused` tem o `eventId` duas vezes e `SelectMany` junta a lista de IDs duas vezes. O contador `poison` fica com o dobro. O consumidor não é afetado, porque usa um `HashSet`.
- **Proposta (antes da Fase 9):** registar o *lag* apenas dos eventos inseridos (ou separar por etiqueta `outcome`), e fazer `Distinct()` sobre os IDs *poison*.

### 10. `MaxBackoff` sem limite superior (informativa)

- **Local:** `RedisStreamConsumerOptions.cs:328-331` e `RedisStreamConsumer.cs:132-141`.
- **Problema:** o `Task.Delay` do *back-off* está dentro de um `try` que só apanha `OperationCanceledException`. Um `MaxBackoff` configurado acima do máximo aceite por `Task.Delay` (cerca de 49 dias) faria sair uma `ArgumentOutOfRangeException` do `ExecuteAsync`. Com o comportamento por omissão do .NET (`BackgroundServiceExceptionBehavior.StopHost`), isso para o processo. É só um erro de configuração extremo.
- **Proposta:** validar um limite razoável em `Validate()` (por exemplo, `MaxBackoff <= TimeSpan.FromMinutes(5)`), ou nada.

## Verificado sem problemas

- **Sem anti-padrões graves no âmbito:** nenhum `async void`, `.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Thread.Sleep` nem *lambda* `async` passada como `Action`. O único `Task.Run` do código é o `RedisConnectionWarmup` da Fase 1 (fora do âmbito; ver achado 2).
- **`XACK` só depois da escrita:** `ProcessAsync` espera o *handler*, que só retorna depois de `AppendAsync` → `InsertManyAsync`. Só então copia as *poison* para a *dead letter* e, por fim, envia o `XACK` (`RedisStreamConsumer.cs:212-230`). Uma exceção em qualquer destes passos salta o `XACK`.
- **Resposta perdida de um `XREADGROUP >`:** se o Redis entregar as entradas (que entram na PEL) mas a resposta não chegar (*timeout* no cliente), o `catch` geral põe `readPending = true` (`:130`). A volta seguinte relê a PEL própria com `0`, por isso as entradas não ficam órfãs.
- **Cancelamento:**
  - o *handler* recebe o `stoppingToken`; o `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)` sai do ciclo sem `XACK` (`:117-120`);
  - um `OperationCanceledException` que não venha da paragem cai no `catch` geral e é tratado como falha transitória;
  - os comandos Redis do `XACK` não recebem o *token*. Se a paragem chegar depois de o *handler* retornar, o `XACK` segue na mesma. Isto evita uma reentrega desnecessária;
  - uma exceção que não seja `OperationCanceledException` durante a paragem leva ao `Task.Delay` com o *token* já cancelado, que lança de imediato e sai do ciclo. O único efeito é um aviso a mais no *log*.
- **Paragem limitada no tempo:** os comandos Redis não são canceláveis, mas o `AsyncTimeout` limita-os a 5 s (medido: 5 019 ms com o Redis inacessível). As chamadas ao MongoDB (`WaitAsync` do semáforo, `CreateManyAsync`, `InsertManyAsync`) recebem o *token*. O pior caso fica bem abaixo do `ShutdownTimeout` por omissão do *host* (30 s).
- **Sem ciclo quente:**
  - leitura vazia de novas entradas → `Task.Delay(PollInterval)`;
  - PEL própria vazia → uma única passagem imediata para `>`, sem repetição;
  - qualquer falha → *back-off* exponencial de 200 ms até `MaxBackoff` (5 s), reposto só depois de um lote processado com sucesso.
- **Exceções que escapam do `ExecuteAsync`:** nenhuma em funcionamento normal (`catch (Exception)` em volta de todo o corpo), salvo o caso extremo do achado 10. O `Task.Yield` inicial impede que o primeiro comando Redis corra dentro do `StartAsync`.
- **Interação com o `XAUTOCLAIM`:** no Redis 7.4.12, reler a PEL própria reinicia o tempo inativo (medido). Um consumidor vivo que repete o seu lote a cada ≤ 5 s não é «roubado» por outro ao fim de `ClaimMinIdle` (60 s), salvo se uma única tentativa durar mais de 60 s. Mesmo nesse caso, o resultado é um processamento duplicado, que o índice único torna inofensivo; um `XACK` de outro consumidor remove a entrada da PEL do grupo, seja qual for o dono.
- **`EnsureIndexesAsync`:** o duplo teste com `volatile bool` e `SemaphoreSlim(1, 1)` está correto. O `WaitAsync` fica fora do `try`, por isso um cancelamento durante a espera não faz `Release` sem ter adquirido. Uma falha em `CreateManyAsync` deixa a marca a `false` e a exceção sobe ao consumidor, que repete o lote: nunca se insere sem o índice único. O registo é *singleton* (`Infrastructure.Documents/DependencyInjection.cs:41-42`), por isso os índices são criados uma vez por processo e não uma vez por âmbito do consumidor.
- **Âmbito de DI por lote:** `CreateAsyncScope` com `await using` liberta o *handler* e as suas dependências *scoped* mesmo quando o *handler* lança.
- **Classificação de erros no *bus*:** `RedisConnectionException` deriva de `RedisException` e `RedisTimeoutException` deriva de `TimeoutException`. As duas são convertidas em `EventBusUnavailableException` → `503`. Isto bate com a exceção observada com o Redis inacessível.
- **Forma de parar o consumidor nos testes:** `RunningConsumer.StopAsync` chama `host.StopAsync`, que cancela o `stoppingToken`. O `Task.Delay(Timeout.Infinite, cancellationToken)` do `StopBeforeAckHandler` lança, o ciclo sai pelo `catch` filtrado e não há `XACK`. É o mesmo caminho de uma paragem ordenada em produção. O teste cobre o cancelamento **depois** da escrita; o cancelamento **a meio** de um `InsertManyAsync` não é testado, mas o resultado é o mesmo (sem `XACK`, reentrega, duplicado recusado pelo índice).
