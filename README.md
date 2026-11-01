# Ilha

A Dynamic Island do iPhone no Windows: uma pílula preta flutuando no topo do monitor principal que cresce,
encolhe e muda de forma para mostrar o que está acontecendo. Roda o tempo todo, sem pesar (quase nada de
processador e uns 125 MB de memória com tudo ligado) e só pega o teclado quando você clica nela.

**Versão 0.4.0:** menu principal com todas as ferramentas, botão "< Menu" e Esc, borda que aparece em fundo escuro,
hora na pílula, conta-gotas, prateleira de arquivos, nota rápida, mixer por app, indicador de gravação, limite do
Claude, mascote que entra e sai da ilha, e o ar só avisando quando muda.

## Como usar

- **Instalar:** `powershell -ExecutionPolicy Bypass -File .\ferramentas\instalar.ps1` (num terminal normal). Ela vai
  para `%LOCALAPPDATA%\Programs\Ilha`, ganha o atalho "Ilha" no Menu Iniciar e passa a abrir com o Windows. Rodar
  de novo atualiza; `-Desinstalar` tira tudo (a configuração fica).
- Depois de aberta, aparece o ícone da ilha perto do relógio.
- **Menu do ícone (botão direito):** pausar a ilha, colada na borda (estilo notch) ou flutuante, iniciar com o
  Windows, demonstração, **Configurações...** (a tela com tudo), abrir o config.json, abrir a pasta dos logs,
  procurar atualização e sair. Clique esquerdo no ícone abre e fecha a ilha.
- **Demonstração:** no menu, liga e desliga um timer de 1 minuto, uma música, o Claude Code trabalhando, o uso do
  PC, o GPT respondendo e a galeria de animações (tudo de mentira), mostra um alerta, ou "Passear por todos os
  estados" faz um tour sozinho. Pela linha de comando: `Ilha.exe --demo claude` (ou `timer`, `midia`, `uso`, `ia`,
  `galeria`), com `--expandir` para abrir o cartão.
- **Atalho global:** `Ctrl + Alt + Espaço` abre a ilha; de novo, fecha. Se outro programa já usa, a ilha pega
  `Ctrl + Shift + Espaço` e avisa no balão do ícone. Sem nada em andamento, ele abre o **menu principal**.
- **Mouse:** passar por cima dá uma prévia (cresce um pouco); clicar expande com os controles; tirar o mouse recolhe
  depois de 600 ms (`atrasoRecolherMs`), e clicar fora também.
- **Menu e voltar:** todo cartão aberto tem "< Menu" em cima. Depois de clicar na ilha, o Esc volta para o menu e, no
  menu, recolhe. A ilha só ouve o teclado depois de um clique nela (o Esc nunca é capturado no resto do Windows).
- **Arrastar arquivos:** solte arquivos na ilha para guardar na prateleira. Com a ilha escondida, segure o arquivo
  perto do topo do meio da tela que a pílula aparece para receber.

## O que ela faz

- **Some** quando não tem nada para mostrar. Na área de trabalho, fica como uma pílula pequena vazia.
- **Algo novo aparece expandindo** por uns segundos e depois vira a pílula compacta.
- **Duas coisas ao mesmo tempo:** a mais importante na pílula, a outra numa bolha ao lado.
- **Alertas** (um fone conectou, o timer acabou, o Claude terminou) abrem a ilha por alguns segundos.
- **Modo competitivo:** com um jogo da lista em foco (CS2, Rocket League, Valorant...), nada aparece, só o timer e o
  alerta dele. A detecção é só pelo nome do programa em foco, sem tocar no jogo (seguro para anticheat).
- **Não Incomodar do Windows:** com ele ligado, a ilha não abre sozinha para alertas, só para o timer acabando.

## Módulos

Cada um liga e desliga na tela de configurações (ou em `config.json` → `modulos` → id → `ligado`).

