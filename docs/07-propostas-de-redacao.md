# RedigiAI — Propostas de redação: modelo, área de administração e guia de uso

> **Ajustes decididos na implementação (12/10/2026), registrados em `docs/decisoes.md`:** **em 20/10/2026, administrador passou a ser a flag `users.is_admin` no banco (A5 abaixo), sem lista de e-mails nem segundo fator;** `<copia_da_coletanea>` vem depois de `<redacao>`, fora do trecho em cache; com 30% ou mais copiado, qualquer hipótese de nota zero é aceita; a folha pautada (`sheet.pdf`) entra numa branch própria; `GET /v1/prompts/{slug}` aceita `?version=`. **Em 15/10/2026:** as categorias saem de `GET /v1/admin/exams/{slug}/essay-types`; os itens do card vão em `data.cardItems` (e não em `data.items`, que é das Instruções). No resto, vale o que está abaixo.

Este documento **substitui** o modelo de temas da especificação (`themes`, `theme_texts`, `theme_essay_types`, `theme_grading_notes`). Ele tem duas partes:

- **Parte A**, para o Claude CLI: modelo de dados, regras, API, telas e mudanças no fluxo.
- **Parte B**, para quem cadastra: como montar e publicar uma proposta pela área de administração, com um exemplo completo.

Referência usada no desenho: proposta de redação da Fuvest ("o papel da ciência no mundo contemporâneo"). Ela tem cinco textos de coletânea (uma tirinha, três textos em prosa e um poema em três colunas), depois o comando ("redija uma **dissertação em prosa**") e por fim as instruções (mínimo de 20 linhas, máximo de 30, dar título).

---

# Parte A · Especificação

## A1. Decisões

1. **O tipo é definido pelo administrador, não pelo aluno.** Cada proposta tem um único tipo, cadastrado na seção **Instruções**. O aluno não escolhe tipo em nenhum momento: lê a proposta, escreve e envia.
2. **Tipo definido pela categoria de correção.** No cartão Instruções, o administrador escolhe a **categoria de correção** numa lista (`essayType`). A primeira categoria é **"Dissertação em prosa"**; "Narrativa" continua disponível.
   - O nome da categoria é o tipo mostrado ao aluno (primeiro item das Instruções), enviado à IA e usado no título da redação.
   - A categoria escolhe o perfil de correção: continua **um perfil ativo por vestibular e categoria**.
   - Não há campo de texto livre para o tipo.
   - As categorias ficam na tabela `essay_types` (`slug`, `name`). A carga inicial cria `dissertacao-em-prosa` ("Dissertação em prosa") e `narrativa` ("Narrativa"), e os perfis atuais de dissertação passam a apontar para `dissertacao-em-prosa`. Categorias novas entram pelo banco, junto com o perfil de correção delas.
3. **Sem campos de linhas e título.** Mínimo e máximo de linhas e título obrigatório saem do cadastro. Essas regras ficam escritas nos itens das Instruções, que a IA lê. O backend continua informando à IA quantas linhas a redação tem e se a primeira linha parece um título; o limite físico é o da folha do vestibular (30 na Fuvest).
4. **Conteúdo em blocos ordenados.** Tudo o que aparece na página da proposta é um bloco com `position` explícita, definida pelo administrador. A tela e a IA usam a mesma ordem.
5. **Cada texto da coletânea pode ser texto, verso ou imagem.** "Texto 1", "Texto 2", "Texto 3"… são todos o mesmo tipo de bloco (`collection_item`), e o administrador escolhe o formato de cada um: prosa, verso em colunas ou imagem.
6. **Tudo o que é título é editável.** Inclui o título da proposta (o tema), o rótulo de cada texto ("Texto 1"), o título da seção de instruções e os títulos de seção livres. Num rascunho, todos podem ser alterados a qualquer momento.
7. **Imagens têm descrição para a IA.** A correção recebe só texto, então toda imagem leva `aiDescription`, com a descrição completa e as falas transcritas.
8. **Versão imutável.** Proposta publicada não é editada. Para mudar, cria-se uma nova versão em rascunho; cada redação guarda a versão que o aluno usou.
9. **Cadastro pela área de administração do site.** O administrador entra com o login normal e acessa `/admin/propostas`. Importar e exportar o JSON completo da proposta continua disponível.

