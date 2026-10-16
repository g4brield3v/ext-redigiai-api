# RedigiAI — Especificação técnica do MVP

> Fonte da verdade do produto. Versão de 11/10/2026. Em conflito com qualquer outro arquivo, vale este.
>
> **Atualização de 12/10/2026:** o modelo de temas foi substituído pelo de propostas de redação: blocos ordenados, um único tipo por proposta, definido no cadastro, e área de administração. Modelo, validação, rotas de administração e fluxo do aluno estão em `docs/07-propostas-de-redacao.md`, que vale para tudo o que trata de proposta.

## 1. Visão geral

O MVP do RedigiAI corrige redações manuscritas da Fuvest com IA: o aluno escolhe vestibular e proposta, envia a foto da folha e recebe, na plataforma, a nota de cada critério com as linhas que pesaram e o que corrigir.

**Escopo do MVP**

- Vestibular: somente Fuvest, com a grade de 4 critérios (níveis de 1 a `max_level`, pesos 3, 2, 3 e 2; na grade publicada, 4 níveis e máximo de 40 pontos).
- Entrada: somente foto JPG da folha, até 10 MB, uma face por redação. Envio em lote permitido.
- Fluxo: seleção (vestibular → proposta → leitura da proposta e da coletânea, em página própria) → foto → confirmação → transcrição → revisão de palavras ilegíveis, se houver → correção → aviso por e-mail → correção na plataforma.
- Cobrança: 1 correção grátis por conta, plano semanal no cartão e correção avulsa por Pix ou cartão.
- Painel do aluno: lista de correções, evolução da nota e foco de melhoria.
- Folha pautada da plataforma para download (PDF, 30 linhas).
- Área de administração de propostas de redação (`/admin/propostas`): cadastro, versões e publicação, só para o administrador (`docs/07-propostas-de-redacao.md`).

**Fora do escopo do MVP**

- Envio de texto digitado.
- Outros vestibulares (Unicamp e Enem aparecem como "em breve").
- O restante do painel administrativo: só as propostas de redação têm área de administração; grades, perfis de correção, usuários e pagamentos entram direto no banco.
- Conversão da nota para a escala da prova.
- Reenvio de foto ou cancelamento pelo aluno depois da confirmação.

## 2. Arquitetura e stack

O MVP roda em quatro serviços gerenciados e um backend .NET com dois processos (API e worker) sobre um único PostgreSQL; o navegador nunca chama a IA, o Cloudinary só recebe upload assinado e todo o resto passa pela API.

| Componente | Escolha | Papel |
|---|---|---|
| Frontend | React + Vite + TypeScript na Vercel (Pro) | Telas, upload direto ao Cloudinary, checkout |
| Backend | ASP.NET Core em Docker no Railway, processos `api` e `worker` | Regras de negócio, fila, integrações |
| Banco | PostgreSQL no Railway | Dados, fila de jobs, livro-razão de créditos |
| Login | Clerk | Google e link por e-mail; o backend valida o JWT |
| Pagamentos | Mercado Pago | Plano semanal no cartão; avulsa por Pix ou cartão |
| Arquivos | Cloudinary | Fotos em pasta privada, URLs assinadas |
| E-mail | SendGrid | Avisos transacionais |
| IA | API do Claude | Transcrição da foto e correção |

Para crescer: aumentar réplicas do `worker` no Railway; depois, mover os mesmos containers para Azure Container Apps ou AWS e, se a fila no PostgreSQL virar gargalo, trocar por um broker sem mudar os handlers.

## 3. Regras de negócio

Cada regra abaixo é requisito de backend e frontend; o backend é a fonte da verdade e o frontend apenas reflete.

**Seleção e título**

1. O aluno escolhe, nesta ordem, o vestibular e uma proposta publicada desse vestibular. O tipo de redação é a categoria de correção que o administrador escolhe numa lista no cadastro da proposta (`essayType`; não há texto livre); o aluno não escolhe tipo nem opção. A proposta e a coletânea ficam numa página só de leitura, separada da página de envio, onde se envia a foto. Cada redação responde a exatamente uma proposta, na versão publicada no momento do envio.
2. O título de toda redação e correção segue o formato "Vestibular – Tipo – Tema", sem ano: nome do vestibular, nome da categoria de correção e título da proposta ("Fuvest – Dissertação em prosa – O papel da ciência no mundo contemporâneo").

**Envio**

3. Somente foto JPG, até 10 MB, uma face por redação. Envio em lote: várias redações numa única confirmação, limitado ao saldo.
4. Antes de enviar, um modal mostra a redação, o custo (1 correção por redação) e o saldo depois do envio. Ao confirmar, o crédito é reservado e a transcrição começa.
5. Depois da confirmação, não há troca de foto nem cancelamento pelo aluno.

**Transcrição e revisão**

6. A transcrição copia o texto exatamente como escrito, sem corrigir nada, e marca cada palavra ilegível com sua linha.
7. Mais de 10 palavras ilegíveis: a redação falha (PHOTO_UNREADABLE), o crédito volta e o aluno é orientado a fazer um novo envio com foto mais nítida.
8. De 1 a 10 palavras ilegíveis: o aluno digita só essas palavras. O resto da transcrição não é editável. O botão de envio só habilita com todas preenchidas, e um modal confirma as palavras antes de seguir.
9. Prazo da revisão: lembrete por e-mail 12 h após o aviso; sem preenchimento em 24 h, a redação é cancelada pelo sistema e o crédito volta.

**Correção e nota**

10. Antes dos critérios, a IA verifica as hipóteses de nota zero: tema não desenvolvido, tipo não atendido, cópia integral da coletânea ou identificação do candidato. Se alguma se aplica, a nota é 0 e os critérios não são pontuados.
11. A IA devolve só o nível (1 a `max_level` da grade), a justificativa e as linhas de cada critério. O backend calcula a nota: soma de nível × peso. Sem conversão de escala.
12. Toda orientação é uma afirmação sobre o que corrigir, com as linhas; nunca uma pergunta. Erros de escrita são agrupados pela regra.
13. A grade é dado versionado no banco. Versão publicada nunca é editada; cada correção guarda a versão usada.
14. Ao fim da correção, o aluno pode avaliar se ela ajudou (3 opções) e deixar um comentário.

**Créditos e cobrança**

| Origem | Preço | Pagamento | Validade |
|---|---|---|---|
| Teste grátis | R$ 0 | — | Não vence; 1 por conta |
| Plano semanal | R$ 39,90/mês | Cartão de crédito, renovação automática | 3 créditos toda segunda-feira, 00h (Brasília); acumulam e vencem no dia 1º de cada mês |
| Avulsa | R$ 16 por correção | Pix ou cartão | Não vence |

15. Consumo: primeiro os créditos do plano (vencem antes), depois o teste grátis, depois os avulsos.
16. Reescrita de uma redação custa 1 correção, como um envio novo.
17. Crédito volta apenas em falha do sistema, foto ilegível (regra 7) ou revisão não feita no prazo (regra 9). Além disso, em até 7 dias da compra, o comprador pode desistir e receber o valor das correções não usadas; no plano, o reembolso é proporcional (CDC, art. 49). Correções já entregues não são reembolsadas.

**Conta e dados**

