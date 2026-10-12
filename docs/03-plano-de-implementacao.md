# RedigiAI — Plano de implementação

Ordem de construção para o Claude Code. Cada etapa termina com algo testável em `staging` e com todos os testes passando. **Não comece uma etapa sem que a anterior esteja concluída e aprovada pela pessoa responsável.**

Como trabalhar em cada etapa:
1. Ler a etapa inteira e as seções da especificação indicadas.
2. Propor um plano curto (arquivos, migrações, endpoints, telas, testes) e esperar aprovação.
3. Implementar em commits pequenos, cada um com testes.
4. Rodar todos os testes, o lint e a checagem de tipos.
5. Rodar o subagente `security-reviewer` sobre a etapa.
6. Conferir cada critério de aceite e relatar o que foi feito, o que ficou de fora e qualquer decisão tomada.

---

## Etapa 1 · Base

Especificação: seções 2, 5, 6 (Conta e Catálogo), 11, 12, 13 e 15.

**Back-end**
- Solução .NET com a estrutura do `CLAUDE.md`; um executável que sobe como `api` ou `worker` conforme o argumento.
- Dockerfile multi-stage; `docker-compose.yml` com PostgreSQL, api, worker e web para desenvolvimento.
- EF Core com Npgsql, UUID v7, `timestamptz`; migrações das tabelas de catálogo, `users`, `audit_log`, `access_logs`.
- Validação do JWT do Clerk (JWKS, emissor, audiência, expiração); webhook do Clerk com verificação Svix.
- `AppError` + middleware de erros no formato `{code, message, retryable, traceId}`; logs JSON com `traceId`; `/health` e `/ready`.
- `GET/POST/PATCH /v1/me` com data de nascimento, recusa de menores de 16, aceite de termos e concessão única do crédito grátis (o ledger completo entra na etapa 4; aqui basta o bucket `trial` com o lançamento no ledger).
- Endpoints de catálogo; `openapi.json` publicado em build.
- Script de seed: Fuvest, grade v1 do Simulado Fuvest 2027, 2ª edição (4 critérios, pesos 3, 2, 3, 2, níveis de 1 a `max_level` = 4, máximo 40, hipóteses de nota zero), tipos Dissertação e Narrativa, 1 tema de exemplo.
- Limites de requisição, CORS restrito, cabeçalhos de segurança.

**Front-end**
- Projeto Vite + React + TS estrito; tokens do design system; componentes base (botão, cartão, campo, navegação, chip, modal); cliente gerado do OpenAPI.
- Login com Clerk no nosso visual, com data de nascimento no primeiro acesso.
- Páginas Início (`/`), Temas (`/temas`) e Proposta e coletânea (`/temas/:themeId`) com dados reais do catálogo.

**Infra**
- GitHub Actions: build, testes, lint e tipos em todo pull request; CodeQL, gitleaks e auditoria de dependências.
- Railway (staging) com api, worker e PostgreSQL; Vercel com preview por pull request.

**Aceite**
- Cadastro com Google e com link por e-mail; menor de 16 recusado com `AGE_BELOW_MINIMUM`.
- Um aluno recebe exatamente 1 crédito grátis, mesmo com `POST /v1/me` repetido ou simultâneo.
- Teste automático de acesso cruzado: aluno A recebe 404 ao pedir recurso do aluno B.
- Início e Temas iguais ao design em 360 px e 1440 px.

## Etapa 2 · Envio e transcrição

Especificação: seções 3 (Seleção, Envio, Transcrição e revisão), 4, 6 (Envio), 7, 8 (Transcrição) e 15 (lote e rascunho).

