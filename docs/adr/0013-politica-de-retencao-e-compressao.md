# ADR-0013 — Política de retenção e compressão dos eventos em bruto (RNF-05)

- **Estado:** aceite (prazos provisórios, `TODO(Diogo)`)
- **Data:** 17-11-2026
- **Fase:** 10 (retenção e sustentabilidade)
- **Relaciona-se com:**
  - ESPECIFICACAO.md: §6 (RNF-05, cenário `retention`), §7 (variante monolítica), §9 (Fase 10);
  - ADR-0007 (log de eventos em MongoDB, D3), ADR-0008 (reconstrução do *read model*), ADR-0010 (tabelas de resumo, D4), ADR-0012 (variante monolítica);
  - revisão `docs/revisoes/fase-10-revisao-codigo.md`.

> Nota de numeração: o guião da Fase 10 pedia «ADR 0011», mas esse número já é o da evolução de esquema (Fase 7). Este registo é o 0013. É a sétima fase seguida em que o número do guião está ocupado.

## Contexto

O RNF-05 pede que se meça o armazenamento e o CPU por cada 10⁶ eventos, e o efeito da retenção (TTL e compressão). Com a D8 entram cerca de 1,4 milhões de eventos por dia: sem retenção, o log do MongoDB cresce sem limite.

A especificação não fixa nenhum prazo de retenção. Além disso, o log não é só um arquivo. Três leitores dependem dele:
1. **A agregação do UC-04 (ADR-0010).** Lê os eventos de cada hora fechada. Lê também o último evento de cada lugar antes da janela, «por mais antigo que seja».
2. **A reconstrução do *read model* (ADR-0008).** Lê o último evento de cada lugar.
3. **O comando `aggregate-occupancy`.** Pode reagregar qualquer intervalo.

Um índice TTL do MongoDB só conhece uma data: não sabe se um dia já foi agregado nem arquivado. O guião da fase pedia que se confirmasse no plano que o TTL não apaga eventos ainda não agregados, para não pôr em risco os relatórios do UC-04.

Na análise antes do código apareceram dois riscos que o guião não previa:
- **O checkpoint não prova a agregação.** No primeiro arranque, o agregador só trata as últimas 24 h (`InitialHours`). O comando manual não mexe no checkpoint. Um dia antigo pode estar atrás do checkpoint sem nunca ter sido agregado.
- **Apagar eventos antigos muda o estado inicial.** Um carro parado há mais tempo do que o prazo perderia o seu único evento. A agregação passaria a contá-lo como livre, e a reconstrução do Redis também.

A revisão independente da fase acrescentou mais três (achados 1, 2 e 4):
- a guarda do TTL podia falhar em aberto;
- os eventos atrasados podiam ser removidos sem nunca terem sido agregados;
- uma agregação longa podia ler um log que o horizonte, entretanto, tinha encurtado.

Decisões tomadas com o Diogo a 17-11-2026, antes do código:

| Ponto | Escolha |
|---|---|
| Prazo dos eventos em bruto | **30 dias**, provisório (`TODO(Diogo)`) |
| Como impedir que o TTL apague o que não foi agregado | **Índice TTL + guarda** (não um *worker* com `deleteMany`, nem um campo `expireAt` atualizado depois) |
| Carga de 10⁶ eventos | **Pela API** (o caminho real), com tempo comprimido no simulador |
| Partições da variante monolítica | **Granularidade configurável**, diária no cenário |

## Decisão

### 1. A mesma sequência nas duas variantes (`OccupancyRetention`, Application)

Em cada execução (por omissão, a cada minuto, nos Workers), os passos são sempre estes, por esta ordem:

1. **Arquivo.** O dia UTC seguinte à posição arquivada é exportado se cumprir quatro condições:
   - acabou há pelo menos `ArchiveDelay` (8 dias);
   - as suas 24 horas estão em `analytics.aggregated_hours` (cobertura);
   - **nenhum evento de cada hora foi gravado depois da última agregação dessa hora** (eventos atrasados);
   - o ficheiro tem exatamente tantos eventos como o armazém.

   Um dia que falhe uma destas condições pára o arquivo nessa execução. A posição nunca salta um dia, por isso nada mais antigo do que um dia por arquivar pode ser removido. Um dia sem eventos é registado sem ficheiro.
