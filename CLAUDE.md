# CLAUDE.md: ilha

Dynamic Island for Windows: a black pill at the top of the main monitor that changes shape to show what is going on
(media, timer, Claude Code, alerts). Created on 01/11/2026, requested by Felipe with a plan approved in 7 phases.
**Public** repository (`FelipeWASD83/ilha`, MIT), his choice when it was created. The README explains how to use it;
this file holds the decisions and the traps.

**1.0.0 (01/11/2026): bilingual interface (Portuguese and English, following Windows, with the `language` option),
documentation in English, and the mascot module out of the public repo.** 0.4.0 brought the main menu, focus only on
click, the adaptive border, the clock, the color picker, the shelf, the note, the mixer, recording, the Claude limit
and the air only speaking up when it changes. Phases 1 to 7 were delivered in 0.3.0. 0.1.0 was the core; 0.2.0 the
logo and dot animations, the installer, the Start menu and start with Windows; 0.3.0 every module (media, volume,
camera/mic, sounds, timer, PC usage, Claude Code, calculator, AI, translator, air, headphones, screenshot, render,
notifications), the settings window and the updater. The modules were written in parallel by agents, each in a
worktree, without opening the island (their rule); integration and on-screen testing came afterwards. What each module
left untested is in "Tested and not tested". The feasibility research for each feature (API, what is fragile, what
needs something special) was done before the plan; the summary is in the plan approved in the 01/11/2026 conversation
and the points that matter are here.

**The mascot module is private now:** it lives outside this repository and is compiled only through the git-ignored
`src/Ilha/local.props`; the public release is built with `ferramentas\compilar.ps1 -Publico`, which skips it.

## Architecture (do not undo without measuring)

- **C# .NET 10 + the system Windows.UI.Composition** in a Win32 window (`DesktopWindowTarget`). The springs
  (`SpringVector2/3`) run **inside the DWM**: the app only decides the target and swaps the scenes. Measured: 250 fps
  median in transitions, app CPU ~0. WinUI 3 animates in the app process; WPF animates in the process and uses a
  layered window; Python would bring a native `.pyd` to Smart App Control.
- **Two windows.** `JanelaVisual` (layered + `WS_EX_TRANSPARENT` + `NOREDIRECTIONBITMAP`) only draws and is transparent
  to the mouse, so the shadow and the animation margin never hold a click. `JanelaToque` has no content, uses
  `SetWindowRgn` in the exact shape of the pill and receives the mouse. `HTTRANSPARENT` does not work across
  processes. Both: `NOACTIVATE`, `TOOLWINDOW`, `TOPMOST`. `--autoteste` checks this on the real screen.
- **Hiding = DWM cloak** (`DWMWA_CLOAK`), not `SW_HIDE`: while cloaked, the window is not composited (nothing on top of
  the game) and it comes back with no delay.
- **Direct2D on WARP (software Direct3D) by default.** The drawing is only text and icons, rare and small. With the
  GPU, the NVIDIA driver alone cost ~110 MB of private RAM (123 MB against 72 MB). `desenharNaGpu` in the config goes
  back to the graphics card (applies after reopening).
- **Nine-grid shadow**, two layers of gaussian blur drawn once. The compositor `DropShadow` redoes the blur every frame
  and, with the GPU busy, dropped the animation from ~184 to 96 fps.