## A2. Modelo de dados

Chaves UUID v7, datas `timestamptz`.

| Tabela | Campos | Regras |
|---|---|---|
| `prompts` | id, exam_id, slug, version, status (`draft`, `published`, `archived`), title, summary, source_label, essay_type_id, created_at, updated_at, published_at, created_by, published_by | `title` é o tema, mostrado na página, na lista e no título da redação. `essay_type_id` é escolhido no cartão Instruções (categoria de correção). `slug` + `version` é único; só uma versão `published` por slug. |
| `prompt_blocks` | id, prompt_id, position, kind, data (jsonb) | `position` inteiro ≥ 0, único por proposta. Conteúdo em `data` (seção A3). |
| `prompt_grading_notes` | id, prompt_id, expected_focus, tangent_signals, off_topic_signals, key_data (jsonb), extra | **Abordagens esperadas do tema** (nome na tela). Uma por proposta; obrigatórias para publicar. Não confundir com o perfil de correção (seção A6). |
| `assets` | (existente) + `scope` (`essay_photo`, `prompt_image`) | Imagens de proposta ficam numa pasta pública do Cloudinary, com URL estável. Fotos de redação continuam privadas. |
| `users.is_admin` | `false` (padrão) ou `true` | Ver seção A5. `created_by` e `published_by` apontam para `users.id`. |

Em `essays`: sai `theme_id`; entra `prompt_id` (a versão exata); `essay_type_id` é **copiado da proposta** no envio, nunca vem do navegador.

Título da redação: `"{exam.name} – {essayType.name} – {prompt.title}"`, por exemplo "Fuvest – Dissertação em prosa – O papel da ciência no mundo contemporâneo".

## A3. Tipos de bloco (`kind` e `data`)

Os textos aceitam um markdown mínimo: `**negrito**`, `*itálico*` e quebra de linha. Nada de HTML; o front renderiza só essas marcações.

| `kind` | Campos de `data` | Exibição | Para a IA |
|---|---|---|---|
| `collection_item` | `label` ("Texto 1"), `format` (`prose`, `verse` ou `image`), `source`, e conforme o formato: **prose** → `body`; **verse** → `columns` (lista de colunas, cada uma uma lista de versos); **image** → `assetId`, `alt`, `aiDescription` | Rótulo em negrito, conteúdo e fonte em itálico alinhada à direita. Verso: colunas lado a lado no computador e em sequência no celular. Imagem: com `alt` | Rótulo, conteúdo (na imagem, o `aiDescription`) e fonte |
| `command` | `body` | Enunciado em destaque | Enviado |
| `instructions` | `title` (padrão "Instruções"), `items` (lista de textos) | Primeiro item, gerado automaticamente: "**Tipo de redação:** {essayType.name}". Depois os itens cadastrados | Enviado, com o tipo |
| `card` | `title?`, `items`: exatamente 3 itens, cada um `{ format: "text" \| "image", body? , assetId?, alt?, aiDescription?, caption? }` | Um card com os 3 itens empilhados na vertical, na ordem cadastrada; texto com o markdown permitido, imagem com `alt`, legenda opcional abaixo de cada item | Título e itens em ordem; imagem pelo `aiDescription` |
| `heading` | `text` | Título de seção livre | Ignorado |
| `paragraph` | `body` | Parágrafo livre | Enviado |
| `divider` | — | Linha separadora | Ignorado |

Uma proposta tem **exatamente um** bloco `instructions`. O botão "Escrever esta proposta" aparece no fim da página e na coluna lateral.

## A4. Validação

