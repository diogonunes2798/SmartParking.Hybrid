# Plano 001b — Zonas, lugares, sensores e tarifários (F2b)

- **Especificação:** [001-configuracao](../specs/001-configuracao.md) (aprovada a 2026-11-24), secção «Implementação em duas partes»
- **Estado:** Aprovado pelo Diogo (2026-11-25), tal como está, incluindo as propostas dos pontos 1 a 8. Implementado no ramo `feat/001-configuracao` (2026-11-25): 201 testes a passar, verificação, testes de mutação e revisão independente feitos (ver [diário](../DIARIO.md), F2b). Desvio: os testes ficaram agrupados por recurso e não por operação; os nomes dos métodos seguem o mapa abaixo.
- **Requisitos:** RF-06 (zonas, lugares, sensores e tarifários); RNF-06 (auditoria só por acréscimo, já garantida na F2a); preparação de UC-02 (tarifário vigente) e UC-05 (registo dos sensores)
- **Ramo:** `feat/001-configuracao`, a continuar a partir de `3cd29ec`. Um PR para a `main` no fim da F2b, com F2a e F2b
- **Critérios desta parte:** AC-7, AC-8, AC-9, AC-11, AC-13, AC-14, AC-15, AC-16 e AC-23, mais os casos de tarifário do AC-17 e os de lugares do AC-18

## Contexto

A F2a deixou pronto o esquema completo: migração `Initial`, FK compostas que mantêm zonas, lugares e sensores dentro do mesmo parque, o `EXCLUDE` das versões de tarifário, o índice único parcial `sensors(spot_id)`, a auditoria e a *outbox* na unidade de trabalho, o `ConcurrencyCheck` (428/412) e a tradução das violações (`23505`, `23503` e `23P01` dão 409; `23514` dá 400). No Domain, `Zone`, `ParkingSpot`, `Sensor` e `Tariff` já são `AggregateRoot` com `Version` (`xmin`), mas só têm estrutura e invariantes de construção.

A F2b acrescenta o comportamento e os eventos destas quatro raízes, um repositório por raiz e uma porta de leitura por família de consultas (D10, ADR 0003), os *endpoints* da tabela «API Contract» que faltam, o ADR das versões de tarifário e a conclusão do `configuration.http`.

**Sem migrações novas.** As restrições de que a F2b precisa já estão na `Initial` (confirmado nas configurações de `Zone`, `ParkingSpot`, `Sensor` e `Tariff`). Se a implementação mostrar que falta alguma, paramos, a especificação volta a «In Review» e pergunta-se ao Diogo antes de continuar.

## Pontos a confirmar pelo Diogo antes de implementar

Estas são leituras da especificação aprovada. Para cada uma há uma proposta; nenhuma muda o esquema nem as decisões D1–D10. **Todas as propostas foram aprovadas pelo Diogo a 2026-11-25.**

1. **Fecho da versão anterior no AC-7.** A secção «Integrations» diz «um evento e uma `AuditEntry` por entidade alterada», mas o AC-7 pede exatamente uma `AuditEntry` e um `TariffVersionCreated.v1` quando se cria uma versão nova, embora a versão anterior também mude (`validTo`). **Proposta:** o AC-7 prevalece, porque é o critério mais específico. O fecho da versão anterior faz parte da criação: o `TariffVersionCreated.v1` leva `previousVersionId` e `previousValidTo`, e a `AuditEntry` da versão nova regista-os no `after`. Pela mesma razão, o `TariffVersionDeleted.v1` leva `reopenedVersionId`. A versão anterior continua a ter o `xmin` verificado na mesma transação. Fica explicado no ADR 0005.
2. **Criação concorrente de versões.** A especificação diz que a segunda criação simultânea dá 409. Como a criação fecha a versão anterior, a corrida é apanhada pelo `xmin` dessa versão e a unidade de trabalho devolve 412. Um `POST` não leva `If-Match`, por isso 412 não faz sentido para o cliente. **Proposta:** o *handler* da criação converte esse 412 em 409 (`Tariff.ConcurrentChange`). Quando ainda não há versão anterior, a corrida é apanhada pelo `EXCLUDE` (`23P01`), que já dá 409.
3. **Zona, lugar ou parque inexistentes.**
   - **Zona ou lugar de outro parque:** a especificação pede 400, mas a FK composta daria 409 (`23503`). **Proposta:** o *handler* verifica antes de gravar e devolve 400 (`Spot.ZoneNotInFacility`, `Sensor.SpotNotInFacility`, `Sensor.ZoneNotInFacility`). Uma zona ou um lugar que não existe em lado nenhum também dá 400, com a mesma mensagem, porque, para o cliente, é o mesmo erro.
   - **Parque inexistente nas rotas `/facilities/{id}/…` e no `facilityId` do `POST /sensors`:** dá 404, que é o erro comum da especificação. A verificação é prévia, com `ExistsAsync`. Se o parque for apagado entre a verificação e a gravação, a FK continua a dar 409.
