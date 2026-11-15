# Revisão de desempenho da base de dados — Fase 2b (UC-02 e outbox)

- **Data:** 15-11-2026
- **Âmbito:** o *diff* do ramo `phase-2b-uc02-outbox` em relação a `main` (`git diff main...HEAD -- src`), com foco nas consultas, no `SmartParkingDbContext`, nos *interceptors* (outbox e auditoria), nas configurações EF Core e na migração `AddOutbox`.
- **Revisor:** um agente separado (Claude, «db-performance-auditor»), sem acesso ao raciocínio da sessão que escreveu o código. Revisão só de leitura: nenhum ficheiro de código foi editado e nada foi *commitado*.
- **Método e provas:**
  - **SQL real gerado pelo EF Core:** um programa descartável (fora do repositório, na pasta temporária da sessão) referenciou `SmartParking.Infrastructure.Relational`, registou-o com `AddRelationalPersistence` (a mesma configuração da API) e executou `StartAsync`, `CloseAsync` e `PayAsync` contra um contentor PostgreSQL 16.16 **descartável** (porta 55432, apagado no fim), com o *log* `Microsoft.EntityFrameworkCore.Database.Command`/`Transaction` ativo. A base de dados de desenvolvimento não recebeu dados.
  - **Planos:** `EXPLAIN (COSTS OFF)` na base de dados de desenvolvimento, só de leitura. As tabelas estão quase vazias (0 tarifários, 4 lugares), por isso os planos foram obtidos com `SET enable_seqscan = off`. Mostram **que índices são elegíveis**, não a escolha do planeador com 5 000 lugares. A escolha real fica para a Fase 9, com dados de carga.
  - **Contadores:** `pg_stat_user_tables` (`n_tup_upd`, `n_tup_hot_upd`) e `pg_column_size` no contentor descartável, depois das três operações.
  - Nenhum valor de latência foi medido. Os tempos que aparecem no *log* do EF Core (1–13 ms por comando, numa máquina local sem carga) são indicativos e **não** servem de resultado para a tese.

## Resumo

| # | Gravidade | Achado | Quando |
|---|---|---|---|
| 1 | Média | Dois processos com `Maximum Pool Size` 100 (omissão do Npgsql) contra `max_connections` = 100 | Antes da Fase 9 (pode ser já: só configuração) |
| 2 | Média | Outbox: crescimento sem limite e `UPDATE processed_at` sem HOT | Fase 5 (worker do outbox) |
| 3 | Baixa | Idas à base de dados por comando: 4 / 4 / 3; uma em Start e uma em Close são evitáveis | Fase 9, só se a medição o justificar |
| 4 | Baixa | `EfTariffLookup` usa índices, mas ordena em memória; `(zone, valid_from)` seria o índice exato | Fase 9 |
| 5 | Baixa | As atualizações de `parking_sessions` (Close, Pay) nunca são HOT | Sem correção; documentar (Cap. 6.4) |
| 6 | Baixa | `DetectChanges` corre várias vezes por `SaveChanges` | Sem correção |
| 7 | Baixa | `AddDbContext` vs `AddDbContextPool`: compatível hoje, mas não com um `ICurrentActor` por pedido | Fase 9 (experiência opcional) |

## Achados

### 1. Pool de ligações maior do que o servidor aceita (média, plausível)

- **Problema:**
  - A *connection string* (`src/SmartParking.Api/appsettings.json:4`) não define `Maximum Pool Size`. O valor por omissão do Npgsql é 100 ligações por *data source*.
  - A API e os Workers registam ambos a persistência relacional (`src/SmartParking.Workers/Program.cs:15` chama `AddRelationalPersistence`). Cada processo tem o seu *pool*: até 200 ligações no total.
  - O servidor de desenvolvimento aceita `max_connections = 100`, com `superuser_reserved_connections = 3` (valores lidos com `SHOW` no contentor `smartparking-hybrid-postgres-1`). Restam 97 ligações para utilizadores normais.
  - Com carga k6 suficiente (muitos pedidos UC-02 em simultâneo), a API pode ocupar quase todas as ligações. Os Workers e o *health check* ficam então sem ligação e recebem o erro `53300 too_many_connections`. O efeito no p95 seria um erro, não só uma latência maior.