- **Content clipped by the shape** (`GeometricClip` with a geometry that copies the pill's through an expression):
  whatever comes in and out never shows outside it.
- **Scene transition**: the scene lives outside the tree and shows up through `CompositionVisualSurface`; it comes in
  with fade, scale and blur (an effect written by hand in `Desfoque.cs`, without Win2D) and, once settled, switches to
  the brush with no effect (a still blur would still cost GPU per frame).
- **Floating mode is the default** (his request on 01/11/2026: docked looked like a notch and, since the monitor is not
  OLED, the black does not disappear into the bezel). `colada: true` goes back to notch style, with the concave
  "shoulders".
- **Visibility by event**: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` out of context (nothing injected) and the `.exe`
  name from the Toolhelp list, **without opening a handle on the game process** (anti-cheat). Do Not Disturb:
  undocumented WNF `0x0D83063EA3BF1C75` (0 off, 1 and 2 on), read every 10 s and on every window switch; if it fails,
  it becomes "unknown" and changes nothing. Focus session: `FocusSessionManager` (documented).
- **Applying does not re-animate if the target did not change** (plan signature): every foreground window switch made
  the DWM compose ~35 frames on top of the game for nothing.
- **Config in `%USERPROFILE%\.ilha\`**, not in AppData: a process opened from inside the Claude app (MSIX) sees a
  virtual AppData. Log in `ilha.log` (1 MB, then rotates to `.1`), measurement in `medicao.txt`.

## Language (1.0.0)

- **`Lang`** (`Lang.cs`): every user-facing text is written as `Lang.T("português", "English")`. Logs, code
  identifiers and code comments stay in Portuguese (owner's decision). Dates and numbers shown to the user use
  `Lang.Culture` (pt-BR or en-US).
- **Choice**: the config key `language` (`"auto"`, `"pt"` or `"en"`, default `"auto"`). `auto` is Portuguese when the
  Windows display language is Portuguese and English otherwise. The settings window has the selector (Language:
  Automatic / Português / English).
- **Config fields in the settings window**: `[Label("pt", "en")]` for the label and `[Help("pt", "en")]` for the help
  line under the field (they win over `[DisplayName]` and `[Description]`).
- **Module names**: `[Modulo("id", "Nome", English = "Name")]`; the settings window shows the name in the interface
  language (`Modulo.Name`).
- **Changing the language** restarts all modules and rebuilds the tray menu, so every text is built again in the new
  language.
- **From 1.0 on, commits and the changelog of this repository are in English.**

## Logo and dot animations (0.2.0)

- **Source**: the videos Felipe made, `videos\ChatGPT-logo.mov` and `videos\Claude-logo.mov` (ProRes 4444 with alpha,
  23.976 fps, 4320 and 7680 px), and the still logos in `imagens\`. Outside git (3.3 GB and third party brands).
- **`ferramentas\preparar_logos.py`** cuts each segment into a horizontal strip of 128 px frames in
  `src\Ilha\imagens\logos\` (outside git) and writes `src\Ilha\animacoes.json` (in git: name, frames, whether it
  loops, fps and zoom). `--so-bolinhas` redoes only the dots without rereading the videos. The segments were found by
  the difference between consecutive frames: loop 0-119 (120 equals 0, a perfect seam), turn 143/144-217, exit
  240-289, entry 312-361, pulse 384/385-431 (and the "vanish" and "appear" halves), Claude turning into a ball 455-480.
- **They stay at 24 fps, as he made them** (his decision: "no need to make it at 250 fps"). The dots also come out at
  23.976 fps, at the same pace. At the island size, a turn at 72°/s moves less than 1 px per frame.
- **Dots**: drawn by the script (Pillow, 4x and downscaled), in 3D (orbit tilted 26°, seen from above, perspective),
  the ones in the back darken down to 40% (only behind the center), motion blur from an average of 8 instants with a
  180° shutter. Orbit, comet, wave, split and ring; each one with an entry (leaves the single ball) and an exit (goes
  back to it), so no switch jumps. They start from the ball in his video (5.86% of the frame, `#D97757`). GPT has the
  same ones in white and a "turning into a ball" made here (its "vanish" segment + the dot being born).
- **Clipping (0.4.2):** the Claude family with 1.8x zoom reaches 56% of the side of the box, and the GPT dots, drawn
  1.8x larger in the frame, went past its edge (18 of 36 orbit frames touched it). Now every scene goes into the island
  with `FOLGA_CENA` (12 DIP) around the surface, and the GPT dots are drawn at 0.72 like the Claude ones, with their
  own zoom from the `chatgpt-bola-` key in `animacoes.json` (a key ending in "-" matches by prefix, the longest one
  wins). The GPT "turning into a ball" (`chatgpt-bola`, without the "-") stays at 0.72 x 1.8 with no zoom, so it
  joins seamlessly.
- **Zoom**: the Claude logo fills 50% of its frame (he rendered it larger because it is asymmetric and went off
  screen) and the GPT one 90%. The island shows the Claude family with 1.8x zoom; the Claude dots are drawn at 72% and
  the GPT ones at 130% so they all show up at the same size.
- **Player** (`Faixas.cs`): each strip is resampled once at the exact pixel size (GDI+ bicubic) and the compositor
  slides the brush along the strip in steps (keyframes with step). It runs in the DWM: with the logo and the dots
  animating, the island uses 0.026% of the processor. One-shot animations chain into the next one through the batch
  `Completed`; the scene that disappears stops the chaining (`LogoAnimada.Soltar`).

## Modules (0.3.0)

- **System**: `src/Ilha/Modulos/<Nome>/`, a class with `[Modulo(id, nome)]`, found by reflection
  (`CatalogoDeModulos`, in `Modulo.cs`). Guide on how to write one: `src/Ilha/Modulos/README.md`. Namespace
  `Ilha.<Nome>`, never `Ilha.Modulos.<Nome>` (it collided with the class that is now `CatalogoDeModulos`). Inside the
  `Ilha` namespace, `Ilha.X` resolves to the **class** `Ilha`: to reach a namespace use `global::Ilha.X`.
- Each module has its own section in `config.json` → `modulos` → id (with `ligado`), talks to the rest only through
  `Servicos` and has a windowless self test (`Ilha.exe --testar [id]`, report in `~/.ilha/testes.txt`; a line with
  `FALHOU` = failure; 884 ok on 01/11/2026).
- **Turning a module off** releases its hotkeys and activities: the core marks the owner (`Atalhos.Dono`,
  `Ilha.DonoAtual`) while `Iniciar` runs.
- Core APIs the modules asked for: `Ilha.FecharAlerta`, `Ilha.Remover`, `Ilha.Ativas`, `Ilha.SoltarArraste` (after
  `DoDragDrop`, which swallows the button release), `Ilha.PodeMostrarAlerta`, `Atividade.ExpandidaMudou`,
  `Atalhos.Padroes`/`Remover`, `Entrada.Tecla`/`Trocar`. A tool (`EhFerramenta`) redrawing with the card open no
  longer closes the card; privacy never goes to the bubble (the dots already stay on the pill); pressing a draggable
  button and releasing it without moving counts as a click.
- **Keys** (AI, air): only in the Credential Manager (`Ilha/openai`, `Ilha/anthropic`, `Ilha/ar-do-quarto`), typed by
  him on the island. Never in a file or in a log.
- **Air**: only `GET /api/estado` with the card open (each GET makes the server read the LG unit), `GET
  /api/historico` every 15 min, `POST /api/comando` one at a time; `/api/atualizar` never leaves the island. The live
  self test uses the simulator at `127.0.0.1:8089`, **never the house server**.
- **Notifications**: reads `wpndatabase.db` (the official API requires package identity); undocumented schema, off by
  default and with an empty app list.
- **Felipe's local config** (outside git, in `~/.ilha/config.json`): `claude.arquivoEventos` (the event queue written
  by his Claude Code hooks), `nota.arquivo` (the `Vida\Caixa de entrada.md` of the vault),
  `limite.statuslineAnterior` (the previous status line) and `ar.endereco` (the mini PC). The paths stay only there: no
  personal path or private project name in the repository (it is public).

