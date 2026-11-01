# Ilha

A Dynamic Island do iPhone no Windows: uma pílula preta flutuando no topo do monitor principal que cresce,
encolhe e muda de forma para mostrar o que está acontecendo. Roda o tempo todo, sem pesar (quase nada de
processador e uns 77 MB de memória) e sem roubar o foco de nada.

**Versão 0.1.0, fase 1:** o motor da ilha pronto, testado com atividades de mentira. Mídia, volume, câmera,
timer de verdade, Claude Code, calculadora, tradutor, IA e o resto chegam nas próximas fases.

## Como usar

- Abrir: `src\Ilha\bin\Release\net10.0-windows10.0.26100.0\win-x64\Ilha.exe` (o atalho no Menu Iniciar chega na
  fase 7). Aparece um ícone de pílula perto do relógio.
- **Menu do ícone (botão direito):** pausar a ilha, colada na borda (estilo notch) ou flutuante, demonstração,
  abrir a configuração, abrir a pasta dos logs e sair. Clique esquerdo no ícone abre e fecha a ilha.
- **Demonstração:** no menu, liga e desliga um timer de 1 minuto, uma música, o Claude Code trabalhando e o uso do
  PC (tudo de mentira), mostra um alerta, ou "Passear por todos os estados" faz um tour sozinho.
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

## Configuração

`%USERPROFILE%\.ilha\config.json` (menu do ícone, "Abrir a configuração"). Salvou, vale na hora. Se tiver erro de
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

## Imagens

A logo do Claude e as fotos dos fones (para a animação de "fone conectou") são de terceiros e **não estão neste
repositório**. A ilha funciona sem elas (o espaço da imagem fica vazio). A origem de cada foto está em
`src/Ilha/imagens/fones/FONTES.md`; a logo vai em `src/Ilha/imagens/logo-claude.png`.

## Para quem mexe no código

Compilar sempre pelo `ferramentas\compilar.ps1` (ele confere se o Smart App Control aceitou o programa). Detalhes,
decisões e medições no `CLAUDE.md`.
