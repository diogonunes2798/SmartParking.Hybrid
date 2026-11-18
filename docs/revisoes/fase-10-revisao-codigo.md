# Fase 10 — revisão independente do código e do cenário de retenção

- **Data:** 17-11-2026.
- **Âmbito:** `git diff 3c6d066..ee06ec3`, ramo `phase-10-retention` (retenção, compressão, arquivo, partições diárias, `MaxEventAge`, simulador e cenário `retention`).
- **Revisor:** subagente geral, só de leitura. Não compilou, não executou nada e não alterou ficheiros: estava a correr a primeira sessão oficial de medição. Essa sessão foi interrompida por causa dos achados 11 e 12 e os seus resultados foram postos de parte (diário).

## Estado de cada achado

| # | Gravidade | Achado (resumo) | Estado |
|---|---|---|---|
| 1 | alta | A guarda do TTL falha em aberto: retenção desligada depois de ativa, ou falha persistente no arquivo, deixa o TTL avançar sem refrescar | **Corrigido.** Com `Enabled=false` o serviço continua registado e repõe «nunca» (`EnforceAsync(null)`). Uma exceção no arquivo é guardada, a guarda corre na mesma com o horizonte atual, e a exceção é relançada no fim. Testes `Retention_switched_off_sets_the_store_back_to_never`, `A_failing_archive_still_refreshes_the_guard_with_the_current_horizon_and_rethrows` e o «nunca» no MongoDB real; mutações M7 e M8 apanhadas. **Fica documentado (ADR-0013):** paragem dos Workers por mais do que `GuardMargin` (iii: *health check* com a idade do último `collMod` não foi feito). |
| 2 | média | 24/24 horas cobertas não provam que os eventos atrasados foram agregados | **Corrigido.** Antes de arquivar, para cada hora: o `storedAt` mais recente dos seus eventos tem de ser anterior à última agregação dessa hora (`aggregated_at`, agora o instante de leitura do agregador, e não o `now()` da BD). Senão o dia espera por um novo `aggregate-occupancy`. Teste com evento atrasado (unitário e integração); mutação M9 apanhada. |
| 3 | média | `MaxEventAge ≤ ArchiveDelay` não é validado; a idade é medida na receção | **Documentado** (ADR-0013, limitação). Os dois valores estão em processos diferentes. No cenário, `MaxEventAge > ArchiveDelay` é deliberado e inofensivo (nenhum evento chega depois do arquivo); explicado no cabeçalho de `retention.sh`. |
| 4 | média/baixa | O horizonte pode avançar a meio de uma agregação longa | **Corrigido.** `EfOccupancySummaryStore.SaveAsync` lê o horizonte `FOR SHARE` na transação e recusa um intervalo que comece antes dele; o avanço do horizonte é um *upsert* da mesma linha. Teste no cenário de integração; mutação M10 apanhada. |
| 5 | baixa | Várias instâncias dos Workers exportam o mesmo dia e colidem no `.partial` | **Corrigido em parte:** nome temporário único por escritor. Sem *advisory lock*: documentado (o custo do arquivo multiplica-se por N). |
| 6 | baixa | Linhas na DEFAULT nunca são removidas | **Documentado** (ADR-0013). A manutenção já avisa quando a DEFAULT tem linhas (Fase 8). |
| 7 | baixa | `ensure_occupancy_day_partitions` mantém o `ACCESS EXCLUSIVE` até ao fim | **Documentado** (corre no arranque e de 24 em 24 h). |
| 8 | baixa | `PartitionDaysBehind` recria partições já removidas | **Corrigido:** o primeiro dia nunca fica antes do horizonte. Teste no fim do cenário de integração (monolítico); mutação M11 apanhada. Encontrado ao escrever o teste: o Npgsql devolve `date` como `DateTime` no `ExecuteScalar`, e a primeira versão (`as DateOnly?`) nunca aplicava o limite. |
| 9 | baixa | Durabilidade do diretório e `ArchivePath` relativo | **Documentado.** O cenário usa um caminho absoluto. |
| 10 | baixa | ADR-0013 e rastreabilidade em falta | **Feito** no passo de documentação da fase. |
| 11 | alta | *Polling* com `mongosh` dentro do cgroup do `mongo`, nas janelas de CPU | **Corrigido:** as contagens correm num contentor próprio (`docker compose run --rm --no-deps`), de 15 em 15 s. |
| 12 | média/alta | A retenção pode começar durante a agregação medida | **Corrigido:** o serviço de retenção está desligado durante a carga e a agregação; os Workers são reiniciados com ele ligado e `Retention:InitialDelay` (30 s), e o instantâneo de CPU é tirado dentro desse atraso. |
| 13 | média | Consumo em repouso não descontado; janelas de duração diferente | **Corrigido:** linhas `*_net_seconds` (delta − taxa em repouso × duração) e só os componentes da variante (no monolítico, sem `mongo` e `redis`, que correm parados). Encontrado nos pilotos: o *healthcheck* do compose corre `mongosh` no contentor de 5 em 5 s, cerca de 4 a 5 s de CPU por 30 s. |
| 14 | média | CPU do `compact` não medido; que espaço usar | **Corrigido:** janela de CPU do `compact` e linha `storage_<fase>_used_bytes` (= `storageSize − freeStorageSize + índices`). |
| 15 | baixa | O serviço bloqueado pesa durante a carga | **Resolvido pelo 12:** o serviço está desligado durante a carga. |
| 16 | baixa | Retenção medida sem ingestão em simultâneo | **Documentado** (ADR-0013, limitação; trabalho futuro). |
| 17 | baixa | Margem e relógio da VM do Docker | **Corrigido em parte:** `meta.json` regista a diferença de relógio do contentor `mongo` no início e no fim de cada execução (`clock_skew_mongo_ms_*`). |
| 18 | baixa | Linha de base das fases anteriores; execuções que passam a meia-noite | **Corrigido em parte:** linha `crossed_midnight`. A mudança de linha de base (4.º índice no log, partições diárias no cenário) fica no `bench/README.md` e no diário. |