## Core of 0.4.0 (do not undo without reading)

- **Main menu** (`Menu.cs`): replaces the time and date panel. Each module adds its entries through
  `S.Menu.Adicionar(new ItemDoMenu {...})` (icon made only of shapes, never a letter); the core marks the owner and
  removes the entries when the module is turned off. Every open card that is not the menu gets the "< Menu" strip at
  the top: the core wraps the scene (`ComVoltar`, `Cena.Interna`), and the buttons of the inner scene keep working
  with the offset.
- **Keyboard:** the island only takes focus on a click on it (`Entrada.Focar`, the same invisible window used by the
  fields, in keys-only mode). Esc goes back to the menu **only if the island was clicked** (`_clicada`); opened by the
  hotkey, Esc closes it and gives the focus back, as before. Collapsing always gives the focus back
  (`Entrada.Fechar`). Esc is never captured in the whole of Windows (the color picker registers Esc only while the
  magnifier is open).
- **Mouse and region (the "stays under the mouse" bug, reproduced with `--teste-mouse` on 01/11/2026):** an alert that
  appeared under a still cursor got a fake WM_MOUSEMOVE from Windows, the island thought the mouse was over it and the
  alert never left (it stayed 10 s when it should vanish in 4). Now a move with the cursor at the same point as the
  last region change does not count (`_cursorNaMudanca`), a 150 ms watcher checks with `WindowFromPoint` while the
  island thinks the mouse is over it, and the touch region only grows when the spring settles (before, it went to the
  final size right away and held clicks on top of another app), except when the mouse itself opened it. A button
  release that was not pressed in the touch window is not a click.