4. **`hardwareId` repetido dentro do mesmo lote.** A especificação só fala dos códigos repetidos no pedido (400). **Proposta:** um `hardwareId` repetido no pedido também dá 400 antes de tocar na base de dados, pela mesma razão. Um `hardwareId` que já existe na base dá 409 e não cria nada.
5. **`validFrom` no passado (AC-9, 400).** **Proposta:** verifica-se no *handler*, com o `TimeProvider`, e devolve um `Error.Validation` com a chave `validFrom`. Não fica no validador da Api, porque é uma regra de negócio que depende do instante atual e tem de ser repetida no Domain. Os casos de formato do AC-17 (preço negativo, `billingIncrementMinutes = 0`) ficam no validador da Api, como nos parques.
6. **Operações sem efeito.**
   - `DELETE /sensors/{id}/spot` num sensor que já está sem lugar: 200, sem auditoria nem evento. É o simétrico do caso «reatribuir para o mesmo lugar» da especificação.
   - `PUT` de zona, lugar ou sensor sem alterações: 200, o mesmo ETag e nada gravado, como nos parques.
7. **Resposta do lote.** 201 com `{ items: [{ spotId, code, sensorId? }] }`, pela ordem do pedido. Sem `Location`, porque o lote não é um recurso endereçável.
8. **Versões vigentes nos testes.** A Api só aceita `validFrom ≥ agora`, por isso uma versão «em vigor» não se cria pela API sem controlar o relógio. **Proposta:** os testes do AC-7, AC-8 e AC-9 inserem a versão vigente por SQL, como dono, como o AC-5 fez com zonas e lugares na F2a. Assim não é preciso o pacote `Microsoft.Extensions.TimeProvider.Testing`, que seria uma dependência nova, fora do âmbito da especificação. A alternativa, sem pacote novo, é um `TimeProvider` de teste escrito à mão, mas obriga a um *host* próprio.

## (a) UC, RF e RNF da fase

- **RF-06** (configuração): zonas, lugares (individualmente e em lote), sensores e versões de tarifário. Com a F2a, cobre o RF-06 por inteiro.
- **UC-02** (preparação): o tarifário vigente por parque (`GET …/tariffs/current`), com versões imutáveis depois de entrarem em vigor. As sessões da F3 vão referir a versão (`tariffId`).
- **UC-05** (preparação): o registo do sensor (`hardwareId` único, ligação ao lugar). A F4 valida `sensorId`/`spotId` sobre estas tabelas.
- **RNF-06:** a auditoria só por acréscimo já está garantida pela base de dados (AC-10). Na F2b, cada alteração nova passa pela mesma unidade de trabalho, e o AC-8 prova que a falha da *outbox* reverte o negócio e a auditoria.
- **Cenário de referência (Tabela 9):** o lote de 1 000 lugares prepara os 5 000 lugares em 5 pedidos. Não há alvo de desempenho nesta fase e não se escreve nenhum valor medido.

## (b) Ficheiros a criar ou alterar

Nenhum projeto novo, nenhum pacote novo e nenhuma migração.

