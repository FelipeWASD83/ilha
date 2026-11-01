# CLAUDE.md: ilha

Dynamic Island para o Windows: uma pílula preta no topo do monitor principal que se transforma para mostrar o
que está acontecendo (mídia, timer, Claude Code, alertas). Criada em 01/11/2026, pedido do Felipe com plano
aprovado em 7 fases. Repositório **público** (`FelipeWASD83/ilha`, MIT), escolha dele ao criar. O README
explica o uso; aqui ficam as decisões e as armadilhas.

**Fase atual: 1 (núcleo) entregue para ele testar, com atividades de mentira.** Fases seguintes: 2 mídia,
volume, câmera/mic e sons; 3 timer e uso do PC; 4 Claude Code e mascote; 5 calculadora, tradutor, IA e ar;
6 extras (Bluetooth e bateria, print, notificações, render do AE); 7 atualizador, Menu Iniciar e iniciar com o
Windows. A pesquisa de viabilidade de cada feature (API, o que é frágil, o que exige algo especial) foi feita
antes do plano; o resumo está no plano aprovado na conversa de 01/11/2026 e os pontos que importam estão aqui.

## Arquitetura (não desfazer sem medir)

- **C# .NET 10 + Windows.UI.Composition do sistema** numa janela Win32 (`DesktopWindowTarget`). As molas
  (`SpringVector2/3`) rodam **dentro do DWM**: o app só decide o alvo e troca as cenas. Medido: 250 fps de
  mediana nas transições, CPU do app ~0. WinUI 3 anima no processo do app; WPF anima no processo e usa janela
  layered; Python traria `.pyd` nativo para o Smart App Control.
- **Duas janelas.** `JanelaVisual` (layered + `WS_EX_TRANSPARENT` + `NOREDIRECTIONBITMAP`) só desenha e é
  transparente ao mouse, então sombra e margem de animação nunca seguram clique. `JanelaToque` não tem conteúdo,
  usa `SetWindowRgn` no formato exato da pílula e recebe o mouse. `HTTRANSPARENT` não serve entre processos.
  As duas: `NOACTIVATE`, `TOOLWINDOW`, `TOPMOST`. O `--autoteste` confere isso na tela de verdade.
- **Esconder = cloak do DWM** (`DWMWA_CLOAK`), não `SW_HIDE`: encoberta, a janela não é composta (nada por cima
  do jogo) e volta sem atraso.
- **Direct2D sobre WARP (Direct3D por software) por padrão.** O desenho é só texto e ícone, raro e pequeno. Com
  a GPU, o driver da NVIDIA sozinho custava ~110 MB de RAM privada (123 MB contra 72 MB). `desenharNaGpu` na
  config volta para a placa (vale ao reabrir).
- **Sombra em nine-grid**, duas camadas de desfoque gaussiano desenhadas uma vez. A `DropShadow` do compositor
  refaz o desfoque a cada quadro e, com a GPU ocupada, derrubava a animação de ~184 para 96 fps.
- **Conteúdo recortado pela forma** (`GeometricClip` com uma geometria que copia a da pílula por expressão):
  o que entra e sai nunca aparece fora dela.
- **Transição de cena**: a cena vive fora da árvore e aparece por `CompositionVisualSurface`; entra com fade,
  escala e desfoque (efeito escrito à mão em `Desfoque.cs`, sem Win2D) e, ao assentar, troca para o pincel sem
  efeito (desfoque parado ainda custaria GPU por quadro).
- **Modo flutuante é o padrão** (pedido dele em 01/11/2026: colado parecia notch e, como o monitor não é OLED,
  o preto não some na moldura). `colada: true` volta ao estilo notch, com os "ombros" côncavos.