| Módulo | O que faz | Atalho |
|---|---|---|
| Mídia | o que está tocando, capa, visualizador, controles, arrastar para avançar, rodinha = volume | |
| Volume | nível ao mudar; no cartão, troca a saída de áudio e o mixer (volume e mudo de cada app) | |
| Privacidade | bolinha verde (câmera) e laranja (microfone) com quem está usando | |
| Timer | timer, cronômetro e pomodoro; digite `25m`, `1h30`, `90s` | `Ctrl + Alt + R` |
| Uso do PC | aparece com processador, placa ou memória no talo; cartão com tudo | `Ctrl + Alt + U` |
| Claude Code | aparece só trabalhando ou pedindo atenção; aviso ao terminar, aprovar ou negar permissão | |
| Mascote | o mascote do app dele morando dentro da ilha, quando você arrasta ele para lá ou escolhe no menu | |
| Conta-gotas | lupa, cor exata do pixel em hex, RGB e HSL, histórico | `Ctrl + Alt + P` |
| Prateleira | arquivos e pastas guardados na ilha para arrastar depois (só o caminho, nada é copiado) | |
| Nota rápida | uma linha com data e hora na caixa de entrada do Obsidian (`arquivo` na config) | `Ctrl + Alt + N` |
| Gravando | bolinha vermelha e tempo quando o OBS grava ou um app captura a tela | |
| Limite do Claude | uso da sessão e da semana e quando volta (pela status line do Claude Code) | |
| Calculadora | contas, unidades e câmbio ao vivo | `Ctrl + Alt + K` |
| IA | chat com GPT e Claude | `Ctrl + Alt + J` |
| Tradutor | traduz o texto selecionado em qualquer programa | `Ctrl + Alt + T` |
| Ar | o ar-condicionado pelo servidor do `ar-do-quarto`, no menu; avisa quando o ar muda | `Ctrl + Alt + A` |
| Fones | foto e bateria do fone ao conectar | |
| Print | miniatura do print, arrastável para outro programa | |
| Render | aviso quando o After Effects ou o Media Encoder termina | |
| Notificações | notificações do Windows na ilha (experimental, desligado) | |
| Atualizador | procura versão nova nas releases do GitHub | |

Os atalhos trocam na tela de configurações (`atalhosModulos` no JSON); ocupado por outro programa, a ilha avisa.

**Chaves (IA e ar):** quem digita é você, na própria ilha (ou na tela de configurações). Elas vão para o
Gerenciador de Credenciais do Windows (`Ilha/openai`, `Ilha/anthropic`, `Ilha/ar-do-quarto`) e nunca para arquivo.

**Limite do Claude:** a ilha lê o que a status line do Claude Code recebe. No `~/.claude/settings.json`, o
`statusLine.command` vira `python "<pasta da ilha>/ferramentas/statusline_uso.py"`, e a status line que você já usava
vai para `modulos.limite.statuslineAnterior` no `config.json` da ilha (o script chama ela e devolve o que ela
mostrar). Ele grava só os limites em `%USERPROFILE%\.ilha\uso-claude.json`. Sem a status line rodando, o módulo
mostra que não tem dado.

**Mascote:** o módulo é só o palco. Quem desenha é o app do mascote (outro programa), que conversa com a ilha pelo
pipe `\\.\pipe\ilha-acompanhante`. `modulos.mascote.comandoAbrir` diz como abrir o app quando você escolhe
"Mascote" no menu com ele fechado.

**Aprovar permissão do Claude Code pela ilha:** precisa do hook no `~/.claude/settings.json`, em `hooks`:

```json
"PermissionRequest": [ { "matcher": "*", "hooks": [ { "type": "command",
  "command": "python \"<pasta da ilha>\\ferramentas\\hook_permissao.py\"", "timeout": 120 } ] } ]
```

Sem a ilha aberta, ou com ela escondida (pausada, jogo competitivo, Não Incomodar, tela cheia), o hook sai na hora
e o diálogo normal do Claude Code segue. A ilha espera o clique por `esperaSegundos` (padrão 45).