2. **Fotografia.** Antes de o horizonte avançar, o último evento de cada lugar anterior ao novo horizonte é gravado em `analytics.spot_state_snapshot`.
3. **Horizonte de purga.** O horizonte é `min(agora − RawEvents, posição arquivada)` e só avança, em passos de pelo menos 1 h. Fica guardado em `analytics.retention_positions`.
4. **Remoção.** O armazém pode remover o que é mais antigo do que o horizonte, e nada mais recente.

**Falha em fechado (revisão, achado 1):**
- **Falha no arquivo.** Uma exceção no passo 1 (por exemplo, disco cheio) não impede o passo 4: a guarda é refrescada com o horizonte atual e só depois a exceção é relançada.
- **Retenção desligada.** Com `Retention:Enabled=false`, o serviço continua registado e repõe o índice TTL em «nunca».

### 2. Cobertura da agregação e eventos atrasados

`analytics.aggregated_hours` tem uma linha por hora UTC agregada, com o instante em que os eventos foram lidos (`aggregated_at`):
- é escrita na mesma transação que as linhas do resumo, pelo *worker* e pelo comando manual;
- conta também as horas sem linhas (por exemplo, sem parques);
- `aggregated_at` só avança;
- a migração `AddRetention` preenche-a com as horas que já tinham linhas antes da Fase 10.

O `aggregated_at` usa o relógio do agregador e não o `now()` da BD, para se poder comparar com o `storedAt` dos eventos, que vem do relógio da aplicação. Antes de arquivar um dia, a retenção compara, hora a hora, o `storedAt` (MongoDB) ou o `stored_at` (monolítico) mais recente com esse instante. Se algum evento chegou depois, o dia espera por um novo `aggregate-occupancy`.

**Gravação recusada (revisão, achado 4).** Dentro da transação da gravação, a agregação lê o horizonte com `FOR SHARE` e recusa um intervalo que comece antes dele. O agregador já recusava um intervalo manual que começasse antes do horizonte, e o *worker* encurtava a sua janela; isto cobre o caso de o horizonte avançar enquanto os eventos estão a ser lidos.

### 3. MongoDB: índice TTL com guarda

- **Índice.** `ix_timestamp_ttl` (`{timestamp: 1}`) é criado com `expireAfterSeconds = 2147483647` (cerca de 68 anos, na prática «nunca»). Até haver horizonte, nada expira.
  - Um TTL tem de ser um índice de um só campo. Por isso este é um **quarto índice** no log, e custa espaço e uma atualização em cada inserção: o cenário mede esse custo.
  - O mesmo índice serve a exportação de cada dia, a procura do evento mais antigo e a verificação dos eventos atrasados.
- **Guarda** (`MongoRawEventRetention`). Mantém, com `collMod`, `expireAfterSeconds = (agora − horizonte) + GuardMargin`, arredondado ao minuto para cima. O monitor TTL remove `timestamp < t − expireAfterSeconds`, por isso, enquanto a guarda correr pelo menos uma vez por margem, a fronteira nunca passa o horizonte.
- **Margem.** `GuardMargin` é de 1 dia por omissão (`TODO(Diogo)`). É a paragem dos Workers que se tolera.
- **Arredondamento.** Ao minuto, e não à hora: com horas, o MongoDB guardava até mais uma hora de eventos do que o `DROP` monolítico, o que distorcia a comparação. Mesmo assim, a fronteira efetiva do TTL fica até 1 minuto antes de `horizonte − GuardMargin`, conforme o segundo em que a guarda corre. O cenário `retention` espera, por isso, por `horizonte − GuardMargin − 1 min`. A primeira sessão oficial esperava por `horizonte − GuardMargin` e falhou com 10^6 eventos, porque restavam sempre alguns nesse último minuto (diário, Fase 10).
- **Reinício.** Se um processo voltar a criar o índice com «nunca» depois de a guarda o ter baixado, o MongoDB devolve `IndexOptionsConflict` (85). Esse erro é tratado como «já existe», e o valor da guarda fica.
- **Espaço.** O TTL apaga documentos, mas o WiredTiger guarda o espaço para reutilizar: o `storageSize` não desce, e até sobe logo a seguir às remoções (cópia na escrita). O espaço livre aparece em `freeStorageSize`, e só o `compact` o devolve ao sistema de ficheiros. O cenário mede os três estados.

### 4. Compressão

`Mongo:EventsBlockCompressor` (`snappy`, por omissão do servidor, `zstd`, `zlib` ou `none`) é aplicado quando a coleção é criada. O WiredTiger fixa o compressor nesse momento, por isso mudar o valor numa coleção que já existe não tem efeito; o `creationString` das estatísticas mostra qual está em vigor, e o *bench* regista-o. Os índices mantêm a compressão de prefixo por omissão.

