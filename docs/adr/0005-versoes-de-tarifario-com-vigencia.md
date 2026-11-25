# ADR 0005 — Versões de tarifário com vigência

- **Estado:** Aceite (especificação 001 aprovada a 2026-11-24; plano 001b aprovado a 2026-11-25)
- **Data:** 2026-11-25
- **Requisitos:** RF-06 (configuração dos tarifários); prepara o UC-02 (cálculo do valor da sessão com o tarifário em vigor).
- **Decisão (D1–D10):** D1 (sessões em PostgreSQL) e D2 (*outbox* em PostgreSQL), nos valores por omissão. Nenhuma decisão D1–D10 é alterada.
- **Secções da tese:** 5.1, 5.4.1 e 5.9.2.
- **Especificação:** [001-configuracao](../specs/001-configuracao.md) (F2b: AC-7, AC-8, AC-9 e os casos de tarifário do AC-17); plano [001b](../plans/001b-zonas-lugares-sensores-tarifarios.md), pontos 1, 2, 5 e 8.

## Contexto

No UC-02, o valor de uma sessão calcula-se com o tarifário do parque. Se o tarifário fosse uma linha que se altera no lugar, uma sessão aberta antes de uma alteração passaria a ser calculada com o preço novo, e não ficaria nenhum registo, no esquema, de qual era o preço em cada instante. A auditoria guarda o histórico, mas uma sessão não pode depender da auditoria para saber o seu preço.

Há também uma regra de integridade: num dado instante, um parque tem, no máximo, um tarifário em vigor.

## Decisão

- **Uma versão por linha, com vigência.** A tabela `tariffs` guarda versões com o intervalo semiaberto [`valid_from`, `valid_to`). A versão mais recente de cada parque é a única com `valid_to` nulo. A sessão (F3) refere a versão com que foi aberta (`parking_sessions.tariff_id`, FK `RESTRICT`).
- **A base de dados garante que as versões não se sobrepõem:** `EXCLUDE USING gist (facility_id WITH =, tstzrange(valid_from, valid_to) WITH &&)`, com a extensão `btree_gist`, mais `CHECK (valid_to IS NULL OR valid_to > valid_from)`. Estão na migração `Initial` (F2a).
- **Regras no Domain (`Tariff`):**
  - uma versão nova começa agora ou depois (senão 400) e depois do início da última versão do parque (senão 409);
  - criar uma versão fecha a última (`valid_to` passa a ser o `valid_from` da nova);
  - uma versão que já começou é imutável: não há `PUT` de tarifários; uma alteração de preço é uma versão nova;
  - só se apaga a última versão, e só enquanto é futura (senão 409); apagá-la reabre a anterior (`valid_to = null`);
  - uma versão referida por sessões nunca se apaga (FK `RESTRICT`, AC-19).
- **Uma auditoria e um evento por operação (AC-7).** Fechar a versão anterior faz parte da criação da nova, por isso não tem auditoria nem evento próprios. O `TariffVersionCreated.v1` leva `previousVersionId` e `previousValidTo`. Do mesmo modo, o `TariffVersionDeleted.v1` leva `reopenedVersionId`. A versão vizinha é gravada na mesma transação e o seu `xmin` é verificado.
- **Concorrência.** Duas criações em simultâneo sobre a mesma última versão: a segunda falha no `xmin` dessa versão (a unidade de trabalho devolve 412), e o *handler* responde 409, porque um `POST` não leva `If-Match`. Quando ainda não há versões, a corrida é apanhada pelo `EXCLUDE` (`23P01`, 409).
- **Instantes em UTC, truncados ao microssegundo**, que é a precisão de `timestamptz`. Assim, o `valid_to` da versão fechada é exatamente igual ao `valid_from` guardado da nova, e a versão anterior encontra-se por igualdade quando a nova é apagada.
- **Leitura da versão em vigor:** `GET /api/facilities/{id}/tariffs/current` procura `valid_from ≤ agora < coalesce(valid_to, ∞)`. Dá 404 se não houver nenhuma.

## Alternativas consideradas

- **Uma linha por parque, alterada no lugar, com o histórico só na auditoria.** É mais simples, mas o preço de uma sessão passada só se reconstruiria a partir da auditoria, e uma alteração a meio de uma sessão mudaria o valor calculado. Contraria o UC-02.
- **Versões sem `valid_to` (só `valid_from`), com a vigência deduzida da versão seguinte.** Evita atualizar a versão anterior, mas a regra «no máximo uma em vigor» deixa de poder ser garantida por uma restrição (`EXCLUDE` precisa do intervalo), e a consulta da versão em vigor passa a precisar de uma subconsulta ou de uma função de janela.
- **Tabelas temporais do sistema (*system-versioned*, SQL:2011).** O PostgreSQL 17 não as suporta de forma nativa; seria preciso uma extensão ou gatilhos, fora da stack aprovada.
- **Guardar o preço na própria sessão, sem versões.** Dispensa a FK, mas perde a ligação entre a sessão e a regra de faturação completa (incremento, máximo diário e minutos grátis), e o operador deixa de poder programar alterações futuras.
- **Um evento e uma auditoria também para a versão fechada.** Seguiria à letra a frase «um par por entidade alterada» da secção «Integrations» da especificação, mas contradiz o AC-7 (exatamente uma auditoria e um evento). O critério de aceitação, mais específico, prevaleceu (plano 001b, ponto 1, aprovado pelo Diogo a 2026-11-25).

## Consequências

- A F3 calcula o valor com a versão referida pela sessão, sem consultar a auditoria.
- Os testes não conseguem criar pela API uma versão já em vigor (`validFrom` ≥ agora). Inserem-na por SQL, como dono, sem pacotes novos (plano 001b, ponto 8).
- Uma alteração de preço «imediata» faz-se com um `validFrom` alguns segundos no futuro: um `validFrom` igual ao instante em que o cliente envia o pedido já está no passado quando o *handler* o verifica (400). A versão anterior fica fechada nesse instante.
- O `EXCLUDE` depende da extensão `btree_gist`, que a `Initial` cria.
- Os projetores (F5, F7) recebem, num só evento, a versão nova e a versão anterior que foi fechada.

## Referências

- Texto das secções 5.1, 5.4.1 e 5.9.2 da tese (v16).
- Documentação do PostgreSQL 17 sobre restrições de exclusão, tipos de intervalo (`tstzrange`) e a extensão `btree_gist` [VERIFICAR]: as páginas e as versões exatas não foram citadas nesta sessão.