- **Confiança:** plausível. Os valores de configuração estão confirmados; o esgotamento depende da concorrência do cenário k6, que ainda não existe.
- **Recomendação:**
  - Definir `Maximum Pool Size` explicitamente, por processo, numa configuração versionada (por exemplo, a API com a maior fatia e os Workers com uma fatia pequena), de modo que a soma fique abaixo de 97.
  - Registar esses valores na configuração de cada experiência da Fase 9, porque influenciam a latência sob carga. Os valores concretos são `TODO(Diogo)`: dependem do número de VUs do cenário `session-payment`.

### 2. Outbox: crescimento sem limite e `UPDATE processed_at` sem HOT (média, confirmado)

- **O que está bem:**
  - O índice parcial `ix_outbox_messages_unprocessed` (`occurred_at WHERE processed_at IS NULL`) serve a consulta prevista do *poller*. O plano é `Index Scan using ix_outbox_messages_unprocessed`, também com `FOR UPDATE SKIP LOCKED`:

    ```
    Limit
      ->  LockRows
            ->  Index Scan using ix_outbox_messages_unprocessed on outbox_messages
                  Filter: (processed_at IS NULL)
    ```

  - As linhas processadas saem do índice, por isso o índice fica pequeno.
- **Problema:**
  - **O *heap* e a chave primária crescem sem limite.** Cada comando UC-02 escreve uma linha (`OutboxSaveChangesInterceptor.cs`); nenhuma é apagada. Ainda não há retenção (RNF-05).
  - **Marcar uma linha como processada não é uma atualização HOT.** `processed_at` faz parte do predicado do índice parcial, e o PostgreSQL trata as colunas de predicado como colunas indexadas. No contentor descartável, `UPDATE outbox_messages SET processed_at = now()` sobre 3 linhas deu `n_tup_upd = 3`, `n_tup_hot_upd = 0`. Cada linha publicada deixa uma versão morta e uma entrada nova em `pk_outbox_messages`. O *autovacuum* tem de as limpar.
  - **Payload `jsonb`:** 216–340 bytes por evento (`pg_column_size`). O custo de *parsing* na escrita é pequeno. O `jsonb` permite inspecionar os eventos em SQL, por isso não há razão para mudar.
- **Recomendação (Fase 5):** escolher uma destas estratégias e registá-la em ADR (complemento ao ADR-0004):
  - `DELETE` depois de publicar;
  - `UPDATE processed_at` com uma tarefa de retenção que apague as linhas processadas há mais de N dias (RNF-05).

  Em ambos os casos, processar em lotes (`LIMIT n FOR UPDATE SKIP LOCKED`).
- **Nota de correção para o *poller*:** `occurred_at` é o instante do domínio, não o do *commit*. Uma transação mais lenta pode tornar visível uma linha com `occurred_at` anterior a outras já publicadas. Ler sempre `WHERE processed_at IS NULL`, como está previsto, e **não** usar um cursor `occurred_at > último_lido`, que perderia essas linhas.

### 3. Idas à base de dados por comando (baixa, confirmado)

- **O que está bem:** o EF Core junta todas as escritas de um `SaveChanges` num único `DbCommand` (*batching*), dentro de uma transação `ReadCommitted`. Os *interceptors* acrescentam linhas a esse lote, não fazem idas extra.
- **Contagem, a partir do *log* do EF Core:**

  | Comando | Leituras antes da transação | Lote de escrita (um só `DbCommand`) | Total |
  |---|---|---|---|
  | Start | `SELECT EXISTS` em `parking_spots`; `SELECT … FROM vehicles … LIMIT 1` | 1 `INSERT` em `audit_log`, 1 em `outbox_messages`, 1 em `parking_sessions` | 3 comandos + `COMMIT` = **4** |
  | Close | `SELECT … FROM parking_sessions … LIMIT 1`; `EfTariffLookup` (achado 4) | 1 `INSERT` em `audit_log`, 1 em `outbox_messages`, `UPDATE parking_sessions … WHERE id = @p AND xmin = @p` | 3 + `COMMIT` = **4** |
  | Pay | `SELECT … FROM parking_sessions … LIMIT 1` | 2 `INSERT` em `audit_log`, 1 em `outbox_messages`, `UPDATE parking_sessions`, 1 `INSERT` em `payments` | 2 + `COMMIT` = **3** |

  O `COMMIT` é uma ida própria. Pelo que se sabe do Npgsql, o `BEGIN` segue no mesmo pacote que o comando seguinte. Esta parte não foi medida ao nível da rede (plausível).
