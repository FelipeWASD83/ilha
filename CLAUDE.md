# CLAUDE.md: ilha

Dynamic Island para o Windows: uma pílula preta no topo do monitor principal que se transforma para mostrar o
que está acontecendo (mídia, timer, Claude Code, alertas). Criada em 01/11/2026, pedido do Felipe com plano
aprovado em 7 fases. Repositório **público** (`FelipeWASD83/ilha`, MIT), escolha dele ao criar. O README
explica o uso; aqui ficam as decisões e as armadilhas.

**Fases 1 a 7 entregues (0.3.0, 01/11/2026).** 0.1.0 foi o núcleo; 0.2.0 as animações das logos e das
bolinhas, o instalador, o Menu Iniciar e o iniciar com o Windows; 0.3.0 todos os módulos (mídia, volume,
câmera/mic, sons, timer, uso do PC, Claude Code, mascote, calculadora, IA, tradutor, ar, fones, print, render,
notificações), a tela de configurações e o atualizador. Os módulos foram escritos em paralelo por agentes, cada um
num worktree, sem abrir a ilha (regra deles); a integração e o teste na tela vieram depois. O que cada módulo
deixou sem testar está em "Testado e não testado". A pesquisa de viabilidade de cada feature (API, o que é frágil, o que exige algo especial) foi feita
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

## Animações das logos e das bolinhas (0.2.0)

- **Fonte**: os vídeos que o Felipe fez, `videos\ChatGPT-logo.mov` e `videos\Claude-logo.mov` (ProRes 4444 com alfa,
  23,976 fps, 4320 e 7680 px), e as logos paradas em `imagens\`. Fora do git (3,3 GB e marcas de terceiros).
- **`ferramentas\preparar_logos.py`** recorta cada trecho numa faixa horizontal de quadros de 128 px em
  `src\Ilha\imagens\logos\` (fora do git) e escreve `src\Ilha\animacoes.json` (no git: nome, quadros, se repete,
  fps e zoom). `--so-bolinhas` refaz só as bolinhas sem reler os vídeos. Os trechos foram achados pela diferença
  entre quadros seguidos: loop 0-119 (o 120 é igual ao 0, emenda perfeita), volta 143/144-217, saída 240-289,
  entrada 312-361, pulso 384/385-431 (e as metades "some" e "surge"), Claude virando bola 455-480.
- **Ficam a 24 fps, como ele fez** (decisão dele: "não precisa fazer em 250 fps"). As bolinhas também saem a
  23,976 fps, no mesmo ritmo. No tamanho da ilha uma volta a 72°/s anda menos de 1 px por quadro.
- **Bolinhas**: desenhadas pelo script (Pillow, 4x e reduzido), em 3D (órbita inclinada 26°, vista de cima,
  perspectiva), as de trás escurecem até 40% (só atrás do centro), borrão de movimento por média de 8 instantes
  com obturador de 180°. Órbita, cometa, onda, divisão e anel; cada uma com entrada (sai da bola única) e saída
  (volta para ela), para nenhuma troca dar pulo. Começam na bola do vídeo dele (5,86% do quadro, `#D97757`). O
  GPT tem as mesmas em branco e um "virando bola" feito aqui (o trecho "some" dele + a bolinha nascendo).
- **Zoom**: a logo do Claude ocupa 50% do quadro dela (ele renderizou maior porque ela é assimétrica e saía da
  tela) e a do GPT 90%. A ilha mostra a família Claude com zoom 1,8x; as bolinhas do Claude são desenhadas a 72%
  e as do GPT a 130% para todas aparecerem do mesmo tamanho.
- **Tocador** (`Faixas.cs`): cada faixa é reamostrada uma vez no tamanho exato em pixels (GDI+ bicúbica) e o
  compositor desliza o pincel pela faixa em degraus (keyframes com step). Roda no DWM: com a logo e as bolinhas
  animando, a ilha gasta 0,026% do processador. As de uma vez emendam na seguinte pelo `Completed` do lote; a
  cena que some para o encadeamento (`LogoAnimada.Soltar`).

## Módulos (0.3.0)