O relatório original do revisor segue sem alterações.

---

Âmbito: `git diff 3c6d066..ee06ec3` (7 commits, ramo `phase-10-retention`). Revisão só de leitura: nada foi compilado nem executado, porque estava a correr uma medição na máquina. As linhas citadas são as de `ee06ec3`.

## A. Correção

### 1. [alta] A guarda do TTL falha em aberto: se não for refrescada, o MongoDB apaga para lá do horizonte
- **Ficheiros:** `src/SmartParking.Application/Retention/OccupancyRetention.cs` (`RunOnceAsync`: o `EnforceAsync` só corre no fim, depois do arquivo e da fotografia; com `Enabled=false` a função devolve logo `Disabled`), `src/SmartParking.Workers/Program.cs` (o `RetentionService` só é registado se `retention.Enabled`) e `RetentionContracts.cs` (o comentário de `Enabled`).
- **Cenário:** o `expireAfterSeconds` fica fixo em `E = (t0 − H) + m`, e o monitor TTL apaga os documentos com `timestamp < t − E = H − m + (t − t0)`. Quando passa o tempo `m` sem refrescar a guarda, o limiar passa o horizonte e começa a apagar dias que não foram agregados nem arquivados. A guarda fica sem refrescar em três casos, e só o terceiro está documentado:
  - (a) **`Retention:Enabled=false` depois de a retenção já ter estado ativa.** O comentário diz que «o índice TTL fica em nunca», o que é falso: o índice fica no último valor e, ao fim de `GuardMargin`, o MongoDB apaga tudo o que tiver mais de `E` segundos.
  - (b) **Uma exceção no passo de arquivo.** Por exemplo, disco cheio (que é justamente quando se liga a retenção), um `IOException` no `.partial` partilhado entre instâncias (ver o ponto 5) ou um erro de PostgreSQL a meio. O `RunOnceAsync` falha antes do `EnforceAsync`, e repete a falha em todas as execuções.
  - (c) **Os Workers parados durante mais do que `GuardMargin`** (documentado).

  A variante monolítica falha em fechado (o `DROP` só corre a pedido), por isso as duas variantes têm garantias diferentes.