- **Collapse delay:** `atrasoRecolherMs` (600).
- **When it hides (0.4.1, his decision):** only in competitive games and in full screen content
  (`Contexto.ConteudoEmTelaCheia`: full screen of an `.exe` from `appsDeConteudo`, or presentation mode; exclusive
  Direct3D full screen is always a game). With nothing to show, the empty pill stays over any window (`pilulaVazia`,
  which replaced `ociosaNaAreaDeTrabalho`). When hidden by content, the mouse at the top edge, near the middle, reveals
  it (`_revelada`, a 150 ms watcher only in that state) until it stays ~0.9 s away. `Visibilidade` re-evaluates every
  3 s because a video going full screen does not switch the foreground window.
- **Adaptive border** (`SondaDeFundo.cs`): a 1 px line outside the pill and the bubble, lit only on a dark background.
  The probe does a BitBlt of the strips just outside the pill and the shadow (behind it the island itself covers the
  background), 2 times per second, only with the island visible and never with full screen in front; hysteresis
  0.22/0.30. Measured on 01/11/2026: each read waits ~10 ms for the compositor and, at 4 per second, cost +0.01% of
  processor (0.020% against 0.010%); the wait is what led to lowering it to 2 and stopping in full screen (games). The
  cost goes to the log every minute ("borda: N leituras"). It does not exist in docked mode.
- **1.0.1 (01/11/2026):** `SHQueryUserNotificationState` 2 (QUNS_BUSY) no longer counts as full screen: it stayed on
  long after the app had gone and hid the island on the desktop and stopped the border probe (only the window rectangle,
  3 and 4 count; `quns` and the window class go to the log when they change). A transitional or empty foreground whose
  previous window is gone, hidden or minimized is the desktop (closing the browser left a stale context). On the
  desktop the island is plain dark: no border, no clock, and the border goes off whenever the probe pauses.
- **Sounds (1.3.0):** all synthesized by `ferramentas/gerar_sons.py` (numpy, fixed seed: same files on every run), in
  the style he liked on 02/11/2026 (soft attack of 40 to 280 ms, warm, 1 to 2 kHz, fifths and octaves); 48 kHz stereo,
  all at -21 dB RMS. `Alerta.SomEmLoop` plays a sound in a loop while the alert is in front (`Sons.TocarEmLoop`); the
  core's `_alerta` setter stops it whenever the alert leaves, by any path. The timer finishing uses `timer-loop.wav`
  (a seamless loop: the tail wraps to the start) and stays up to `segundosDoAlarme` (60); "+1 min" stops the melody
  and closes the alert 2.5 s later.
- **Drawing bitmaps (1.2.0):** Vortice's `DrawBitmap(bitmap, opacity, mode, Rect)` takes the SOURCE rectangle and
  draws at the origin. Use `Bitmaps.Draw(dc, bmp, x, y, w, h, mode)` (destination); every positioned photo was in the
  corner, cut, until 01/11/2026. `Faixas.cs` draws at the origin and was left as it was.
- **Effort selector (1.2.0):** the effort submenu of the Claude app opens by the MSAA default action on its menu item
  (a click, which only focuses it) followed by a Right arrow posted to the render widget; the WM_MOUSEMOVE hover is
  the fallback. The hover alone failed whenever the real cursor was over the island (always, when the card is
  clicked). `Ilha.exe --ponte-claude ler | esforco SESSION LEVEL | ultracode SESSION on|off [--foco]` runs the bridge
  with no window and writes to `~/.ilha/ponte-claude.txt` (`--foco` takes the foreground first, like a click on the
  island); test it with the cursor parked over the island, which is the real case.
- **Battery tiles (1.2.0):** `bateria:<key>` items in the main menu (owner `fones`), shown while the device is on (G733
  by HID++), connected (AirPods) or listed by G HUB (mouse), removed when it goes; `bateriaNoMenu` turns them off.
  Since 1.4.0 they are one tile, `baterias`, drawn by the module with `BatteryPill` (`Desenho.cs`: green above 50,
  yellow, red at 25 and below, green while charging). Its size follows the devices (his rule on 02/11/2026): one = a
  1x1 ball with the battery under it, two = 2x1 side by side, three or four = 2x2 one per line. Photos sit in a light
  gray ring, no backdrop; each device is a `Parts` area that opens it. The AirPods show their emptier bud.