Num rascunho, o salvamento aceita conteúdo incompleto e devolve a lista de pendências. A publicação exige tudo abaixo:

1. `title` com 3 a 120 caracteres; `slug` em minúsculas, números e hífen.
2. `essayType` existente no vestibular, com perfil de correção `active`.
3. Ao menos um `collection_item`, exatamente um `instructions` e ao menos um `command`.
4. `position` única por proposta. Use 10, 20, 30… para poder inserir no meio depois.
5. `collection_item`: `label` e `source` obrigatórios; `label` único na proposta.
   - `prose` exige `body`;
   - `verse` exige de 1 a 4 colunas com ao menos um verso;
   - `image` exige `assetId` de um asset `prompt_image`, `alt` com até 300 caracteres e `aiDescription` com 40 a 3.000 caracteres.
6. `card`: exatamente 3 itens. Item de texto exige `body`; item de imagem exige `assetId`, `alt` e `aiDescription` (mesmas regras do item 5).
7. Abordagens esperadas com `expected_focus` e `tangent_signals` preenchidos.
8. Limites: até 40 blocos; cada texto com até 6.000 caracteres; proposta inteira com até 30.000 caracteres.
9. Markdown só com as marcações permitidas. Qualquer `<` ou `>` é recusado.

## A5. Acesso de administrador, API e telas

**Quem é administrador**
- Flag `users.is_admin` (padrão `false`), ligada só direto no banco, pelo `clerk_user_id` conferido no painel do Clerk (o e-mail não é único): `BEGIN; UPDATE users SET is_admin = true WHERE clerk_user_id = '<user_… do painel do Clerk>'; -- confira "UPDATE 1"; senão ROLLBACK; COMMIT;`. Nem o e-mail, nem o token, nem o navegador fazem alguém administrador.
- Toda rota `/v1/admin/*` exige token válido e a flag, lida no banco a cada requisição: desligar a flag corta o acesso na requisição seguinte. Para os demais usuários: `404`.
- O perfil (`GET /v1/me`) traz `isAdmin`; o front usa só para mostrar o menu e a área `/admin`.

**Rotas** (prefixo `/v1/admin`; 120 requisições por minuto por usuário; toda escrita vai para o `audit_log`; validação → `422 VALIDATION_FAILED` com `details: [{ path, message }]`)

| Método e rota | Faz |
|---|---|
| `POST /v1/admin/assets` | `multipart/form-data` com `file` (JPG, PNG ou WebP, até 5 MB). Pasta pública de propostas. Devolve `{ assetId, url, width, height }`. |
| `POST /v1/admin/prompts` | Cria um rascunho, vazio ou a partir do JSON completo (importação, seção B4). Devolve `201` com `{ id, slug, version, status, blocks[{ id, position }], issues[] }`. Exige `Idempotency-Key`. |
| `GET /v1/admin/prompts?status=&exam=&q=` | Lista com status, versão, tipo, número de textos e data de atualização. |
| `GET /v1/admin/prompts/{id}` | Proposta no formato de cadastro (o mesmo JSON da importação). Serve para "Exportar JSON". |
| `PUT /v1/admin/prompts/{id}` | Substitui o rascunho inteiro. Só em `draft` (publicado → `409 PROMPT_NOT_DRAFT`). Com `expectedUpdatedAt`: se houve outro salvamento antes → `409 PROMPT_CHANGED`. |
| `PATCH /v1/admin/prompts/{id}/order` | Reordena: `{ "blocks": [{ "id": "...", "position": 10 }, ...] }`. Só em `draft`. |
| `POST /v1/admin/prompts/{id}/validate` | Validação de publicação sem publicar: `{ valid, issues[] }`. |
| `POST /v1/admin/prompts/{id}/publish` | Valida e publica. A versão publicada anterior do mesmo `slug` vira `archived`. |
| `POST /v1/admin/prompts/{id}/new-version` | Cria um rascunho (versão + 1) a partir da versão publicada. |
| `POST /v1/admin/prompts/{id}/archive` | Tira a proposta da lista dos alunos. Não apaga nada. |
| `DELETE /v1/admin/prompts/{id}` | Apaga **só rascunho** que nunca foi publicado. |