- **Correção sugerida:**
  - (i) Correr `EnforceAsync` num `finally`, ou num `try` próprio, com o horizonte lido do *ledger*, para que uma falha do arquivo não congele a guarda.
  - (ii) Com `Enabled=false`, repor o índice em «nunca» (`EnforceAsync(null)`) no arranque, ou manter só a guarda ativa.
  - (iii) Criar uma métrica ou *health check* com a idade do último `collMod` bem-sucedido, que degrade `/health/ready` acima de `GuardMargin/2`.
  - (iv) Registar a assimetria no ADR-0013 e nas ameaças à validade. Como alternativa que falha em fechado, considerar `deleteMany({timestamp: {$lt: H}})` em lotes, pelo próprio Worker.

### 2. [média] A cobertura de 24 horas não prova que os eventos atrasados foram agregados
- **Ficheiros:** `OccupancyRetention.cs` (a verificação `covered < 24`), `EfOccupancySummaryStore.cs` (o *upsert* de `aggregated_hours`) e `AnalyticsContracts.cs` (`ReaggregateHours = 2`, `AllowedLateness = 2 min`). A API aceita eventos até `MaxEventAge = 7 d`.
- **Cenário:** um *gateway* fica 5 horas offline e reenvia os eventos depois. A API aceita-os, mas as horas desses eventos já estão em `aggregated_hours` (o *worker* só reagrega as 2 últimas horas). Oito dias depois, a retenção vê 24/24 horas, arquiva o dia e remove esses eventos. Ficam no ficheiro, mas nunca entraram no relatório UC-04. A partir daí, a agregação manual desse dia é recusada (`from < horizon`), por isso o erro já não se corrige pela loja operacional. O invariante «nada é removido antes de o dia estar agregado» só se cumpre na forma, não no conteúdo.
- **Correção sugerida:**
  - Antes de arquivar, comparar o `max(storedAt)` dos eventos do dia (no monolítico, `received_at` ou o equivalente) com o `min(aggregated_at)` das 24 horas. Se houver eventos mais recentes do que a agregação, reagregar o dia.
  - Alternativa mais simples: reagregar sempre o dia (`AggregateAsync(start, end)`) imediatamente antes de o arquivar. Custa um dia de agregação por dia arquivado.
  - Acrescentar um teste com um evento atrasado depois da agregação da sua hora.

### 3. [média] A regra `MaxEventAge ≤ ArchiveDelay` não é validada e vale na receção, não no armazenamento
- **Ficheiros:**
  - `IngestOccupancyBatchHandler.cs` (`MaxEventAge`, na API);
  - `RetentionContracts.cs` (`ArchiveDelay`, nos Workers; a frase «Must be at least Ingestion:MaxEventAge» não é verificada em lado nenhum);
  - `bench/scenarios/retention.sh:63,69` (`MaxEventAge = 7 d` e `ArchiveDelay = 3 d`, ou seja, o próprio cenário viola a regra).
- **Cenário:**
  - Basta configurar mal um dos dois processos. Um evento chega a um dia já registado em `retention_archive` e nunca é arquivado; quando o horizonte passa, é removido.
  - No híbrido, a idade é medida no `receivedAt` da API. Com o *outbox* ou o *broker* parados mais de `ArchiveDelay − MaxEventAge` (1 dia por omissão), o evento chega ao MongoDB depois de o seu dia ter sido arquivado.
  - No cenário de medição isto não faz mal, porque toda a carga entra antes da agregação. Mas a configuração versionada contradiz o invariante.