- Tabelas `essays`, `assets`, `transcriptions`, `illegible_words`, `jobs`, `email_log`.
- Fila no PostgreSQL: `FOR UPDATE SKIP LOCKED`, `LISTEN/NOTIFY` + polling de 5 s, `locked_until`, backoff 30 s / 2 min / 8 min / 30 min, `dead`, concorrência configurável por tipo.
- Upload assinado no Cloudinary (pasta privada, JPG, 10 MB).
- `POST /v1/essays/batch` com `Idempotency-Key`, reserva de crédito na mesma transação.
- Job `transcribe` com a API do Claude: ferramenta com JSON Schema, validação, regras de 0 / 1–10 / mais de 10 ilegíveis, custo por chamada gravado.
- `PUT /v1/essays/{id}/illegible`; jobs agendados de lembrete (12 h) e cancelamento (24 h) com devolução.
- Job `send_email` com SendGrid e os templates `illegible_words`, `illegible_reminder`, `review_expired`, `photo_unreadable`, `essay_failed`.
- Telas: Proposta e coletânea, Tipo e envio, modal de confirmação, câmera no celular, rascunho antes do login, Palavras ilegíveis com modal, painel com status e polling.
- Folha pautada em PDF.

**Aceite**
- Dois envios simultâneos com 1 crédito: só um passa.
- Matar o worker no meio de um job: o job é retomado depois de `locked_until`.
- Transcrição nunca corrige o texto (teste com redação cheia de erros propositais).
- Prazo vencido cancela e devolve o crédito exatamente uma vez.

## Etapa 3 · Correção

Especificação: seções 3 (Correção e nota), 8 inteira, 10 e 12.

- Tabelas da base de conhecimento: `prompt_templates`, `rubric_guidelines`, `writing_rules`, `anchor_essays`, `anchor_scores`, `theme_grading_notes`, `grading_profiles` (índice único parcial do ativo), `reference_essays`, `eval_runs`; `corrections`, `correction_criteria`, `correction_marks`, `correction_feedback`.
- Job `grade`: lê o perfil ativo, monta as camadas na ordem de cache, chama o modelo, valida as 7 regras, calcula a nota (soma de nível × peso), consome o crédito, `completed`, e-mail `correction_ready`.
- Controle de custo: teto de referência US$ 0,15 (alerta) e limite rígido US$ 0,225 (falha com devolução).
- Defesa contra prompt injection: redação delimitada como dado.
- Comando de linha `eval` que roda um perfil sobre o conjunto de referência (Batch API) e grava em `eval_runs`; comando `activate-profile` e `rollback-profile`.
- Testes sem professor: defeitos plantados, consistência (3 execuções) e transcrição contra digitação.
- Telas: Correção completa (`EssaySheet` com marcas), nota zero, avaliação da correção, "Seu foco agora" e evolução no painel.

**Aceite**
- Resposta da IA com pergunta, critério faltando, linha inexistente ou nota enviada pelo modelo é rejeitada e tentada de novo.
- Mesma redação corrigida 3 vezes varia no máximo 1 nível por critério.
- Nome escrito na folha resulta em nota zero por identificação.
- Correção igual ao design, com comentário da margem na mesma linha da marca.

## Etapa 3b · Propostas de redação e área de administração

Documento: `docs/07-propostas-de-redacao.md` inteiro. Substitui o modelo de temas das etapas 1 a 3. A tela de correção v2 precisa estar concluída antes.

- Acesso de administrador: `users.role`, `Admin__Emails`, `Admin__Require2fa`; e-mail principal verificado consultado na Backend API do Clerk no `POST /v1/me` e a cada login; segundo fator pela claim `fva`; limite de 120 requisições por minuto e `audit_log` em toda escrita.
- Modelo: `prompts`, `prompt_blocks`, `prompt_grading_notes`, `assets.scope`; migração que converte os temas existentes em propostas.
- Validação de rascunho (lista de pendências) e de publicação (seção A4).
- API de administração: as 11 rotas da seção A5, com `Idempotency-Key` na criação e `expectedUpdatedAt` no salvamento.
- Fluxo do aluno: `GET /v1/exams/{slug}/prompts`, `GET /v1/prompts/{slug}`, `POST /v1/essays/batch` com `promptId`; saem as rotas `/themes`.
- Correção: `<proposta>` e `<abordagens_esperadas>` na chamada, verificação automática de cópia da coletânea e contagem de linhas (seção A6).
- Telas, no repositório do front: `/propostas`, `/propostas/:slug`, `/propostas/:slug/enviar`, `/admin/propostas` e `/admin/propostas/:id` (referências 02b, 03, 11 e 11b).
- Fica para uma branch própria, depois desta: a folha pautada (`GET /v1/prompts/{id}/sheet.pdf`).

