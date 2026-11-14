# Especificação do protótipo — SmartParking.Hybrid

Versão 0.1 (13-11-2026). Baseada nos Capítulos 3 e 4 da tese (versão de revisão de 24-09-2026, com as notas do orientador de 02-10-2026).

## 1. Objetivo

Implementar e avaliar uma arquitetura híbrida de persistência para a gestão do estacionamento urbano. Os dados críticos e transacionais ficam numa base de dados relacional. Os eventos de sensores ficam numa base de dados documental. O estado de disponibilidade em tempo real fica numa estrutura chave-valor em memória. As escritas e as leituras são separadas com CQRS.

O protótipo tem de permitir medir três coisas, que alimentam os Capítulos 5 e 6:

1. o desempenho em cenários de elevado volume (secção 6.2);
2. a capacidade de evolução do esquema (secção 6.3);
3. a comparação com uma variante monolítica, só relacional (secção 6.5).

## 2. Decisões em aberto

Estas decisões dependem da reunião com o orientador. Implementa o valor por omissão atrás de uma interface, para que a troca não obrigue a reescrever o código.

| ID | Decisão | Valor por omissão | Notas |
|---|---|---|---|
| D1 | Onde ficam as sessões de estacionamento | **Relacional** (como no Cap. 3: UC-02, RF-02, 3.5) | O Cap. 4 (4.4.2 e 4.5) coloca-as em NoSQL. É uma contradição a corrigir na tese. |
| D2 | Broker de mensagens para ingestão e sincronização | **Outbox em PostgreSQL + Redis Streams** | Alternativa: Apache Kafka (referido no Cap. 2, ausente no Cap. 4). Interface `IEventBus`. |
| D3 | Event Sourcing nos eventos de ocupação | **Log de eventos imutável em MongoDB** (append-only), sem reconstrução de agregados | Corresponde a «Event Store + Read Store» do Cap. 2 (2.4.4) numa versão mínima. |
| D4 | Camada analítica (UC-04, RF-05) | **Agregados periódicos em PostgreSQL** (tabelas de resumo atualizadas por um worker) | Alternativa: data lake ou lakehouse. Só se o orientador quiser manter a 2.4.5. |
| D5 | Azure Cosmos DB | **Fora do protótipo** | Mencionar apenas como trabalho futuro, salvo decisão em contrário. |
| D6 | Ferramenta de carga | **k6** | Alternativa: NBomber (em .NET). |
| D7 | Latência alvo do UC-01 (ponta a ponta) | **< 1 s no p95** (RNF-01) | O UC-01 diz < 2 s. Tem de ficar um valor único na tese. |
| D8 | Cenário de referência | **5 000 lugares, 1 evento por lugar a cada 5 min em média, picos de 10×** | Dá cerca de 1,4 milhões de eventos por dia e 170 eventos/s em pico. Confirmar com o Diogo e usar o valor final no Cap. 3. |

## 3. Domínio (Cap. 3.5)

Entidades:

- **ParkingFacility** (Parque de Estacionamento): id, nome, localização (GeoJSON), capacidade, tipo (coberto, descoberto, multipiso), operador, zona.
- **ParkingSpot** (Lugar): id, facilityId, código, tipo de viatura permitido, estado atual (só no read model).
- **Sensor**: id, spotId, modelo, versão do firmware, estado operacional. Os dados de configuração são relacionais (RF-06). As leituras ficam no MongoDB. Esta separação resolve a contradição «Sensor» assinalada pelo orientador.
- **OccupancyEvent** (Evento de Ocupação, imutável): eventId, sensorId, spotId, facilityId, tipo (OCCUPIED ou FREED), timestamp, `payload` extensível (campos adicionais por geração de sensor, UC-05).
- **ParkingSession** (Sessão): id, spotId, vehicleId, userId, início, fim, tarifa aplicada, custo, estado (OPEN, CLOSED, PAID).
- **Tariff** (Tarifário): id, zona, período de validade, regras de cálculo.
- **User** (Utilizador) e **Vehicle** (Veículo): dados pessoais mínimos (RNF-06, RGPD) e matrícula.
- **Payment** (Pagamento): id, sessionId, valor, método, estado, timestamp.

Mapeamento para a persistência:

| Dados | Motor | Razão (requisito) |
|---|---|---|
| Facility, Spot, Sensor (configuração), Tariff, User, Vehicle, Session, Payment, auditoria | PostgreSQL | ACID e integridade referencial (UC-02, RF-02, RF-06) |
| OccupancyEvent, leituras ambientais | MongoDB | Volume, esquema flexível (UC-01, UC-05, RF-01, RNF-04) |
| Estado atual de cada lugar e contagens por parque e zona | Redis | Leitura de baixa latência (UC-03, RF-03, RNF-01) |
| Agregados históricos | PostgreSQL (tabelas de resumo) | Isolamento analítico (UC-04, RF-05). Ver D4. |

## 4. Fluxos principais

### UC-01 — Ingestão de um evento de ocupação

`POST /api/ingest/occupancy` (aceita lotes)
1. Validar o esquema mínimo (RF-01) e rejeitar duplicados (idempotência por `eventId`).
2. Publicar no bus (D2) e responder `202 Accepted`.
3. Um consumidor escreve o evento no MongoDB (append-only).
4. Um consumidor atualiza o Redis (estado do lugar e contadores do parque) e publica em pub/sub.
5. Medir a latência entre o `timestamp` do sensor e a atualização no Redis (D7).

### UC-02 — Sessão e pagamento