18. Somente pessoas com 16 anos ou mais, conferido pela data de nascimento informada no cadastro. Usuários de 16 e 17 anos compram com autorização do responsável, declarada no pagamento. Não há conta de responsável.
19. Contas sem assinatura ativa e sem uso por 3 meses são excluídas; aviso por e-mail 1 semana antes.
20. Exclusão (automática ou a pedido): dados pessoais apagados e redações anonimizadas; créditos não usados se perdem.

## 4. Máquina de estados da redação

A redação tem 7 estados e só o sistema muda de um para outro; o aluno não cancela nem troca a foto depois de confirmar o envio.

```mermaid
stateDiagram-v2
  [*] --> uploaded: aluno confirma o envio (crédito reservado)
  uploaded --> queued: transcrição sem palavras ilegíveis
  uploaded --> awaiting_review: 1 a 10 palavras ilegíveis
  uploaded --> failed: mais de 10 ilegíveis (PHOTO_UNREADABLE) ou TRANSCRIPTION_FAILED
  awaiting_review --> queued: aluno preenche todas as palavras
  awaiting_review --> cancelled: 24 h sem preencher (sistema)
  queued --> processing: worker pega o job grade
  processing --> completed: correção validada (crédito consumido)
  processing --> failed: GRADING_FAILED
  failed --> [*]: crédito devolvido
  cancelled --> [*]: crédito devolvido
  completed --> [*]
```Toda transição é um `UPDATE essays SET status = <novo> WHERE id = ? AND status = <esperado>`, na mesma transação do job seguinte e do lançamento de crédito; se nenhuma linha muda, outro processo já avançou e a operação termina sem efeito.

| Status | O aluno vê |
|---|---|
| `uploaded` | Lendo sua redação |
| `awaiting_review` | Precisa de você: digite as palavras ilegíveis até [prazo] |
| `queued` | Na fila de correção |
| `processing` | Em correção |
| `completed` | Corrigida |
| `failed` | Não corrigida, crédito devolvido |
| `cancelled` | Cancelada por falta de revisão, crédito devolvido |

## 5. Modelo de dados

PostgreSQL é a única base: dados do produto, fila de jobs e livro-razão de créditos. Chaves primárias são UUID v7; datas em `timestamptz` (UTC), convertidas para Brasília só na exibição e nas regras de calendário.

**Catálogo (inserido via banco no MVP)**

| Tabela | Campos principais | Regras |
|---|---|---|
| `exams` | id, slug (`fuvest`), name, status (`active`, `coming_soon`), sheet_lines (30) | Vestibular. |
| `rubric_versions` | id, exam_id, version, published_at, max_level, max_score, source, label ("Fuvest 2027"), zero_rules (jsonb: `code`, `description`, `studentNote`) | Imutável depois de publicada. `studentNote` é a explicação da hipótese para o aluno, exibida na correção com nota zero. |
| `rubric_criteria` | id, rubric_version_id, position (1–4), name, short_name ("Tema e autoria"), weight, level_descriptors (jsonb: um por nível, de 1 a `max_level`) | Peso e descritores oficiais por nível. O nome curto é o que a tela da correção usa em títulos e filtros. |
| `prompts` | id, exam_id, slug, version, status (`draft`, `published`, `archived`), title, summary, source_label, essay_type_id, created_at, updated_at, published_at, created_by, published_by | Proposta de redação (`docs/07-propostas-de-redacao.md`, A2). `title` é o tema, sem ano. `slug` + `version` é único; só uma versão `published` por slug. Depois de publicada não é editada: mudar cria a versão seguinte, e a anterior fica arquivada. |
| `prompt_blocks` | id, prompt_id, position, kind, data (jsonb) | Conteúdo da página da proposta, em ordem (`docs/07-propostas-de-redacao.md`, A3). |
| `prompt_grading_notes` | id, prompt_id, expected_focus, tangent_signals, off_topic_signals, key_data (jsonb), extra | Abordagens esperadas do tema. Uma por proposta; obrigatórias para publicar. |
| `essay_types` | id, exam_id, slug (`dissertacao-em-prosa`, `narrativa`), name, description | Categorias de correção do vestibular. O nome ("Dissertação em prosa") é o tipo que o aluno vê e o que entra no título da redação. Um perfil de correção ativo por vestibular e categoria; categoria nova entra pelo banco, junto com o perfil dela. |

**Usuário, redação e correção**

| Tabela | Campos principais | Regras |
|---|---|---|
| `users` | id, clerk_user_id (único), email, name, birth_date, role (`student`, `admin`), last_active_at, deletion_warned_at, deleted_at | Tabela própria ligada ao Clerk. `role` é `student` por padrão e vem da configuração (`docs/07-propostas-de-redacao.md`, A5). |
| `essays` | id, user_id, batch_id, prompt_id, essay_type_id, rubric_version_id, status, photo_asset_id, version (1, 2…), parent_essay_id, review_deadline_at, failure_code, grading_profile_id, created_at | `prompt_id` é a versão exata da proposta; `essay_type_id` é copiado da proposta no envio. `parent_essay_id` liga a reescrita à versão anterior. |
| `transcriptions` | id, essay_id, lines (jsonb: número + texto), illegible_count, model, created_at | Texto fiel, nunca editado. |
| `illegible_words` | id, transcription_id, line_number, char_start, char_end, student_value, filled_at | O que o aluno digitou fica separado da transcrição. |
| `corrections` | id, essay_id, rubric_version_id, grading_profile_id, prompt_id, prompt_grading_notes_id, collection_copy (jsonb), zero_reason, zero_explanation, score, max_score, strengths, main_issue, main_issue_criterion, next_steps (jsonb: `[{text, criterion}]`), model, raw_response (jsonb), created_at | `score` calculado pelo backend. Uma por redação. O critério do problema principal e de cada passo é a posição na grade; nulo com nota zero. `prompt_id` é a versão da proposta usada; `collection_copy` guarda os trechos copiados da coletânea achados pela verificação automática. |
| `correction_criteria` | id, correction_id, criterion_id, position, level, points, justification, guidance, lines (int[]) | `points` = level × weight. |
| `correction_marks` | id, correction_id, criterion_id, criterion_position, kind (`good`, `issue`), line_start, line_end, char_start, char_end, note, rule_code, fix | Apontamentos na redação. `fix` é a forma correta do trecho e só existe em desvio de escrita (marca `issue` com `rule_code`). |
| `correction_feedback` | id, correction_id, rating (`helped_a_lot`, `helped_partly`, `did_not_help`), comment (até 1.000 caracteres), created_at | 1 por correção (índice único em `correction_id`). |
| `assets` | id, user_id, provider (`cloudinary`), public_id, bytes, mime, purge_after, scope (`essay_photo`, `prompt_image`) | Fotos de redação privadas. Imagens de proposta ficam numa pasta pública, com URL estável. |

**Créditos e pagamentos**

| Tabela | Campos principais | Regras |
|---|---|---|
| `credit_ledger` | id, user_id, source (`trial`, `plan_week`, `single`, `refund`), delta (+1/−1), bucket_id, essay_id, payment_id, expires_at, created_at | Livro-razão: só insere, nunca altera. Saldo = soma por bucket não vencido. |
| `credit_buckets` | id, user_id, source, granted, remaining, expires_at (null = não vence) | Cache do saldo, atualizado na mesma transação do ledger com `UPDATE … WHERE remaining >= n`. |
| `subscriptions` | id, user_id, mp_preapproval_id, status (`active`, `paused`, `cancelled`), next_billing_at, card_last4 | Plano semanal no Mercado Pago. |
| `payments` | id, user_id, mp_payment_id (único), kind (`plan`, `single`), method (`pix`, `card`), amount_cents, status, quantity, created_at | Idempotência pelo id do Mercado Pago. |
| `webhook_events` | id, provider, external_id (único), payload, processed_at | Guarda todo webhook antes de processar. |