- **Idas evitáveis:**
  - **Start:** a verificação do lugar e a leitura do veículo podem ser uma só consulta (por exemplo, `SELECT` do veículo com `EXISTS` do lugar na mesma instrução). Retirar o `EXISTS` e confiar na FK `fk_parking_sessions_spots_spot_id` também poupava uma ida, mas mudava a resposta de 404 para 409 (`ReferentialIntegrityException`). Não se recomenda.
  - **Close:** o carregamento da sessão e a procura do tarifário podem ser uma só consulta (junção a partir de `parking_sessions`). Fica menos legível.
- **Peso:** cada leitura por chave primária custou 1–3 ms no *log* local, sem carga. Isto é pequeno em relação ao alvo de 2 s no p95 (RNF-01, UC-02). Não justifica complicar o código antes de haver medições.
- **Amplificação de escrita** (dado útil para o Cap. 6.4, custo assumido de RNF-06 + ADR-0004): cada operação de negócio escreve 3 linhas (Start, Close) ou 5 linhas (Pay), duas a três delas `jsonb` de auditoria ou outbox. Tamanhos medidos: `audit_log.changes` entre 57 e 317 bytes.
- **Recomendação:** não alterar agora. Na Fase 9, medir; se o tempo na base de dados pesar no p95, fundir as leituras de Start e de Close.

### 4. `EfTariffLookup` usa índices, mas ordena em memória (baixa, confirmado)

- **SQL gerado** (`Persistence/EfSessionPersistence.cs:8-18`):

  ```sql
  SELECT t.id, t.xmin, t.zone, t.rules, t.valid_from, t.valid_to
  FROM parking_spots AS p
  INNER JOIN parking_facilities AS p0 ON p.facility_id = p0.id
  INNER JOIN tariffs AS t ON p0.zone = t.zone
  WHERE p.id = @spotId AND t.valid_from <= @instant AND (t.valid_to IS NULL OR t.valid_to > @instant2)
  ORDER BY t.valid_from DESC, t.id DESC
  LIMIT 1
  ```

- **Plano** (com `enable_seqscan = off`): `pk_parking_spots` → `pk_parking_facilities` → `ix_tariffs_zone`, com filtro de validade e um `Sort` antes do `Limit`. O filtro sobre a *complex property* `Validity` traduz-se em colunas simples, sem obstáculo ao uso de índices. `parking_facilities.zone` tem índice (`ix_parking_facilities_zone`), mas esta consulta não precisa dele: entra pela chave primária.
- **Problema:** o `Sort` processa todas as versões de tarifário da zona. Hoje são poucas por zona, por isso o custo é desprezável. Só cresce se o histórico de tarifários crescer muito.
- **Recomendação (Fase 9, se aparecer no perfil):**
  - substituir `ix_tariffs_zone` por `(zone, valid_from DESC)`, que serve também as consultas só por zona e elimina o `Sort`;
  - em alternativa, guardar em memória o tarifário em vigor por zona, porque os tarifários mudam raramente.

### 5. As atualizações de `parking_sessions` nunca são HOT (baixa, confirmado)

- **Problema:**
  - **Close** altera `tariff_id`, que tem índice (`ix_parking_sessions_tariff_id`).
  - **Pay** só altera `status`, mas `status` está no predicado de `ux_parking_sessions_spot_id_open`.
  - Nos dois casos, o PostgreSQL não pode fazer uma atualização HOT. Cada atualização acrescenta entradas aos cinco índices não parciais da tabela.
  - No contentor descartável: `n_tup_upd = 2`, `n_tup_hot_upd = 0`, numa página com espaço livre. A falta de espaço não explica o resultado.