- **Sistema**: `src/Ilha/Modulos/<Nome>/`, classe com `[Modulo(id, nome)]`, achada por reflexão
  (`CatalogoDeModulos`, em `Modulo.cs`). Guia de como escrever um: `src/Ilha/Modulos/LEIA.md`. Namespace
  `Ilha.<Nome>`, nunca `Ilha.Modulos.<Nome>` (colidia com a classe que hoje é `CatalogoDeModulos`). Dentro do
  namespace `Ilha`, `Ilha.X` resolve para a **classe** `Ilha`: para chegar num namespace use `global::Ilha.X`.
- Cada módulo tem a seção dele em `config.json` → `modulos` → id (com `ligado`), conversa com o resto só pelos
  `Servicos` e tem autoteste sem janela (`Ilha.exe --testar [id]`, relatório em `~/.ilha/testes.txt`; linha com
  `FALHOU` = falha; 884 ok em 01/11/2026).
- **Desligar um módulo** solta os atalhos e as atividades dele: o núcleo marca o dono (`Atalhos.Dono`,
  `Ilha.DonoAtual`) enquanto o `Iniciar` roda.
- APIs do núcleo que os módulos pediram: `Ilha.FecharAlerta`, `Ilha.Remover`, `Ilha.Ativas`, `Ilha.SoltarArraste`
  (depois do `DoDragDrop`, que engole o botão solto), `Ilha.PodeMostrarAlerta`, `Atividade.ExpandidaMudou`,
  `Atalhos.Padroes`/`Remover`, `Entrada.Tecla`/`Trocar`. Ferramenta (`EhFerramenta`) redesenhando com o cartão
  aberto não fecha mais o cartão; a privacidade nunca vai para a bolha (as bolinhas já ficam na pílula); apertar
  num botão arrastável e soltar sem mexer vale como clique.
- **Chaves** (IA, ar): só no Gerenciador de Credenciais (`Ilha/openai`, `Ilha/anthropic`, `Ilha/ar-do-quarto`),
  digitadas por ele na ilha. Nunca em arquivo nem em log.
- **Ar**: só `GET /api/estado` com o cartão aberto (cada GET faz o servidor ler a LG), `GET /api/historico` de 15 em
  15 min, `POST /api/comando` um por vez; `/api/atualizar` nunca sai da ilha. O autoteste ao vivo usa o simulador
  em `127.0.0.1:8089`, **nunca o servidor da casa**.
- **Notificações**: lê o `wpndatabase.db` (a API oficial exige identidade de pacote); esquema não documentado,
  desligado por padrão e com lista de apps vazia.
- **Config local do Felipe** (fora do git, em `~/.ilha/config.json`): `claude.arquivoEventos`,
  `mascote.arquivoEventos`, `mascote.pastaFolhas` e `ar.endereco` apontam para coisas do PC dele (a fila de eventos
  que os hooks do mascote já escrevem, as folhas do Clawd e o mini PC). Os caminhos ficam só lá: nada de caminho
  pessoal nem nome de projeto privado no repositório (é público).

## Aprovar permissão do Claude Code pela ilha

- `ferramentas/hook_permissao.py` é o hook `PermissionRequest`, instalado em 01/11/2026 no `~/.claude/settings.json`
  dele (cópia de antes em `settings.json.antes-ilha`). Conversa com a ilha pelo pipe `\\.\pipe\ilha-permissao`
  (confere que quem serve é o `Ilha.exe`). Sem ilha, sem resposta ou sem decisão: sai sem saída e o diálogo normal
  segue. Nunca permite por tempo esgotado. Subagente, `AskUserQuestion` e `ExitPlanMode` ficam de fora.
- **Não confirmado:** se o diálogo do Claude Code aparece junto com o hook ou só depois que ele sai (a documentação
  não diz; o teste no terminal falhou porque o login do CLI de lá tinha expirado). Por isso a espera é curta
  (`esperaSegundos` 45) e, com a ilha sem poder aparecer (pausada, jogo competitivo, Não Incomodar, tela cheia), o
  pedido volta na hora para o diálogo normal, inclusive se isso mudar com o pedido esperando. Se ele disser que o
  diálogo demora a aparecer, é isso.

## Atualizador

