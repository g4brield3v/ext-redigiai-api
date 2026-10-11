# RedigiAI

SaaS que corrige redações manuscritas da Fuvest com IA. O aluno escolhe vestibular → tema → tipo, envia a foto da folha e recebe na plataforma a nota de cada critério, com as linhas que pesaram e o que corrigir. Domínio: redigiai.com.br. Público: estudantes a partir de 16 anos.

## Documentos (leia antes de qualquer tarefa)

| Arquivo | Conteúdo |
|---|---|
| `docs/01-especificacao-tecnica.md` | **Fonte da verdade:** regras de negócio, estados, modelo de dados, API, fila, IA, pagamentos, e-mails, segurança, erros |
| `docs/02-frontend.md` | Design system, rotas e o comportamento de cada tela |
| `docs/03-plano-de-implementacao.md` | Etapas, critérios de aceite e checklist de segurança |
| `docs/design/*.dc.html` | Telas aprovadas (referência visual: medidas, cores, textos) |

Se algo na tarefa contradiz a especificação, **pare e pergunte**. Não invente regra de negócio. Se a especificação não cobre um caso, proponha a solução mais simples, implemente só depois de aprovada e registre a decisão em `docs/decisoes.md` (data, contexto, decisão).

## Stack

- **Back-end:** .NET 10, ASP.NET Core (API mínima ou controllers), EF Core + Npgsql, PostgreSQL 16+. Um executável com dois modos: `api` e `worker`.
- **Front-end:** React + Vite + TypeScript (strict), React Router, TanStack Query, cliente gerado do `openapi.json`.
- **Serviços:** Clerk (login), Mercado Pago (pagamentos), Cloudinary (fotos privadas), SendGrid (e-mail), API do Claude (transcrição e correção), Sentry.
- **Hospedagem:** Railway (api, worker, PostgreSQL) e Vercel (web). CI no GitHub Actions.

## Estrutura do repositório

```
api/
  RedigiAI.sln
  src/RedigiAI.Domain/         agregados, entidades, objetos de valor, eventos de domínio, interfaces de repositório (sem dependência alguma)
  src/RedigiAI.Application/    casos de uso (comandos e consultas), portas para serviços externos; depende só do Domain
  src/RedigiAI.Infrastructure/ EF Core, migrações, repositórios, fila, Claude, Mercado Pago, Cloudinary, SendGrid, Clerk
  src/RedigiAI.Api/            host: endpoints, middleware, modo worker
  tests/RedigiAI.UnitTests/
  tests/RedigiAI.IntegrationTests/   Testcontainers + PostgreSQL real
  Dockerfile
web/
  src/api/        cliente gerado (não editar à mão)
  src/components/ componentes do design system
  src/features/   uma pasta por área (temas, envio, revisao, correcao, painel, perfil, checkout)
  src/routes/
  e2e/            Playwright
docs/
docker-compose.yml
```

## Arquitetura (DDD)

Monólito modular em camadas: `Api → Application → Domain` e `Infrastructure → Application/Domain`. O Domain não referencia nenhum outro projeto nem pacote de infraestrutura.

- **Contextos delimitados**, uma pasta por contexto em cada camada: `Catalog` (vestibular, tema, grade), `Accounts` (usuário, aceite, idade), `Essays` (envio, transcrição, revisão), `Grading` (correção, perfil de correção), `Billing` (créditos, pagamentos, assinatura), `Notifications` (e-mails).
- **Agregados** guardam as invariantes e expõem as transições como métodos (`Essay.MarkQueued()`); nada de setters públicos. Um agregado referencia outro só pelo id.
- **Objetos de valor** para conceitos com regra própria: `EssayTitle`, `Score`, `Level`, `BirthDate`, `Money`, `EssayStatus`.
- **Repositórios:** interface no Domain, uma por agregado; implementação no Infrastructure. Cada caso de uso é uma transação.
- **Concorrência:** o agregado decide se a transição é válida; o repositório a grava com o `UPDATE` condicional das regras 10 e 12 e devolve se a linha mudou. O DDD não substitui essas regras.
- **Eventos de domínio** viram jobs na tabela `jobs`, gravados na mesma transação (regra 11).
- **Leituras** (painel, listas) podem consultar o banco direto na camada Application/Infrastructure, sem carregar agregados.

## Comandos

```bash
docker compose up -d db                     # PostgreSQL local
dotnet build api/RedigiAI.sln
dotnet test api/RedigiAI.sln
dotnet run --project api/src/RedigiAI.Api -- api      # API em http://localhost:5080
dotnet run --project api/src/RedigiAI.Api -- worker   # worker
dotnet ef migrations add <Nome> -p api/src/RedigiAI.Infrastructure -s api/src/RedigiAI.Api
cd web && npm ci && npm run dev             # http://localhost:5173
cd web && npm run gen:api                   # regenera o cliente a partir do openapi.json
cd web && npm test && npm run typecheck && npm run lint
cd web && npx playwright test
```