**Domain** (`src/SmartParking.Domain/Facilities/`)
- `Zone.cs`: `Create` levanta `ZoneCreated`; `Rename(name)` devolve se mudou e levanta `ZoneUpdated`; `MarkForDeletion()` levanta `ZoneDeleted`.
- `ParkingSpot.cs`: `Create` levanta `SpotCreated`; `Update(zoneId, code, type, status)` devolve se mudou e levanta `SpotUpdated` com `previousZoneId`; `MarkForDeletion()`. O `facilityId` nunca muda; a regra «zona do mesmo parque» é verificada no *handler* e garantida pela FK composta.
- `Sensor.cs`: `Create` levanta `SensorRegistered`; `Update(model, status, zoneId)`; `AssignTo(spotId)` (sem efeito se for o mesmo lugar; recusa sensores ambientais) levanta `SensorReassigned` com `previousSpotId`; `Unassign()` levanta `SensorUnassigned`; `MarkForRemoval()` (recusa se ainda estiver num lugar) levanta `SensorRemoved`.
- `Tariff.cs`: as regras das versões, com `now` passado pelo *handler*:
  - `CreateVersion(id, facilityId, latest?, validFrom, …, now)`: exige `validFrom ≥ now` e `validFrom > latest.ValidFrom`; fecha a última versão (`latest.CloseAt(validFrom)`, sem evento próprio); levanta `TariffVersionCreated`;
  - `CanBeDeleted(isLatest, now)`: só uma versão futura (`ValidFrom > now`) e a última;
  - `MarkForDeletion(previous?)`: reabre a anterior (`ValidTo = null`) e levanta `TariffVersionDeleted`.
  As violações por código lançam `ArgumentException`, como na F2a. A entrada do cliente é verificada antes, no *handler*.
- `Events/`: `ZoneCreated`, `ZoneUpdated`, `ZoneDeleted`; `SpotCreated`, `SpotUpdated`, `SpotDeleted`; `SensorRegistered`, `SensorUpdated`, `SensorReassigned`, `SensorUnassigned`, `SensorRemoved`; `TariffVersionCreated`, `TariffVersionDeleted`. Todos `sealed record` com `[DomainEvent(nome, 1)]`. Todos levam `id` e `facilityId`, para as contagens por parque nos testes e para os projetores da F5. *Payload* com o estado novo:
  - zona: `id, facilityId, name`;
  - lugar: `id, facilityId, zoneId, code, type, status` (+ `previousZoneId` no `SpotUpdated`);
  - sensor: `id, facilityId, hardwareId, kind, model, status, spotId?, zoneId?` (+ `previousSpotId` no `SensorReassigned` e no `SensorUnassigned`);
  - tarifário: os campos da versão (+ `previousVersionId?`, `previousValidTo?` na criação; `reopenedVersionId?` no apagamento).

**Application** (`src/SmartParking.Application/`)
- `Abstractions/Persistence/` (escrita, D10, sem `IQueryable` nem `SaveChanges`):
  - `IParkingFacilityRepository`: mais `ExistsAsync(id, ct)` (ver `find_references` antes de mudar a interface);
  - `IZoneRepository`: `GetByIdAsync`, `GetIdsByFacilityAsync` (para validar as zonas do lote numa só consulta), `Add`, `Remove`;
  - `IParkingSpotRepository`: `GetByIdAsync`, `Add`, `AddRange`, `Remove`;
  - `ISensorRepository`: `GetByIdAsync`, `Add`, `AddRange`, `Remove`;
  - `ITariffRepository`: `GetByIdAsync`, `GetLatestAsync(facilityId)`, `GetPreviousAsync(tariff)`, `Add`, `Remove`.
- `Abstractions/Reading/` (leitura, sem repositório): `IZoneReader`, `ISpotReader` (filtros `zoneId?` e `status?`), `ISensorReader` (filtros `facilityId?` e `kind?`), `ITariffReader` (`GetCurrentAsync(facilityId, at)`). Uma lista dentro de um parque devolve `null` quando o parque não existe (404). As *views* são `ZoneView`, `SpotView`, `SensorView` e `TariffView`, todas com `Version`.
- `Features/Zones/`: `CreateZone`, `UpdateZone`, `DeleteZone`, `GetZone`, `ListZones`, `ZoneErrors`.
- `Features/Spots/`: `CreateSpot`, `CreateSpotBatch`, `UpdateSpot`, `DeleteSpot`, `GetSpot`, `ListSpots`, `SpotErrors`. No lote, o *handler* valida as zonas com uma só consulta, cria os lugares e os sensores de ocupação e grava tudo numa só chamada à unidade de trabalho (tudo ou nada).
- `Features/Sensors/`: `RegisterSensor`, `UpdateSensor`, `AssignSensorToSpot`, `UnassignSensor`, `RemoveSensor`, `GetSensor`, `ListSensors`, `SensorErrors`.
- `Features/Tariffs/`: `CreateTariffVersion`, `DeleteTariffVersion`, `GetTariffVersion`, `GetCurrentTariff`, `ListTariffVersions`, `TariffErrors`.
- O padrão é o dos parques: uma classe estática por operação, com `Command` ou `Query` e `Handler` aninhados, e o `ConcurrencyCheck` nos `PUT` e `DELETE`. Um recurso filho de outro parque (por exemplo, `GET /facilities/A/zones/{zona de B}`) dá 404.