- **Main menu (1.4.0, reworked in 1.5.0, `Menu.cs`):** iPhone Control Center style, decided with him on 02/11/2026
  from his mockup and his phone. An `ItemDoMenu` has a size (`Cols` x `Rows`), and `Menu.Pack` puts each tile in the
  first free spot of the 4-column grid, in `Ordem` (a later small tile fills an earlier hole). A 1x1 is a gray ball
  (58 DIP) with the name under it; bigger blocks have corners of half a column (`RAIO_BLOCO`, 42), so their ends match
  the balls. The menu's card radius is that plus the padding (60, concentric); other cards keep 38. No lighter rim,
  gray at 6% white. `Draw` replaces icon and name (sliders, batteries), `Drag` and `Wheel` act on the tile (the wheel
  reaches the card through `Atividade.Rolar(delta, noCartao)` with the last mouse position), `Parts` are clickable
  areas inside it, `Corner` puts the entry as a plain icon in the top right (settings at the edge, recording next to
  it), `IconSize` grows the icon (the limit ring) and `DetailSwatch` puts a color dot before the detail. The header
  centers time, date, the privacy dots and the corner icons on one line by the glyphs' middle (`Meio`), not the text
  boxes. `MenuSlider` is the tall slider (sound, brightness): relative drag, a tap changes nothing. With his modules
  the grid has 5 rows: twelve balls, then Sound and Brightness (1x2) and Batteries (2x1 with two devices, which
  leaves two empty cells under it; 2x2 with three or four, none).