**Telas** (referências `11-admin-propostas` e `11b-admin-editar-proposta`)
- `/admin/*` só renderiza para quem tem `isAdmin`; para os demais, a página de "não encontrado". O link "Admin" aparece no menu do avatar só para administradores.
- **`/admin/propostas`:** filtros por status, busca, tabela (proposta, tipo, versão, status, atualizada, ação), "Importar JSON" e "Nova proposta".
- **`/admin/propostas/:id`:**
  - **Barra fixa:** voltar, status e versão, "Salvo às hh:mm", "Exportar JSON", "Validar" e "Publicar" (com confirmação).
  - **Formulário, de cima para baixo:**
    1. **Pendências:** lista clicável, que leva até o campo.
    2. **Título e dados gerais:** título da proposta (editável, em destaque), vestibular, endereço, resumo.
    3. **Conteúdo da página:** um cartão por bloco, com **Posição** (número editável), alça para arrastar, ↑ ↓, duplicar e remover.
       - Cada **Texto da coletânea** tem rótulo editável e um seletor de formato **Texto / Verso / Imagem**. Os campos mudam conforme o formato.
       - "Adicionar bloco" oferece: Texto da coletânea, Card com 3 blocos, Comando, Título de seção, Parágrafo e Separador.
       - Mudar a ordem renumera os blocos de 10 em 10.
    4. **Instruções:** um cartão fixo, que também tem posição, com:
       - título da seção (editável);
       - **Categoria de correção**, numa lista (`GET /v1/admin/exams/{slug}/essay-types`): o nome dela é o tipo de redação;
       - os itens, um por linha, incluindo as regras de linhas e de título.
    5. **Abordagens esperadas do tema** (só a IA vê), com uma linha explicando a diferença para o perfil de correção.
  - **Prévia fixa à direita:** mostra a proposta exatamente como o aluno vê, alternando entre computador e celular. Atualiza enquanto se digita.
  - **Salvamento automático:** 2 segundos depois da última alteração, com `expectedUpdatedAt`. Se falhar, os dados ficam no navegador até salvar.
  - Proposta publicada ou arquivada abre só para leitura, com "Criar nova versão".

## A6. Como a proposta entra na correção

**Perfil de correção × abordagens esperadas.** São coisas diferentes e as duas entram na chamada:
- O **perfil de correção** diz **como corrigir qualquer redação** daquele vestibular e categoria: grade, manual do corretor, regras de escrita, instruções da IA e modelo. É o mesmo para todas as propostas de dissertação da Fuvest e muda raramente.
- As **abordagens esperadas** dizem **o que é estar no tema desta proposta**: o recorte esperado, o que conta como tangenciar, o que é fuga e as ideias de cada texto da coletânea. Mudam a cada proposta. A Fuvest divulga isso com esse mesmo nome no simulado ("abordagens esperadas").

Sem as abordagens, a IA decidiria tangenciamento e fuga ao tema só pela leitura do enunciado, e é aí que a nota mais erra.


A parte da proposta na chamada de correção (seção 8 da especificação), em cache por versão de proposta:

```
<proposta titulo="{title}" tipo="{essayType.name}" linhas_da_folha="{exam.sheetLines}">
  {blocos na ordem de position, convertidos em texto:
   collection_item prose → "[Texto 2] corpo — Fonte"
   collection_item verse → "[Texto 3 · verso] coluna 1 / coluna 2 / … — Fonte"
   collection_item image → "[Texto 1 · imagem] aiDescription — Fonte"
   card → "[Card: título] 1) texto ou aiDescription · 2) … · 3) …"
   command → "[Comando] corpo"
   instructions → "[Instruções] Tipo de redação: {essayType.name}; item; item"}
</proposta>
<abordagens_esperadas>
  Recorte esperado: {expected_focus}
  Sinais de tangenciamento: {tangent_signals}
  Sinais de fuga ao tema: {off_topic_signals}
  Dados-chave da coletânea: {key_data}
</abordagens_esperadas>
<copia_da_coletanea>{trechos encontrados pela verificação automática, ou "nenhum"}</copia_da_coletanea>
```

