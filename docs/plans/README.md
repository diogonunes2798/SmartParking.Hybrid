# Planos de implementação

Índice dos planos produzidos pelo comando `/plan` do *kit* (dotnet-claude-kit) a partir de uma especificação aprovada em [`docs/specs/`](../specs/README.md).

- Cada plano refere a especificação de origem e faz corresponder cada critério de aceitação (`AC-n`) aos passos de implementação e aos testes.

| Plano | Especificação | Estado | Data |
|---|---|---|---|
| [000-fundacao](000-fundacao.md) | [000-visao-geral](../specs/000-visao-geral.md) | Implementado | 2026-11-24 |
| [001a-dominio-esquema-parques](001a-dominio-esquema-parques.md) | [001-configuracao](../specs/001-configuracao.md) (F2a) | Implementado e integrado na `main` (PR #2) | 2026-11-25 |
| [001b-zonas-lugares-sensores-tarifarios](001b-zonas-lugares-sensores-tarifarios.md) | [001-configuracao](../specs/001-configuracao.md) (F2b) | Implementado e integrado na `main` (PR #2) | 2026-11-25 |
| [002a-utilizadores-sessoes-pagamento](002a-utilizadores-sessoes-pagamento.md) | [002-sessoes-pagamentos](../specs/002-sessoes-pagamentos.md) (F3a) | Implementado no ramo `feat/002-sessoes-pagamentos` (por integrar; PR no fim da F3b) | 2026-11-25 |
| [002b-reservas-worker](002b-reservas-worker.md) | [002-sessoes-pagamentos](../specs/002-sessoes-pagamentos.md) (F3b) | Implementado no ramo `feat/002-sessoes-pagamentos` (por integrar; PR da F3a e da F3b por abrir) | 2026-11-25 |