- **Peso:** com 5 000 lugares e sessões à escala humana, o efeito é algum *bloat* de índices, que o *autovacuum* resolve. Baixar o `fillfactor` não ajuda, porque a causa são as colunas indexadas e não o espaço na página.
- **Recomendação:** não alterar. Registar no Cap. 6.4 como custo do invariante «um lugar, no máximo uma sessão aberta» garantido pela base de dados.

### 6. `DetectChanges` repetido em cada `SaveChanges` (baixa, plausível)

- **Problema:**
  - `OutboxSaveChangesInterceptor` chama `ChangeTracker.Entries<IHasDomainEvents>()`, que deteta alterações.
  - `AuditSaveChangesInterceptor.cs` chama `DetectChanges()` explicitamente e depois `Entries()`, que deteta outra vez.
  - O próprio `SaveChanges` volta a detetar depois dos *interceptors*.
  - São cerca de quatro passagens sobre o *change tracker* por comando. A contagem exata depende da ordem interna do EF Core e não foi instrumentada.
- **Peso:** cada passagem percorre apenas as 1–3 entidades seguidas por pedido (contexto por pedido). O custo fica na ordem dos microssegundos.
- **Recomendação:** nenhuma. Só seria relevante num contexto com muitas entidades seguidas, o que não acontece neste desenho.

### 7. `AddDbContext` vs `AddDbContextPool` (baixa, plausível)

- **Estado:** `DependencyInjection.cs` usa `AddDbContext`, com os dois *interceptors* registados como *singletons*. São compatíveis com `AddDbContextPool`: são *stateless*, e `AuditSaveChangesInterceptor` só depende de `ICurrentActor` (*singleton*, `FixedActor`) e de `TimeProvider`.
- **Problema futuro:**
  - Quando houver autenticação (RNF-06), `ICurrentActor` passa a ser por pedido.
  - Um *interceptor singleton* não pode receber um serviço *scoped*.
  - Com *pooling*, as opções são construídas uma vez a partir do *provider* raiz, por isso também não se podem usar *interceptors scoped*.
  - Será preciso obter o ator por outra via (por exemplo `IHttpContextAccessor`, que é *singleton*).
- **Peso:** o *pooling* poupa a criação do contexto por pedido. O ganho é pequeno face às idas à base de dados do achado 3. Não há medição neste projeto.
- **Recomendação:** manter `AddDbContext` agora. Se a Fase 9 quiser testar `AddDbContextPool`, que seja uma variável da experiência, medida contra o valor por omissão. Ao introduzir o ator por pedido, escolher uma solução que não impeça essa opção.

## Verificado sem problemas

- **`EfRepository.GetAsync` (`FindAsync`):** gera `SELECT … WHERE id = @p LIMIT 1` pela chave primária (`Index Scan using pk_parking_sessions`). A entidade fica seguida, como é necessário para o `UPDATE … WHERE xmin = @p`. O `UPDATE` só envia as colunas alteradas.
- **`ExistsAsync` (`AnyAsync`):** gera `SELECT EXISTS (…)` com `Index Only Scan using pk_parking_spots`.
- **Concorrência no índice único parcial:**
  - Dois Start simultâneos no mesmo lugar: o segundo `INSERT` espera pelo *commit* do primeiro no índice `ux_parking_sessions_spot_id_open` e falha com 23505, que dá 409.
  - O bloqueio dura apenas o lote e o `COMMIT`, porque as leituras ficam fora da transação.
  - Com 5 000 lugares, só pedidos para o mesmo lugar competem.
- **Conflitos de `xmin`** em Close e Pay concorrentes:
  - Em `ReadCommitted`, o segundo `UPDATE` espera pelo primeiro e, sem linhas afetadas, dá `DbUpdateConcurrencyException`, que dá 409.
  - O `INSERT` do pagamento está no mesmo lote, por isso também é revertido. Não há pagamento órfão.
  - Não há novas tentativas automáticas, o que está de acordo com a observação do `OutboxSaveChangesInterceptor`.
- **Índices existentes relevantes:** `payments.session_id` (`ix_payments_session_id`), `parking_sessions(spot_id, started_at)`, `tariffs.zone` e `parking_facilities.zone` existem na migração `InitialCreate`.