**Infraestrutura**

| Tabela | Campos principais | Regras |
|---|---|---|
| `jobs` | id, type, payload (jsonb), status (`ready`, `running`, `done`, `dead`), attempts, max_attempts, run_at, locked_until, last_error, created_at | Fila; ver seção 7. |
| `email_log` | id, user_id, template, ref_id, sent_at, provider_message_id | Garante 1 envio por evento (único em template + ref_id). |
| `audit_log` | id, actor, action, entity, entity_id, data, created_at | Mudanças de status, créditos, exclusões. |

## 6. API

API REST em ASP.NET Core, prefixo `/v1`, JSON. Rotas públicas servem só catálogo; o resto exige o token do Clerk (`Authorization: Bearer`), validado pelo JWKS do Clerk. As rotas de administração (`/v1/admin/*`) exigem ainda o papel `admin` e o segundo fator na sessão; estão descritas em `docs/07-propostas-de-redacao.md`, A5. Todo acesso a redação filtra por `user_id` do token: um aluno nunca lê a redação de outro (resposta 404, não 403).

**Catálogo (público)**

| Método e rota | Faz |
|---|---|
| `GET /v1/exams` | Vestibulares e status (`active`, `coming_soon`). |
| `GET /v1/exams/{slug}/prompts?q=` | Propostas publicadas, com `title`, `summary`, `essayType` (`slug`, `name`) e nº de textos. |
| `GET /v1/prompts/{slug}?version=` | Versão publicada da proposta: blocos em ordem, `essayType` (`slug`, `name`), linhas da folha, URLs públicas das imagens e `essayTitle` (o título que a redação vai ter). Não devolve as abordagens esperadas nem a descrição das imagens feita para a IA. Com `version`, devolve essa versão se ela já foi publicada (`status` `archived`): é o que a redação feita numa versão antiga continua mostrando. |
| `GET /v1/prompts/{id}/sheet.pdf` | Folha pautada em PDF, com o título da proposta e o tipo no topo. Ainda não implementada: entra numa branch própria, depois da etapa 3b. |
| `GET /v1/rubrics/{versionId}` | Critérios, pesos e descritores (seção "Como a nota é calculada"). |

**Conta**

| Método e rota | Faz |
|---|---|
| `POST /v1/me` | Primeiro acesso: cria o usuário local a partir do token e grava a data de nascimento e recusa menores de 16 anos (`birthDate` obrigatório). Concede o crédito grátis uma única vez. |
| `GET /v1/me` | Perfil, saldo por origem e assinatura. |
| `PATCH /v1/me` | Nome e preferências de e-mail. |
| `POST /v1/me/export` | Gera arquivo com os dados do aluno (job). |
| `DELETE /v1/me` | Exclusão a pedido (job). |

**Envio e acompanhamento**

| Método e rota | Faz |
|---|---|
| `POST /v1/uploads/sign` | Devolve assinatura de upload do Cloudinary para 1 JPG (pasta privada, limite de 10 MB, expira em 10 min). |
| `POST /v1/essays/batch` | Corpo: lista de `{promptId, assetId, parentEssayId?}` + `Idempotency-Key`. Sem tipo: o backend usa o da proposta. Valida proposta publicada, arquivo e saldo; reserva N créditos e cria N redações + jobs `transcribe` na mesma transação. Responde 202 com os ids. |
| `GET /v1/essays?status=&cursor=` | Lista do painel, com título no formato "Vestibular – Tipo – Tema". |
| `GET /v1/essays/{id}` | Status, prazo de revisão e, se houver, palavras ilegíveis. |
| `PUT /v1/essays/{id}/illegible` | Corpo: `[{wordId, value}]`. Só aceito em `awaiting_review`, antes do prazo e com todas as palavras preenchidas; muda para `queued` e cria job `grade`. |
| `GET /v1/essays/{id}/correction` | Correção completa, com tudo já calculado (o front só exibe): `rubric` (`maxLevel`, `maxScore`, `versionLabel`); `score`, `scoreDelta` e `previousVersionScore`; `zero` (`applies`, `ruleCode`, `ruleLabel`, `ruleNote`, `explanation`); `strengths`; `mainIssue` e `nextSteps` (`text`, `criterion`, `potentialGain` = (nível máximo − nível) × peso; os passos saem do maior ganho para o menor); `lines` (transcrição com as palavras do aluno); `annotations` (`id` sequencial em ordem de linha, `kind`, `criterion`, linhas e posições, `quote` recortado pelo backend, `note`, `ruleName`, `fix`); `criteria` (`shortName`, `fullName`, `weight`, `level`, `points`, `maxPoints`, `pointsLost`, `currentDescriptor`, `nextDescriptor`, `justification`, `guidance`, `annotationIds`); `feedback`, se o aluno já avaliou. |
| `GET /v1/essays/{id}/photo` | URL assinada da foto, válida por 5 min. |
| `POST /v1/corrections/{id}/feedback` | Avaliação (3 opções) + comentário opcional. Corpo: `{rating, comment?}`. Uma por correção: a segunda responde `INVALID_STATE` (409). Correção de outro aluno → 404. |
| `GET /v1/me/progress` | Notas por data e critério que mais tirou pontos nas últimas 3 correções. |

**Pagamentos**

| Método e rota | Faz |
|---|---|
| `POST /v1/checkout/single` | Corpo: quantidade e método (`pix` ou `card`). Cria o pagamento no Mercado Pago; Pix devolve QR code e copia-e-cola. |
| `POST /v1/checkout/plan` | Cria a assinatura (preapproval) no cartão e devolve a URL de checkout. |
| `POST /v1/subscription/cancel` | Cancela a renovação; os créditos já concedidos seguem até o dia 1º. |
| `POST /v1/webhooks/mercadopago` | Recebe notificações; valida a assinatura `x-signature`, grava em `webhook_events` e enfileira job. Sem token do Clerk. |
| `POST /v1/webhooks/clerk` | Eventos de usuário (e-mail alterado, conta excluída); valida a assinatura Svix. |

**Contratos gerais**

- Erros sempre no formato `{"code", "message", "retryable", "traceId"}` (seção 12).
- `POST` que cria redação ou pagamento exige `Idempotency-Key`; repetir a chave devolve a mesma resposta.
- Limites por usuário: 30 requisições por minuto na API e 10 por hora em `POST /v1/essays/batch`.

## 7. Fila de jobs e workers

A fila é a tabela `jobs` no PostgreSQL: gravar a mudança de status e o job na mesma transação substitui RabbitMQ e outbox no MVP.

**Funcionamento**