- **Correção sugerida:**
  - Validar no arranque dos Workers que `Ingestion:MaxEventAge ≤ Retention:ArchiveDelay` (o Workers pode ler as duas secções).
  - Do lado do armazenamento, quando o consumidor recebe um evento com `timestamp < archived_until`, desviá-lo para uma tabela ou coleção de eventos tardios e contá-lo numa métrica, em vez de o gravar sem garantias.
  - No cenário, alinhar `MaxEventAge` com `ArchiveDelay`, ou explicar no README por que razão a exceção é inofensiva.

### 4. [média/baixa] Corrida entre o horizonte e uma agregação longa (verificação seguida de uso)
- **Ficheiro:** `OccupancyAggregator.cs` (`AggregateAsync`: o horizonte é lido uma vez, no início).
- **Cenário:**
  1. Uma reagregação manual de dias já cobertos começa com `from ≥ horizon`.
  2. A meio dela, a retenção arquiva esses dias e avança o horizonte.
  3. No monolítico, o `DROP` é imediato: só espera pela instrução em curso, não pela transação da agregação, que lê faceta a faceta em instruções separadas.
  4. As facetas lidas depois do `DROP` dão horas com 0 eventos, gravadas com um `aggregated_at` mais recente. Pela regra do *upsert*, essas linhas substituem as boas.

  No híbrido, a `GuardMargin` (1 d) cobre a janela.
- **Correção sugerida:**
  - Voltar a ler o horizonte dentro da transação de `SaveAsync`, com `SELECT … FROM analytics.retention_positions WHERE name='purge_horizon' FOR SHARE`, e abortar se `horizon > from`.
  - Em `AdvancePurgeHorizonAsync`, usar `FOR UPDATE`, ou um *advisory lock* partilhado entre a agregação e a retenção.

### 5. [baixa] Não há coordenação entre várias instâncias dos Workers
- **Ficheiros:** `RetentionService.cs` e `GzipNdjsonEventArchive.cs` (`partial = path + ".partial"`, com `FileShare.None`).
- **Cenário:** com 4 Workers no mesmo anfitrião (cenário `ingest-scale`) e o mesmo `ArchivePath`, todas as instâncias exportam o mesmo dia ao mesmo tempo.
  - As que perdem a corrida ao `.partial` recebem um `IOException`. A execução falha e regista um erro, e nessa instância a guarda também não é refrescada (ver o ponto 1).
  - O custo do arquivo multiplica-se por N.
  - As posições estão protegidas (`GREATEST`, `ON CONFLICT`), por isso não há corrupção.
- **Correção sugerida:** `pg_try_advisory_lock` à volta de `RunOnceAsync`, para que só uma instância corra de cada vez, e um nome temporário único (`.partial-{Guid}`).

### 6. [baixa] Eventos na partição DEFAULT nunca são removidos
- **Ficheiros:** `MonolithicRawEventRetention.cs` (`EnforceAsync` só remove partições nomeadas) e a migração `AddRetention` (com `check_violation`, o dia é saltado com um `WARNING`).
- **Cenário:** os dias com linhas em DEFAULT não chegam a ter partição diária, e essas linhas ficam lá para sempre, sem aviso por parte da retenção. O mesmo acontece, até ao fim do mês, com uma partição mensal que tenha linhas (este caso está documentado). O desvio vai no sentido seguro, mas a RNF-05 deixa de se cumprir sem que nada o assinale.
- **Correção sugerida:** um `DELETE … FROM monolith.occupancy_events_default WHERE "timestamp" < @horizon`, em lotes, ou pelo menos uma métrica ou aviso com o número de linhas antigas em DEFAULT.

### 7. [baixa] `ensure_occupancy_day_partitions` mantém o `ACCESS EXCLUSIVE` até ao fim da função
- **Ficheiro:** migração `20261117091322_AddRetention.cs`, linhas 148–172.
- **Cenário:** o `LOCK TABLE` corre dentro de um bloco `BEGIN … EXCEPTION`. Quando o bloco termina sem erro, a subtransação é confirmada e os seus *locks* ficam com a transação de topo, ou seja, até ao fim do `SELECT` que chamou a função. Daí resulta:
  - A substituição de um mês cria cerca de 30 partições, cada uma com uma leitura de DEFAULT, sempre com a ingestão bloqueada.
  - Um mês com linhas volta a pedir o *lock* e a emitir o `WARNING` em cada dia do ciclo.
  - Não há risco de *deadlock*: a ordem é sempre pai e depois filho.
