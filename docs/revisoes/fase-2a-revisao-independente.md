# Revisão independente — Fase 2a (domínio, EF Core e RF-06)

- **Data:** 15-11-2026
- **Âmbito:** todo o trabalho da Fase 2a ainda por *commitar*: domínio, `Application`, `Infrastructure.Relational` (incluindo a migração `InitialCreate`), *endpoints* de RF-06, auditoria (RNF-06), testes e documentação.
- **Revisor:** um agente separado (Claude, *general-purpose*), sem acesso ao raciocínio da sessão que escreveu o código. Só podia ler o código, compilar e correr os testes; não podia editar ficheiros. É o passo 6 do fluxo por fase.
- **Instruções dadas ao revisor:** procurar, por esta ordem:
  1. erros de correção (concorrência, auditoria, tradução de erros, códigos HTTP, datas e paginação);
  2. problemas de esquema;
  3. violações da regra de dependências (ADR-0001);
  4. qualidade dos testes;
  5. afirmações da documentação que não correspondem ao código.

  As decisões já aprovadas (ADR-0003, P1–P4) não estavam em discussão. Cada achado tinha de ser verificado no código antes de ser reportado.
- **Verificação dos achados:** a sessão principal confirmou cada achado no código antes de o aceitar. Os testes novos dos achados 1 a 3 foram escritos para falhar sem a correção. No achado 1, isso foi confirmado: com a correção desligada, os 4 testes falharam; com ela ligada, passaram.

## Resumo

| # | Gravidade | Achado | Estado |
|---|---|---|---|
| 1 | Média | Um corpo ilegível dava 400 vazio, sem ProblemDetails, fora do ambiente Development | Corrigido e testado |
| 2 | Média | Erros do PostgreSQL que o domínio deixava passar chegavam ao cliente como 500 | Corrigido e testado |
| 3 | Baixa-média | Campos obrigatórios em falta no JSON recebiam valores por omissão sem aviso | Corrigido e testado |
| 4 | Baixa | O ponto de extensão do D1 não é «só uma nova implementação» | Documentado (ADR-0003, ponto 5) |
| 5 | Baixa | O tipo `numeric(10,2)` não está alinhado com o domínio | Adiado para a Fase 2b |
| 6 | Baixa | Qualidade dos testes: um teste que não podia falhar, falta de cobertura e uma suposição frágil | Corrigido |
| 7 | Baixa | O texto dizia que os UUIDv7 «seguem a ordem de criação» | Corrigido (ADR-0003 e comentário no código) |

## Achados

### 1. Corpo ilegível → 400 vazio fora do ambiente Development (média, confirmado)

- **Problema:** nas Minimal APIs, `RouteHandlerOptions.ThrowOnBadRequest` só está ativo por omissão em Development. Noutros ambientes, um JSON malformado, um corpo vazio ou um enum desconhecido não lançam `BadHttpRequestException`. A resposta é um 400 com `Content-Length: 0`, e o `ProblemDetailsExceptionHandler` nunca é chamado. Isto contradizia o próprio comentário do *handler* e o diário («400 com `errors.<campo>`»).
- **Prova do revisor:** executou a API em Production e enviou `{"name":`, um corpo vazio e `{"type":"Underwater"}`. As três respostas foram 400 sem corpo.
- **Porque é que os testes não o apanharam:** `Facility_with_unknown_enum_value_is_rejected` só verificava o código de estado.
- **Correção:** em `Api/Program.cs`, `Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true)`.
- **Testes:**
  - `Unreadable_body_returns_400_problem_details` (3 casos);
  - `Facility_with_unknown_enum_value_is_rejected`, que agora exige `application/problem+json`.

  Correm no ambiente Testing, onde o problema se manifestava.

### 2. Erros do PostgreSQL como 500 (média, verificado)