### 5. Variante monolítica: DROP de partições

- `Persistence:Monolithic:PartitionInterval` pode ser `Month` (por omissão, como na Fase 8) ou `Day`.
- A função `monolith.ensure_occupancy_day_partitions` cria as partições `occupancy_events_dYYYYMMDD`:
  - uma partição mensal vazia no caminho é removida e substituída pelos dias de todo o mês;
  - uma partição mensal com linhas fica como está;
  - a manutenção nunca começa antes do horizonte, para não recriar dias já removidos (revisão, achado 8).
- A remoção (`MonolithicRawEventRetention`) é um `DROP TABLE` das partições que acabam no horizonte ou antes dele.
  - **Bloqueios.** Antes do `DROP`, o pai é bloqueado em `ACCESS EXCLUSIVE`, com `lock_timeout` de 2 s. O `DROP` bloqueia a partição antes do pai, ao contrário de um `INSERT`. Sem bloquear o pai primeiro, os dois podiam ficar em *deadlock*.
  - **Sem `DETACH … CONCURRENTLY`.** O PostgreSQL não o permite quando existe uma partição DEFAULT.
- A DEFAULT nunca é removida.
- Com partições diárias, fica no máximo mais um dia do que o prazo. Com partições mensais, ficaria até mais um mês.
- O `DROP` devolve o espaço de imediato: não há tuplos mortos nem `VACUUM`.

### 6. Arquivo

- **Formato.** Um ficheiro por dia UTC: `{ArchivePath}/occupancy-events-AAAA-MM-DD.ndjson.gz`, um evento JSON por linha.
  - No MongoDB, em *Relaxed Extended JSON* (os UUID ficam como `$binary`).
  - No PostgreSQL, como `row_to_json` das colunas. O formato não é o mesmo, porque o modelo também não é (ADR-0012).
- **gzip.** Está na biblioteca base do .NET; o zstd não está no .NET 10.
- **Escrita.** Primeiro num ficheiro temporário com nome único por escritor, com `flush` para o disco, e só depois renomeado. O manifesto `analytics.retention_archive` guarda o dia, o ficheiro, o n.º de eventos, os bytes e o SHA-256 dos bytes comprimidos.

### 7. Leitores protegidos e entrada

- **`SnapshotAwareOccupancyEventLog`.** Este decorador do log, registado nos Workers, devolve para cada lugar o mais recente de dois eventos:
  - a resposta do log;
  - a entrada da fotografia, quando esta é anterior ao instante pedido.

  Serve a agregação e a reconstrução do Redis.
- **Ingestão.** A validação do RF-01 recusa eventos mais antigos do que `Ingestion:MaxEventAge` (7 dias, `TODO(Diogo)`).
- **Atraso inicial.** `Retention:InitialDelay` (0 por omissão) atrasa a primeira execução depois do arranque. O *bench* usa-o para tirar o instantâneo de CPU antes de a retenção começar.

## Alternativas consideradas

| Alternativa | Porque não |
|---|---|
| *Worker* com `deleteMany` até `min(corte, checkpoint, arquivo)` | É exato, não precisa de um quarto índice (usa `(facilityId, timestamp)`) e falha em fechado: se os Workers pararem, nada é removido. Foi recusado por decisão do Diogo: o guião pedia um índice TTL, e o custo do TTL passa a ser um resultado medido. Continua a ser a melhor alternativa se o custo do índice ou o risco da margem se revelarem relevantes. |
| TTL num campo `expireAt`, preenchido por `updateMany` depois de agregar | Quebra o *append-only* (ADR-0007) e soma uma escrita por evento (cerca de 1,4 M por dia). |
| TTL sem guarda, com um prazo muito maior do que o atraso da agregação | Apagaria dias nunca agregados (primeiro arranque, comando manual) e o estado dos carros parados há muito. |
| Reagregar sempre o dia antes de o arquivar (revisão, achado 2) | É mais simples do que comparar `storedAt` com `aggregated_at`, mas custa um dia de agregação por dia arquivado. A comparação só pede uma reagregação quando há de facto eventos atrasados. |
| *Time series collection* do MongoDB | Tem compressão por intervalo e TTL próprio, mas não aceita o índice único em `eventId` (ADR-0007). Não foi medida. |
| `_id = eventId` (menos um índice) | Era uma variante a medir, segundo o ADR-0007. Mudaria o modelo do log. Não foi medida nesta fase. |
| Partições mensais no monolítico | Com um prazo de 30 dias, ficariam até 60 dias em disco. A baseline não seria «bem feita» (princípio da Fase 8). |
| `pg_partman` | Não está na imagem; a função própria segue o padrão da Fase 8. |
| zstd no arquivo | Não está na biblioteca base do .NET 10. |