O critério 2 (modalidade) é avaliado contra o nome da categoria e o comando.

**Verificação automática de cópia da coletânea** (antes da chamada):
- O backend normaliza o texto do aluno e o de cada `collection_item` em prosa ou verso (minúsculas, sem acento nem pontuação) e procura sequências de **8 ou mais palavras iguais**.
- Os trechos encontrados vão para a IA em `<copia_da_coletanea>` e ficam gravados na correção.
- Se os trechos copiados somarem 30% ou mais das palavras da redação e a IA não aplicar `collection_copy`, a resposta é inconsistente e a chamada é refeita. Os limites ficam em configuração.

**Linhas e título:** o backend informa à IA quantas linhas a redação tem (`<redacao linhas="N">`). A IA compara com o que os itens das Instruções pedem (mínimo de linhas, título) e considera isso no critério 2. Linhas abaixo do mínimo não zeram a redação, salvo se a grade da versão disser o contrário.

## A7. Mudanças no fluxo do aluno

- `GET /v1/exams/{slug}/prompts?q=` substitui `/themes`. Devolve as propostas publicadas com `title`, `summary`, `essayType` e a contagem de textos.
- `GET /v1/prompts/{slug}` devolve a versão publicada, com os blocos em ordem, `essayType`, linhas e as URLs públicas das imagens. Não devolve as abordagens esperadas.
- `POST /v1/essays/batch`: cada item passa a ser `{ promptId, assetId, parentEssayId? }`. Sem tipo; o backend usa o da proposta. Proposta não publicada → `PROMPT_NOT_AVAILABLE` (404).
- Rotas do front:
  - `/propostas`: lista;
  - `/propostas/:slug`: página da proposta, com o botão "Escrever esta proposta";
  - `/propostas/:slug/enviar`: envio, que mostra o tipo só para leitura e não tem escolha de tipo.
- Folha pautada: `GET /v1/prompts/{id}/sheet.pdf` imprime no topo o título da proposta e o tipo.
- Reescrita: usa a mesma versão de proposta da redação original.

## A8. Testes obrigatórios

- Validação: cada regra da seção A4 com um caso que falha e um que passa, inclusive os três formatos de `collection_item`.
- Ordem: blocos saem por `position`; `PATCH /order` com posição repetida → 422; reordenar depois de publicar → 409.
- Versão: publicar a v2 arquiva a v1; uma redação feita na v1 continua mostrando a v1.
- Envio: `POST /v1/essays/batch` com um campo de tipo é recusado.
- Perfil: só um perfil `active` por vestibular e categoria; a redação usa o perfil da categoria da proposta.
- `card`: com 2 ou 4 itens → 422; item de imagem sem `aiDescription` → pendência.
- Segurança:
  - rota de admin sem token → 401; com token de aluno → 404;
  - conta nova nunca é admin, qualquer que seja o e-mail; flag desligada → perde o acesso na requisição seguinte;
  - o navegador não liga a flag (`PATCH`/`POST /v1/me` ignoram `isAdmin`);
  - `PUT` com `expectedUpdatedAt` antigo → 409;
  - `/admin` para aluno → página de não encontrado.
- Cópia: redação com um parágrafo copiado gera trechos encontrados; com 30% ou mais copiado e sem nota zero da IA, a chamada é refeita.

---

# Parte B · Guia de cadastro

## B1. Antes de começar

