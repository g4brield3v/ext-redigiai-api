# Registro de decisões

Decisões tomadas durante a implementação que não estavam na especificação. Formato: data · contexto · decisão.

- 11/10/2026 · Direito de arrependimento sem tela desenhada · Pedido pelo Perfil ("Desistir da compra"), cálculo e reembolso automáticos pelo backend (especificação, seção 15).
- 11/10/2026 · Envio em lote sem tela desenhada · Várias fotos do mesmo tema e tipo na tela do tema, confirmadas num único modal (especificação, seção 15).
- 11/10/2026 · Foto escolhida antes do login · Rascunho guardado no navegador e envio retomado depois do login (especificação, seção 15).
- 11/10/2026 · Repositório existente com outra estrutura · Reestruturado para o monorrepo `api/` + `web/`; o código anterior (upload temporário pelo backend) é descartado. Branch principal é `master`.
- 11/10/2026 · Padrão de arquitetura do back-end · DDD em monólito modular: `RedigiAI.Core` dividido em `RedigiAI.Domain` e `RedigiAI.Application`, com contextos delimitados Catalog, Accounts, Essays, Grading, Billing e Notifications. As regras de concorrência (UPDATE condicional) continuam valendo e ficam nos repositórios (ver `CLAUDE.md`, seção Arquitetura).
- 11/10/2026 · Códigos de erro fora da tabela da seção 12 · Criados `VALIDATION_ERROR` (422, dados inválidos ou ilegíveis), `PROFILE_NOT_FOUND` (404 em `GET/PATCH /v1/me` antes do primeiro acesso; é o sinal para o front chamar `POST /v1/me`) e `NOT_FOUND` (404 para rota, vestibular ou grade inexistente). **A confirmar.**
- 11/10/2026 · Token de sessão do Clerk não traz e-mail nem `aud` por padrão · O token precisa ser personalizado no Clerk com as claims `email` e `name`. A "audiência" é conferida pela claim `azp` contra `Cors__AllowedOrigins`; `aud` só é exigido se `Clerk__Audience` estiver definido.
- 11/10/2026 · Preferências de e-mail sem coluna no modelo de dados · Colunas `notify_correction_ready`, `notify_illegible_reminder` e `notify_new_themes` em `users`.
- 11/10/2026 · "Registro de acesso em cada login" sem evento de login no backend · Um registro por sessão do Clerk (claim `sid`), gravado no primeiro `GET /v1/me` da sessão; coluna `access_logs.session_id`.
- 11/10/2026 · Limite de requisições para quem não está logado · 30/min por aluno (especificação) e 120/min por IP para visitantes, porque vários alunos podem compartilhar o IP da escola. Configurável em `RateLimit__*`.
- 11/10/2026 · Aniversário em 29/02 · Em ano não bissexto, a idade é completada em 01/03.
- 11/10/2026 · Data de nascimento na tela de login · O campo aparece como no design, mas só é obrigatório no primeiro acesso: quem já tem cadastro entra sem digitar de novo; se faltar, é pedido depois do login. **A confirmar.**
- 11/10/2026 · Textos que não estão nas telas aprovadas · Mensagens de validação, "link enviado", busca sem resultado, sem conexão e página não encontrada foram escritos na implementação. **A revisar.**
- 11/10/2026 · Configuração local · O back-end carrega o `.env` da raiz em desenvolvimento, e o Vite lê o mesmo arquivo (`envDir`).
- 11/10/2026 · Revisão de segurança da etapa 1 · Token sem `azp` passa a ser recusado; a data de nascimento informada antes do login fica só na aba (`sessionStorage`); segredo de webhook com menos de 16 bytes é recusado. Ficaram para depois: deduplicação de webhooks por `svix-id` (com `webhook_events`, etapa 4), CSP restrita aos domínios finais e actions fixadas por SHA (antes de entrarem segredos de deploy), e a conferência do `X-Forwarded-For` do Railway em staging.
- 11/10/2026 · Forma dos endpoints · Controllers em vez de API mínima: `CatalogController`, `AccountController`, `ClerkWebhookController` e `HealthController`. Todo endpoint novo entra num controller e aparece no Swagger (`/swagger`, só em desenvolvimento; pacote Swashbuckle.AspNetCore.SwaggerUI sobre o documento OpenAPI nativo).
- 11/10/2026 · Folha pautada · PDF gerado no backend com QuestPDF, só com o layout de 30 linhas e campo de título (especificação, seção 15).