1. O worker acorda por `LISTEN/NOTIFY` ou a cada 5 s e pega 1 job com `run_at <= now()` usando `FOR UPDATE SKIP LOCKED`.
2. Marca `status = running` e `locked_until = now() + 10 min`. Enquanto o handler roda, o worker renova a trava a cada 2 minutos (`Jobs__Heartbeat`), só se o job ainda for dele (mesma tentativa, `status = running`). Se o worker morrer, o job volta a ficar disponível quando `locked_until` vencer.
3. Sucesso: `done`. Erro com `retryable = true`: `run_at = now() + backoff exponencial` (30 s, 2 min, 8 min, 30 min) até `max_attempts`. Erro definitivo ou tentativas esgotadas: `dead` e o handler de falha do tipo roda.
4. Todo handler é idempotente: antes de agir, confere o status atual da redação com `UPDATE … WHERE status = <esperado>` e sai sem fazer nada se outra execução já avançou.
5. Concorrência: N workers por instância (configurável), com limite separado para `grade` e `transcribe` para respeitar o limite de requisições da API do Claude.

**Tipos de job**

| Tipo | Disparo | Faz | Tentativas | Falha definitiva |
|---|---|---|---|---|
| `transcribe` | Confirmação do envio | Transcreve a foto (seção 8). 0 ilegíveis → `queued` + job `grade`; 1 a 10 → `awaiting_review` + e-mail; mais de 10 → `failed` (PHOTO_UNREADABLE) + devolve crédito | 4 | `failed` (TRANSCRIPTION_FAILED), devolve crédito, e-mail |
| `grade` | Transcrição sem ilegíveis ou revisão confirmada | Corrige (seção 8), valida, calcula a nota, grava, consome o crédito, `completed`, job `send_email` | 4 | `failed` (GRADING_FAILED), devolve crédito, e-mail |
| `send_email` | Eventos da seção 10 | Envia pelo SendGrid; único por template + referência | 6 | Registra e alerta |
| `process_payment` | Webhook do Mercado Pago | Consulta o pagamento na API, concede créditos ou atualiza a assinatura | 6 | Alerta para verificação manual |
| `export_user` / `delete_user` | Pedido do aluno ou inatividade | Gera arquivo de dados / apaga e anonimiza | 3 | Alerta |

**Jobs agendados** (horário de Brasília)

| Quando | Faz |
|---|---|
| A cada 15 min | Redações em `awaiting_review`: com 12 h sem preencher e sem lembrete → e-mail de lembrete; com `review_deadline_at` vencido → `cancelled`, devolve crédito, e-mail. |
| Segunda-feira, 00h | Concede 3 créditos a cada assinatura ativa, com vencimento no próximo dia 1º às 00h. |
| Dia 1º, 00h | Vence os créditos do plano não usados (lançamento negativo no ledger). |
| Diariamente, 03h | Inatividade: contas sem assinatura ativa com `last_active_at` há 3 meses menos 7 dias → aviso; há 3 meses e já avisadas → job `delete_user`. |
| Diariamente, 04h | Apaga do Cloudinary as fotos com `purge_after` vencido. |

## 8. Integração com IA

Duas chamadas à API do Claude por redação: uma de transcrição (imagem → texto) e uma de correção (texto → avaliação). O modelo de cada uma é configuração, não código, e fica gravado em `transcriptions.model` e `corrections.model`.

**Modelos iniciais**

| Etapa | Modelo | Por quê |
|---|---|---|
| Transcrição | `claude-sonnet-5-5` | Leitura de letra cursiva é a base de tudo; um erro aqui vira erro "do aluno" na nota. |
| Correção | `claude-opus-5-5` | É a etapa em que a qualidade mais pesa. Cabe no teto com uma nova tentativa. |

Custo estimado da combinação: US$ 0,08 por redação, abaixo do teto de US$ 0,15. Primeiro teste de economia: rodar o conjunto de referência com `claude-haiku-5-5` na transcrição e trocar se a precisão for a mesma.

**Base de conhecimento da correção**

A IA não "sabe" a correção da Fuvest de antemão: a cada redação, o worker monta a chamada com camadas guardadas no banco, da mais geral à mais específica. Toda camada é editável no banco e versionada: editar cria uma versão nova em `draft`, que só vira `active` depois de passar no conjunto de referência.

| Ordem | Camada | Tabela | O que contém | Muda com |
|---|---|---|---|---|
| 1 | Instruções do corretor | `prompt_templates` | Papel da IA, regras de forma (afirmações, linhas, limites de tamanho), defesa contra instruções no texto do aluno, schema da saída | Melhorias de prompt |
| 2 | Grade | `rubric_versions`, `rubric_criteria` | Critérios, pesos, descritores oficiais de cada nível, hipóteses de nota zero | Edital da Fuvest |
| 3 | Manual do corretor | `rubric_guidelines` | Por critério: o que verificar, sinais de cada nível, erros comuns de avaliação, como escrever a orientação | Experiência acumulada |
| 4 | Regras de escrita | `writing_rules` | Catálogo dos desvios do critério 4: código, nome ("Crase antes de verbo"), explicação curta, exemplo certo e errado | Raramente |
| 5 | Redações-âncora | `anchor_essays`, `anchor_scores` | Redações reais avaliadas por professor, com nível e justificativa por critério; uma por faixa de desempenho, por tipo de redação | Novas âncoras |
| 6 | Proposta | `prompts`, `prompt_blocks`, `prompt_grading_notes` | Blocos da proposta, na ordem, e abordagens esperadas do tema: recorte esperado, sinais de tangenciamento e de fuga, dados-chave da coletânea (`docs/07-propostas-de-redacao.md`, A6) | Cada proposta ou versão nova |
| 7 | Redação | `transcriptions`, `illegible_words` | Texto final numerado por linha | Cada envio |

Camadas 1 a 5 são iguais para todas as redações do mesmo vestibular e tipo; a 6, para a mesma versão de proposta. Por isso entram no cache nessa ordem e só a 7 é cobrada pelo preço cheio.

- **Perfil de correção.** `grading_profiles` reúne as versões ativas das camadas 1 a 5 e o modelo, por vestibular e tipo. Cada correção grava o `grading_profile_id` e a versão da proposta usados: qualquer nota pode ser explicada e reproduzida depois.
- **Conjunto de referência.** `reference_essays` guarda redações com os níveis dados por professor; `eval_runs` guarda cada execução de um perfil sobre elas, com a diferença por critério e o custo. Um perfil só é ativado se ficar a no máximo 1 nível da referência em 90% dos critérios. Enquanto não houver correções humanas, valem três testes sem professor: redações com defeitos plantados (o defeito precisa ser apontado no critério e na linha certos), consistência (a mesma redação corrigida 3 vezes varia no máximo 1 nível por critério) e transcrição comparada à digitação manual da folha. Fontes de referência humana, em ordem de prioridade: redações do Simulado Fuvest com a nota oficial, doadas por alunos com autorização; redações corrigidas por professores contratados; amostra de correções da plataforma auditada por professor depois do lançamento.
- **Sem âncoras ainda.** O MVP pode começar só com as camadas 1 a 4 e 6; as âncoras entram quando houver redações avaliadas por professor.

**Fluxo da correção**

Sem âncoras, a IA compara a redação só com os descritores da grade e o manual do corretor; com âncoras, também com exemplos reais de cada nível, o que tende a deixar o nível mais calibrado. Nos dois casos, a nota é a mesma conta feita pelo backend.

**Fluxo de ativação de um perfil**

O perfil que corrige os alunos nunca muda direto: toda edição passa pelos testes, com a referência de professor quando ela existir e pelos testes sem professor enquanto não existir.

**Por que o perfil de correção existe**

O perfil existe porque a grade e as instruções são editáveis no banco: sem ele, cada edição valeria na hora para todos os alunos, sem teste, e as notas antigas perderiam a explicação.

