# Registro de decisões

Decisões tomadas durante a implementação que não estavam na especificação. Formato: data · contexto · decisão.

- 11/10/2026 · Direito de arrependimento sem tela desenhada · Pedido pelo Perfil ("Desistir da compra"), cálculo e reembolso automáticos pelo backend (especificação, seção 15).
- 11/10/2026 · Envio em lote sem tela desenhada · Várias fotos do mesmo tema e tipo na tela do tema, confirmadas num único modal (especificação, seção 15).
- 11/10/2026 · Foto escolhida antes do login · Rascunho guardado no navegador e envio retomado depois do login (especificação, seção 15).
- 11/10/2026 · Repositório existente com outra estrutura · Reestruturado para o monorrepo `api/` + `web/`; o código anterior (upload temporário pelo backend) é descartado. Branch principal é `master`.
- 11/10/2026 · Padrão de arquitetura do back-end · DDD em monólito modular: `RedigiAI.Core` dividido em `RedigiAI.Domain` e `RedigiAI.Application`, com contextos delimitados Catalog, Accounts, Essays, Grading, Billing e Notifications. As regras de concorrência (UPDATE condicional) continuam valendo e ficam nos repositórios (ver `CLAUDE.md`, seção Arquitetura).
- 11/10/2026 · Folha pautada · PDF gerado no backend com QuestPDF, só com o layout de 30 linhas e campo de título (especificação, seção 15).