- `Modulos/Atualizador`: procura nas releases de `FelipeWASD83/ilha` (cache de 6 h), baixa, confere o SHA-256,
  troca os arquivos por um executor (`Ilha.exe --instalar-atualizacao`, tratado na primeira linha do `Main`) e volta
  para a anterior se a nova não sinalizar que abriu bem (`Atualizador.AbriuBem`, chamado pelo `Programa` com o
  núcleo montado). Estado em `~/.ilha/atualizacao/`. Nunca apaga nada de `~/.ilha`.
- Publicar: `ferramentas/publicar-versao.ps1` (recusa se o `.csproj` e o topo do `CHANGELOG.md` não baterem;
  `-SoGerar` só monta o pacote). **Publicar release é decisão dele.** Ainda não existe release (01/11/2026).
- O executável da release é compilado aqui e tem o hash desta compilação; o SAC pode barrar o `.exe` baixado
  mesmo tendo aceitado o local. Testar o pacote antes de publicar.

## Instalação e início com o Windows

- `ferramentas\instalar.ps1` publica pelo `compilar.ps1` (checagem do SAC), fecha a ilha aberta, copia para
  `%LOCALAPPDATA%\Programs\Ilha`, cria `Ilha.lnk` no Menu Iniciar com o `icone\icone.ico` e abre a cópia
  instalada. **Rodar num terminal normal** (o `run_in_terminal`), nunca do shell do Claude: processo aberto pelo app
  (MSIX) vê AppData e registro virtuais e instalaria na cópia virtual.
- O `Run` (`HKCU\...\Run`, valor "Ilha") é gravado **só pela cópia instalada** (`Inicio.cs`), conforme
  `iniciarComWindows` (padrão ligado). A compilação da pasta muda a cada build e pode ser barrada pelo SAC.