`POST /api/sessions`, `POST /api/sessions/{id}/close` e `POST /api/sessions/{id}/pay`
- Usar uma transação EF Core e concorrência otimista (token de concorrência).
- Escrever, na mesma transação, um evento de domínio na tabela **outbox** (por exemplo `SessionClosed` ou `PaymentRegistered`).
- Um worker lê a outbox e publica os eventos, para que o read model e os agregados sejam atualizados. A consistência eventual é medida (janela de sincronização, Cap. 4.5).

### UC-03 — Consulta de disponibilidade

`GET /api/availability?lat=&lon=&radius=&type=`
- Ler apenas do Redis (GEO e hashes).
- Alvo: p95 < 200 ms (RNF-01).

### UC-04 — Relatório analítico

`GET /api/reports/occupancy?zone=&from=&to=&granularity=hour|day|week|month`
- Ler das tabelas de resumo, nunca das tabelas operacionais.

### UC-05 — Evolução do esquema dos sensores

- O simulador passa a emitir uma nova geração de sensores, com `temperature`, `airQuality` e `noise` dentro de `payload`.
- O sistema tem de continuar a funcionar sem migrações síncronas nem paragem (RNF-04).
- Medir o tempo de indisponibilidade (esperado: 0) e o impacto na latência durante a transição.
- Para comparação, a variante monolítica (secção 7) faz a mesma alteração com uma migração de tabela.

### Configuração (RF-06)

CRUD de parques, lugares, sensores e tarifários. As alterações relevantes emitem eventos para o read model.

## 5. Padrões (Cap. 4.1 e 4.6)

- **CQRS**: comandos e consultas separados na camada Application, com handlers simples e sem bibliotecas de mediação com licença comercial.
- **Repository**: interfaces na camada Application e implementações por motor de base de dados.
- **Outbox transacional**: garante que nenhuma alteração relacional fica por propagar, sem recorrer a transações distribuídas (two-phase commit), como justificado no Cap. 4.5.
- **Idempotência** nos consumidores, porque a entrega é «pelo menos uma vez».
- **Particionamento**: os eventos em MongoDB usam um índice composto `(facilityId, timestamp)`. Se se usar *sharding*, a chave segue a secção 4.5 (zona mais janela temporal).

## 6. Requisitos não funcionais medidos

| RNF | Métrica | Alvo inicial | Como medir |
|---|---|---|---|
| RNF-01 | Latência p95 de consulta (UC-03) | < 200 ms | k6, cenário `availability-read` |
| RNF-01 | Latência ponta a ponta da ingestão (UC-01) | < 1 s p95 (D7) | timestamp do sensor → atualização no Redis (OpenTelemetry) |
| RNF-01 | Latência da escrita transacional (UC-02) | < 2 s p95 | k6, cenário `session-payment` |
| RNF-02 | Throughput de ingestão ao escalar consumidores (1, 2, 4) | crescimento próximo do linear | `ingest-scale` |
| RNF-03 | Comportamento com falha de um nó (réplica) | sem perda de eventos | `failover` (parar um contentor durante a carga) |
| RNF-04 | Indisponibilidade durante a alteração de esquema | 0 s | `schema-evolution` |
| RNF-05 | Armazenamento ocupado e CPU por 10⁶ eventos; efeito da retenção (TTL e compressão) | redução mensurável | `retention` |
| RNF-06 | Cifra em trânsito, RBAC e auditoria | verificação funcional | testes de integração |

Todos os cenários escrevem para `results/<cenario>/<AAAA-MM-DD_HHMM>.csv` com estas colunas: cenário, variante (hybrid ou monolithic), métrica, valor, unidade, n.º de execução e configuração. Cada cenário corre pelo menos 5 vezes.

## 7. Variante monolítica (baseline)

Para a secção 6.5, a mesma API corre com uma configuração `Persistence:Mode=Monolithic`, em que tudo fica em PostgreSQL:

- os eventos ficam numa tabela particionada por tempo;
- a disponibilidade é obtida por consulta, sem Redis;
- o UC-05 é feito com uma migração de esquema.

A comparação usa os mesmos cenários de carga e o mesmo hardware.

## 8. Simulador de sensores (tools/SensorSimulator)

- Parâmetros: número de parques e lugares, taxa média, fator de pico, duração, proporção de cada geração de sensores e semente aleatória (para ser reprodutível).
- Gera ocupações coerentes (um lugar não fica ocupado duas vezes seguidas) e envia por HTTP em lotes.

## 9. Plano de implementação (para o Claude Code)

1. **Fundação**: solução, projetos, docker compose, health checks, OpenTelemetry e o primeiro ADR.
2. **Domínio e relacional**: entidades, EF Core, migrações, RF-06 e UC-02 com outbox. Testes com Testcontainers.
3. **Ingestão e documentos**: endpoint UC-01, bus (D2), consumidor MongoDB e idempotência.
4. **Read model**: consumidor Redis e UC-03 com GEO.
5. **Sincronização**: worker da outbox e medição da janela de consistência.
6. **Analítica**: tabelas de resumo e UC-04.
7. **Simulador e evolução de esquema**: UC-05.
8. **Variante monolítica**.
9. **Benchmarks**: cenários da secção 6, scripts e CSV.
10. **Retenção e sustentabilidade**: TTL, compressão e arquivo (RNF-05), com as respetivas medições.

No fim de cada fase:
- atualizar `docs/RASTREABILIDADE.md`;
- registar no `docs/DIARIO.md` o que foi feito, os problemas encontrados e as decisões tomadas. Este diário é a matéria-prima do Capítulo 5.

## 10. Fora de âmbito

Interface gráfica (basta Swagger/OpenAPI), pagamentos reais, autenticação federada, implantação em cloud e Cosmos DB (D5).