Ele resolve três requisitos de uma vez:

1. **Testar antes de afetar alunos.** Uma edição gera um perfil novo em teste; os alunos continuam sendo corrigidos pelo perfil aprovado até o novo passar.
2. **Explicar qualquer nota.** Cada correção guarda o perfil e a versão da proposta usados, então dá para reconstruir exatamente com que grade, instruções e modelo ela foi feita.
3. **Trocar e desfazer com segurança.** Todas as camadas mudam juntas, numa única troca, e a volta ao perfil anterior é imediata.

**Como funciona tecnicamente**

- Tabela `grading_profiles`: `id`, `exam_id`, `essay_type_id`, `version`, `status` (`draft`, `active`, `retired`), `transcription_prompt_version_id`, `grading_prompt_version_id`, `rubric_version_id`, `guidelines_version_id`, `writing_rules_version_id`, `anchor_set_id` (pode ser nulo), `transcription_model`, `grading_model`, `created_at`, `activated_at`.
- Índice único parcial em (`exam_id`, `essay_type_id`) onde `status = 'active'`: o banco impede dois perfis ativos para o mesmo vestibular e tipo.
- O perfil só guarda referências. O conteúdo fica nas tabelas de cada camada, que nunca são alteradas depois de criadas; editar é inserir uma versão nova.
- No início do job de correção, o worker lê o perfil ativo do tipo da redação, grava o `grading_profile_id` na correção e monta a chamada só com as versões apontadas. Uma nova tentativa da mesma redação usa o mesmo perfil.
- A ativação é uma transação com dois `UPDATE`: o ativo atual vira `retired` e o novo vira `active`. Voltar atrás é a mesma operação no sentido inverso.
- O perfil em si não tem custo de IA. O custo aparece nos testes de um perfil novo (cerca de US$ 1,70 por rodada pela Batch API) e no modelo que ele define para as correções.

**Ciclo de vida do perfil de correção**

Existe no máximo um perfil `active` por vestibular e tipo de redação; um perfil ativo nunca é editado, só substituído.

| Etapa | O que acontece | Status do perfil |
|---|---|---|
| 1. Criação inicial | No lançamento, um script insere a versão 1 de cada camada e o perfil da Fuvest – Dissertação e da Fuvest – Narrativa | `draft` |
| 2. Edição | Alterar uma camada no banco cria uma versão nova dela; a versão anterior continua intacta | — |
| 3. Montagem | Cria-se um perfil novo apontando para as versões escolhidas de cada camada e o modelo | `draft` |
| 4. Testes | O perfil roda sobre o conjunto de referência ou os testes sem professor; resultados e custo vão para `eval_runs` | `draft` |
| 5. Ativação | Se passar, numa única transação o novo vira `active` e o anterior vira `retired` | `active` |
| 6. Uso | Ao iniciar cada correção, o worker lê o perfil ativo do tipo da redação e usa só as versões dele | `active` |
| 7. Registro | A correção grava `grading_profile_id` e a versão da proposta | — |
| 8. Volta atrás | Se um perfil novo der problema, reativa-se o anterior (`retired` → `active`) sem refazer testes | `retired` / `active` |

A proposta não faz parte do perfil: tem versão própria, porque muda a cada proposta nova e vale para qualquer perfil do mesmo vestibular e categoria. Redações que já estão em correção no momento de uma ativação terminam com o perfil com que começaram.

**Regras comuns**

- Saída sempre estruturada: a chamada define uma ferramenta com JSON Schema e exige seu uso; texto livre é rejeitado.
- O conteúdo do aluno vai delimitado como dado (`<redacao>…</redacao>`), com instrução explícita de ignorar qualquer ordem escrita nele. Defesa contra prompt injection.
- Partes fixas (instruções, grade, enunciado e coletânea) vêm primeiro e usam prompt caching; o texto do aluno vem por último.
- Chamadas em streaming, com timeout de 10 min por chamada (`docs/08-ajustes-correcao.md`, item 1). Erros 429, 5xx, overloaded, conexão perdida e timeout são retentáveis; 400 e resposta inválida após 2 tentativas são definitivos.
- `max_tokens` em configuração, como teto de segurança e não como meta: `Ai__Grading__MaxTokens` (32.000) e `Ai__Transcription__MaxTokens` (8.000). Resposta cortada (`stop_reason = "max_tokens"`) nunca é completada: a nova chamada usa o dobro, até o máximo do modelo; cortada de novo, `AI_OUTPUT_TRUNCATED`, a redação falha e o crédito volta. Toda nova chamada respeita o limite rígido de gasto.
- A resposta bruta é guardada (`raw_response`) para auditoria e para o feedback do aluno.

**Transcrição**

- Entrada: a foto (URL assinada de 5 min ou base64) e o número de linhas da folha do vestibular.
- Instrução: copiar exatamente o que está escrito, linha por linha, sem corrigir ortografia, acentuação ou pontuação; marcar cada palavra que não dá para ler; informar se há nome, assinatura ou outra identificação na folha.
- Saída: `{ lines: [{number, text, illegible: [{start, end}]}], identification_found: bool, readable: bool }`.
- Validação: números de linha dentro do limite da folha; trechos ilegíveis dentro do texto da linha. `readable = false` ou mais de 10 ilegíveis → PHOTO_UNREADABLE.

**Correção**

- Entrada: grade da versão (critérios, pesos, descritores por nível, hipóteses de nota zero), a proposta (blocos em ordem, com o tipo definido no cadastro), as abordagens esperadas, os trechos copiados da coletânea e a transcrição final com o número de cada linha (com as palavras digitadas pelo aluno inseridas no lugar).
- Instruções: avaliar como a banca; verificar nota zero primeiro; para cada critério dar nível, justificativa e linhas; orientações sempre como afirmações do que corrigir, nunca perguntas; não reescrever o texto do aluno nos critérios 1 a 3; no critério 4, agrupar desvios pela regra e indicar a forma correta.
- Saída:

```json
{
  "zero": {"applies": false, "reason": null, "explanation": null},
  "criteria": [
    {"position": 3, "level": 2, "justification": "...", "lines": [11, 12, 13],
     "guidance": "Troque o parágrafo das linhas 11 a 13 por ..."}
  ],
  "marks": [{"criterion": 4, "kind": "issue", "line": 14, "start": 0, "end": 8,
             "note": "Não há crase antes de verbo", "rule": "CRASE_VERBO", "fix": "A partir"}],
  "strengths": "...",
  "main_issue": {"text": "...", "criterion": 3},
  "next_steps": [{"text": "...", "criterion": 3}, {"text": "...", "criterion": 4}]
}
```

`fix` é a forma correta do trecho marcado e só vem nos desvios de escrita (critério 4); nos demais é nulo. Em `main_issue` e em cada próximo passo, `criterion` é a posição do critério a que o texto se refere: é com ele que o backend calcula quantos pontos aquele passo pode render. Com nota zero, `criterion` é nulo.

**Validação no backend** (falha → nova tentativa; repetida → GRADING_FAILED)