**Infrastructure/Relational**
- `Repositories/`: `ZoneRepository`, `ParkingSpotRepository`, `SensorRepository` e `TariffRepository`, com consultas compiladas no `GetByIdAsync`, como o `ParkingFacilityRepository`.
- `Reading/`: `ZoneReader`, `SpotReader`, `SensorReader` e `TariffReader`, com `AsNoTracking` e projeções.
- `RelationalRegistration.cs`: regista as oito portas novas, iguais nos dois modos (o AC-22 descobre-as por reflexão).
- `SmartParkingDbContext`, `PostgresErrorTranslator` e configurações: sem alterações previstas. Se alguma for precisa, regista-se no diário.

**Api** (`src/SmartParking.Api/Features/`)
- `Zones/ZoneEndpoints.cs` (`/api/facilities/{facilityId}/zones`), `Spots/SpotEndpoints.cs` (`/api/facilities/{facilityId}/spots`, com `/batch`), `Sensors/SensorEndpoints.cs` (`/api/sensors`, `/{id}/spot`) e `Tariffs/TariffEndpoints.cs` (`/api/facilities/{facilityId}/tariffs`, com `/current`). Cada um é um `IEndpointGroup` com `AllowAnonymous` no grupo, `TypedResults`, ETag nas leituras e nas escritas, e `If-Match` (`EntityTagHeaders`) nos `PUT` e `DELETE`.
- DTO do pedido e da resposta e validadores internos por recurso. As regras repetidas entre o `POST` e o `PUT` ficam num validador partilhado, como o `FacilityDetailsValidator`. O validador do lote verifica: de 1 a 1 000 itens, códigos únicos (comparação ordinal, como o índice) e `hardwareId` únicos no pedido. O sensor ambiental com `spotId` é recusado no validador (400). Os enums são comparados com os nomes exatos, como o `status` dos parques.
- `tests/http/configuration.http`: um pedido por *endpoint* da tabela «API Contract», encadeados a partir do operador de referência (AC-23).

**Testes**
- `tests/SmartParking.Api.Tests/Fixtures/`: `ZoneApi`, `SpotApi`, `SensorApi` e `TariffApi` (pedidos com dados únicos, criação com ETag), e o `WithIfMatch` generalizado para receber a rota. Uma função de apoio ao `DbProbe` para inserir uma versão vigente por SQL, como dono (ponto 8).
- `tests/SmartParking.Api.Tests/Features/Zones/`, `Spots/`, `Sensors/` e `Tariffs/`: ver (e).
- `tests/SmartParking.UnitTests/Domain/`: regras das versões de tarifário, `ParkingSpot.Update` (com `previousZoneId`), `Sensor.AssignTo` e `Unassign`, e operações sem efeito.

**Documentação**
- `docs/adr/0005-versoes-de-tarifario-com-vigencia.md` e uma linha no índice.
- `docs/plans/README.md` (esta linha), `docs/specs/README.md` (estado da 001), `docs/RASTREABILIDADE.md` e `docs/DIARIO.md`.

## (c) Skills do kit e adaptações

| Skill | Uso | Adaptação |
|---|---|---|
| `ef-core` | Repositórios, leitores, gravação do lote | Exceção D10: um repositório por raiz. O lote usa `AddRange` e uma só chamada a `SaveChanges`, sem bibliotecas de *bulk insert* (seriam dependências novas). O EF Core ordena as inserções pelas FK compostas (lugar antes do sensor) |
| `clean-architecture` | Portas em `Application/Abstractions`; Domain sem dependências | As regras das versões ficam no Domain; o `now` vem do `TimeProvider` do *handler* |
| `minimal-api`, `openapi` | Um `IEndpointGroup` por recurso, rotas aninhadas em `/facilities/{facilityId}` | ETag e `If-Match` à mão sobre o `xmin`, como nos parques |
| `error-handling` | Result e ProblemDetails | Verificações prévias para os 400 e 404 da especificação; o 412 da criação concorrente de versões passa a 409 (ponto 2) |
| `modern-csharp` | *records*, `sealed`, *primary constructors* | Eventos `sealed record` com `[DomainEvent]` |
| `testing` | Testcontainers, AAA, `…_ACn` | Dados únicos por parque, sem bases isoladas, exceto no AC-8 (o *trigger* não pode afetar os outros testes) |
| `dependency-injection` | Registo das portas | Igual nos dois modos (AC-22) |