**Aceite**
- Todos os testes da seção A8.
- Rota de administração sem token responde 401; com token de aluno, 404; sem segundo fator, 403.
- O administrador de teste do ambiente local não existe fora de `Development`.
- Publicar a versão 2 arquiva a versão 1, e uma redação feita na versão 1 continua mostrando a versão 1.
- A redação sempre recebe o tipo da proposta; um tipo enviado pelo navegador não vale.
- Proposta, envio e as duas telas de administração iguais ao design em 360 px e 1440 px.

## Etapa 4 · Créditos e pagamentos

Especificação: seções 3 (Créditos e cobrança), 9 e 15 (arrependimento e autorização do responsável).

- `credit_ledger` só com inserção; `credit_buckets` como cache; ordem de consumo plano → grátis → avulso; devolução para bucket vencido vira avulso.
- Mercado Pago: avulsa por Pix e cartão; plano por preapproval no cartão; webhooks com `x-signature`, `webhook_events` e job `process_payment` que consulta a API.
- Jobs agendados: segunda 00h (3 créditos) e dia 1º 00h (vencimento), em horário de Brasília.
- Estorno e chargeback; direito de arrependimento com `refund_requests` e job `process_refund`.
- Tela de checkout; compras e assinatura no Perfil; e-mails de pagamento.

**Aceite**
- Webhook repetido não concede crédito duas vezes.
- Crédito só é concedido após consulta à API do Mercado Pago.
- Saldo nunca fica negativo sob concorrência (teste com 20 envios paralelos).
- Testes das bordas de data: segunda 00h, dia 1º 00h, troca de mês no meio da semana, fuso de Brasília.

## Etapa 5 · Conta e lançamento

Especificação: seções 3 (Conta e dados), 11 e 13.

- Perfil completo; exportação (`export_user`) e exclusão (`delete_user`) com anonimização e remoção no Clerk. O comentário da avaliação da correção (`correction_feedback.comment`) é texto livre e pode trazer dado pessoal: a exclusão apaga o comentário (a opção escolhida fica) e a exportação inclui a avaliação.
- Rotina de inatividade (aviso 7 dias antes, exclusão em 3 meses); limpeza das fotos após 90 dias; limpeza de `access_logs` após 6 meses.
- Envio em lote na interface; reescrita com `parent_essay_id`.
- Páginas de Termos e Privacidade; rodapé com identificação da empresa; aviso sobre IA.
- Sentry, alertas e métricas da seção 13; testes ponta a ponta do caminho completo em Playwright.
- Revisão de segurança completa e checklist pré-lançamento.

**Aceite**
- Caminho completo no Playwright: chegar pela página inicial → escolher tema → enviar foto → login → confirmar → revisar palavras → ver correção → avaliar.
- Exclusão apaga dados pessoais e foto e mantém a redação anonimizada.
- Nenhum achado alto ou crítico aberto na revisão de segurança.

---

## Checklist de segurança (rodar em toda etapa)

- [ ] Toda rota autenticada filtra por `user_id` do token; teste de acesso cruzado existe.
- [ ] Nenhum segredo no código, nos logs ou no front-end.
- [ ] Entradas validadas no backend (tamanho, tipo, formato), mesmo que o front valide.
- [ ] Consultas só com parâmetros (EF Core ou Dapper parametrizado); nada de SQL concatenado.
- [ ] Webhooks recusam assinatura inválida antes de gravar qualquer coisa.
- [ ] Toda operação de crédito e status é idempotente e protegida contra concorrência.
- [ ] Texto do aluno nunca é tratado como instrução pela IA.
- [ ] Logs sem redação, e-mail completo, token ou dado de cartão.
- [ ] Dependências sem vulnerabilidade alta conhecida.