1. JSON válido contra o schema. Aspas que o modelo escreve já com a barra de escape (`\"assim\"`) são gravadas sem a barra em todos os campos de texto; `raw_response` guarda a resposta como veio.
2. Exatamente um item por critério da versão; `level` inteiro de 1 a `max_level` da versão. Sem nota zero, `main_issue.criterion` é um critério da grade que não está no nível máximo (a não ser que todos estejam); o `criterion` de um próximo passo é um critério da grade ou nulo.
3. Linhas e posições citadas existem na transcrição.
4. Nenhuma orientação, próximo passo ou problema principal termina com ponto de interrogação nem traz "?" fora de um trecho entre aspas.
5. No máximo 3 próximos passos.
6. Se `zero.applies`, `reason` é uma das hipóteses da grade e a nota gravada é 0.
7. A transcrição indicou identificação na folha e a correção não aplicou nota zero → inconsistência, nova tentativa.
8. A verificação automática achou 30% ou mais das palavras da redação copiadas da coletânea e a correção não aplicou nota zero → inconsistência, nova tentativa. Os limites (8 palavras em sequência, 30%) ficam em configuração (`CollectionCopy__MinWords`, `CollectionCopy__ZeroShare`); ver `docs/07-propostas-de-redacao.md`, A6.

A nota é sempre calculada pelo backend: soma de `level × weight`; o modelo nunca envia a nota.

**Qualidade**

- Conjunto de referência: redações já avaliadas por professor, corrigidas a cada mudança de prompt, modelo ou grade. A mudança só entra se a diferença de nível por critério ficar dentro do limite combinado.
- As avaliações "Não ajudou" e seus comentários entram numa fila de revisão semanal.

**Custo por correção**

O custo de IA por correção fica em centavos de real e é controlado por um teto de **US$ 0,15** por redação (transcrição + correção + novas tentativas), cerca de 5% do preço da correção avulsa.

Estimativa com preços de tabela da API (out/2026) e estas premissas: foto recortada na área da folha (até 4.784 tokens de imagem), transcrição de 1.500 tokens de saída, correção com 7.000 tokens fixos em cache (instruções, grade, enunciado, coletânea) + 1.200 da redação e 2.500 de saída. Câmbio de referência: R$ 5,50 por US$.

| Combinação (transcrição / correção) | Custo estimado por redação | Em reais |
|---|---|---|
| Haiku 5.5 / Haiku 5.5 | US$ 0,003 | R$ 0,02 |
| Haiku 5.5 / Sonnet 5.5 | US$ 0,030 | R$ 0,17 |
| Sonnet 5.5 / Sonnet 5.5 | US$ 0,055 | R$ 0,30 |
| Sonnet 5.5 / Opus 5.5 | US$ 0,080 | R$ 0,44 |