## (d) Decisões D1–D10 em causa

- **D10 (ADR 0003):** quatro repositórios novos (`IZoneRepository`, `IParkingSpotRepository`, `ISensorRepository`, `ITariffRepository`) e quatro portas de leitura. Nenhum repositório genérico e nenhum `IQueryable`. O AC-21 continua a verificar isto sem alterações.
- **D2:** cada alteração grava a sua mensagem na *outbox*, com `published_at = null` (o leitor é da F3/F4).
- **D1:** sessões em PostgreSQL; a FK `parking_sessions.tariff_id` é a razão pela qual uma versão com sessões nunca se apaga (ADR 0005).
- Nenhuma decisão é alterada.

## (e) Correspondência AC → teste (F2b)

| AC | Teste |
|---|---|
| AC-7 | `Api.Tests/Features/Tariffs/CreateTariffVersionTests.Create_WithCurrentVersion_ClosesPreviousWithOneAuditOneEventSameTransaction_AC7`: `validTo` anterior = `validFrom` novo; uma `AuditEntry` e um `TariffVersionCreated.v1` novos; o mesmo `xmin` nas linhas novas e na versão fechada |
| AC-8 | `…Tariffs/TariffAtomicityTests.Create_OutboxInsertFails_Returns500AndWritesNothing_AC8`: base isolada com um *trigger* `BEFORE INSERT` em `outbox_messages`, criado pelo teste como dono; 500; nenhuma versão nem auditoria nova; a versão anterior continua com `validTo = null` |
| AC-9 | `…Tariffs/TariffVersionRulesTests`: `Create_ValidFromInPast_Returns400_AC9`, `Delete_CurrentVersion_Returns409_AC9`, `Delete_LastFutureVersion_Returns204AndReopensPrevious_AC9` |
| AC-11 | `…Spots/CreateSpotTests` (`Create_DuplicateCodeSameFacility_Returns409_AC11`, `Create_SameCodeOtherFacility_Returns201_AC11`); `…Sensors/RegisterSensorTests.Register_DuplicateHardwareId_Returns409_AC11` |
| AC-13 | `…Zones/DeleteZoneTests.Delete_ZoneWithSpots_Returns409AndWritesNothing_AC13`; `…Spots/DeleteSpotTests.Delete_SpotWithSensor_Returns409AndWritesNothing_AC13` |
| AC-14 | `…Sensors/SensorSpotTests`: `Reassign_ToFreeSpotSameFacility_Returns200WithSensorReassigned_AC14`, `Reassign_ToSpotWithSensor_Returns409_AC14`, `Reassign_ToSpotOfOtherFacility_Returns400_AC14`, `Unassign_SensorOnSpot_ClearsSpotAndEmitsSensorUnassigned_AC14`; `…RegisterSensorTests.Register_EnvironmentalWithSpot_Returns400_AC14` |
| AC-15 | `…Spots/SpotBatchTests`: `Batch_1000SpotsWithSensors_Returns201AndCreatesAllWithAuditAndOutbox_AC15` (1 000 lugares, 1 000 sensores, 2 000 auditorias e 2 000 mensagens desse parque), `Batch_1001Spots_Returns400_AC15`, `Batch_ExistingCode_Returns409AndCreatesNothing_AC15` |
| AC-16 | `…Spots/UpdateSpotTests`: `Put_ZoneOfSameFacility_Returns200WithPreviousZoneId_AC16`, `Put_ZoneOfOtherFacility_Returns400_AC16` |
| AC-17 | `…Tariffs/TariffValidationTests.Create_InvalidInput_Returns400ValidationProblemAndWritesNothing_AC17` (*theory*: `pricePerHour` negativo, `billingIncrementMinutes = 0`). O comentário do `FacilityValidationTests` passa a apontar para aqui |
| AC-18 | `…Spots/ListSpotsTests`: `List_Page2Size10Of25_Returns10AndTotal25_AC18` (25 lugares num parque novo; a contagem é por parque, por isso não precisa de base isolada), `List_PageSize101_Returns400_AC18`, `List_UnknownFacility_Returns404_AC18` |
| AC-23 | Documental: `configuration.http` completo, ADR 0004 e ADR 0005 no índice, RF-06 ligado aos AC na RASTREABILIDADE. Extra proposto: `Api.Tests/Features/Configuration/ConfigurationHttpFileTests.HttpFile_EveryConfigurationEndpoint_HasOneRequest_AC23`, que compara as rotas do `EndpointDataSource` sob `/api` com os pedidos do ficheiro |

