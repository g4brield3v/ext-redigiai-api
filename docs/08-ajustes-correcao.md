# Ajustes: tamanho da resposta da IA, leitura da correção e motivo das falhas

Três problemas vistos no uso e como resolvê-los mexendo pouco no backend. Prevalece sobre os trechos correspondentes da especificação (seção 8), do `05-base-de-conhecimento.md` e do `06-tela-correcao.md`.

Referências visuais: `docs/telas/png/09-correcao-*` (modo "Trechos comentados"), `09f-redacao-nao-corrigida-*` e `08-painel-*`. Os HTML interativos estão em `docs/telas/html/`.

---

## 1. Resposta da IA cortada por tamanho

**Causa:** a chamada tem um `max_tokens` fixo e baixo, e as instruções limitam o número de marcas. Em redação longa ou com muitos desvios, a resposta para no meio (`stop_reason = "max_tokens"`), o JSON fica incompleto e a correção falha ou é refeita à toa.

**Mudanças (só no worker e nas instruções):**
1. **Sem limite artificial de saída.** Use `max_tokens` = 32.000 na correção e 8.000 na transcrição, como valores de configuração (`Ai__Grading__MaxTokens`, `Ai__Transcription__MaxTokens`). São tetos de segurança, não metas: o uso típico continua entre 3.000 e 6.000 tokens.
2. **Streaming obrigatório** nas duas chamadas (o SDK exige para saídas longas), com timeout de 10 minutos por chamada.
3. **`stop_reason` sempre conferido.** Se vier `max_tokens`:
   - é erro retentável, com código interno `AI_OUTPUT_TRUNCATED`;
   - a nova tentativa usa o dobro do `max_tokens`, até o máximo do modelo;
   - se acontecer de novo, a redação falha com `GRADING_FAILED`, a correção volta para o saldo e o motivo aparece em tela (seção 3).
   - Nunca tente "completar" um JSON cortado.
4. **Instruções da correção (nova versão do prompt de correção, perfil novo):**
   - sai "até 12 marcas": marque **todos** os trechos que afetam a nota e os principais acertos;
   - sai o limite rígido de 60 palavras por justificativa. Fica a orientação: "seja direto; uma justificativa curta e precisa é melhor que uma longa";
   - continua: no máximo 3 próximos passos. É limite pedagógico, não de tamanho.
5. **Validação:** retire as regras que recusavam resposta por número de marcas ou tamanho de texto.
6. **Custo:** respostas maiores custam mais (a saída do Opus custa US$ 20 por milhão de tokens). Valores recomendados:
   - referência de US$ 0,15 por redação: continua, mas só gera alerta;
   - limite rígido: **US$ 0,33** por redação (`Cost__HardLimitUsd`), somando transcrição, correção e novas tentativas. Antes de cada chamada, o worker confere se ela cabe no que falta do limite; se não couber, a redação falha com `GRADING_FAILED` e a correção volta para o saldo. A nova tentativa por resposta cortada também respeita esse limite;
   - alerta diário quando o custo médio passar de US$ 0,15.

**Testes:** cliente falso que devolve `stop_reason = "max_tokens"` uma vez (a segunda chamada usa o dobro e conclui) e duas vezes (falha com `GRADING_FAILED` e devolução da correção).

---

## 2. Leitura da correção: "Trechos comentados" como padrão

A redação inteira continua disponível, mas deixa de ser o centro da tela.

**Por quê:**
- O aluno aprende mais vendo cada erro **dentro do trecho em que aconteceu** e logo ao lado do que corrigir, do que procurando números numa folha inteira.
- Num celular, a folha inteira mais um painel lateral não cabem. Os trechos cabem.
- A folha da Fuvest tem no máximo 30 linhas, então o texto nunca é "grande demais". O corte que aparecia era um defeito de CSS (`white-space: nowrap` com `overflow: hidden`). **Em nenhum modo uma linha pode ser cortada**: o texto quebra linha.

**Como fica a seção "Texto comentado":**
- **Seletor no topo, à direita:** "Trechos comentados" (padrão) | "Redação inteira". A escolha fica salva no navegador por usuário (com `try/catch`; sem salvar, volta ao padrão).
- **Modo "Trechos comentados":**
  - Filtros iguais aos atuais (tipo e critério), numa linha acima da lista.
  - Um cartão por apontamento, em ordem de linha, com borda esquerda vermelha (tirou pontos) ou verde (funcionou).
  - À esquerda do cartão: as linhas marcadas, mais uma linha antes e uma depois em cinza (contexto), com número de linha. Só o trecho deste apontamento fica destacado.
  - À direita do cartão: tipo, critério, regra (critério 4), explicação e o quadro "Escreva…".
  - No celular, as duas partes do cartão ficam empilhadas.
