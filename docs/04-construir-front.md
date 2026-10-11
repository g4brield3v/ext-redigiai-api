# Construção do front-end completo, idêntico às telas aprovadas

O front-end é construído **inteiro antes do back-end estar pronto**, com dados simulados. Depois cada tela é ligada à API real, etapa por etapa. Assim o visual fica fechado cedo e não depende do servidor.

## Referências

- `docs/telas/html/*.html`: telas aprovadas. Abra no navegador e leia o markup. É a referência de estrutura, medidas, espaçamentos, cores e textos.
- `docs/telas/png/*-desktop.png` (1440 px) e `*-celular.png` (390 px): como cada tela tem que ficar. **Olhe as imagens antes de construir cada tela.**
- `docs/02-frontend.md`: comportamento, estados, rotas e regras.

Se o HTML e a imagem divergirem, vale a imagem. Se a imagem e o `02-frontend.md` divergirem em comportamento, vale o `02-frontend.md`.

## Ordem

1. **Base visual:** tokens (cores, fontes, espaçamentos) como variáveis CSS; componentes `Button`, `Card`, `Chip`, `Field`, `Modal`, `Nav`, `Footer`, `Logo`, `EssaySheet`, `CriterionCard`, `ScoreSummary`, `StepBar`. Uma página `/_componentes` (só em desenvolvimento) mostrando todos.
2. **Dados simulados:** MSW (Mock Service Worker) respondendo a todos os endpoints da seção 6 da especificação, com os mesmos dados das telas (Ana, "Algoritmos e o debate público", nota 23/30, 2 palavras ilegíveis nas linhas 6 e 12 etc.). Os mocks usam os tipos gerados do OpenAPI; enquanto o OpenAPI não existir, crie os tipos em `src/api/mock-types.ts` e apague quando o cliente gerado chegar.
3. **Todas as telas**, nesta ordem: 01 Início, 02 Temas, 03 Tema e envio, 03b Modal de envio, 04 Login, 05 Câmera, 06 Revisão, 06b Modal de palavras, 08 Painel, 09 Correção, 10 Perfil, Checkout. Uma tela por vez; só passe para a próxima depois da comparação do passo 4.
4. **Comparação visual de cada tela:** um teste Playwright abre a rota com os mocks em 1440 × 900 e 390 × 844, tira captura da página inteira e você compara com o PNG correspondente em `docs/telas/png/`. Abra as duas imagens e liste as diferenças (posição, tamanho, cor, fonte, texto, quebra de linha). Corrija até não sobrar diferença visível. Guarde as capturas aprovadas como snapshot (`toHaveScreenshot`) para detectar regressões.
5. **Comportamento:** estados de carregando, vazio e erro; modais; botões desabilitados; polling; navegação entre telas pelos mesmos links do HTML.
6. **Ligação com a API real:** feita nas etapas 1 a 5 do plano, trocando o mock de cada endpoint pela API.

## Regras de fidelidade

- Copie textos letra por letra, inclusive pontuação e travessões ("Fuvest – Dissertação – …").
- Use os mesmos valores de CSS do HTML de referência (cores, raios, sombras, tamanhos de fonte, alturas de linha). Não arredonde nem "melhore".
- Na folha (`EssaySheet`), o texto fica sentado na pauta, cada comentário da margem fica na mesma linha da marca e os números das linhas ficam na coluna da margem vermelha.
- No celular, tabelas largas (Correções no painel, compras no perfil) viram lista de cartões, sem rolagem horizontal da página.
- Nenhuma tela pode ter rolagem horizontal em 360 px.

## Pedido para iniciar

> Leia o CLAUDE.md, o docs/02-frontend.md e este arquivo. Olhe todas as imagens em docs/telas/png/. Depois apresente o plano para construir o front-end completo com dados simulados, tela por tela, com a comparação visual de cada uma. Não comece a programar antes da minha aprovação.