1. Entre no site com a sua conta; ela precisa ter a flag de administrador ligada no banco (peça a quem cuida do banco).
2. No menu do avatar, clique em **Admin** (ou acesse `/admin/propostas`).
3. Cadastre primeiro em **staging**, confira e só depois em produção. Para levar uma proposta de um ambiente para o outro, use "Exportar JSON" num e "Importar JSON" no outro; as imagens precisam ser enviadas de novo.

## B2. Passo a passo na tela

1. **Nova proposta.** Escreva o **título** (o tema) e preencha vestibular e resumo. O endereço é sugerido a partir do título.
2. **Textos da coletânea, na ordem da prova.** Clique em "+ Texto da coletânea" para cada texto e escolha o formato:
   - **Texto:** cole o texto em prosa;
   - **Verso:** um verso por linha em cada coluna (até 4 colunas);
   - **Imagem:** envie a tirinha, a charge ou o gráfico e preencha o texto alternativo e a **descrição para a IA**, com as falas transcritas.

   Em todos, informe o rótulo ("Texto 1") e a fonte.
3. **Comando.** Adicione o enunciado. Use **B** para deixar o tema em negrito.
4. **Card com 3 blocos (opcional).** Para conteúdo que deve aparecer agrupado, como um quadro de apoio, use "+ Card com 3 blocos". Escolha para cada bloco se ele é texto ou imagem; os três aparecem empilhados, na ordem.
5. **Instruções.** No cartão de Instruções:
   - escolha a **categoria de correção** ("Dissertação em prosa" ou "Narrativa"). O nome dela é o tipo que o aluno vê como primeiro item das instruções;
   - escreva os **itens**, um por linha, incluindo as regras de linhas e de título ("Escreva, no mínimo, 20 linhas…", "Dê um título a sua redação").
6. **Ordem.** Arraste pela alça, use ↑ ↓ ou digite a posição. A prévia à direita mostra o resultado na hora.
7. **Abordagens esperadas do tema.** Escreva o recorte esperado do tema e o que conta como tangenciá-lo. Se quiser, inclua os sinais de fuga e os dados-chave de cada texto.
8. **Validar e publicar.** "Validar" lista o que falta; "Publicar" coloca a proposta na lista dos alunos.
9. **Corrigir depois de publicada.** "Criar nova versão" abre um rascunho com o conteúdo atual. A versão antiga continua valendo para as redações já feitas.

**Importar JSON:** cadastra uma proposta inteira de uma vez, no formato da seção B4. Ela entra como rascunho e abre no editor para conferência.

## B3. Referência dos campos

| Campo | Obrigatório | Exemplo | O que é |
|---|---|---|---|
| `exam` | sim | `"fuvest"` | Vestibular |
| `slug` | sim | `"papel-da-ciencia"` | Endereço da proposta no site; não muda entre versões |
| `title` | sim | `"O papel da ciência no mundo contemporâneo"` | Título da proposta (tema) |
| `summary` | não | `"Coletânea com tirinha, poema e três textos."` | Linha de apoio no cartão da lista |
| `sourceLabel` | não | `"Fuvest (prova anterior)"` | Origem, para controle interno |
| `essayType` | sim | `"dissertacao-em-prosa"` | Categoria de correção: `dissertacao-em-prosa` ("Dissertação em prosa") ou `narrativa` |
| `blocks[].position` | sim | `10` | Ordem na tela, de cima para baixo |
| `blocks[].kind` | sim | `"collection_item"` | Tipo do bloco (seção A3) |
| `gradingNotes.expectedFocus` | sim | ver exemplo | Recorte que a banca espera |
| `gradingNotes.tangentSignals` | sim | ver exemplo | O que caracteriza tangenciar o tema |
| `gradingNotes.offTopicSignals` | não | ver exemplo | O que caracteriza fuga total |
| `gradingNotes.keyData` | não | lista | Ideias de cada texto que valem como bom uso da coletânea |

## B4. Formato JSON (importar e exportar), com a proposta anexada