- **Correção sugerida:** chamar a função dia a dia a partir do C#, uma transação por dia, ou aceitar e documentar (corre de 24 em 24 horas).

### 8. [baixa, a confirmar] `PartitionDaysBehind` pode recriar partições já removidas
- **Ficheiro:** `OccupancyPartitionMaintenance.cs` (`from_day = now − PartitionDaysBehind`).
- **Cenário:** se `PartitionDaysBehind` chegar a dias anteriores ao horizonte, a manutenção seguinte volta a criar partições vazias para dias que já foram removidos. No cenário, `DaysBehind = DAYS+3 = 7` cobre os dias removidos (−5 e −4). A manutenção corre no arranque e de 24 em 24 horas, por isso dentro de uma execução é improvável. Se acontecer, distorce `storage_*_partitions`.
- **Correção sugerida:** `from_day = max(now − DaysBehind, FloorDay(horizon))`.

### 9. [baixa] Durabilidade e localização do arquivo
- **Ficheiro:** `GzipNdjsonEventArchive.cs`.
- **O que falta:**
  - O diretório não recebe `fsync` depois do `File.Move`. No Linux (ext4), uma queda logo a seguir ao *commit* do manifesto pode deixar o manifesto a apontar para um ficheiro que não existe, com os eventos já removidos. No NTFS o risco é baixo.
  - Um `ArchivePath` relativo é resolvido contra o diretório corrente. Com `dotnet run`, isso é a pasta do projeto, dentro de `src/`.
- **Correção sugerida:** fazer `fsync` ao diretório no Linux e resolver o caminho contra o `ContentRoot`, ou exigir um caminho absoluto.

### 10. [baixa, processo] ADR-0013 e rastreabilidade em falta
O ADR-0013 é citado em quase todos os ficheiros novos, mas `docs/adr/0013-*.md` não existe. A linha RNF-05 de `docs/RASTREABILIDADE.md` continua com «—». Talvez esteja previsto no passo de documentação da fase. Fica registado para não se perder.

## B. Validade da medição (`bench/scenarios/retention.sh`, `bench/lib/retention_stats.py`)

### 11. [alta] O *polling* com `mongosh` entra no CPU do contentor `mongo`
- **Ficheiros:** `retention.sh:107-115` (`wait_settled`, uma contagem por segundo, dentro da janela de carga c1→c2), `retention.sh:169-172` (uma contagem de 5 em 5 s, dentro da janela de retenção c3→c4) e `retention_stats.py:63-66` (`docker compose exec mongo mongosh …`).
- **Cenário:**
  - Cada contagem arranca um processo Node.js (o `mongosh`) dentro do contentor e faz autenticação SCRAM. Isso custa, por ordem de grandeza, várias décimas de segundo de CPU por chamada (a confirmar), e tudo é somado ao `usage_usec` do cgroup do `mongo`.
  - O `psql` do monolítico é um binário em C e custa muito menos.
  - Na janela de retenção do híbrido, que espera por ciclos do monitor TTL (60 s), há dezenas de chamadas. Podem pesar tanto como o próprio trabalho de remoção.
  - O enviesamento é sistemático e penaliza o híbrido em `cpu_load_mongo_seconds` e `cpu_retention_mongo_seconds`.
- **Correção sugerida:**
  - Contar a partir de fora do contentor medido: `pymongo`/`psycopg` no anfitrião, ou um contentor auxiliar (`docker run --network …`).
  - Em alternativa, calibrar o custo de N contagens em repouso, subtraí-lo e registar o número de contagens por janela.