A saída custa 5 vezes a entrada e é a maior parte da conta; o texto fixo lido do cache custa de 5% a 10% do preço normal de entrada ([preços](https://platform.claude.com/docs/en/about-claude/pricing), [imagens](https://platform.claude.com/docs/en/build-with-claude/vision)).

Regras para manter qualidade dentro do teto:

1. **Modelo por tarefa.** Transcrição e correção são configuradas separadamente. A transcrição testa primeiro o modelo mais barato; a correção usa o modelo que passar no conjunto de referência. A escolha final sai dos números do conjunto de referência, não de suposição.
2. **Cache na ordem certa.** Instruções e grade (iguais para todos) → proposta e abordagens esperadas (iguais por versão de proposta) → redação (única). Redações da mesma proposta em sequência reaproveitam o cache.
3. **Saída curta e estruturada.** Justificativa de até 60 palavras por critério, até 12 marcas na redação, 3 próximos passos. O `max_tokens` é só teto de segurança (ver a seção 8): resposta cortada custa a chamada inteira e nunca é aproveitada.
4. **Imagem enxuta.** Recortar a área da folha e reduzir para o menor tamanho que mantenha a leitura, medido no conjunto de referência. A imagem vai só para a transcrição; a correção recebe apenas texto.
5. **Teto aplicado no código.** O custo de cada chamada é calculado pelo `usage` da resposta e gravado. O teto de US$ 0,15 é a referência; a redação nunca é bloqueada enquanto o gasto ficar até o limite rígido de US$ 0,33 (`Ai__HardCapUsd`). Passar do teto de referência só gera alerta. Antes de uma nova tentativa, o worker confere se ela cabe no limite rígido; se não couber, a redação falha com crédito devolvido e alerta. Alerta também quando o custo médio diário passar de 60% do teto.
6. **Batch API como opção futura.** Dá 50% de desconto, mas pode levar até 24 h (em geral menos de 1 h). Fica fora do fluxo normal; serve para rodar o conjunto de referência e reprocessamentos.

## 9. Pagamentos e créditos

O Mercado Pago processa os pagamentos; o RedigiAI nunca recebe nem guarda dados de cartão (o cartão é tokenizado no navegador pelo SDK do Mercado Pago). Crédito só é concedido depois que o backend confirma o pagamento consultando a API do Mercado Pago, nunca pelo retorno do navegador.

**Plano semanal (cartão, recorrente)**

1. `POST /v1/checkout/plan` cria uma assinatura (preapproval) de R$ 39,90 mensais e devolve a URL de checkout.
2. O webhook de assinatura autorizada cria `subscriptions` com `status = active`. A primeira concessão de 3 créditos é imediata; as seguintes vêm toda segunda-feira.
3. Cada cobrança mensal aprovada mantém a assinatura ativa e atualiza `next_billing_at`. Cobrança recusada → `paused`: sem novas concessões até regularizar; os créditos já concedidos seguem até o dia 1º.
4. Cancelamento pelo aluno → `cancelled` ao fim do período pago.

**Correção avulsa (Pix ou cartão)**

1. `POST /v1/checkout/single` cria o pagamento de R$ 16 × quantidade. Pix devolve QR code e copia-e-cola, com validade de 30 min.
2. Pagamento aprovado → cria um bucket `single` sem vencimento com a quantidade comprada.
3. A tela de pagamento consulta o status a cada 3 s enquanto aberta, para liberar o envio sem recarregar.

**Webhooks**

- Valida o cabeçalho `x-signature` com o segredo do Mercado Pago; inválido → 401 e nada é gravado.
- Grava em `webhook_events` com `external_id` único (reentrega não duplica) e responde 200 em seguida; o processamento é o job `process_payment`.
- O job busca o recurso na API do Mercado Pago e age pelo status de lá, não pelo corpo do webhook.
- Estornos e chargebacks: removem do saldo os créditos ainda não usados daquele pagamento e registram no `audit_log`.

**Saldo e consumo**

- Reserva no envio: um único `UPDATE credit_buckets SET remaining = remaining - 1 WHERE id = ? AND remaining >= 1` por redação, na ordem de consumo (plano → grátis → avulso), dentro da transação que cria a redação. Dois envios simultâneos não furam o saldo.
- Devolução: lançamento `refund` no ledger para o mesmo bucket. Se o bucket já venceu (plano depois do dia 1º), o crédito volta como avulso sem vencimento.
- O saldo mostrado ao aluno é a soma de `remaining` dos buckets não vencidos, com o detalhe por origem e a data de vencimento.

## 10. E-mails transacionais

Todos os e-mails saem pelo SendGrid, por job `send_email`, com no máximo um envio por evento (`email_log` único em template + referência). Nenhum traz nota ou conteúdo da correção: só o aviso e um botão para a plataforma.

| Template | Quando | Botão leva a | Pode desligar |
|---|---|---|---|
| `correction_ready` | Correção concluída | Correção | Sim |
| `illegible_words` | Transcrição com 1 a 10 palavras ilegíveis | Tela de revisão, com o prazo | Não |
| `illegible_reminder` | 12 h sem preencher | Tela de revisão | Sim |
| `review_expired` | Prazo de 24 h vencido; redação cancelada | Painel | Não |
| `photo_unreadable` | Mais de 10 palavras ilegíveis | Nova redação | Não |
| `essay_failed` | Falha definitiva de transcrição ou correção | Painel | Não |
| `payment_approved` | Pagamento avulso ou do plano aprovado | Painel | Não |
| `subscription_payment_failed` | Cobrança do plano recusada | Gerenciar assinatura | Não |
| `inactivity_warning` | 7 dias antes da exclusão por inatividade | Entrar | Não |
| `account_deleted` | Exclusão concluída | — | Não |
| `new_themes` | Proposta nova publicada | Propostas | Sim, desligado por padrão |

- Remetente `correcoes@redigiai.com.br`, com SPF, DKIM e DMARC configurados no domínio.
- Assunto e pré-visualização trazem o título no formato "Vestibular – Tipo – Tema".
- Links levam à rota da plataforma; o login do Clerk acontece antes, se a sessão tiver expirado.

## 11. Segurança e LGPD

O público inclui adolescentes de 16 e 17 anos e as fotos podem conter caligrafia e dados pessoais, então o padrão é mínimo acesso e mínima retenção.

**Aplicação**

- Autenticação pelo Clerk (Google e link por e-mail, sem senha). O backend valida assinatura, emissor, audiência e expiração do JWT em toda requisição.
- Autorização por recurso: toda consulta filtra por `user_id`; testes automáticos cobrem acesso cruzado entre contas.
- Fotos em pasta privada do Cloudinary: upload assinado, só JPG, até 10 MB; leitura só por URL assinada de 5 min.
- Limites de requisição por usuário e por IP; o envio de redações tem limite próprio.
- Webhooks do Mercado Pago e do Clerk só aceitos com assinatura válida.
- CORS restrito a `redigiai.com.br`; HTTPS em tudo; cabeçalhos de segurança (CSP, HSTS) na Vercel.
- Prompt injection: texto do aluno delimitado como dado, saída validada (seção 8).
- Administração: o papel `admin` vale só para o e-mail da lista `Admin__Emails`, com o e-mail principal verificado no Clerk (consultado na Backend API do Clerk, não no token) e o segundo fator na sessão (`Admin__Require2fa`). Para os demais, `/v1/admin/*` responde 404. Não existe chave de API de administração. Imagens de proposta ficam em pasta pública do Cloudinary; fotos de redação continuam privadas.

**Segredos e infraestrutura**

- Chaves da API do Claude, Mercado Pago, Cloudinary, SendGrid e Clerk só em variáveis de ambiente do Railway e da Vercel; nunca no repositório nem no frontend.
- Banco do Railway sem acesso público; backups diários com retenção de 7 dias.
- Logs sem conteúdo de redação, e-mail completo ou tokens.

**LGPD**

- Cadastro com data de nascimento, recusando menores de 16 anos, e aceite dos Termos e da Política de Privacidade, que informam o envio da redação a provedor de IA no exterior.
- Retenção: redações e correções enquanto a conta existir; foto apagada 90 dias após a correção (fica a transcrição); conta inativa por 3 meses sem assinatura excluída com aviso 7 dias antes.
- Direitos do titular: exportar dados (`POST /v1/me/export`) e excluir conta (`DELETE /v1/me`), atendidos por job; exclusão apaga dados pessoais e foto, anonimiza redações e correções, e remove o usuário no Clerk.
- Registro em `audit_log` de exclusões, exportações, mudanças de crédito e de status.

## 12. Erros padronizados

Toda exceção conhecida é lançada como `AppError(code, httpStatus, userMessage, retryable)`; um middleware converte em `{"code", "message", "retryable", "traceId"}`. Exceção desconhecida vira `INTERNAL_ERROR`, com detalhes só no log. O frontend mostra `message` e decide ações pelo `code`, nunca pelo texto.

| Código | HTTP | Retentável | Mensagem ao aluno |
|---|---|---|---|
| `UNAUTHENTICATED` | 401 | Não | Entre na sua conta para continuar. |
| `AGE_BELOW_MINIMUM` | 422 | Não | A plataforma é destinada a pessoas com 16 anos ou mais. |
| `QUOTA_EXCEEDED` | 402 | Não | Você não tem correções suficientes para este envio. |
| `PROMPT_NOT_AVAILABLE` | 404 | Não | Essa proposta não está disponível. Escolha outra. |
| `ADMIN_2FA_REQUIRED` | 403 | Não | Ative a verificação em duas etapas para acessar a administração. |
| `PROMPT_NOT_DRAFT` | 409 | Não | Esta proposta já foi publicada. Crie uma nova versão para alterar. |
| `PROMPT_CHANGED` | 409 | Não | A proposta foi alterada em outro lugar. Recarregue para continuar. |
| `VALIDATION_FAILED` | 422 | Não | Há pendências na proposta. (Só nas rotas de administração; traz `details: [{ path, message }]`.) |
| `INVALID_FILE` | 422 | Não | Envie uma foto JPG da folha, com até 10 MB. |
| `ESSAY_NOT_FOUND` | 404 | Não | Não encontramos essa redação na sua conta. |
| `REVIEW_INCOMPLETE` | 422 | Não | Preencha todas as palavras para enviar. |
| `REVIEW_EXPIRED` | 409 | Não | O prazo para preencher as palavras acabou. A redação foi cancelada e a correção voltou para o seu saldo. |
| `INVALID_STATE` | 409 | Não | Essa ação não está disponível para esta redação agora. |
| `PHOTO_UNREADABLE` | — | Não | Não conseguimos ler mais de 10 palavras. Sua correção foi devolvida; envie uma foto mais nítida. |
| `TRANSCRIPTION_FAILED` | — | Sim | Não conseguimos ler a foto. Sua correção foi devolvida. |
| `GRADING_FAILED` | — | Sim | A correção falhou. Sua correção foi devolvida; envie de novo. |
| `PAYMENT_FAILED` | 402 | Sim | O pagamento não foi aprovado. Tente outro meio de pagamento. |
| `RATE_LIMITED` | 429 | Sim | Muitas tentativas seguidas. Aguarde um minuto. |
| `AI_UNAVAILABLE` | — | Sim | Uso interno da fila; o aluno não vê. |
| `INTERNAL_ERROR` | 500 | Não | Algo deu errado do nosso lado. Tente novamente em instantes. |

Códigos sem HTTP são falhas assíncronas: ficam em `essays.failure_code` e aparecem no painel e no e-mail.

## 13. Observabilidade, ambientes e deploy

Dois ambientes, `staging` e `production`, cada um com seu projeto no Railway, seu banco, suas chaves e seu domínio; `staging` usa as contas de teste do Mercado Pago e do Clerk.

**Repositório e deploy**

- Monorrepositório: `api/` (.NET, API e worker no mesmo projeto, iniciados por argumento), `web/` (React + Vite + TypeScript), `db/` (migrações).
- Backend em imagem Docker: o Railway roda dois serviços da mesma imagem, `api` e `worker`. Migrar depois para Azure Container Apps ou AWS não exige reescrever código.
- Frontend na Vercel (plano Pro, por ser uso comercial), com preview por pull request.
- Migrações versionadas (EF Core migrations) aplicadas no deploy, antes de subir a nova versão.
- CI no GitHub Actions: build, testes e checagem de tipos em todo pull request; deploy automático em `staging` a cada merge e em `production` por aprovação manual.

**Observabilidade**

- Logs estruturados em JSON com `traceId` em todas as camadas, inclusive dentro dos jobs.
- Sentry no frontend e no backend para erros.
- `GET /health` (vivo) e `GET /ready` (banco acessível) para o Railway.
- Métricas mínimas, com alerta por e-mail: jobs em `ready` há mais de 10 min, jobs `dead` por hora, taxa de `GRADING_FAILED`, tempo do envio até a correção (alvo: até 5 min), webhooks não processados.
- Custo de IA por correção: tokens de entrada, saída e cache gravados por chamada.

**Testes**

- Unitários: cálculo da nota, consumo e devolução de créditos, regras de vencimento, validação da resposta da IA.
- Integração com PostgreSQL real (Testcontainers): fila com `SKIP LOCKED`, reserva concorrente de créditos, idempotência dos webhooks.
- Conjunto de referência de redações corrigidas (seção 8) rodado antes de mudar prompt, modelo ou grade.

## 14. Plano de entrega e pontos em aberto

A entrega segue o caminho do aluno, em cinco etapas; cada uma termina com algo testável em `staging`.

1. **Base:** repositório, CI, Railway e Vercel, banco com migrações, login com Clerk, `POST /v1/me` com data de nascimento, catálogo da Fuvest carregado via banco.
2. **Envio e transcrição:** seleção vestibular → proposta, upload no Cloudinary, modal de confirmação, fila de jobs, transcrição, revisão de palavras ilegíveis com prazo.
3. **Correção:** job `grade`, validação, cálculo da nota, tela da correção, painel, e-mails pelo SendGrid, conjunto de referência com redações reais.
4. **Créditos e pagamentos:** ledger, teste grátis, avulsa por Pix e cartão, plano semanal no cartão, concessões de segunda-feira e vencimento no dia 1º, webhooks.
5. **Conta e lançamento:** perfil, exportação e exclusão, rotina de inatividade, avaliação da correção, envio em lote, página inicial, alertas e revisão de segurança.

**Pontos em aberto**

- [x] Retenção da foto: apagada 90 dias após a correção, mantendo a transcrição. Confirmado.
- [ ] Grade: editável no banco. Cada alteração gera uma nova versão; correções antigas continuam ligadas à versão com que foram feitas. A versão 1 usa os critérios do Simulado Fuvest 2027, 2ª edição (4 níveis, máximo 40).
- [ ] Conjunto de referência: quem fornece as redações já avaliadas por professor e qual diferença de nível por critério é aceitável.
- [ ] Termos de Uso e Política de Privacidade: minutas no documento de estrutura jurídica, em revisão. Decidido: idade mínima de 16 anos pela data de nascimento, sem conta de responsável; compras de 16 e 17 anos com autorização do responsável; reembolso de correções não usadas em 7 dias (CDC, art. 49).
- [ ] Taxas do Mercado Pago para Pix e cartão confirmadas na contratação.
- [ ] Modelos definidos (seção 8): Sonnet 5.5 na transcrição e Opus 5.5 na correção; testar Haiku 5.5 na transcrição com o conjunto de referência.
- [ ] Folha pautada em PDF: mesmo modelo da folha da Fuvest (30 linhas e campo de título). Decidido: só o layout, sem elementos de identidade da Fuvest (código de barras, marcas de leitura, nome).

## 15. Complementos para a implementação

Itens que decorrem das decisões jurídicas e de produto e que precisam existir no código. Eles completam as seções anteriores.

**Tabelas e campos adicionais**

| Tabela / campo | Campos | Regra |
|---|---|---|
| `users.terms_version`, `users.terms_accepted_at` | versão aceita dos Termos e da Política, data do aceite | Gravados no `POST /v1/me`. Se a versão vigente mudar, o próximo acesso pede novo aceite antes de qualquer envio. |
| `payments.guardian_authorized_at` | data da declaração | Obrigatório quando o comprador tem 16 ou 17 anos na data da compra: o checkout mostra a caixa "Meu responsável autorizou esta compra" e o backend recusa sem ela (`GUARDIAN_AUTHORIZATION_REQUIRED`, 422). |
| `access_logs` | id, user_id (nulo após exclusão), ip, user_agent, occurred_at | Marco Civil, art. 15: registro de acesso por 6 meses, mantido mesmo após a exclusão da conta. Gravado em cada login e em cada envio. Job diário apaga os registros com mais de 6 meses. |
| `refund_requests` | id, user_id, payment_id, status (`requested`, `approved`, `rejected`, `paid`), amount_cents, credits_removed, reason, created_at, decided_at | Direito de arrependimento (abaixo). |

**Direito de arrependimento (CDC, art. 49)**

- Vale em até 7 dias corridos da data do pagamento aprovado.
- Avulsa: devolve R$ 16 por crédito ainda não usado daquele pagamento. Plano: devolve o valor proporcional aos créditos do plano ainda não usados no período; a assinatura é cancelada.
- Correções já entregues ou em andamento não são devolvidas.
- No MVP, o pedido é feito em Perfil → Correções e compras → "Desistir da compra" (visível só dentro dos 7 dias), `POST /v1/payments/{id}/refund-request`. O backend calcula o valor, remove os créditos não usados no ledger (lançamento negativo, na mesma transação) e cria o reembolso pela API do Mercado Pago num job `process_refund`. Registro no `audit_log`.
- Erro `REFUND_WINDOW_EXPIRED` (409): "O prazo de 7 dias para desistir desta compra terminou."

**Folha pautada (`GET /v1/prompts/{id}/sheet.pdf`)**

- A4, 30 linhas numeradas, campo de título no topo e a margem igual à da folha da Fuvest.
- Sem código de barras, marcas de leitura, logotipo ou nome da Fuvest. Rodapé: "Folha de treino RedigiAI · não escreva seu nome".
- Gerada no backend (QuestPDF) e guardada em cache por versão de proposta: imprime no topo o título da proposta e o tipo.

**Envio em lote (sem tela desenhada ainda)**

- Na página de envio (`/propostas/:slug/enviar`), o aluno pode adicionar várias fotos; cada foto é uma redação daquela proposta.
- O modal de confirmação lista todas, mostra o custo (N correções) e o saldo depois. Se o saldo não cobre todas, o botão fica desabilitado e aparece "Comprar correções".
- Uma chamada `POST /v1/essays/batch` com todas; tudo ou nada.

**Rascunho antes do login**

- O aluno escolhe a proposta e a foto sem conta. Ao clicar "Enviar para correção" sem sessão, a foto e as escolhas ficam guardadas no navegador (IndexedDB, com fallback em memória) e o login abre. Depois de entrar, o upload e o modal de confirmação continuam de onde pararam.

**Aviso sobre IA e identificação do fornecedor**

- Antes do primeiro envio de cada conta: aviso de que a correção é feita por IA, segue os critérios públicos da Fuvest, não substitui a banca e envia o texto a provedor de IA nos EUA. O aceite fica em `users.ai_notice_accepted_at`.
- Rodapé do site e de todos os e-mails: razão social, CNPJ, endereço e e-mail de atendimento (valores em variáveis de configuração até a empresa estar aberta).