- **Camera and microphone (1.5.0, `Modulos/Privacidade`):** the ConsentStore stopped getting these times on Windows
  26300 (newest entries 15/10 and 18/10/2026), which is why nothing showed. `UsoAoVivo` now reads the capture sessions
  of every microphone (WASAPI, Active = recording) and the Media Foundation sensor activity monitor (cameras), on one
  MTA thread fed by system notices, no polling; names come from Toolhelp (no handle opened, anti-cheat). Tested on
  02/11/2026 with ffmpeg holding the mic and the webcam. Kaspersky asks before letting a new program use the mic and
  holds it until answered: in a test, a program waiting on that prompt is not recording yet. The screen capture
  detector (Gravando) still reads the ConsentStore, through `RegistroDeConsentimento.cs`; whether Windows still
  writes its keys was not checked. The pill: a ring around the time (green camera, orange microphone) when the time
  shows, the dot on the side when it does not; only the privacy activity left = the idle pill with the time and the
  ring. The menu draws its own dots after the time (the core's card dots skip it).
- **Brightness (1.5.0, `Modulos/Brilho`):** the primary monitor over DDC/CI, VCP 0x10 (`dxva2`), on its own thread;
  a drag sends only the newest value. On 02/11/2026 the ASUS XG34WCDMTG answered. No answer = no tile. The self test
  only reads, never writes.
- **ALTURA_MAX (640 DIP)** bounds the shape visuals of the island: a card taller than it loses the bottom of its body.
- **Low battery alerts (1.1.0, `BatteryAlerts.cs`, `GHub.cs`, `ModuloFones.cs`):** a level (50, 25, 10, 5, 1 by
  default, `limiaresBateria` in the settings) fires when a reading goes from above it to at or below it, once per level
  and per discharge; a big drop fires only the lowest level crossed; the first reading of a battery fires only if it is
  already at 10% or less; charging starts over and a level only fires again after the battery went up by 5 points (10 for
  AirPods). AirPods report in steps of 10, so their levels are the configured ones rounded down to a multiple of 10 (50,
  20, 10, 0, where 0 means "under 10%"); each bud has its own count (a bud charging in the case is left out) and the alert
  names the lower one. G733: the percentage comes from G HUB when it answers (the exact number; without it the voltage
  curve, `~` estimate), and only while the HID++ side says the headset is on. The mouse (`g502` in the G HUB model) is read
  only through G HUB (`ws://localhost:9010`, subprotocol `json`, `Origin: file://`, GET `/devices/list` and
  `/battery/<id>/state` every 60 s; format of 01/11/2026, undocumented by Logitech, so any failure is just "no reading").
  The alert is `Importante` (passes Do Not Disturb, which reads "alarms only" all the time on his PC, and full screen
  content); competitive games still silence it. Mouse photos: `g502-x-plus-<preto|roxo|branco|menta>.png` in
  `imagens/fones` (outside git, source in `FONTES.md`), color in `corMouse`.
- **Pointer (1.0.3):** `NativeWindow` registers its window class with no cursor (`GetClassLongPtr(GCL_HCURSOR)` was 0),
  so over the touch window Windows left the pointer as the window below had it, and it vanished when that one hid
  it. `JanelaToque` answers `WM_SETCURSOR` (client area) with the arrow.
- **Content swap (1.0.2, `ContentSwap.cs`):** a change of content without a change of size (a new song, a new
  Claude Code step) no longer just redraws: the old content is drawn once into a temporary layer above the live one,
  slides out and fades while the live layer (already redrawn) slides in, all in the compositor; the temporary layer is
  registered in the scene, so a scene released mid-swap releases it too. Media uses it for the cover and the card
  header (direction from the last skip button, 4 s window), Claude Code for the step text on the pill (up). A change
  that alters the size still goes through the core scene transition (fade, scale and blur).
- **Discreet hook (1.0.1):** `Ilha.Discreto` (static `Func<bool>?`, null in the public build) lets a private module
  keep other people's text off the screen: notifications show only "New notification", and alerts with `Privado` are
  dropped, even from the queue, while it returns true. A hook that throws counts as discreet.
- **Clock:** the empty pill shows the time everywhere except on the desktop (1.0.1). On the compact pill, the
  activity declares `MeioLivreCompacta`; the core draws the time there when it fits, never on the
  desktop.
- **Dropping files** (`Soltura.cs`): an OLE target on the touch window, only real paths, link or copy effect (never
  move). With the island hidden, the empty pill shows up at the top center when a file drag (the mouse capture is in a
  `CLIPBRDWNDCLASS` window, the DoDragDrop one) that started elsewhere comes close. **Not confirmed on screen** whether
  that class is the one Explorer uses for dragging on this Windows: if the zone never shows up, the log says which
  class it saw ("zona de soltar: captura com ...").
- **Claude limit:** `ferramentas/statusline_uso.py` takes the place of the Claude Code status line, writes only
  `rate_limits` to `~/.ilha/uso-claude.json` and calls the previous status line (installed on 01/11/2026; copy of the
  settings in `settings.json.antes-limite`). **Open question:** whether Desktop app sessions run the status line; by
  the end of the 01/11/2026 session the file had not shown up (that session was already open before the switch).
- **Air:** it left the pill. The module reads `GET /api/historico` (which does not make the server read the LG unit)
  every 45 s and gives a notice when power, mode or target change; the room temperature alone does not trigger a
  notice. A change made from the island gives the notice right away and the confirmation that comes later stays quiet.
  A change from the remote control shows up within 15 min (the server cadence with nobody watching), or 3 min with
  something watching.

## Approving Claude Code permissions from the island

- `ferramentas/hook_permissao.py` is the `PermissionRequest` hook, installed on 01/11/2026 in his
  `~/.claude/settings.json` (copy of the previous one in `settings.json.antes-ilha`). It talks to the island through
  the pipe `\\.\pipe\ilha-permissao` (it checks that the server is `Ilha.exe`). No island, no answer or no decision:
  it exits with no output and the regular dialog goes on. It never allows on a timeout. Subagents,
  `AskUserQuestion` and `ExitPlanMode` are left out.
- **Not confirmed:** whether the Claude Code dialog shows up together with the hook or only after it exits (the
  documentation does not say; the terminal test failed because the CLI login there had expired). That is why the wait
  is short (`esperaSegundos` 45) and, when the island cannot show up (paused, competitive game, Do Not Disturb, full
  screen), the request goes back to the regular dialog right away, even if that changes while the request is waiting.
  If he says the dialog takes a while to show up, this is why.

## Updater

- `Modulos/Atualizador`: looks at the releases of `FelipeWASD83/ilha` (6 h cache), downloads, checks the SHA-256,
  swaps the files through an executor (`Ilha.exe --instalar-atualizacao`, handled on the first line of `Main`) and goes
  back to the previous version if the new one does not signal that it opened fine (`Atualizador.AbriuBem`, called by
  `Programa` with the core built). State in `~/.ilha/atualizacao/`. It never deletes anything from `~/.ilha`.
- Publishing: `ferramentas/publicar-versao.ps1` (refuses if the `.csproj` and the top of `CHANGELOG.md` do not match;
  `-SoGerar` only builds the package; it builds with `compilar.ps1 -Publico`, so no private module goes into the
  release). **Publishing a release is his decision.** There is no release yet (01/11/2026).
- The release executable is compiled here and has the hash of this build; Smart App Control may block the downloaded
  `.exe` even after accepting the local one. Test the package before publishing.

## Installation and start with Windows

- `ferramentas\instalar.ps1` publishes through `compilar.ps1` (SAC check), closes the open island, copies to
  `%LOCALAPPDATA%\Programs\Ilha`, creates `Ilha.lnk` in the Start menu with `icone\icone.ico` and opens the installed
  copy. **Run it in a regular terminal** (`run_in_terminal`), never from the Claude shell: a process opened by the app
  (MSIX) sees a virtual AppData and registry and would install into the virtual copy.
- The `Run` entry (`HKCU\...\Run`, value "Ilha") is written **only by the installed copy** (`Inicio.cs`), following
  `iniciarComWindows` (on by default). The build in the project folder changes with every build and may be blocked by
  SAC.
- Icon: `ilha()` in `_organizacao\atalhos\desenhar_icones.py` (black pill with the timer ring and the visualizer bars
  on blue-violet), rendered by `gerar_icones.py ilha` into `icone\`.

## Smart App Control (important)

On 01/11/2026 a build of `Ilha.dll` was **blocked** (0x800711C7, "Application Control policy") after several passed,
and later **`Ilha.exe`** too (then `Start-Process` does not even open it). SAC judges each binary by its cloud
reputation. `dotnet Ilha.dll` does **not** get around it (the block is on the DLL). So: **always build through
`ferramentas\compilar.ps1`**. It builds, runs `Ilha.exe --ping` and, if SAC blocks it, swaps the stamp and builds
again, up to 8 times: `Carimbo.g.cs` (a constant with a block of letters of random length, outside git) changes the
DLL, and `-p:SourceRevisionId=<random>` changes the informational version, which goes into the `.exe` resources
(without it the apphost comes out the same in every build). One day it blocked three in a row. Same method as
`quadra`. Turning SAC off is out of the question. A release built outside this PC will have another hash: test the
`.exe` in the package, not the one from the local build.

## Commands

- Build (with the SAC check): `powershell -ExecutionPolicy Bypass -File .\ferramentas\compilar.ps1`
  (`-Publicar <folder>` publishes with ReadyToRun; `-Publico` skips the private modules of `local.props`). Output in
  `src\Ilha\bin\Release\net10.0-windows10.0.26100.0\win-x64`.
- Open: `Ilha.exe`. Close the running one: `Ilha.exe --sair`. Single instance.
- Automatic demo of every state: `Ilha.exe --roteiro`. A single activity: `Ilha.exe --demo <tipo>` (`timer`,
  `midia`, `claude`, `uso`, `ia`, `galeria`), with `--expandir` to open the card.
- Install or update the PC copy: `ferramentas\instalar.ps1` (in a regular terminal); `-Desinstalar` removes
  everything.
- Redo the animations: `python ferramentas\preparar_logos.py` (needs ffmpeg, numpy and Pillow).
- Measure fps, CPU and memory: `Ilha.exe --medir` (tours the states, ~1 min, result in `~/.ilha/medicao.txt`, closes by
  itself). It opens Windows capture, which wakes the GPU: the memory at the end of the measurement is not the memory of
  normal use.
- Window self test on the real screen: `Ilha.exe --autoteste` (flashes a test pill for 0.7 s; result in
  `~/.ilha/autoteste.txt`, code 0 = ok).

## Measurements (01/11/2026, 3440x1440 at 250 Hz, 125% scale)

- Transitions: median interval 4.00 ms (250 fps). With the GPU busy with other programs (Unreal from `quadra`,
  Blender, Wallpaper Engine) it stayed at ~188 fps, **the same** as a minimal reference (one animated rectangle and
  nothing else) running at the same time: the island does not lose frames beyond what the busy GPU already takes.
  Above the 60 minimum.
- Idle CPU: ~0.01% (compact with the timer redrawing 1x/s) and ~0.02% hidden.
- RAM: ~77 MB private after opening; ~79 MB with everything showing (0.2.0, no modules). **With the 16 modules
  (0.3.0): ~125 MB private**, 33 threads, ~1000 handles; idle CPU measured at 0.010% (20 s average, compact with Claude
  Code and the timer on screen).
- First appearance after being hidden: it starts animating right away (the ~70 ms delay went away with the small
  starting size and the warm-up of fonts and window on open).

## Tested and not tested

**0.4.0 (01/11/2026):** the self tests of all 21 modules pass (`--testar`: 1195 ok). The mouse bug was reproduced in
0.3.0 with `--teste-mouse` (alert stuck for 10 s, region covering the cursor at 12 ms). A review with agents found and
fixed 11 defects in the new core (Esc from the hotkey trapped the menu, the drop zone opening with any held click,
focus stolen from the settings window, among others) and the modules had their own check. The rest of what was seen
or not on screen is in that version's section of `CHANGELOG.md` and in the session report.

**0.3.0 (01/11/2026):** the self tests of every module pass (`--testar`: 912 ok, 1 skipped because the air simulator
was not open). On screen, with the real island and the global hotkeys: pill with the real Claude Code (sessions on
this machine from the event queue and the session files), card with two sessions, calculator, PC usage (RTX 3060
through NVML), AI keys screen, air asking for the key, and the timer end to end (field, "1m", bubble with the ring,
"Timer acabou" alert); the focus went back to the previous window in all of them. **Not seen on screen:** media with
music playing, volume and output switching (only the IPolicyConfig QueryInterface was tested; switching changes the
system audio), camera and mic dots, translator, real chat (no key), real air (no key), headphones connecting,
screenshot, render, notifications, settings window, updater (no release exists), approving a permission through the
hook with Claude Code calling it, hover and click with the real mouse.

- Tested: window self test; measurement; photos of every state on dark and light (floating and docked); competitive
  mode (with `claude.exe` put on the list: only the timer stays, media and alerts go away); Do Not Disturb on his PC
  read as on (that is why the test alert does not show up with `respeitarNaoPerturbe: true`).
- **Not tested, left to him**: real hover and click with the mouse; global hotkey; desktop (Win+D); switching the main
  monitor and the resolution; sleep; Explorer restart; driver restart (Win+Ctrl+Shift+B); a real game (with and
  without exclusive full screen) and the measurement with PresentMon; whether the Do Not Disturb WNF value matches the
  real button.

## Traps

- **One `[ComImport]` class per COM object for the whole process.** `MMDeviceEnumerator` returns the same object for
  the whole process; with one class per module (media and volume had one each), .NET wraps it with the first one and
  the `new` of the other fails on the cast (the media visualizer froze). Create it through
  `ComDeAudio.NovoEnumerador()` and cast it to the module's interface.
- On his PC, `Ctrl + Alt + M` and `Ctrl + Alt + L` belong to another program: the timer uses `Ctrl + Alt + R`. A taken
  hotkey does not break anything, the module just says so through the tray; before choosing a new default, test with
  `RegisterHotKey` whether it is free.
- `Ctrl + Alt + Space` (the default hotkey) was taken on his PC; the island falls back to `Ctrl + Shift + Space` and
  says so through the tray. The config keeps the preferred one, not the one it ended up with.
- Testing a window on screen gets in the way of whoever is using the PC: check the foreground window before
  `--roteiro`, `--medir` and `--autoteste`. On 01/11/2026 a test photo caught Brave in full screen with `cordilheira`.
- Measuring fps with another program using the GPU gives a lower number: always compare with a reference running at
  the same time before blaming the island.
- **Third party images stay outside git** (the repository is public): the official headphone photos in
  `imagens/fones/*.png` (Apple, Logitech and other brands, source in `imagens/fones/FONTES.md`, which is in git) and the
  Claude and GPT logos and animations (`imagens\`, `videos\`, `src\Ilha\imagens\logos\`). They are on his PC and in the
  SSD backup; `.gitignore` holds them back. A new brand image goes in the same way. Without them the island opens
  normally (the spot stays empty).
- The HyperX photo has a white background: cut it out in phase 6 before using it. The Razer one is only 500 px.
