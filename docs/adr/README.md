# Registos de decisões de arquitetura (ADR)

Índice dos registos de decisões de arquitetura (*Architecture Decision Records*, ADR) do protótipo. Fundamentam as escolhas descritas no Cap. 5 da tese.

- Cada ADR fica em `docs/adr/NNNN-titulo.md` e contém: contexto, decisão, alternativas consideradas, consequências, e o requisito (RF/RNF) e a secção da tese a que responde.

| N.º | Título | Decisão (D1–D10) | RF/RNF | Secção da tese | Estado | Data |
|---|---|---|---|---|---|---|
| 0001 | [Infraestrutura local de desenvolvimento](0001-infraestrutura-local.md) | — | RF-04, RNF-03 | 5.9.1 | Proposto | 2026-11-24 |
| 0002 | [Clean Architecture e estrutura da solução](0002-clean-architecture-e-estrutura.md) | — (E1; pontos de extensão D2, D10) | transversal (OE6) | 5.9.1 | Proposto | 2026-11-24 |
| 0003 | [Repository por raiz de agregado sobre o EF Core](0003-repository-por-raiz-de-agregado.md) | D10 | RF-02 | 5.4.2, 5.7 | Aceite | 2026-11-24 |
| 0004 | [Papel da aplicação e auditoria só por acréscimo](0004-papel-da-aplicacao-e-auditoria.md) | D2 (valor por omissão) | RNF-06, RF-06 | 5.4.1, 5.6.2, 5.9.2 | Aceite | 2026-11-25 |
| 0005 | [Versões de tarifário com vigência](0005-versoes-de-tarifario-com-vigencia.md) | D1, D2 (valores por omissão) | RF-06; prepara UC-02 | 5.1, 5.4.1, 5.9.2 | Aceite | 2026-11-25 |
| 0006 | [Auditoria por entidade alterada](0006-auditoria-por-entidade-alterada.md) | D2 (valor por omissão) | RF-02, RNF-06 | 5.4.1, 5.4.2 | Aceite | 2026-11-25 |
| 0007 | [Reservas: cumprimento na transação da abertura e expiração por *worker*](0007-reservas-cumprimento-e-expiracao.md) | D1, D2 (valores por omissão); D10 | RF-02, RNF-06 | 5.4.1, 5.4.2 | Aceite | 2026-11-25 |