Testes sem AC: as operações sem efeito (ponto 6); um 404 para um recurso filho de outro parque; o 409 da criação concorrente de versões (ponto 2), com duas criações em paralelo; o 400 de um `hardwareId` repetido no lote (ponto 4); e os testes unitários do Domain. O AC-21 e o AC-22 voltam a correr sem alterações e passam a cobrir os eventos e as portas novas.

## Passos

1. Este plano no índice de `docs/plans/`; a especificação passa a «Em implementação (F2b)» no índice; a linha RF-06 da RASTREABILIDADE passa a «F2b em implementação».
2. Domain: comportamento e eventos de `Zone`, `ParkingSpot` e `Sensor`. Testes unitários.
3. Domain: regras das versões de `Tariff` e os dois eventos. Testes unitários.
4. Portas: os quatro repositórios, `ExistsAsync`, as quatro portas de leitura e as *views* (Application); as implementações e o registo (Infrastructure). AC-21 e AC-22 a passar.
5. Zonas: operações, `ZoneEndpoints`, DTO e validadores, `ZoneApi`. Testes AC-13 (zona) e o 404 do recurso de outro parque.
6. Lugares, individualmente: operações, `SpotEndpoints`, `SpotApi`. Testes AC-11 (lugares), AC-13 (lugar), AC-16 e AC-18.
7. Lote de lugares: `CreateSpotBatch`, o validador e a rota `/batch`. Testes AC-15 e o caso do ponto 4.
8. Sensores: operações, `SensorEndpoints`, `SensorApi`. Testes AC-11 (sensor) e AC-14.
9. Tarifários: operações, `TariffEndpoints`, `TariffApi` e a versão vigente inserida por SQL. Testes AC-7, AC-9, AC-17 e a corrida do ponto 2.
10. Atomicidade: teste AC-8 numa base isolada, com o *trigger* criado pelo próprio teste.
11. `configuration.http` completo e o teste extra do AC-23. Verificação manual contra o `compose.yaml` com a Api ligada como `smartparking_api`: um pedido por *endpoint* e, na base, uma auditoria e uma mensagem por alteração (e um par por lugar e outro por sensor no lote).
12. ADR 0005 (versões de tarifário com vigência) e linha no índice dos ADR.
13. `/verify`: build com `-warnaserror`, format, `dotnet test`, `get_diagnostics`, `detect_antipatterns`, `get_project_graph` e `get_di_registrations`. Teste de mutação de uma regra (por exemplo, sem o fecho da versão anterior, o AC-7 tem de falhar).
14. Revisão independente (`code-reviewer`, só leitura) do diff da F2b; os achados são tratados com o acordo do Diogo.
15. RASTREABILIDADE, DIARIO, estado do plano e da especificação. *Commits* atómicos ao longo dos passos (`feat(domain)…`, `feat(zones)…`, `feat(spots)…`, `feat(sensors)…`, `feat(tariffs)…`, `test…`, `docs(adr)…`, com «(spec 001)»). O PR da F2a e da F2b para a `main` só depois do OK do Diogo. Fecho com `/checkpoint`.

## Linhas a acrescentar em `docs/RASTREABILIDADE.md`

Na matriz, a linha RF-06 passa a:

| RF-06 | UC-02, UC-05 | AC-1 a AC-6, AC-12, AC-17, AC-18, AC-21, AC-22 (F2a); AC-7 a AC-9, AC-11, AC-13 a AC-16, AC-17 e AC-18 (tarifários e lugares), AC-23 (F2b) | 001-configuracao (F2a, F2b); planos [001a](plans/001a-dominio-esquema-parques.md) e [001b](plans/001b-zonas-lugares-sensores-tarifarios.md); [ADR 0005](adr/0005-versoes-de-tarifario-com-vigencia.md) | `Domain/Facilities`, `Application/Features/{Facilities,Zones,Spots,Sensors,Tariffs}`, `Infrastructure/Relational` (modelo, migração `Initial`, unidade de trabalho, repositórios e leitores), `Api/Features/{Facilities,Zones,Spots,Sensors,Tariffs}`, `tests/http/configuration.http` | `Api.Tests/Features/{Facilities,Zones,Spots,Sensors,Tariffs}`, `Schema/InitialMigrationTests`, `Configuration/PersistenceModeTests` (AC-22), `Configuration/ConfigurationHttpFileTests` (AC-23); `ArchitectureTests/Features/RepositoryRulesTests`, `DomainEventRulesTests` (AC-21) | a passar (F2a 2026-11-25; F2b <data>) |

