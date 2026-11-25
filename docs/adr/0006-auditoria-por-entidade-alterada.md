# ADR 0006 — Auditoria por entidade alterada

- **Estado:** Aceite (especificação 002 aprovada a 2026-11-25; plano 002a aprovado a 2026-11-25, proposta 5)
- **Data:** 2026-11-25
- **Requisitos:** RF-02 (cada transição é atómica: a alteração, a auditoria e a *outbox* na mesma transação); RNF-06 (auditoria só por acréscimo).
- **Decisão (D1–D10):** D2 (*outbox* em PostgreSQL), no valor por omissão. Nenhuma decisão D1–D10 é alterada.
- **Secções da tese:** 5.4.1 e 5.4.2.
- **Especificação:** [002-sessoes-pagamentos](../specs/002-sessoes-pagamentos.md), secção «Integrations» e AC-3, AC-4, AC-12, AC-13 e AC-21; plano [002a](../plans/002a-utilizadores-sessoes-pagamento.md). Estende o [ADR 0004](0004-papel-da-aplicacao-e-auditoria.md).

## Contexto

Na F2 (ADR 0004), a unidade de trabalho grava, por cada evento de domínio de uma raiz de agregado, uma `OutboxMessage` e uma `AuditEntry` da raiz que o levantou. Isto chegava para a configuração, em que cada operação altera uma só raiz (o fecho da versão de tarifário anterior é a exceção documentada no ADR 0005).

A especificação 002 pede duas coisas que esse mecanismo não dá:

- **um evento por transição, mas uma auditoria por entidade alterada**, todas com o `outboxMessageId` desse evento. Pagar uma sessão altera o `Payment` e a `ParkingSession` (duas auditorias, um `SessionPaid.v1`). Na F3b, abrir uma sessão com reserva altera a sessão e a reserva;
- **auditar entidades que não são raiz de agregado:** `Vehicle` e `PaymentMethod` (agregado do utilizador) e `Payment` (agregado da sessão), sem copiar o `providerToken` para a auditoria (AC-4).

## Decisão

- **O evento declara as entidades que a transição alterou.** `AggregateRoot.Raise(evento, params entidades alteradas)`. Sem entidades, a entidade alterada é a própria raiz, que é exatamente o comportamento da F2: nenhuma chamada da F2 mudou, e os 201 testes da F2 passaram sem alterações.
- **A unidade de trabalho grava uma `OutboxMessage` por evento e uma `AuditEntry` por entidade alterada**, todas com o `outboxMessageId` desse evento, na mesma chamada a `SaveChanges`, ou seja, na mesma transação. O `entityType`, a operação e os estados `before`/`after` vêm da entrada de cada entidade no *change tracker*. O identificador vem da chave primária do modelo do EF Core, porque as entidades que não são raiz não têm uma base comum. Uma entidade alterada que não está no *change tracker* é um erro de programação e lança uma exceção.
- **A raiz só é «tocada»** (passa a `Modified`, para o seu `xmin` ser verificado e mudar) quando está entre as entidades alteradas. Assim, registar um veículo não reescreve a linha do utilizador, e uma recusa de pagamento (`PaymentFailed.v1`, só o pagamento alterado) não muda o `xmin` da sessão.
- **Os filhos do agregado do utilizador são alterados através da raiz.** É o `User` que levanta `VehicleRegistered`, `VehicleRemoved`, `PaymentMethodAdded` e `PaymentMethodRemoved`, com o filho como única entidade alterada (uma auditoria, AC-3). As fábricas de `Vehicle` e `PaymentMethod` passaram a ser internas ao Domain.
- **Propriedades excluídas da auditoria:** `AuditExclusions`, no Infrastructure, lista as propriedades que nunca são copiadas para `before`/`after`. Por agora contém só `PaymentMethod.ProviderToken`. O Domain não ganha atributos de auditoria.
- **Mantém-se a exceção do ADR 0005:** a versão de tarifário fechada por uma versão nova continua sem auditoria própria, porque o `TariffVersionCreated` só declara a versão nova.

| Transição | Evento | Auditorias |
|---|---|---|
| Registar utilizador | `UserRegistered.v1` | utilizador |
| Registar ou remover veículo | `VehicleRegistered.v1`, `VehicleRemoved.v1` | veículo |
| Adicionar ou remover meio de pagamento | `PaymentMethodAdded.v1`, `PaymentMethodRemoved.v1` | meio de pagamento |
| Abrir, fechar | `SessionOpened.v1`, `SessionClosed.v1` | sessão |
| Pagar (aprovado ou 0,00 €) | `SessionPaid.v1` | pagamento e sessão |
| Recusa do gateway | `PaymentFailed.v1` | pagamento |

## Alternativas consideradas

- **Auditar todas as entidades alteradas da transação e ligá-las ao único evento.** Dispensa a declaração no Domain, mas falha quando uma gravação tem vários eventos (o lote de lugares da F2 grava 2 000 eventos numa transação) e auditaria a versão de tarifário fechada, contra o AC-7 da especificação 001.
- **Um evento por entidade alterada.** Seguiria o mecanismo da F2 sem alterações, mas contradiz a especificação 002 («exatamente um evento na *outbox* por transição», AC-21) e obrigaria os consumidores da F5 a juntar dois eventos para reconstruir um pagamento.
- **Fazer de `Vehicle`, `PaymentMethod` e `Payment` raízes de agregado.** Daria eventos próprios a cada um, mas parte os agregados da Tabela 15 da tese (utilizador com veículos e meios; sessão com pagamentos) e acrescentaria repositórios sem regra de negócio própria (D10).
- **Atributos de auditoria no Domain (`[NotAudited]`).** Exprime a exclusão junto da propriedade, mas põe uma preocupação da persistência no Domain, que não tem dependências.

## Consequências

- Cada transição diz, no Domain, que entidades alterou; os testes unitários verificam a lista (`AggregateRootTests`, `UserTests`, `ParkingSessionTests`).
- O AC-12 prova a mesma transação: a sessão, o pagamento, as duas auditorias e a mensagem têm o mesmo `xmin`. O AC-21 conta as mensagens com o `xmin` da linha alterada.
- Uma propriedade sensível nova tem de ser acrescentada a `AuditExclusions`; o AC-4 só cobre o `providerToken`.
- A F3b (reservas) usa o mesmo mecanismo: `SessionOpened.v1` com a sessão e a reserva como entidades alteradas.

## Referências

- Especificação 002, secção «Integrations» (rondas 8 e 9 do `/spec`).
- ADR 0004 (auditoria e *outbox* na unidade de trabalho) e ADR 0005 (versões de tarifário).