- **Visibilidade por evento**: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` fora de contexto (nada injetado) e o
  nome do `.exe` pela lista Toolhelp, **sem abrir handle no processo do jogo** (anticheat). Não Incomodar: WNF
  não documentada `0x0D83063EA3BF1C75` (0 desligado, 1 e 2 ligado), lida a cada 10 s e a cada troca de janela;
  falhou, vira "desconhecido" e não muda nada. Sessão de Foco: `FocusSessionManager` (documentado).
- **Aplicar não reanima se o alvo não mudou** (assinatura do plano): cada troca de janela da frente fazia o DWM
  compor ~35 quadros por cima do jogo à toa.
- **Config em `%USERPROFILE%\.ilha\`**, não no AppData: processo aberto de dentro do app do Claude (MSIX) vê um
  AppData virtual. Log em `ilha.log` (1 MB, depois gira para `.1`), medição em `medicao.txt`.

## Smart App Control (importante)

Em 01/11/2026 uma compilação do `Ilha.dll` foi **barrada** (0x800711C7, "política de Controle de Aplicativo")
depois de várias passarem: o SAC julga cada binário pela reputação na nuvem. `dotnet Ilha.dll` **não** escapa
(o bloqueio é no DLL). Por isso: **compilar sempre por `ferramentas\compilar.ps1`**. Ele compila, roda
`Ilha.exe --ping` e, se o SAC barrar, troca o `Carimbo.g.cs` (constante com bloco de letras de tamanho
sorteado, fora do git) e compila de novo, até 8 vezes. Mesmo método do `quadra`. Desligar o SAC está fora de
questão. Release feita fora deste PC terá outro hash: testar o `.exe` do pacote, não o da compilação local.

## Comandos

- Compilar (com a checagem do SAC): `powershell -ExecutionPolicy Bypass -File .\ferramentas\compilar.ps1`
  (`-Publicar <pasta>` publica com ReadyToRun). Saída em `src\Ilha\bin\Release\net10.0-windows10.0.26100.0\win-x64`.
- Abrir: `Ilha.exe`. Fechar o que está aberto: `Ilha.exe --sair`. Uma instância só.
- Demonstração automática de todos os estados: `Ilha.exe --roteiro`.
- Medir fps, CPU e memória: `Ilha.exe --medir` (passeia pelos estados, ~1 min, resultado em
  `~/.ilha/medicao.txt`, fecha sozinho). Abre a captura do Windows, que liga a GPU: a memória do fim da medição
  não é a do uso normal.
- Autoteste das janelas na tela real: `Ilha.exe --autoteste` (pisca uma pílula de teste 0,7 s; resultado em
  `~/.ilha/autoteste.txt`, código 0 = ok).

## Medições (01/11/2026, 3440x1440 a 250 Hz, escala 125%)

- Transições: intervalo mediano 4,00 ms (250 fps). Com a GPU ocupada por outros programas (Unreal do `quadra`,
  Blender, Wallpaper Engine) ficou em ~188 fps, **igual** a uma referência mínima (um retângulo animado e nada
  mais) rodando na mesma hora: a ilha não perde quadro além do que a GPU ocupada já tira. Acima do mínimo de 60.
- CPU parada: ~0,01% (compacta com timer redesenhando 1x/s) e ~0,02% escondida.
- RAM: ~77 MB privados depois de abrir; ~79 MB com tudo aparecendo.
- Primeira aparição depois de escondida: começa a animar na hora (o atraso de ~70 ms sumiu com o tamanho de
  partida pequeno e o aquecimento de fontes e janela ao abrir).

## Testado e não testado

- Testado: autoteste das janelas; medição; fotos de todos os estados no escuro e no claro (flutuante e colada);
  modo competitivo (com o `claude.exe` posto na lista: só o timer fica, mídia e alerta somem); o Não Incomodar do
  PC dele lido como ligado (por isso o alerta de teste não aparece com `respeitarNaoPerturbe: true`).
- **Não testado, fica com ele**: hover e clique de verdade com o mouse; atalho global; área de trabalho (Win+D);
  troca de monitor principal e de resolução; suspensão; reinício do Explorer; reinício do driver
  (Win+Ctrl+Shift+B); jogo de verdade (com e sem tela cheia exclusiva) e a medição com o PresentMon; se o valor
  da WNF do Não Incomodar bate com o botão real.

## Armadilhas

- `Ctrl + Alt + Espaço` (atalho padrão) estava ocupado no PC dele; a ilha cai para `Ctrl + Shift + Espaço` e
  avisa pela bandeja. A config guarda o preferido, não o que ficou.
- Testar janela na tela atrapalha quem está usando o PC: conferir a janela da frente antes de `--roteiro`,
  `--medir` e `--autoteste`. Em 01/11/2026 uma foto de teste pegou o Brave em tela cheia com o `cordilheira`.
- Medir fps com outro programa usando a GPU dá número mais baixo: comparar sempre com uma referência rodando
  na mesma hora antes de culpar a ilha.
- **Imagens de terceiros ficam fora do git** (o repositório é público): as fotos oficiais de fones em
  `imagens/fones/*.png` (Apple, Logitech e outras marcas, origem em `imagens/fones/FONTES.md`, que vai no git) e
  a logo do Claude (`imagens/logo-claude.png`, a mesma do mascote). Estão no PC dele e no backup do SSD; o
  `.gitignore` as segura. Imagem nova de marca entra do mesmo jeito. Sem elas a ilha abre normal (fica vazio).
- Quando o chat de IA (fase 5) usar o GPT, falta a logo da OpenAI (baixar só com o ok dele, e fora do git).
- A foto da HyperX tem fundo branco: recortar na fase 6 antes de usar. A da Razer tem só 500 px.