Atualize esta seção sempre que um comando mudar.

## Regras inegociáveis

**Produto**
1. Ordem de escolha fixa: vestibular → tema → tipo → foto. Cada redação responde a exatamente uma proposta (tema + tipo).
2. Título sempre "Vestibular – Tipo – Tema", sem ano. É gerado pelo backend.
3. Somente foto JPG, até 10 MB, uma face. Sem envio de texto digitado.
4. Depois de confirmar o envio, o aluno não troca a foto nem cancela.
5. A transcrição copia exatamente o que está escrito, sem corrigir nada. O aluno só digita as palavras ilegíveis (1 a 10); mais de 10 → `PHOTO_UNREADABLE` e o crédito volta.
6. **A nota é sempre calculada pelo backend:** soma de nível × peso. A IA nunca envia a nota. Sem conversão de escala.
7. **Feedback é sempre afirmação, nunca pergunta.** Respostas da IA com "?" em orientação ou próximo passo são rejeitadas.
8. A correção aparece só na plataforma. E-mail tem só o aviso e um botão.
9. Idade mínima de 16 anos pela data de nascimento. Compras de 16 e 17 anos exigem a declaração de autorização do responsável.

**Dados e concorrência**
10. Toda mudança de status: `UPDATE ... SET status = @novo WHERE id = @id AND status = @esperado`. Zero linhas afetadas = outro processo já avançou; sair sem efeito.
11. Status, job seguinte e lançamento de crédito na **mesma transação**.
12. `credit_ledger` só recebe `INSERT`. Saldo debitado com `UPDATE credit_buckets SET remaining = remaining - 1 WHERE id = @id AND remaining >= 1`.
13. Todo handler de job é idempotente. Todo `POST` que cria redação ou pagamento exige `Idempotency-Key`.
14. Versões publicadas (grade, prompt, manual, regras, perfil de correção) nunca são editadas: editar = inserir versão nova.
15. Chaves primárias UUID v7; datas em `timestamptz` UTC; horário de Brasília só na exibição e nas regras de calendário (segunda 00h, dia 1º 00h).

**Segurança**
16. Toda consulta de dados do aluno filtra por `user_id` do token. Recurso de outro aluno → 404.
17. Nenhum segredo no código, em logs ou no front-end. Configuração só por variável de ambiente; `.env.example` lista as chaves sem valores.
18. Texto do aluno vai para a IA delimitado como dado (`<redacao>…</redacao>`) com instrução para ignorar ordens dentro dele.
19. Crédito de pagamento só depois de consultar a API do Mercado Pago; nunca pelo retorno do navegador nem pelo corpo do webhook.
20. Logs sem conteúdo de redação, e-mail completo, tokens ou dados de cartão.
21. Erros conhecidos como `AppError(code, httpStatus, userMessage, retryable)`; o front decide pelo `code`.

**Front-end**
22. Siga `docs/02-frontend.md` e os arquivos em `docs/design/`. Copie os textos das telas; não reescreva.
23. Nunca escreva tipos da API à mão: regenere o cliente quando o backend mudar.
24. Sem bibliotecas de componentes prontas (MUI, Chakra etc.). Sem emojis, gradientes ou ícones de "IA".

## Como trabalhar

- Siga as etapas de `docs/03-plano-de-implementacao.md`, uma por vez. Antes de codificar, apresente um plano curto e espere aprovação.
- Commits pequenos e descritivos, em português, no imperativo ("Adiciona reserva de crédito no envio"). Uma branch por etapa ou funcionalidade; nunca faça commit direto na `master`.
- Toda funcionalidade vem com testes. Regras de crédito, fila e status exigem teste de integração com PostgreSQL real, incluindo concorrência.
- Antes de dar uma tarefa por concluída: `dotnet test`, `npm test`, `npm run typecheck`, `npm run lint` e, quando houver tela, Playwright. Relate o resultado real; não diga que passou sem rodar.
- Ao fim de cada etapa, rode o subagente `security-reviewer` e corrija os achados altos e críticos.
- Use só chaves de teste (Mercado Pago sandbox, Clerk de desenvolvimento). Nunca faça deploy em produção nem rode comandos contra bancos remotos.
- Não adicione dependência nova sem dizer por quê. Prefira a biblioteca padrão.
- Código, nomes de tabelas, colunas, endpoints e identificadores em inglês; textos para o aluno em português do Brasil.