- **Instantes abaixo do microssegundo:**
  - Um `DateTimeOffset` do .NET tem resolução de 100 ns; o `timestamptz` guarda microssegundos.
  - `validFrom = …0000001Z` e `validTo = …0000009Z` passavam a regra do domínio (`validTo > validFrom`).
  - Na base de dados, os dois valores ficavam iguais e a restrição `ck_tariffs_validity` falhava com o erro 23514, que chegava ao cliente como 500.
  - Efeito secundário: a resposta ao POST mostrava valores que diferiam dos que um GET devolvia depois.
- **Carácter NUL:** `"a\u0000b"` passava o `Guard.Text`, e o PostgreSQL rejeitava-o com o erro 22021 (500).
- **Correção:**
  - `Guard.Utc` passa a truncar ao microssegundo, e `ValidityPeriod` compara depois de truncar;
  - `Guard.Text` rejeita caracteres de controlo;
  - como rede de segurança, o `EfUnitOfWork` traduz `CheckViolation` (23514) e os erros de dados da classe 22 em `DomainValidationException`, que dá 400.
- **Testes:** `Tariff_instants_are_stored_with_microsecond_precision` e `Control_characters_in_text_are_rejected`.

### 3. Campos obrigatórios em falta (baixa-média, verificado)

- **Problema:** por omissão, o System.Text.Json preenche com `default` os parâmetros do construtor que não vêm no JSON.
  - Um tarifário sem `validFrom` recebia `DateTimeOffset.MinValue`, que o Npgsql grava como `-infinity`: o tarifário ficava válido desde sempre.
  - Um PUT sem `version` dava um 409 enganador («expected version 0»).
- **Correção:**
  - `RespectRequiredConstructorParameters = true` nas opções JSON da API;
  - os campos opcionais declaram um valor por omissão: `ValidTo = null` e `DailyCap = null`. Para isso, a ordem dos parâmetros de `CreateTariff` e `UpdateTariff` mudou; nos pedidos JSON, que usam nomes, nada muda.
- **Teste:** `Missing_required_fields_return_400_instead_of_default_values`.

### 4. Limite do ponto de extensão do D1 (baixa, confirmado)

- **Problema:** o ADR-0003 e a rastreabilidade diziam que levar as sessões para o MongoDB exigiria só uma nova implementação de `IParkingSessionRepository`. Não é verdade com o contrato atual:
  - `IRepository<T>` não tem `Update`;
  - os casos de uso alteram o agregado carregado e chamam `IUnitOfWork.SaveChangesAsync`, que só grava o contexto EF;
  - uma implementação MongoDB perderia o `Close()` e o `MarkPaid()` sem dar erro, e não seria atómica com a outbox.
- **Decisão:** não alterar o contrato agora. O D1 está «EM ABERTO» e o valor por omissão (relacional) não precisa disso. O texto foi corrigido:
  - ADR-0003, ponto 5, e «A rever»;
  - rastreabilidade, linha do D1;
  - comentário de `IParkingSessionRepository`.
- `TODO(Diogo):` se o orientador escolher sessões em NoSQL, acrescentar ao contrato uma gravação explícita e decidir como garantir a atomicidade com a outbox.

### 5. `numeric(10,2)` versus domínio (baixa, plausível)

- **Problema:**
  - um custo de `2.405` seria arredondado pelo PostgreSQL sem aviso;
  - um valor ≥ 10⁸ daria o erro 22003. Com a rede de segurança do achado 2, já seria um 400, mas a validação devia estar no domínio.
- **Adiado:** nenhum *endpoint* escreve montantes antes da Fase 2b (`ParkingSession.Close`, `Payment.Register`).
- Fica registado nos «Pontos a levar» do diário: arredondar e limitar os montantes no domínio.

### 6. Qualidade dos testes (baixa, confirmado)