- **Modo "Redação inteira":** é a versão atual (folha + painel de apontamentos sincronizados), com a correção do corte.
- **"Comece por aqui → Ver no texto"** abre o modo "Trechos comentados", filtrado pelo critério do problema principal, e rola até o primeiro cartão.

**Backend:** nenhuma mudança. Os trechos são montados no front a partir de `transcription.lines` e das marcas (`lineStart`, `lineEnd`, `charStart`, `charEnd`) que a API já devolve.

**Testes:**
- trecho que atravessa duas linhas aparece num cartão só;
- apontamento na linha 1 não mostra linha de contexto antes;
- linha longa quebra sem rolagem horizontal em 360 px;
- a escolha do modo persiste ao recarregar.

---

## 3. Redação não corrigida: o motivo sempre aparece

**Causa provável:** nem todo caminho de falha grava `failure_code` (o cancelamento por prazo, por exemplo), e a API não devolve o motivo de forma que a tela possa mostrar.

**Backend (pequeno):**
1. **Toda transição para `failed` ou `cancelled` grava**, na mesma transação:
   - `failure_code`;
   - `failed_at`;
   - `failure_details` (jsonb), com dados úteis para a tela, como `{ "illegibleCount": 14 }`.

   Se nenhum código se aplicar, use `INTERNAL_ERROR`; um status de falha sem código não é permitido. Garanta isso com uma restrição no banco: `status in ('failed','cancelled')` implica `failure_code` não nulo.
2. **`GET /v1/essays` e `GET /v1/essays/{id}` devolvem** `failure: { code, occurredAt, creditRefunded, details }` quando a redação falhou ou foi cancelada.
3. **`GET /v1/essays/{id}` devolve `timeline[]`**, com os eventos da redação: enviada, lida, palavras preenchidas, em correção, corrigida, falhou ou cancelada, e correção devolvida. Cada evento tem `at`, `type` e `details`. A fonte é o `audit_log` que já existe, filtrado pela redação.

**Catálogo de motivos (no front, pelo `code`; a API não manda texto):**

| Código | Status | Título na tela | Texto | Ação principal |
|---|---|---|---|---|
| `PHOTO_UNREADABLE` | Não corrigida | Não conseguimos ler a sua foto | Mais de 10 palavras ficaram ilegíveis ({illegibleCount}), então a redação não foi corrigida. A correção voltou para o seu saldo. | Enviar nova foto desta redação |
| `REVIEW_EXPIRED` | Cancelada | O prazo para preencher as palavras acabou | As palavras ilegíveis não foram preenchidas em 24 horas, então a redação foi cancelada. A correção voltou para o seu saldo. | Enviar de novo |
| `TRANSCRIPTION_FAILED` | Não corrigida | Não conseguimos processar a foto | Houve uma falha do nosso lado ao ler a foto. A correção voltou para o seu saldo. | Enviar de novo |
| `GRADING_FAILED` | Não corrigida | A correção não foi concluída | Houve uma falha do nosso lado ao corrigir a redação. A correção voltou para o seu saldo. | Enviar de novo |
| `INTERNAL_ERROR` | Não corrigida | Algo deu errado do nosso lado | A redação não foi corrigida e a correção voltou para o seu saldo. | Enviar de novo |

Os textos dizem sempre o que aconteceu, se a correção voltou e o que fazer. Nunca culpam o aluno por falha do sistema. Os outros códigos internos (`AI_OUTPUT_TRUNCATED`, `AI_UNAVAILABLE`, limite de custo) aparecem para o aluno como `GRADING_FAILED`.

**Telas:**
- **Painel (`08-painel`):** a linha da redação mostra, abaixo do título, o motivo em uma frase (em vermelho escuro) e o botão "Ver o motivo". Os status ficam "Não corrigida · crédito devolvido" ou "Cancelada · crédito devolvido".
- **`/redacoes/:id` com status `failed` ou `cancelled` (`09f-redacao-nao-corrigida`)**, de cima para baixo:
  - chip de status;
  - título da redação e data de envio;
  - cartão "O que aconteceu", com o título e o texto do catálogo e o saldo atual;
  - para `PHOTO_UNREADABLE`, a caixa "Para a próxima foto", com 3 dicas;
  - botões: ação principal, que leva ao envio da mesma proposta, e "Ver a foto enviada";
  - "Linha do tempo" com os eventos;
  - em letra pequena, o código e o id para o suporte.
- **E-mail:** os e-mails de falha já existentes passam a usar o mesmo título e texto do catálogo.

**Testes:**
- cada caminho de falha grava código, data e detalhes;
- tentar gravar `failed` sem código viola a restrição do banco;
- a API devolve `failure` e `timeline`;
- o front mostra o texto certo para cada código e cai em `INTERNAL_ERROR` para um código desconhecido.