Os corpos dos textos estão como `[cole aqui…]`: copie-os da prova, palavra por palavra.

```json
{
  "exam": "fuvest",
  "slug": "papel-da-ciencia",
  "title": "O papel da ciência no mundo contemporâneo",
  "summary": "Coletânea com tirinha, poema e três textos em prosa.",
  "sourceLabel": "Fuvest (prova anterior)",
  "essayType": "dissertacao-em-prosa",
  "blocks": [
    { "position": 10, "kind": "collection_item",
      "data": {
        "label": "Texto 1", "format": "image",
        "assetId": "<assetId da tirinha>",
        "alt": "[Descreva a tirinha em uma frase, para leitores de tela.]",
        "aiDescription": "[Descreva os três quadros e transcreva as falas, em ordem.]",
        "source": "Luis Fernando Verissimo, *As cobras: Antologia Definitiva*."
      } },
    { "position": 20, "kind": "collection_item",
      "data": {
        "label": "Texto 2", "format": "prose",
        "body": "[cole aqui o texto 2]",
        "source": "Oscar Sala, *O papel da ciência na sociedade*. 1974. Disponível em http://www.revistas.usp.br/revhistoria. Adaptado."
      } },
    { "position": 30, "kind": "collection_item",
      "data": {
        "label": "Texto 3", "format": "verse",
        "columns": [
          ["[verso 1 da 1ª coluna]", "[verso 2 da 1ª coluna]"],
          ["[verso 1 da 2ª coluna]", "[verso 2 da 2ª coluna]"],
          ["[verso 1 da 3ª coluna]", "[verso 2 da 3ª coluna]"]
        ],
        "source": "Gilberto Gil, *Quanta*. 1997."
      } },
    { "position": 40, "kind": "collection_item",
      "data": { "label": "Texto 4", "format": "prose", "body": "[cole aqui o texto 4]", "source": "Carl Sagan, 1996." } },
    { "position": 50, "kind": "collection_item",
      "data": {
        "label": "Texto 5", "format": "prose",
        "body": "[cole aqui o texto 5]",
        "source": "Alicia Kowaltowski, *Usando a ciência para negar a ciência*. 2019. Disponível em https://www.nexojornal.com.br/. Adaptado."
      } },
    { "position": 60, "kind": "command",
      "data": { "body": "Considerando as ideias apresentadas nos textos e também outras informações que julgar pertinentes, redija uma dissertação em prosa, na qual você exponha seu ponto de vista sobre o tema: **o papel da ciência no mundo contemporâneo**." } },
    { "position": 70, "kind": "instructions",
      "data": { "title": "Instruções", "items": [
        "A dissertação deve ser redigida de acordo com a norma-padrão da língua portuguesa.",
        "Escreva, no mínimo, 20 linhas, com letra legível e não ultrapasse o espaço de 30 linhas da folha de redação.",
        "Dê um título a sua redação."
      ] } }
  ],
  "gradingNotes": {
    "expectedFocus": "Ponto de vista sobre a função da ciência hoje: o paradoxo entre a dependência da sociedade em relação à ciência e a desconfiança ou ignorância sobre ela (textos 4 e 5), a ciência como parte da cultura (texto 2) e a aproximação entre ciência e arte (texto 3).",
    "tangentSignals": "Fala de tecnologia ou de inovação em geral sem discutir o papel da ciência; trata só da história da ciência; lista avanços científicos sem posicionamento sobre o mundo contemporâneo.",
    "offTopicSignals": "Não menciona ciência nem conhecimento científico.",
    "keyData": [
      "Texto 2: a ciência só floresce numa cultura que valoriza a curiosidade; ciência e tecnologia são associadas.",
      "Texto 4: a sociedade depende da ciência, mas quase ninguém a compreende.",
      "Texto 5: pessoas usam a ciência para negar a ciência.",
      "Texto 3: arte e ciência como formas irmãs de conhecimento."
    ]
  }
}
```