- Ícone: `ilha()` no `_organizacao\atalhos\desenhar_icones.py` (pílula preta com o anel do timer e as barras do
  visualizador sobre azul-violeta), renderizado pelo `gerar_icones.py ilha` em `icone\`.

## Smart App Control (importante)

Em 01/11/2026 uma compilação do `Ilha.dll` foi **barrada** (0x800711C7, "política de Controle de Aplicativo")
depois de várias passarem, e mais tarde o **`Ilha.exe`** também (aí o `Start-Process` nem abre). O SAC julga cada
binário pela reputação na nuvem. `dotnet Ilha.dll` **não** escapa (o bloqueio é no DLL). Por isso: **compilar
sempre por `ferramentas\compilar.ps1`**. Ele compila, roda `Ilha.exe --ping` e, se o SAC barrar, troca o carimbo
e compila de novo, até 8 vezes: `Carimbo.g.cs` (constante com bloco de letras de tamanho sorteado, fora do git)
muda o DLL, e `-p:SourceRevisionId=<sorteado>` muda a versão informativa, que vai para os recursos do `.exe` (sem
isso o apphost sai igual em toda compilação). Num dia chegou a barrar três seguidas. Mesmo método do `quadra`. Desligar o SAC está fora de
questão. Release feita fora deste PC terá outro hash: testar o `.exe` do pacote, não o da compilação local.

## Comandos

- Compilar (com a checagem do SAC): `powershell -ExecutionPolicy Bypass -File .\ferramentas\compilar.ps1`
  (`-Publicar <pasta>` publica com ReadyToRun). Saída em `src\Ilha\bin\Release\net10.0-windows10.0.26100.0\win-x64`.
- Abrir: `Ilha.exe`. Fechar o que está aberto: `Ilha.exe --sair`. Uma instância só.
- Demonstração automática de todos os estados: `Ilha.exe --roteiro`. Uma atividade só: `Ilha.exe --demo <tipo>`
  (`timer`, `midia`, `claude`, `uso`, `ia`, `galeria`), com `--expandir` para abrir o cartão.
- Instalar ou atualizar a cópia do PC: `ferramentas\instalar.ps1` (num terminal normal); `-Desinstalar` tira tudo.
- Refazer as animações: `python ferramentas\preparar_logos.py` (precisa de ffmpeg, numpy e Pillow).
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
- RAM: ~77 MB privados depois de abrir; ~79 MB com tudo aparecendo (0.2.0, sem módulos). **Com os 16 módulos
  (0.3.0): ~125 MB privados**, 33 threads, ~1000 handles; CPU parada medida em 0,010% (média de 20 s, compacta com o
  Claude Code e o timer na tela).
- Primeira aparição depois de escondida: começa a animar na hora (o atraso de ~70 ms sumiu com o tamanho de
  partida pequeno e o aquecimento de fontes e janela ao abrir).

## Testado e não testado

**0.3.0 (01/11/2026):** os autotestes de todos os módulos passam (`--testar`: 912 ok, 1 pulou porque o simulador do
ar não estava aberto). Na tela, com a ilha de verdade e os atalhos globais: pílula com o Claude Code real (sessões
desta máquina pela fila de eventos e pelos arquivos de sessão), cartão das duas sessões, calculadora, uso do PC
(RTX 3060 pela NVML), tela de chaves da IA, ar pedindo a chave, e o timer de ponta a ponta (campo, "1m", bolha
com o anel, alerta "Timer acabou"); o foco voltou para a janela de antes em todos. **Não visto na tela:** mídia com
música tocando, volume e troca de saída (só o QueryInterface do IPolicyConfig foi testado; a troca mexe no áudio do
sistema), bolinhas de câmera e mic, mascote, tradutor, chat de verdade (sem chave), ar de verdade (sem chave),
fones conectando, print, render, notificações, tela de configurações, atualizador (não existe release), aprovar
permissão pelo hook com o Claude Code chamando, hover e clique com o mouse de verdade.

- Testado: autoteste das janelas; medição; fotos de todos os estados no escuro e no claro (flutuante e colada);
  modo competitivo (com o `claude.exe` posto na lista: só o timer fica, mídia e alerta somem); o Não Incomodar do
  PC dele lido como ligado (por isso o alerta de teste não aparece com `respeitarNaoPerturbe: true`).
- **Não testado, fica com ele**: hover e clique de verdade com o mouse; atalho global; área de trabalho (Win+D);
  troca de monitor principal e de resolução; suspensão; reinício do Explorer; reinício do driver
  (Win+Ctrl+Shift+B); jogo de verdade (com e sem tela cheia exclusiva) e a medição com o PresentMon; se o valor
  da WNF do Não Incomodar bate com o botão real.

## Armadilhas

- **Uma classe `[ComImport]` por objeto COM do processo inteiro.** O `MMDeviceEnumerator` devolve o mesmo objeto
  para o processo todo; com uma classe por módulo (mídia e volume tinham), o .NET embrulha com a primeira e o `new`
  da outra falha no cast (o visualizador da mídia travava). Criar pelo `ComDeAudio.NovoEnumerador()` e converter
  para a interface do módulo.

- No PC dele, `Ctrl + Alt + M` e `Ctrl + Alt + L` são de outro programa: o timer usa `Ctrl + Alt + R`. Atalho
  ocupado não quebra nada, o módulo só avisa pela bandeja; antes de escolher padrão novo, testar com
  `RegisterHotKey` se está livre.
- `Ctrl + Alt + Espaço` (atalho padrão) estava ocupado no PC dele; a ilha cai para `Ctrl + Shift + Espaço` e
  avisa pela bandeja. A config guarda o preferido, não o que ficou.
- Testar janela na tela atrapalha quem está usando o PC: conferir a janela da frente antes de `--roteiro`,
  `--medir` e `--autoteste`. Em 01/11/2026 uma foto de teste pegou o Brave em tela cheia com o `cordilheira`.
- Medir fps com outro programa usando a GPU dá número mais baixo: comparar sempre com uma referência rodando
  na mesma hora antes de culpar a ilha.
- **Imagens de terceiros ficam fora do git** (o repositório é público): as fotos oficiais de fones em
  `imagens/fones/*.png` (Apple, Logitech e outras marcas, origem em `imagens/fones/FONTES.md`, que vai no git) e
  as logos e animações do Claude e do GPT (`imagens\`, `videos\`, `src\Ilha\imagens\logos\`). Estão no PC dele
  e no backup do SSD; o `.gitignore` as segura. Imagem nova de marca entra do mesmo jeito. Sem elas a ilha abre
  normal (fica vazio).
- A foto da HyperX tem fundo branco: recortar na fase 6 antes de usar. A da Razer tem só 500 px.
