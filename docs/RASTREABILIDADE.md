# Rastreabilidade dos requisitos

Liga cada requisito funcional (RF, Tabela 13 da tese) e não funcional (RNF, Tabela 14) ao caso de uso (UC, Tabela 12), aos critérios de aceitação (`AC-n`) da especificação, ao código e aos testes que o demonstram. Deve bater certo com a Tabela 17 da tese.

## Referência: casos de uso e requisitos da tese

Resumo das Tabelas 12, 13 e 14, para consulta durante o `/spec` e o `/plan`. Os alvos da Tabela 14 são requisitos a verificar, não resultados: os valores medidos ficam só em `results/`.

### Casos de uso (Tabela 12)

| UC | Nome | Em resumo |
|---|---|---|
| UC-01 | Ingestão de eventos de ocupação | O sensor envia eventos (em lote), que são validados, deduplicados por `eventId`, gravados no MongoDB (só por acréscimo) e refletidos no Redis. Latência ponta a ponta < 1 s no p95. |
| UC-02 | Sessão de estacionamento e pagamento | Abrir, fechar e pagar a sessão no PostgreSQL, numa transação ACID com concorrência otimista e evento na *outbox*. |
| UC-03 | Consulta de disponibilidade | Lugares livres por localização, raio e tipo de viatura, lidos só do Redis (GEO e *hashes*). Latência < 200 ms no p95. |
| UC-04 | Relatório analítico de ocupação | Por zona e período, com granularidade de hora, dia, semana ou mês. Lê das tabelas de resumo, nunca das operacionais. |
| UC-05 | Evolução do esquema dos sensores | Uma nova geração de sensores traz `temperature`, `airQuality` e `noise` no `payload`, sem migração síncrona e sem paragem. |

### Requisitos funcionais (Tabela 13)

| RF | Requisito | Ligado a | Prioridade (D9) |
|---|---|---|---|
| RF-01 | Receber e validar eventos de ocupação: esquema mínimo, idempotência e `payload` extensível | UC-01, UC-05 | Alta |
| RF-02 | Gerir sessões, reservas e pagamentos com integridade transacional | UC-02 | Alta |
| RF-03 | Manter e disponibilizar o estado de ocupação em tempo real | UC-03 | Alta |
| RF-04 | Pesquisa geoespacial de lugares disponíveis | UC-03 | Média |
| RF-05 | Produzir agregados históricos e relatórios analíticos | UC-04 | Média |
| RF-06 | Configuração: CRUD de parques, lugares, sensores e tarifários | UC-02, UC-05 | Alta |

### Requisitos não funcionais (Tabela 14)

| RNF | Requisito | Alvo / verificação | Prioridade (D9) |
|---|---|---|---|
| RNF-01 | Desempenho (latência) | UC-03 < 200 ms p95; UC-01 < 1 s p95; UC-02 < 2 s p95 | Alta |
| RNF-02 | Escalabilidade | Débito de ingestão com 1, 2 e 4 consumidores a crescer de forma próxima da linear | Alta |
| RNF-03 | Disponibilidade / tolerância a falhas | Falha de uma réplica sem perda de eventos nem de transações confirmadas | Média |
| RNF-04 | Evolução do esquema | 0 s de indisponibilidade durante a alteração | Alta |
| RNF-05 | Sustentabilidade / ciclo de vida dos dados | Armazenamento e CPU por 10⁶ eventos; redução mensurável com TTL, compressão e arquivo | Média |
| RNF-06 | Segurança e proteção de dados (RGPD) | Cifragem em trânsito e em repouso, controlo de acesso baseado em papéis (RBAC) por ator e auditoria só por acréscimo, verificados por testes de integração | Média |

## Matriz de rastreabilidade

Estado: «por preencher» até existir uma especificação aprovada que cubra o requisito. A coluna «UC» segue a coluna «Ligado a» da Tabela 13 e, nos RNF, os UC que a Tabela 14 nomeia; «—» quer dizer que a Tabela 14 não liga o requisito a um UC. A coluna «Especificação» indica a especificação reservada e a fase (ordem em [`docs/specs/README.md`](specs/README.md)).

| Requisito | UC | AC-n | Especificação | Código | Teste | Estado |
|---|---|---|---|---|---|---|
| RF-01 | UC-01, UC-05 | por preencher | 003-ingestao (F4); 007-simulador-esquema (F8, `payload` extensível) | por preencher | por preencher | por preencher |
| RF-02 | UC-02 | por preencher | 002-sessoes-pagamentos (F3) | por preencher | por preencher | por preencher |
| RF-03 | UC-03 | por preencher | 004-disponibilidade (F5) | por preencher | por preencher | por preencher |
| RF-04 | UC-03 | por preencher | 004-disponibilidade (F5) | por preencher | por preencher | por preencher |
| RF-05 | UC-04 | por preencher | 006-analitica (F7) | por preencher | por preencher | por preencher |
| RF-06 | UC-02, UC-05 | por preencher | 001-configuracao (F2) | por preencher | por preencher | por preencher |
| RNF-01 | UC-01, UC-02, UC-03 | por preencher | 005-sincronizacao (F6, UC-01); 011-medicoes (F12, os três UC) | por preencher | por preencher | por preencher |
| RNF-02 | — | por preencher | 011-medicoes (F12) | por preencher | por preencher | por preencher |
| RNF-03 | — | por preencher | 011-medicoes (F12) | por preencher | por preencher | por preencher |
| RNF-04 | UC-05 | por preencher | 007-simulador-esquema (F8) | por preencher | por preencher | por preencher |
| RNF-05 | — | por preencher | 009-ciclo-de-vida (F10) | por preencher | por preencher | por preencher |
| RNF-06 | — | por preencher | 010-seguranca (F11) | por preencher | por preencher | por preencher |

### Critérios transversais (sem RF/RNF próprio)

A especificação 000 (F1, aprovada a 2026-11-24) não responde a nenhum RF ou RNF da tese: prepara a fundação comum (OE6, secção 5.9.1). Os seus critérios ficam aqui, fora da matriz, para não inflacionar a Tabela 17.

| Especificação | AC-n | Tema | Código | Teste | Estado |
|---|---|---|---|---|---|
| 000-visao-geral (F1) | AC-1 a AC-3 | Regras de arquitetura (camadas, subcamadas E1, D10) | por preencher | por preencher | especificado |
| 000-visao-geral (F1) | AC-4 a AC-9 | *Health checks* e `Persistence:Mode` | por preencher | por preencher | especificado |
| 000-visao-geral (F1) | AC-10 a AC-13 | ProblemDetails, Result, validação, OpenAPI | por preencher | por preencher | especificado |
| 000-visao-geral (F1) | AC-14 | Imagens dos Testcontainers iguais às do `compose.yaml` | por preencher | por preencher | especificado |
| 000-visao-geral (F1) | AC-15, AC-16 | Documentais: ADR 0003 e compilação limpa | por preencher | `/verify` | especificado |