- **Problemas:**
  - `All_migrations_are_applied_to_an_empty_database` não podia falhar, porque o *fixture* já tinha aplicado as migrações antes do teste;
  - a auditoria de propriedades compostas não estava testada: a localização em colunas, a validade e as regras em `jsonb`;
  - `Facility_full_crud_cycle` procurava o parque criado na primeira página de 200. Com a base de dados partilhada pelo grupo de testes, a partir de 200 parques o teste falharia.
- **Correção:**
  - `Migrations_apply_to_a_new_database_and_roll_back_completely` cria uma base de dados nova no mesmo contentor, aplica todas as migrações, compara as tabelas, reverte até à base vazia (testa o `Down()`) e apaga a base;
  - `Complex_and_json_properties_are_audited_with_dotted_names` cobre `location.latitude` (old/new numa alteração), `rules.*` (jsonb) e `validity.validFrom`;
  - a listagem passa a percorrer todas as páginas.

### 7. UUIDv7 e ordem de criação (baixa, confirmado)

- **Problema:** o `Guid.CreateVersion7()` do .NET não tem contador dentro do mesmo milissegundo. A ordem segue a criação **ao milissegundo**; dentro do mesmo milissegundo (por exemplo, as linhas de auditoria de um `SaveChanges`) é aleatória. A paginação continua estável, porque a ordem é total.
- **Correção:** o texto do ADR-0003 (ponto 3) e o comentário de `EfConfigurationQueries`.
- Os testes não dependem dessa ordem: os de auditoria ordenam por `OccurredAt` e só desempatam por `Id` quando a ordem não interessa.

## O que o revisor verificou e considerou correto

- **Concorrência otimista:**
  - `EnsureVersion` compara a versão enviada pelo cliente com a versão carregada;
  - o EF Core põe o `xmin` original no `WHERE` do `UPDATE` e lê o valor novo;
  - a corrida entre dois pedidos é testada a sério (`Concurrent_modification_of_a_session_raises_a_concurrency_conflict`).
- **Caminhos 409:** código de lugar duplicado, segundo sensor no mesmo lugar (também ao mover um sensor), e eliminação com dependentes (`RESTRICT`).
- **Caminhos 400:** `location`, `rules`, `coordinates` ou `type` nulos, número errado de coordenadas, NaN, valores inteiros de enum fora do domínio, e `skip`/`take` inválidos.
- **Datas:** a normalização para UTC dos `DateTimeOffset` com outro *offset* está testada.
- **Coordenadas:** os literais das restrições `CHECK` correspondem aos limites do domínio.
- **Auditoria:** fica na mesma transação da alteração (o teste da alteração falhada é significativo); o `xmin` é excluído; o mascaramento por `[PersonalData]` funciona.
- **Regra de dependências:** o `Application` continua sem pacotes, e o registo dos *handlers* está na API.
- **Contagens do diário:** estavam certas. O revisor reproduziu 60/60 antes das correções.

## Efeito secundário durante a revisão

Para provar o achado 1, o revisor arrancou a API em `127.0.0.1:5199` em Production, com ligações para endereços inexistentes, e parou-a depois. Uma primeira tentativa usou por engano o perfil de arranque: correu em `:5080`, em Development e ligada às bases de dados do compose, durante cerca de 30 s. Não recebeu pedidos e não escreveu nada.

A sessão principal confirmou depois que não ficou nenhum processo `SmartParking.Api` a correr e que o revisor não alterou nenhum ficheiro (`git status`).

**Lição para a próxima revisão:** pedir explicitamente ao revisor que use `--no-launch-profile` quando precisar de executar a API.

## Resultado após as correções

- `dotnet build`: compilação sem erros nem avisos.
- `dotnet test`: **67/67** em 41,3 s.
  - Na Fase 2a há 36 testes novos em relação à Fase 1: `ConfigurationEndpointsTests` 25, `AuditTests` 5, `MigrationTests` 3, `ParkingSessionRepositoryTests` 3.
  - Em relação ao estado antes da revisão (60), há mais 7 testes; o teste de migração que não podia falhar foi substituído.