## Consequências

- **Positivas:**
  - nenhum evento é removido antes de estar agregado, com os atrasados incluídos, e arquivado. Isto é verificado por testes nas duas variantes (`RetentionTests`, `RetentionOrderTests`, `SnapshotAwareLogTests`), com 11 mutações apanhadas (diário);
  - os relatórios do UC-04 e a reconstrução do Redis mantêm o estado dos lugares cujo último evento foi removido;
  - o espaço do monolítico é devolvido de imediato.
- **Limitações (para a tese):**
  1. **Assimetria de garantias.** O TTL falha em aberto se os Workers pararem por mais tempo do que `GuardMargin`: o monitor continua e pode passar o horizonte. O `DROP` monolítico falha em fechado. Não há *health check* com a idade do último `collMod` (revisão, achado 1, alínea iii).
  2. **Quarto índice.** O índice TTL custa espaço e CPU em cada inserção (medido no cenário). Os resultados de ingestão a partir desta fase não são diretamente comparáveis com os das fases anteriores.
  3. **Espaço no MongoDB.** O TTL não devolve espaço ao disco sem `compact`, e o `compact` não é uma operação de rotina.
  4. **`MaxEventAge` e `ArchiveDelay` em processos diferentes.** A regra `MaxEventAge ≤ ArchiveDelay` não é validada, e a idade conta-se na receção. Com o *bus* parado mais do que a diferença, um evento pode chegar a um dia já arquivado; fica no log, mas não no ficheiro (revisão, achado 3).
  5. **Várias instâncias dos Workers.** Exportam o mesmo dia ao mesmo tempo (sem *advisory lock*): o resultado é correto, mas o custo multiplica-se.
  6. **DEFAULT.** Linhas na DEFAULT nunca são removidas; a manutenção só avisa (revisão, achado 6).
  7. **Partições diárias.** A substituição de um mês mantém o `ACCESS EXCLUSIVE` do pai até ao fim da função (cerca de 30 partições), uma vez por dia (revisão, achado 7).
  8. **Arquivo.** Não há `fsync` do diretório depois da mudança de nome, e um `ArchivePath` relativo depende do diretório corrente (revisão, achado 9).
  9. **Retenção sem carga.** A retenção foi medida sem ingestão em simultâneo; a interferência nas inserções fica por medir (revisão, achado 16).
  10. **Prazos provisórios.** `RawEvents` (30 dias), `ArchiveDelay` (8 dias), `GuardMargin` (1 dia) e `MaxEventAge` (7 dias) são todos `TODO(Diogo)`.
  11. **Formatos de arquivo diferentes.** As duas variantes não usam o mesmo formato, e o tamanho do arquivo não é comparável byte a byte.
  12. ***Stream* Redis e outbox fora da retenção** (revisão final da 10b, achado 2):
      - o *stream* (`XADD` sem `MAXLEN`) e a tabela `outbox_messages` nunca são aparados nem purgados;
      - o espaço do *stream*, uma cópia de cada evento na memória do Redis, não entra na medição, o que favorece o híbrido na comparação de espaço;
      - `TODO(Diogo)`: implementar o aparo e a purga, ou manter como limitação declarada.
  13. **Componentes parados no monolítico.** O `mongo` e o `redis` continuam a correr e competem pelo CPU da máquina, embora não sejam somados (revisão final, achado 15).
  14. **Medições da 10a corrigidas na 10b:**
      - as linhas `_free_bytes` e `_used_bytes` do híbrido nos CSV de 17-11-2026 são inválidas (o *helper* `stats()` do `mongosh` não devolvia `freeStorageSize`, e o valor ficava 0);
      - o CPU da carga incluía os *healthchecks*.

      A leitura passou para `$collStats`, e o `bench/compose.bench.yml` tira os *healthchecks* das janelas (diário da 10b).
- **Ponto de extensão:** `IRawEventRetentionStore`, `IRetentionLedger`, `ISpotStateSnapshotStore` e `IEventArchive` (Application).