Na linha RNF-06, a coluna de teste ganha `Tariffs/TariffAtomicityTests` (AC-8: a falha da *outbox* reverte o negócio e a auditoria), e o estado continua «parcial (auditoria); cifra e RBAC na F11».

## Entrada a acrescentar em `docs/DIARIO.md`

```
## F2b: zonas, lugares, sensores e tarifários (especificação 001)

- **Data:** <início> a <fim>
- **Feito:**
  - Plano aprovado pelo Diogo (docs/plans/001b-zonas-lugares-sensores-tarifarios.md), no mesmo ramo da F2a.
  - Domain: comportamento e eventos de Zone, ParkingSpot, Sensor e Tariff (13 eventos .v1); regras das versões de tarifário.
  - Repositórios por raiz de agregado (IZoneRepository, IParkingSpotRepository, ISensorRepository, ITariffRepository) e quatro portas de leitura (D10).
  - Endpoints de zonas, lugares (individualmente e em lote de 1 a 1 000), sensores e versões de tarifário, com ETag/If-Match.
  - ADR 0005 (versões de tarifário com vigência); configuration.http completo.
  - Testes: AC-7, AC-8, AC-9, AC-11, AC-13 a AC-16, os casos de tarifário do AC-17, os de lugares do AC-18 e o AC-23; <n> testes a passar (131 antes da F2b). Preencher com o resultado real.
  - Verificação manual contra o compose.yaml: <preencher>.
- **Problemas:** <preencher durante a implementação, incluindo as abordagens que falharam e se foi preciso voltar a planear>
- **Decisões:**
  - Pontos 1 a 8 do plano 001b, com a resposta do Diogo e a data.
  - <desvios ao plano, com a razão>
- **Commits:** <hashes>
```

## Riscos e pontos de atenção

- **Lote de 1 000 lugares com sensores:** 6 000 linhas numa transação (lugares, sensores, auditorias e mensagens). O EF Core agrupa os comandos; não se escreve nenhum tempo. Se o AC-15 se mostrar demasiado lento para o `dotnet test`, regista-se no diário e discute-se com o Diogo, sem baixar o limite da especificação.
- **Ordem das inserções no lote:** o sensor depende do lugar pela FK composta `(facility_id, spot_id)`. Se o EF Core não ordenar bem as inserções, a alternativa é ligar o sensor ao lugar pela navegação. Se falhar duas vezes, para-se e volta-se a planear.
- **Tradutor de FK:** distingue «ainda referenciado» de «referência inexistente» pelo comando (DELETE). Na F2b há `UPDATE`s que mudam FK (zona do lugar, lugar do sensor), mas esses casos são apanhados antes pelas verificações do *handler* (400). Se o `23503` chegar por corrida, a mensagem «referência inexistente» continua certa.
- **Relógio:** as regras das versões usam o `TimeProvider` da Api; os testes inserem versões com margens largas (um dia no passado, uma hora no futuro), para que uma diferença entre o relógio do contentor e o do *host* não afete os testes.
- **Base partilhada nos testes:** o *trigger* do AC-8 vive numa base isolada, para não fazer falhar os outros testes.
- **Migração:** se for precisa uma migração, a especificação volta a «In Review» (regra da especificação) e para-se.

## Verificação ponta a ponta

1. `dotnet build SmartParking.slnx -warnaserror` e `dotnet format --verify-no-changes`.
2. `dotnet test` (com o Docker ligado): os 131 testes da F1 e da F2a e os `…_ACn` da tabela acima.
3. Local: `docker compose up -d --wait`; a base já migrada (sem migrações novas: `dotnet ef migrations list` não mostra nada pendente); a Api ligada como `smartparking_api`; todos os pedidos do `configuration.http` por ordem; na base, uma auditoria `anonymous` e uma mensagem por publicar por alteração.
4. Roslyn: `get_project_graph` (dependências só para dentro), `get_di_registrations` (as portas novas nos dois modos), `get_diagnostics`, `detect_antipatterns`.
