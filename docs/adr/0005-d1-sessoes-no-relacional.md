# ADR-0005 — D1: sessões de estacionamento na subcamada relacional

- **Estado:** aceite (valor por omissão de uma decisão «EM ABERTO»; pode ser revisto após a reunião com o orientador)
- **Data:** 15-11-2026
- **Fase:** 2b (UC-02 com outbox)
- **Relaciona-se com:** ESPECIFICACAO.md §2 (D1), §3 (mapeamento), §4 (UC-02); Cap. 3 (UC-02, RF-02, 3.5) e Cap. 4 (4.4.2, 4.5) da tese; ADR-0003 (ponto 5); ADR-0004

## Contexto

A decisão D1 («onde ficam as sessões de estacionamento») está «EM ABERTO». A tese contradiz-se:

- o **Cap. 3** (UC-02, RF-02, secção 3.5) trata a sessão e o pagamento como dados transacionais, com integridade referencial (lugar, veículo, utilizador, tarifário) e atomicidade entre o fecho e o pagamento;
- o **Cap. 4** (secções 4.4.2 e 4.5) coloca as sessões numa base de dados NoSQL.

> **Contradição a corrigir na tese.** O mesmo dado não pode estar, ao mesmo tempo, na subcamada relacional por exigir ACID (Cap. 3) e na subcamada documental (Cap. 4). A especificação (§2) usa o Cap. 3 como valor por omissão e pede que a contradição seja assinalada. `TODO(Diogo):` decidir com o orientador e alinhar o texto dos Caps. 3 e 4.

## Decisão

As sessões e os pagamentos ficam no **PostgreSQL**, atrás de `IParkingSessionRepository` (valor por omissão de D1). Razões, todas verificadas no protótipo:

1. **Integridade referencial.** A sessão referencia lugar, veículo, utilizador e tarifário; o pagamento referencia a sessão. As chaves estrangeiras (`ON DELETE RESTRICT`, ADR-0003) garantem-no na base de dados.
2. **Invariante «um lugar, no máximo uma sessão aberta».** É garantido pelo índice único parcial `ux_parking_sessions_spot_id_open`, mesmo com pedidos concorrentes (`SessionEndpointsTests.Second_open_session_on_the_same_spot_is_a_conflict`).
3. **Atomicidade do pagamento.** Pagar grava o pagamento, muda o estado da sessão para `Paid` e escreve `PaymentRegistered` na outbox numa só transação (ADR-0004).
4. **Concorrência otimista** com o token `xmin`: dois fechos concorrentes dão um 200 e um 409 (`SessionEndpointsTests.Two_concurrent_closes_of_the_same_session_one_succeeds_and_the_other_gets_409`).
5. **Volume compatível.** O volume de sessões é ordens de grandeza inferior ao dos eventos de ocupação (D8: cerca de 1,4 milhões de eventos por dia), que são os dados que justificam o MongoDB (UC-01, RNF-04).

## Alternativas consideradas

- **Sessões no MongoDB (como no Cap. 4.4.2/4.5).**
  - Vantagens: esquema flexível; proximidade com os eventos de ocupação.
  - Custos, que ficariam na tese como argumento:
    - sem chaves estrangeiras: a integridade com o lugar, o veículo e o tarifário passa para a aplicação;
    - o invariante «uma sessão aberta por lugar» precisa de um índice único parcial no MongoDB (`partialFilterExpression`), que existe, mas a atomicidade sessão + pagamento exige transações multi-documento (só em *replica set*);
    - a outbox teria de passar também para o MongoDB (ADR-0004), porque a transação tem de ser local ao motor das sessões;
    - o contrato atual do repositório pressupõe o *change tracking* do EF Core e não tem gravação explícita (ADR-0003, ponto 5): é preciso acrescentar uma operação `UpdateAsync` e uma unidade de trabalho do MongoDB.
- **Sessões abertas no Redis e fechadas no PostgreSQL.**
  - Rejeitada: duplica o estado e cria uma terceira fonte de verdade para o mesmo agregado.

## Consequências

- O ponto de extensão existe (`IParkingSessionRepository`, `IUnitOfWork`, `IHasDomainEvents`), mas **não é suficiente** para trocar D1 sem alterações: ver as alternativas e o ADR-0003, ponto 5.
- A comparação com a variante monolítica (Cap. 6.5, §7) não é afetada: nas duas variantes, as sessões estão no PostgreSQL. A diferença entre as variantes está nos eventos de ocupação e na disponibilidade.
- Se o orientador escolher NoSQL, a mudança toca: o repositório, a unidade de trabalho, a outbox (ADR-0004) e os testes de atomicidade e concorrência desta fase.
