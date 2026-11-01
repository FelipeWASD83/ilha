# Ilha

A Dynamic Island do iPhone no Windows: uma pílula preta flutuando no topo do monitor principal que cresce,
encolhe e muda de forma para mostrar o que está acontecendo. Roda o tempo todo, sem pesar (quase nada de
processador e uns 77 MB de memória) e sem roubar o foco de nada.

**Versão 0.3.0:** todos os módulos do plano estão lá (mídia, volume, câmera e microfone, timer, uso do PC,
Claude Code, mascote, calculadora, chat com IA, tradutor, ar-condicionado, fones, print, render e notificações),
mais a tela de configurações e a atualização pela própria ilha.

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
- **Atalho global:** `Ctrl + Alt + Espaço` abre a ilha expandida; de novo, fecha. Se outro programa já usa, a ilha
  pega `Ctrl + Shift + Espaço` e avisa no balão do ícone.
- **Mouse:** passar por cima dá uma prévia (cresce um pouco); clicar expande com os controles; tirar o mouse ou
  clicar fora volta.

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
| Volume | nível ao mudar; no cartão, troca a saída de áudio | |
| Privacidade | bolinha verde (câmera) e laranja (microfone) com quem está usando | |
| Timer | timer, cronômetro e pomodoro; digite `25m`, `1h30`, `90s` | `Ctrl + Alt + R` |
| Uso do PC | aparece com processador, placa ou memória no talo; cartão com tudo | `Ctrl + Alt + U` |
| Claude Code | sessões trabalhando, aviso ao terminar, aprovar ou negar permissão | |
| Mascote | o mascote na ilha, reagindo ao Claude Code (desligado por padrão) | |
| Calculadora | contas, unidades e câmbio ao vivo | `Ctrl + Alt + K` |
| IA | chat com GPT e Claude | `Ctrl + Alt + J` |
| Tradutor | traduz o texto selecionado em qualquer programa | `Ctrl + Alt + T` |
| Ar | o ar-condicionado pelo servidor do `ar-do-quarto` | `Ctrl + Alt + A` |
| Fones | foto e bateria do fone ao conectar | |
| Print | miniatura do print, arrastável para outro programa | |
| Render | aviso quando o After Effects ou o Media Encoder termina | |
| Notificações | notificações do Windows na ilha (experimental, desligado) | |
| Atualizador | procura versão nova nas releases do GitHub | |

Os atalhos trocam na tela de configurações (`atalhosModulos` no JSON); ocupado por outro programa, a ilha avisa.

**Chaves (IA e ar):** quem digita é você, na própria ilha (ou na tela de configurações). Elas vão para o
Gerenciador de Credenciais do Windows (`Ilha/openai`, `Ilha/anthropic`, `Ilha/ar-do-quarto`) e nunca para arquivo.

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
autotestes de todos os módulos rodam sem janela: `Ilha.exe --testar` (ou `--testar <id>`). Como escrever um módulo:
`src/Ilha/Modulos/LEIA.md`. Publicar uma versão: `ferramentas\publicar-versao.ps1` (gera o pacote com o SHA-256 e
cria a release; confere que a versão do `.csproj` bate com o topo do `CHANGELOG.md`). Detalhes, decisões e medições
no `CLAUDE.md`.