## Atualização

"Procurar atualização" (ou sozinha, no máximo a cada 6 horas) olha as releases do repositório. Achou, a ilha mostra; clicou
em instalar, ela baixa, confere o SHA-256, troca os arquivos e reabre. Se a versão nova não abrir direito, volta
para a anterior sozinha. O que ela anota fica em `%USERPROFILE%\.ilha\atualizacao\` (`checagem.json`,
`estado.json`, `resultado.json`, `atualizacao.log` e a cópia da versão anterior em `anterior\`). A configuração
nunca é apagada.

## Configuração

`%USERPROFILE%\.ilha\config.json` (menu do ícone, "Configurações..." ou "Abrir o config.json"). Salvou, vale na hora. Se tiver erro de
digitação, a ilha avisa e continua com a anterior.

| Chave | O que faz |
|---|---|
| `colada` | `false` (padrão) flutua; `true` cola na borda de cima, estilo notch |
| `distanciaDoTopo` | distância da borda de cima no modo flutuante (padrão 10) |
| `tamanho` | escala da ilha inteira (1 = normal, de 0,5 a 3) |
| `deslocamentoX`, `deslocamentoY` | empurra a ilha para o lado ou para baixo |
| `sombra` | liga ou desliga a sombra |
| `atalho` | `mods` (1 Alt, 2 Ctrl, 4 Shift, 8 Win, somados) e `tecla` (código da tecla; 32 = espaço) |
| `jogosCompetitivos` | lista de `.exe` do modo competitivo |
| `prioridades` | ordem entre as atividades (alerta sempre passa por cima) |
| `dividir` | mostrar a segunda atividade na bolha |
| `ociosaNaAreaDeTrabalho` | a pílula vazia na área de trabalho |
| `respeitarNaoPerturbe` | obedecer o Não Incomodar e a sessão de Foco |
| `alertaSegundos` | quanto tempo um alerta fica aberto |
| `atrasoRecolherMs` | quanto esperar depois que o mouse sai antes de recolher (padrão 600) |
| `bordaAdaptativa` | a borda clara que aparece com fundo escuro (padrão ligada) |
| `relogio` | a hora no meio da pílula compacta (padrão ligada) |
| `mola` | `amortecimento` (abaixo de 1 passa do ponto e volta) e `periodoMs` (velocidade) |
| `desenharNaGpu` | desenhar texto pela placa de vídeo (gasta uns 110 MB a mais; vale ao reabrir) |
| `pausada` | ilha pausada |
| `iniciarComWindows` | abrir junto com o Windows (só vale na ilha instalada) |

## Imagens e animações

As logos do Claude e do GPT, os vídeos das animações delas e as fotos dos fones (para a animação de "fone
conectou") são de terceiros e **não estão neste repositório**. A ilha funciona sem elas (o espaço da imagem fica
vazio). As animações saem dos vídeos em `videos\` pelo `python ferramentas\preparar_logos.py`, que também desenha
as bolinhas; a origem de cada foto de fone está em `src/Ilha/imagens/fones/FONTES.md`.

## Para quem mexe no código

Compilar sempre pelo `ferramentas\compilar.ps1` (ele confere se o Smart App Control aceitou o programa). Os
autotestes de todos os módulos rodam sem janela: `Ilha.exe --testar` (ou `--testar <id>`). `Ilha.exe --teste-mouse`
mexe no cursor de verdade para reproduzir os casos de hover (só com ninguém usando o PC). Como escrever um módulo:
`src/Ilha/Modulos/LEIA.md`. Publicar uma versão: `ferramentas\publicar-versao.ps1` (gera o pacote com o SHA-256 e
cria a release; confere que a versão do `.csproj` bate com o topo do `CHANGELOG.md`). Detalhes, decisões e medições
no `CLAUDE.md`.