## Medição

Cenário `retention` (`bench/scenarios/retention.sh`, `bench/README.md`), com 5 execuções por configuração:
- híbrido com `snappy`;
- híbrido com `zstd`;
- monolítico com partições diárias.

Cada execução tem cinco passos:
1. **Repouso.** CPU durante 30 s, sem carga. A partir da 10b, 120 s, com os *healthchecks* fora das janelas (`bench/compose.bench.yml`).
2. **Carga.** Cerca de 10⁶ eventos pela API, a 500/s, com o tempo dos sensores comprimido em 4 dias UTC passados.
3. **Agregação** desses dias.
4. **Retenção.** Os Workers são reiniciados com a retenção ligada e removem a primeira metade dos dias.
5. ***Compact*** (só no híbrido).

**CPU.** Contam o cgroup de cada contentor da variante e o processo da API e o dos Workers. Cada janela tem duas formas:
- bruta;
- líquida, ou seja, menos a taxa em repouso vezes a duração da janela.

**Espaço.** Na carga e na retenção mede-se `storageSize`, `freeStorageSize` e os índices (no monolítico, o tamanho das partições). No híbrido mede-se também depois do `compact`.

**Resultados** (*commit* `2c94609`; `results/retention/2026-11-17_1236.csv`, `_1540.csv`, `_1845.csv`; análise e limitações no diário da Fase 10). Medianas de 5 execuções, MB = 10⁶ bytes, dados + índices:

| | Híbrido `snappy` | Híbrido `zstd` | Monolítico |
|---|---|---|---|
| Espaço depois da carga | 242,2 MB | 217,4 MB | 357,3 MB |
| Espaço depois da retenção | 241,8 MB | 216,8 MB | 178,7 MB |
| Espaço depois do `compact` | 99,6 MB | 86,6 MB | — |
| Eventos removidos (de 499 847 elegíveis) | 498 833 a 499 006 | 498 836 a 499 001 | 499 847 |
| CPU da retenção, bruto | 45,7 s | 48,3 s | 11,5 s |

- O `zstd` reduz os dados da coleção em 32,7 % e o total em 10,2 %, porque os índices não mudam.
- No MongoDB, o TTL não devolve espaço ao disco; só o `compact` o faz. No monolítico, o `DROP` liberta-o logo.
- O CPU da carga não é comparado aqui: inclui os *healthchecks* do compose, e a linha de base em repouso é curta demais.
  - **Decisão da 10b:** opção A. Os *healthchecks* só correm no arranque e depois de hora a hora (`bench/compose.bench.yml`), e o repouso passa a 120 s.
  - **Medido em repouso:** o `mongo` desce de 0,145 para 0,0045 s de CPU por segundo.
  - As sessões foram repetidas na 10b (resultados abaixo).
- **Espaço livre.** As linhas `_free_bytes` e `_used_bytes` do híbrido nestes CSV são inválidas (sempre 0; limitação 14). Os valores de `_total_bytes` e os de depois do `compact` estão certos.

**Resultados repetidos na 10b** (*commit* `81a7a5f`; `results/retention/2026-11-18_1510.csv`, `_1824.csv`, `_2138.csv`):
- os *healthchecks* ficaram fora das janelas e o espaço livre passou a ser lido;
- medianas de 5 execuções (no monolítico, a retenção usa as 4 execuções que não passaram a meia-noite UTC);
- MB = 10⁶ bytes; espaço em `used` / `total`.

| | Híbrido `snappy` | Híbrido `zstd` | Monolítico |
|---|---|---|---|
| Espaço depois da carga | 182,5 / 246,8 MB | 154,8 / 218,1 MB | 357,4 MB |
| Espaço depois da retenção | 95,9 / 245,3 MB | 83,2 / 218,1 MB | 178,7 MB |
| Espaço depois do `compact` | 95,9 / 102,5 MB | 83,2 / 86,1 MB | — |
| CPU da carga, bruto (por 10⁶ eventos) | 323,3 s | 333,6 s | 175,0 s |
| CPU da retenção, bruto | 36,5 s | 37,1 s | 11,8 s |

- **O TTL liberta o espaço dentro do WiredTiger** (o `used` desce). O `compact` devolve-o ao disco.
- **Na tese, o CPU bruto é a comparação principal.** O líquido ainda tem componentes ligeiramente negativos (diário da 10b).
- **Limitação 12:** os números de espaço continuam sem o *stream* Redis.