### 12. [média/alta] A retenção pode começar durante a agregação medida
- **Ficheiros:** `OccupancyAggregator.cs` (`AggregateAsync` grava a cobertura em cada fatia de `MaxHoursPerRun = 24 h`) e `retention.sh:157` e `164-166`.
- **Cenário:**
  1. A primeira fatia cobre o dia START inteiro e é confirmada antes de o comando terminar.
  2. O serviço de retenção, de 15 em 15 s, pode arquivar START, avançar o horizonte e fazer o `DROP`/TTL antes de c3.
  3. O CPU desse arquivo e dessa remoção cai fora da janela `retention`.
  4. `events_eligible`, contado depois da agregação, já pode vir reduzido. No monolítico é quase certo, porque o `DROP` é imediato.

  O resultado depende da duração da agregação, por isso varia entre execuções e entre variantes.
- **Correção sugerida:**
  - Correr o `aggregate-occupancy` do cenário com `Analytics__MaxHoursPerRun=$((24*RETENTION_DAYS))`, para que a cobertura seja confirmada numa só transação, no fim.
  - Contar os elegíveis antes da agregação.
  - Imediatamente antes de c3, verificar que `analytics.retention_positions` ainda não tem `archived_until`; se tiver, falhar a execução.

### 13. [média] O CPU total inclui o consumo de fundo, e as janelas têm durações diferentes
- **Ficheiros:** `retention_stats.py` (`cpu_diff_rows` soma todos os componentes) e `retention.sh` (a fase `idle` é medida, mas não é usada).
- **Cenário:** a janela de retenção do híbrido inclui a espera pelos ciclos do monitor TTL (≥ 60 s), mais `fsync`/*checkpoints*. O monolítico acaba num intervalo de 15 s. O consumo em repouso dos três contentores e dos dois processos entra em `cpu_retention_total_seconds` e em `_per_1e6_events`, proporcionalmente à duração da janela. Isto penaliza a variante mais lenta a remover, mesmo que o trabalho útil seja igual.
- **Correção sugerida:**
  - Escrever a duração de cada janela.
  - Escrever também o CPU líquido: `delta − taxa_idle × duração`, por componente.
  - Na tese, usar o líquido, ou as duas formas, de modo explícito.

### 14. [média] O custo de devolver o espaço no híbrido não é medido
- **Ficheiro:** `retention.sh:187-197` (o `compact` só mede segundos).
- **Cenário:**
  - Não há instantâneo de CPU à volta do `compact`, por isso o custo real de devolver o espaço ao sistema de ficheiros no híbrido fica subestimado.
  - A comparação justa de espaço é «monolítico `after_retention`» contra «híbrido `after_compact`». Mas em produção o `compact` não é rotina: o WiredTiger reutiliza os blocos livres.
  - A tese tem de dizer qual das duas leituras usa, `storage_bytes` ou `storage_bytes − free_bytes`.
- **Correção sugerida:** instantâneos `cpu-compact0/1` e uma linha `storage_after_retention_reusable_bytes = storage − free`.

### 15. [baixa, a confirmar] O serviço de retenção pesa de forma diferente durante a carga
- **Ficheiro:** `MonolithicRawEventRetention.cs` (`GetEarliestTimestampAsync` → `min("timestamp")` em cada partição; `CountAsync` do dia inteiro).
- **Cenário:**
  - Enquanto está bloqueado, o serviço corre de 15 em 15 s: lê o evento mais antigo e conta o primeiro dia.
  - No monolítico, nenhum índice começa por `timestamp`: a chave primária é `(event_id, timestamp)` e os índices começam por `facility_id`. Por isso, `min()` e `count()` fazem leituras sequenciais de ~250 mil linhas.
  - No híbrido, as mesmas leituras usam o novo índice TTL.

  O custo é pequeno, mas entra em `cpu_load_postgres_seconds`.
- **Correção sugerida:** registar a duração de cada execução da retenção, ou dar ao serviço um intervalo maior enquanto está bloqueado.

### 16. [baixa] A retenção é medida sem ingestão em simultâneo
- **Ficheiro:** `retention.sh` (passo 4).
- **Cenário:** fica por medir:
  - a interferência das remoções TTL nas inserções (híbrido);
  - a fila de *locks* do `DROP` com `ACCESS EXCLUSIVE` no pai, que pode bloquear novas inserções até 2 s (`lock_timeout`), no monolítico.

  As conclusões da RNF-05 sobre o impacto na operação não ficam suportadas por este cenário.
- **Correção sugerida:** uma variante opcional com carga de fundo a uma taxa baixa e a latência e2e (D7) durante a janela de retenção.

### 17. [baixa] Margem da guarda e relógios diferentes
- **Ficheiro:** `retention.sh:34,55`.
- **Cenário:**
  - Margem: o híbrido guarda 5 min antes do horizonte, e o monolítico remove o dia exato. Fica registado em `events_removed` (cerca de 0,35 % de um dia).
  - Relógios: o monitor TTL usa o relógio da VM do Docker (WSL2), e a guarda usa o relógio do Windows. Depois de uma suspensão, a deriva do WSL2 pode passar os 5 minutos e remover eventos posteriores ao horizonte.
- **Correção sugerida:** registar no `meta.json` a diferença entre `date` no contentor `mongo` e no anfitrião, no início e no fim de cada execução.

### 18. [baixa] Comparabilidade com fases anteriores e execuções que passam a meia-noite
- **Cenário:**
  - Este cenário usa partições diárias no monolítico, e as Fases 8 e 9 usavam mensais.
  - O híbrido tem agora um 4.º índice, `ix_timestamp_ttl`, que pesa em todas as inserções. Os resultados de ingestão posteriores a `b28d804` deixam de ser comparáveis com os anteriores sem uma nova linha de base.
  - Uma execução que passe a meia-noite UTC arquiva um dia a mais. Hoje isso só se vê indiretamente, em `events_removed`.
- **Correção sugerida:**
  - Escrever uma linha `days_archived`, lida do *ledger*, e uma *flag* `crossed_midnight`, para excluir essas execuções.
  - Anotar no `bench/README.md` a mudança de linha de base.

## C. Lacunas nos testes
- Não há teste da guarda quando a retenção é desligada depois de ativa, nem quando o arquivo falha (ponto 1).
- Não há teste de um evento atrasado que chega depois da agregação da sua hora (ponto 2), nem de um evento tardio depois do arquivo (ponto 3).
- O ramo de *clamp* do *worker* (`RunAsync` com `from < CeilHour(horizon)`, que grava o *checkpoint* sem linhas) não é exercitado.
- Não há teste com duas execuções concorrentes de `RunOnceAsync` (ponto 5), nem do caminho `lock_not_available` → `break` do `DROP`.
- Não há teste unitário do decorador: empate de *timestamp*, entrada da fotografia com `Timestamp ≥ before`, e *spot* que só existe na fotografia na leitura sem faceta.
- O teste da guarda verifica a fórmula. O teste de integração confirma que B e C ficam, mas não exercita a margem (eventos entre `H − m` e `H`).

## D. Verificado sem problemas
- **O horizonte e `archived_until` só avançam.** Usam `GREATEST` em `ON CONFLICT`, e o manifesto e a posição gravam-se na mesma transação.
- **O arquivo não salta dias.** Pára no primeiro dia que falhe a cobertura ou a contagem. Um dia vazio só se regista se `count = 0`. O horizonte é `min(now − RawEvents, archived)`.
- **Limites de dia e de hora:** `[start, end)` é igual em `CountAsync`, `ExportAsync`, `CountAggregatedHoursAsync` e no `generate_series(from, to − 1h, 1h)`. O `interval '1 hour'` não é afetado pela hora de verão. `FloorDay` e `DateOnly` são sempre UTC. Os limites das partições (`day::timestamp AT TIME ZONE 'UTC'` formatado com `%L`) levam o deslocamento, por isso não dependem do `TimeZone` da sessão.
- **Contas da guarda:** com `E = ⌈(t0 − H + m)/60⌉·60`, o limiar `H − m + (t − t0)` fica ≤ H enquanto `t − t0 ≤ m`. O arredondamento para cima só aumenta a margem. O teto `int.MaxValue` é o máximo que o MongoDB aceita. Sem horizonte, o índice é reposto em «nunca».
- **Tratamento do `IndexOptionsConflict` (85):** está certo. Um reinício não repõe «nunca» por cima do valor da guarda, e `EnsureCollectionAsync` trata `NamespaceExists` (48).
- **Caminho do `DROP`:**
  - Só remove partições com `To ≤ horizon`, ou seja, com todas as linhas antes do horizonte.
  - Pede primeiro o *lock* do pai (e o `LOCK TABLE` sem `ONLY` desce às partições), na mesma ordem que as inserções.
  - Com `lock_timeout`, faz `break` e não remove fora de ordem; o `IF EXISTS` cobre outra instância.
  - A DEFAULT nunca é removida (`TryRange` recusa-a).
- **Funções SQL:**
  - O `CONTINUE WHEN` está fora do bloco de exceção.
  - Uma falha (`check_violation`) a meio da substituição de um mês desfaz toda a subtransação, incluindo o `DROP` da partição mensal.
  - O `has_rows` é lido já com o *lock*.
  - `duplicate_table` e `lock_not_available` são tratados.
- **Fotografia:** é tirada antes de o horizonte avançar. O *upsert* só substitui por um evento mais recente (`(timestamp, event_id) <`). A ordem de desempate dos UUID é a mesma em .NET (`Guid.CompareTo` por campos), em PostgreSQL e em MongoDB (bytes na ordem RFC). Duas instâncias com alvos diferentes não regridem a fotografia.
- **Tempos de vida no DI:**
  - O decorador é *singleton* sobre um *log singleton* e uma loja de fotografia *singleton*, que usa o `NpgsqlDataSource` e não o `DbContext`. Não há dependência cativa.
  - O *ledger* e o `OccupancyRetention` são *scoped*.
  - `RetentionOptions` é a mesma instância no `MongoRawEventRetention` (`RemoveAll` + `Add` antes do `Build`).
  - `NpgsqlDataSource` também está registado no modo híbrido.
- **Atomicidade do arquivo:** `.partial`, `Flush(true)`, `Move`; a contagem é verificada e o ficheiro é apagado se não bater certo.
- **Agregador:** a recusa de intervalos manuais antes do horizonte é conservadora (`from < horizon`, sem arredondar). No *worker*, o *clamp* para `CeilHour(horizon)` só salta horas que já estão cobertas, porque o horizonte não passa de `archived`, e `archived` exige cobertura.
- **Simulador:** `--sensor-start` e `--time-scale` não alteram o `Describe()` dos cenários anteriores. A validação impede *timestamps* no futuro.
- **`write_meta.py`:** a descodificação em UTF-8 com `errors="replace"` está certa.

## Resumo
- **Correção:** o risco principal é a guarda do TTL falhar em aberto (ponto 1). Desligar a retenção, ou uma falha persistente no passo de arquivo, deixa o MongoDB apagar dias não arquivados ao fim de `GuardMargin`. A seguir vêm os eventos atrasados que a cobertura de 24 horas dá como agregados sem o estarem (ponto 2) e a regra `MaxEventAge ≤ ArchiveDelay`, que não é validada (ponto 3).
- **Medição:** o *polling* com `mongosh` dentro das janelas de CPU (ponto 11) e a retenção que pode começar durante a agregação (ponto 12) enviesam os números de CPU. O consumo de fundo não é descontado (ponto 13) e o CPU do `compact` não é medido (ponto 14).
- **O que está sólido:** a ordem arquivo → fotografia → horizonte → remoção, a matemática da guarda, o SQL das partições e do `DROP`, a semântica da fotografia e os tempos de vida no DI. As correções propostas são locais.
