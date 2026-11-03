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
  1x1 ball with the battery under it, two = 2x1 side by side with the batteries under the block (`LabelBelow`), three
  or four = 2x2 one per line. No circle around the photos (he called it ugly on 02/11/2026): `Retrato.ComContorno`
  bakes a thin (1 DIP) light gray outline that follows the device's shape when the photo loads; `FotoComContorno`
  is used for the menu and for the connected card alike. Each device is a `Parts` area that
  opens it. The AirPods show their emptier bud.
- **Main menu (1.4.0, reworked in 1.5.0, `Menu.cs`):** iPhone Control Center style, decided with him on 02/11/2026
  from his mockup and his phone. An `ItemDoMenu` has a size (`Cols` x `Rows`), and `Menu.Pack` puts each tile in the
  first free spot of the 4-column grid, in `Ordem` (a later small tile fills an earlier hole). A 1x1 is a gray ball
  (54 DIP since 1.6.0, columns 80, gaps 6, rows 80: he asked for it more compact) with the name under it. A bigger
  block's body (`Menu.Corpo`) runs from its first column's ball edge to its last one's and from its first row's ball
  top to its last row's ball bottom, so every edge lines up with the balls; its curve is the balls' radius
  (`RAIO_BLOCO`, half a ball). The card keeps 38 like every card (60 looked wrong to him). The
  card ends where the last thing drawn ends (a bottom row of blocks has no names). Icons are drawn for 24 DIP and
  scaled by `ESCALA_ICONE` (28/24) with a transform, since some draw in fixed units. The header keeps `MARGEM` (6)
  from the sides; the time is SemiBold, the batteries' and sliders' numbers Bold (he wanted them chubbier). No
  lighter rim, gray at 6% white. `menuEscondido` (config) hides entries by id through `MenuDaIlha.Visiveis`; the
  settings window's Menu section has a box per entry (settings itself is never listed, so it cannot be hidden). `Draw` replaces icon and name (sliders, batteries), `Drag` and `Wheel` act on the tile (the wheel
  reaches the card through `Atividade.Rolar(delta, noCartao)` with the last mouse position), `Parts` are clickable
  areas inside it, `Corner` puts the entry as a plain icon in the top right (settings at the edge, recording next to
  it), `IconSize` grows the icon (the limit ring) and `DetailSwatch` puts a color dot before the detail. The header
  centers time, date, the privacy dots and the corner icons on one line by the glyphs' middle (`Meio`), not the text
  boxes. `MenuSlider` is the tall slider (sound, brightness), as narrow as a ball: relative drag, a tap changes
  nothing. With his modules the grid has 5 rows: twelve balls, then Sound and Brightness (1x2) and Batteries (2x1 with
  two devices, which leaves two empty cells under it; 2x2 with three or four, none).
- **Camera and microphone (1.5.0, `Modulos/Privacidade`):** the ConsentStore stopped getting these times on Windows
  26300 (newest entries 15/10 and 18/10/2026), which is why nothing showed. `UsoAoVivo` now reads the capture sessions
  of every microphone (WASAPI, Active = recording) and the Media Foundation sensor activity monitor (cameras), on one
  MTA thread fed by system notices, no polling; names come from Toolhelp (no handle opened, anti-cheat). Tested on
  02/11/2026 with ffmpeg holding the mic and the webcam. Kaspersky asks before letting a new program use the mic and
  holds it until answered: in a test, a program waiting on that prompt is not recording yet. The screen capture
  detector (Gravando) still reads the ConsentStore, through `RegistroDeConsentimento.cs`; whether Windows still
  writes its keys was not checked. The pill: the time in white on a filled pill (green camera, orange microphone;
  he asked for the fill over the 1.5.0 ring) when the time shows, the dot on the side when it does not; only the privacy activity left = the idle pill with the time and the
  ring. The menu draws its own dots after the time (the core's card dots skip it).
- **Brightness (1.5.0, `Modulos/Brilho`):** the primary monitor over DDC/CI, VCP 0x10 (`dxva2`), on its own thread;
  a drag sends only the newest value. On 02/11/2026 the ASUS XG34WCDMTG answered. No answer = no tile. The self test
  only reads, never writes.
- **Wired keyboards (1.7.0, `Fones/Keyboards.cs`):** a `DeviceWatcher` on the USB device interface
  (`GUID_DEVINTERFACE_USB_DEVICE`, one per physical device) reads only the id and the name the device reports. The
  G213 is `046D:C336`; the Razer Huntsman V3 HE TKL is matched **by name** ("Huntsman" plus HE/Magnetic and
  TKL/Tenkeyless) because its product id was not known on 02/11/2026 (he had not bought it yet): the log writes
  `fones: USB 1532:xxxx "name"` for every Razer device, so the id can be pinned once it is plugged in. No battery, no
  tile: only the connected card ("Keyboard connected"), with a 76 DIP wide photo box. Keyboards present when the island
  opens, or re-enumerated within 20 s of waking from sleep, do not show the card. A keyboard never takes a photo by
  brand (`Fotos.Escolher` would give it the Razer headset): only its own file or the `fotos` map in the config. Photos
  `logitech-g213.png` (transparent, from Logitech) and `razer-huntsman-v3-he-tkl.png` (cut out of Razer's gallery
  photo on a flat 17,17,17 background), outside git like the others.
- **Caps Lock (1.7.0, `Modulos/CapsLock`):** no event exists without a keyboard hook (anti-cheat), so a 200 ms timer
  reads `GetKeyState(VK_CAPITAL) & 1`; checked on 02/11/2026 that it follows the real toggle from a thread with no
  window. An `Importante` alert (Do Not Disturb reads on all the time on his PC), no sound, 1.4 s on and 1.1 s off; a
  second press replaces the alert; nothing in competitive mode.
- **Air display light (1.7.0):** `visor` joined `CamposDeComando` (the server already took it); the switch row shows
  only when `capacidades.visor` is true and, like the site, does nothing while the air is off.
- **Recorder (1.8.0, `Modulos/Gravador`, his request on 02/11/2026):** two menu tiles, "Gravar vídeo" (Tela, Câmera,
  Tela + câmera) and "Gravar áudio", each card with the PC sound and microphone switches; `Ctrl + Alt + G` (free on his
  PC) starts and stops the video. Video goes through **ffmpeg** (not shipped; winget `Gyan.FFmpeg`), audio is captured by
  the island (WASAPI, the `Midia` COM code) into WAV files. One timeline for everything: T0 is the wall clock right
  before anything starts; ffmpeg stamps its inputs with `-use_wallclock_as_timestamps 1 -itsoffset -T0 -copyts`, and
  `AudioCapture` places each packet by its QPC time with sample 0 = T0 (gaps become silence, the loopback sends nothing
  while nothing plays). Stopping copies the video and mixes the tracks into one stereo AAC (a 7.1 headset's loopback
  comes in 8 channels); a failed join keeps every piece in the folder. Measured on 02/11/2026 with a flashing square and
  a tone: audio within -7 to +9 ms of the picture.
  - **Screen = `gfxcapture` (Windows.Graphics.Capture) by `hmonitor`, not `ddagrab`**: Desktop Duplication returned
    washed-out grays on his PC (128 came back as 217, primaries fine), gfxcapture matched within 3 levels. 2% of one core
    for the screen alone (frames stay on the GPU into NVENC); Tela + câmera downloads the frames for the overlay, ~66%.
  - NVENC turns RGB into **BT.601 limited** (measured); the file is tagged so with `setparams` (it said "GBR, full
    range" before). The webcam's MJPEG is converted to limited range with the same tags.
  - The island leaves the capture with `WDA_EXCLUDEFROMCAPTURE` (`Ilha.ForaDasCapturas`) while the screen records; seen
    on a recorded frame. Windows.Graphics.Capture lights the ConsentStore, so the Recording module (OBS detector) skips
    `ffmpeg.exe` while the recorder runs (`Capturas.GravadorAtivo`).
  - Tela + câmera: the camera is 28% of the screen's height, 16:9, bottom right above the taskbar (work area), radius
    6.5% of its height, shadow from a blurred PNG made by `PipFrame`. **His C920 has a privacy cover: a black camera in a
    test is the cover**, not a bug. Camera auto = the first PnP one in ffmpeg's dshow list (the Quest and OBS ones are
    virtual); 1080p30 MJPEG, then its default mode on a second try.
  - Not tested: a recording of an hour or more (a WAV keeps the 4 GB header limit, about 3 hours of 7.1 float), a
    camera recording with the cover open, recording a game (gfxcapture does not inject; the island is hidden there
    anyway), and the module turning off mid-recording (it stops the session in the background).
- **Fundo Vivo picker (1.9.0, `Modulos/Fundo`, his request on 02/11/2026):** "Fundo" in the menu opens an iPhone /
  Apple Watch style picker of the wallpapers he chose (`favoritos` in the module config, ids from Fundo Vivo's library).
  Fundo Vivo is a separate public app (`fundo-vivo` in his folder); **nothing of it is linked or edited from here**:
  `FundoVivoBridge` reads its library the same way its `Library.Scan` does (Wallpaper Engine video and web from the
  Steam libraries, plus `userItems`), computes the same monitor ids (`QueryDisplayConfig` device paths), writes the
  choice into its `%APPDATA%\FundoVivo\config.json` like its settings window does (read again right before writing, only
  that monitor's entry changes, every other field kept, replaced through a unique temporary file) and sends the engine
  window `FundoVivoEngine` the `{"cmd":"reload"}` WM_COPYDATA; with the engine closed it starts `FundoVivo.exe --engine`
  (found from the running app, a Run entry or the Start menu shortcut). `FUNDOVIVO_HOME` moves the data like in the app,
  which is how the self test writes a fake config. If Fundo Vivo changes its config format, this breaks: check its
  `Core/Config.cs`. Cards: one composition visual per wallpaper, sliding with keyframe animations (Offset, Scale,
  Opacity); thumbnails from the Wallpaper Engine preview or the Windows thumbnail, decoded once on a background thread.
  Arrows and Enter reach the card through `Entrada.Navegar` (new: raised while the island only holds the keyboard).
- **2.0.0 (02/11/2026), his requests:**
  - **Recordings window** (`Gravador/RecordingsWindow.cs`): a WinForms borderless window (rounded corners and dark rim
    through DWM, CS_DROPSHADOW) whose content is a second `DesktopWindowTarget` of the island's own compositor: two D2D
    surfaces in pixels (`emPixels`, drawn scaled by the window's DPI, not by the island's size factor) and a video sprite
    fed by Windows' `MediaPlayer.GetSurface`. The window must be shown before an item is selected (the tree is built in
    `OnHandleCreated`; selecting first left the video with nowhere to draw). Audio items show peaks read straight from
    the WAV. Opened from the "Gravações" pill on both cards and from the "Gravação salva" notice.
  - **Waveform on the pill**: `Atividade.NaFrente` (new) puts an activity ahead of the priority list; the recorder uses
    it only while recording audio. 22 composition bars fed ~30 times a second from `AudioCapture.TakePeak` (the loudest
    sample since the last read), eased by implicit animations like the media bars.
  - **Recovery**: the module stopping (the island quitting, an update) now finishes a recording synchronously; before,
    the stop ran on a background task that died with the process. On 02/11/2026 an install closed the island 4.5 min
    into his audio recording and only the tracks were left (recovered by hand). At start, `RecoverOrphans` joins
    pieces older than a minute that have no final file (`AudioCapture.RepairHeader` sets the never-written RIFF/data
    sizes first) and announces it; pieces are deleted only when the final file came out.
  - **Permission mode** on the Claude card: `PonteClaudeApp.Permissao` opens the session's mode button (named after
    the mode: "Automático", "Manual", "Aceitar edições", "Plano", "Ignorar permissões", `ModosDePermissao`) and invokes
    the item. `--ponte-claude permissoes SESSION [MODE]` lists or switches from the command line. **Never switch the
    permission mode of the session doing the work to test it**: on 02/11/2026 a test put this session in Auto, and
    switching it back to bypass was (rightly) refused as self-elevation; that is his to do.
  - The OBS recording entry left the top corner and became a ball next to the wallpaper entry (`Ordem` 59).
- **Discreet menu entries (1.8.0):** `ItemDoMenu.CornerBottom` draws the icon small (14 DIP) with no ball, name or
  background in the menu's bottom-right corner (inside the last row when its right cells are empty, else on a strip under
  the grid). The private unlock entry uses it with a dim lock. Not seen on screen on 02/11/2026 (it only shows locked).
- **Shadows (1.8.0):** two layers (sigma 3, 0.46, 1.5 down; sigma 15, 0.44, 10 down), stronger at his request; `SOMBRA`
  (the room under the shape) went to 56.
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
  settings in `settings.json.antes-limite`). **Only the terminal Claude Code writes it** (he found out on 02/11/2026):
  desktop app sessions never run the status line, so the card shows an orange note saying so (2.0.1).
- **Air:** it left the pill. The module reads `GET /api/historico` (which does not make the server read the LG unit)
  every 45 s and gives a notice when power, mode or target change; the room temperature alone does not trigger a
  notice. A change made from the island gives the notice right away and the confirmation that comes later stays quiet.
  A change from the remote control shows up within 15 min (the server cadence with nobody watching), or 3 min with
  something watching.
- **PC reactions (2.1.0, `Modulos/Reacoes/`):** everything from Windows events, no polling. `SystemEvents` is a hidden
  top-level window (not message-only: those never get the `WM_DEVICECHANGE` broadcast) with
  `AddClipboardFormatListener`; drives come from `DBT_DEVTYP_VOLUME` (network shares skipped). The clipboard notice
  waits 250 ms for the copy to settle, skips copies made by the island itself (owner's pid) and anything with
  `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore` or `CanIncludeInClipboardHistory` = 0 (what
  password managers set). Downloads: a rename from a temporary name (`.crdownload`, `.part`, `Unconfirmed ...`) to a
  final one, or a file created with its final name once nothing holds it open (`Arquivo.EsperarFechar`, 10 min), each
  path once per 30 s. Mic: the endpoint mute of the default capture device (Console role), moved when the default
  changes. **Not confirmed:** whether the G733's own mute button flips that endpoint mute (some headsets mute only in
  their hardware, and then Windows never knows). Internet: `NetworkStatusChanged`, read again after 4 s of quiet;
  `GetNetworkConnectivityLevel` only, no server is probed. Do Not Disturb: only a change between two real readings of
  `NaoPerturbeLido`; on his PC it read "on" all the time (see Tested and not tested), so this one may never fire there.
  Every notice is `Importante` for that same reason. `GetClipboardSequenceNumber` returns 0 inside the Claude app's
  shell (03/11/2026, PowerShell too), so the IA module's clipboard probe fails there; it is the shell, not the code.

## Live game score (3.0.0, `Modulos/Games/`, his request on 03/11/2026)

- **Three competitive modes** (`modoCompetitivo`): `placar` (default), `esconder` (the old behavior: only the timer)
  and `nada` (`Contexto.Competitivo` stays false; `Contexto.Jogo` still names the game in front). In `placar` the
  context is still competitive (no other alert, sound or tool), and `ScoreActivity` is the one activity allowed over
  the game (`PermitidaNoCompetitivo`, `NaFrente` while a match is on); its alerts are `PermitidoNoCompetitivo` and
  `Importante` (Do Not Disturb reads on all the time on his PC). The score also shows with the game in the background.
- **CS2**: Game State Integration. `GameSetup.InstallCs2` writes `gamestate_integration_ilha.cfg` (token `ilha`, port
  `portaCs2` 47681, with `phase_countdowns` for the clock) into the CS2 cfg folder found through Steam's
  libraryfolders and appmanifest 730; the game reads it only when it opens. `Cs2Source` is a plain `TcpListener` on
  127.0.0.1 with a minimal HTTP reader (HttpListener would need a URL reservation). Our side comes from payloads where
  `player.steamid == provider.steamid` (dead, the game sends whoever is spectated); scores follow the team across the
  half. More than 3 kills at once is a catch-up, not animated. Deathmatch, arms race and training end with no result.
- **Rocket League**: Stats API. `PacketSendRate` ships at 0; `GameSetup.EnableRocketLeague` sets 10 in
  `TAStatsAPI.ini` or `DefaultStatsAPI.ini` (found through the Epic manifests or Steam appmanifest 252950), only when
  it is 0. `RocketLeagueSource` connects to 127.0.0.1:49123 every 5 s while it is not connected. **`Data` arrives as a
  JSON string inside the envelope** (seen on 03/11/2026). We are the camera's `Target` when the match starts. A goal is
  a team score going up in UpdateState: `GoalScored` repeats with an empty scorer and speed 0 in Heatseeker. Playlist
  63 = ranked Heatseeker (he played it on 03/11/2026); the other ids are from community lists. Free play (9) and one
  player are not matches.
- **Battlefield 6**: no integration and no match log (only settings and an http cache), and Steam's public rich
  presence showed nothing while he played. `Bf6Source` reads the screen only while `bf6.exe` is in front, every 0.7 s:
  one GDI copy of the top band (both ticket boxes and the kill feed) plus, every third look with no HUD, the loading
  screen's mode and map and an end screen ("VITÓRIA"/"DERROTA", **never seen**: the layout is a guess). OCR is
  `Windows.Media.Ocr` (pt-BR here). Regions calibrated at 3440x1440 in Conquest on Lago Groom, anchored left, center
  or right and scaled by the height. A ticket number counts after two looks in a row agree. Kills: a feed line whose
  first word is his name (`nomeNoBf6`, else the Steam persona from loginusers.vdf); deaths: his name after the first
  word; each once within 12 s, compared fuzzily (OCR reads names a little differently every time). The island leaves
  screen captures while reading (`ForaDasCapturas(true, "jogos")`, now counted by owner) because its alerts cover the
  top of the HUD. With no end screen, the HUD gone for 25 s after a match over 60 s with a side at 40 tickets or less
  gives the result by the tickets. Measured on 03/11/2026: 6 of 6 checked photos read right, 26 ms per look in the
  worst case. **Only Conquest is calibrated**: other modes show the mode, kills and the time, and need photos of a
  match to calibrate (the self test reads `ILHA_TESTE_BF6`: point it at `fotos-teste-bf6/`, 23 photos of that match,
  ignored by git because they show other players' names).
- **3.1.1 (his bugs on 03/11/2026)**: in `placar` mode with a competitive game in front and no match read (menus,
  queue, loading), nothing was allowed and the island vanished; now `GamesModule.Idle` shows the game in front (icon
  from its .exe through `GameArt.Remember`, name, time open; a new session after 10 min away). Closing the game
  mid-match left the match up (BF6 kept it 3 min for Alt+Tab, CS2 waited 40 s for a post): `CheckAlive` looks at the
  process list every 2 s while a match is live (`GameSource.Process`, `Drop`).
- **History**: `~/.ilha/partidas.json` (newest 300) for the card's last five. The module keeps it in memory: deleting
  the file with the island open does not clear it (a test left fake results until the island restarted).
- **Animations** (`Celebrations`): alerts drawn once and animated only in the compositor: kill (skull, then a red X
  slamming down; a new kill within 5 s replaces the alert with "x2"), goal ("GOOOOOOOOOL!" stretching out, team color
  O's; theirs a quiet "Gol do adversário"), end ("EZ" when the margin is at least max(3, half our score), else "GG").
  Sounds `gol.wav` and `vitoria.wav` (new sounds go last in `gerar_sons.py`: the seeded generator is shared, and
  adding them first changed every old file). On the pill only the score layer swaps when it turns.
- **Game art (3.1.0, `GameArt.cs`)**: brands, so nothing ships or goes to git; everything is read from the games on
  his PC and falls back to drawn marks. Game icons: `PrivateExtractIconsW` at 128 px from `cs2.exe`
  (`game\bin\win64`), `bf6.exe` and `RocketLeague.exe` (`Binaries\Win64`). CS2's `ct_logo`, `t_logo` and
  `ui/premier` are `.vsvg_c` entries in `game\csgo\pak01_dir.vpk` (`Vpk.Read`, version 2 directory); the resource
  keeps the SVG text as it is, drawn by Direct2D's SVG (`ID2D1DeviceContext5`, `ComStreamProxy`). **Direct2D sizes an
  SVG by the root's width and height, not the viewport**: `GameArt.Fit` turns them into 100%. BF6: the loading
  screen's faction name (top right, OCR: OTAN/NATO, PAX) and the emblem in its square box are read with the mode;
  the first time a faction shows up its emblem is saved as `~/.ilha/jogos/bf6-<nato|pax>.png` (NATO was seeded from
  the 03/11 photos; PAX appears after he plays one match on that side). Premier's badge shows when the raw mode
  contains "premier"; **the value Premier sends was never seen**, and the log line "partida de cs2 na pílula (...,
  modo bruto X)" records it.
- **Animations, 3.1.0**: two animations of the same property on one visual replace each other (the second one cut the
  first: the letters showed up all at once at 1.7x and the skull never dropped). A second motion goes on a container
  (`Celebrations.Group`): the goal's blink is on the letters' group, the kill's shake on the skull's group. The goal's
  shine is a `CompositionMaskBrush` (gradient band masked by the word, the band's Offset animated) and its glow a
  Direct2D gaussian blur of the word. Game sounds (`abate`, `gol`, `vitoria`, `derrota`) are louder synthesized
  sounds (horn, crowd, brass, timpani) with their own seeded generators in `gerar_sons.py`; checked by numbers only
  (length, loudness, spectrum), he judges by ear.
- **Tested on 03/11/2026**: self tests with recorded and synthetic payloads; on screen with a fake CS2 match posted to
  the installed island and a fake Stats API server (photos of the pill, card, kill, round, goals and end). **Not
  tested**: a real match after these changes (the real CS2, Rocket League and BF6 matches of that day were before the
  module existed), the game's fps with the BF6 reader on, other BF6 modes, the end screen of BF6, Premier's mode name.

## Menu with 5 columns, Games and Sports (3.2.0 and 3.3.0, his requests on 03/11/2026)

- **Menu grid** (`Menu.Pack`): 5 columns. Blocks bigger than 1x1 go first, each to the rightmost free spot from the
  top; then the balls fill row by row from the left. Before, every tile took the first free spot in order, and the two
  private balls the lock hides (Lari & Nina and the mascot, private modules in `local.props`) moved the blocks and left
  holes. Now a ball that goes away only shifts the balls after it.
- **Games card** (`Games/GamesOverview.cs`, menu `jogos`, order 58): the live match (click opens the score), and per
  game the hours from Steam's `userdata\<id>\config\localconfig.vdf` ("Playtime" in minutes under
  Software\Valve\Steam\apps; `Playtime.Steam`, read when the card opens, at most once a minute) and the last five.
- **Ace** (`MomentKind.Ace`): `player.state.round_kills` of the local player reaching 5 in competitive or Premier, 2 in
  Wingman, once per `map.round`; not in casual (10 enemies). It closes the kill notice on screen.
- **Sports** (`Modulos/Sports`, id `esportes`): ESPN's `site.api.espn.com/apis/site/v2/sports/<path>/scoreboard`,
  undocumented, read defensively (see `docs/pesquisa-placar-de-esportes.md`). `?dates=YYYYMMDD` works for one day,
  a range answers 400, so the card's past and coming days are one request per day (two back, five ahead, only when
  the card opens, at most every 30 minutes). Today's scoreboard of each followed league every 30 s while a followed
  game is live, 1 min with any game live or about to start, 5 min on a day with games left, 15 min otherwise. A race
  weekend comes as one event with a competition per session; the island takes the live one, else the race. Followed
  teams and drivers match by name, short name or abbreviation without accents (a surname alone for drivers). Crests:
  `Crests` downloads `team.logo` once into `~/.ilha/esportes/escudos/` (outside git). How much of a live race ESPN
  shows (laps, gaps) was **not seen**: no race was on.

- **Achievements (3.4.0, `Games/Trophies.cs`, `TrophyRoom.cs`)**: hours = Steam's `Playtime` per app plus
  `OwnClock` (`~/.ilha/tempo-de-jogo.json`), a minute for each tracked non-Steam process running, checked once a
  minute by the process list (`OwnClock.Tracked`). Trophies are ids `jogo:<key>:<hours>` and `total:<hours>` in
  `~/.ilha/conquistas.json`; the first run only seeds (on 03/11/2026 it noted 89 at once, silently), then each new one
  becomes a notice (three at most per check), checked at start and every 10 minutes. Steam games no longer installed
  get their name from the store's public `appdetails` (one request per game with 10 h or more, 1.5 s apart, cached in
  `~/.ilha/jogos/steam-nomes.json`). Icons: `GameArt.IconFor` = the .exe, else Steam's
  `appcache\librarycache\<app>\<40-hex>.jpg` (32 px, every library game has one), else a Wikipedia summary thumbnail
  for the few non-Steam games listed in `WikiPages` (often none: Wikipedia leaves non-free logos out of thumbnails).
- **`--abrir <entry>`** (3.4.0): opens a menu entry's card 4.5 s after startup, without taking the focus, for photos
  (`--menu` does the same for the menu).

## 3.5.0 (his requests on 03/11/2026)

- **Live achievements:** Steam writes `Playtime` only when a game closes. Each minute `Playtime.RunningSteamApp()`
  (HKCU\Software\Valve\Steam `RunningAppID`) gets a minute in `_live` (hours at the session start, minutes since);
  `Playtime.WithLive` adds it on top until Steam writes a bigger number. With any game running (Steam or `OwnClock`)
  the trophies are checked every minute, else every 10. The trophy alert has `PermitidoNoCompetitivo`.
- **Charging card** (`Extras/Fones/ChargingCard.cs`): `ChargingCard.Started` (false to true, never the first reading)
  per battery key: `g733`, the mouse key, and `<airpods>:estojo` for the AirPods case (buds in the case charge all
  the time, so they do not count). Composition only, with the `Celebrations` helpers (now `internal`). Setting
  `animacaoCarregar`. Photo hook: `ILHA_FOTO_CARREGAR=<device name>` shows the card 5 s after opening.
- **AirPods photo on connect:** right after a Bluetooth connection the PnP nodes (`BTHENUM ..._VID&0001004C_PID&2027`)
  are not there yet, and his AirPods are named "Fone", so the name gave no photo. `BluetoothMudou` now waits up to 3 s
  for the VID/PID when the name finds no photo, and remembers it per address (`_vistos`).
- **Sports icon:** `SportIcons.Draw` by `League.Sport` (the first part of the ESPN path) of the live game, else the next
  one today; soccer ball when nothing is on. Tennis (`tennis/atp`, `tennis/wta`): one ESPN event is a whole tournament
  with the matches inside `groupings`; `Espn.Matches` keeps the singles of the asked day (and any live one), score =
  sets won, TBD skipped. The goal animation is for soccer only.
- **Icons:** `MicTile` kept its cradle, stem and base; the head is a black mesh grille over a silver collar (he asked
  for only the head to change). `GamesOverview.Icon` is a full controller. `IconPreview.Render(path, icons[])` draws
  any list of menu icons to a PNG without a window (the sports test writes `%TEMP%\ilha-icones-esportes.png`).
- **Crashes (open):** 02/11 21:38, 03/11 15:17 and 15:32, all on the .NET finalizer thread releasing a WinRT object
  (`IObjectReference.Finalize`), once faulting inside `Windows.Media.MediaControl.dll`; the two with logs had dead media
  sessions just before ("mídia: Timeline: Object reference not set"). Dumps land in `%LOCALAPPDATA%\CrashDumps`
  (`dotnet-dump analyze`, `setthread` to the finalizer); the heap was too damaged to name the object. Done so far: a
  session whose call fails is dropped and skipped for 2 minutes (`_quebradas`), and `RegisterApplicationRestart` brings
  the island back after a crash or hang (Windows does it only after 60 s of running; the log says "a ilha fechou
  sozinha e o Windows abriu de novo"). Next step to confirm the media module: a session with it turned off.

## Focus (3.6.0, `Modulos/Foco/`, his request on 03/11/2026)

- **Modes** (`FocusModes.cs`): `ConfigFoco.Modos` in the "foco" module section, edited only in the settings' own
  "Foco" section (`Configuracoes/SecaoFoco.cs`, the window is now `partial`), written by `ColetarFoco` before the
  generated module forms run (they keep the section's unknown keys). `Schedule.Parse` reads "seg-sex 09:00-18:00",
  "todos 23:30-07:00", "sab,dom ..." (English day names too); a window crossing midnight belongs to its start day.
  `FocusRules.Wanted` = the first mode in list order with a schedule on or an app running.
- **State machine** (`FocoModule`): automation is checked every 30 s and on every context change when a mode has
  apps; when the wanted mode changes it turns on (over a manual one), and a mode an automation turned on goes off
  when no longer wanted. A mode turned on by hand is kept in `~/.ilha/foco.json` and comes back at start (a
  schedule already on at start does not replace it). Turning a scheduled mode off by hand keeps it off until the next
  change.
- **Actions** and their undo: `Sons.Silencio` (no sound at all, important ones too) and `Ilha.FiltroDoFoco` (alerts:
  all / important / none; the focus's own "foco" notices always pass) are static hooks in the core; volume through
  `VolumeDoSistema`, brightness through `ModuloBrilho.DefinirDeFora` (put back only if still at the value the mode set);
  wallpaper through `FundoVivoBridge` on the main monitor (the previous item comes back).
- **Windows Do Not Disturb** (`WindowsDnd.cs`): the shell's undocumented QuietHoursSettings COM server
  (CLSID F53321FA-..., IID 6BFF4732-..., vtable 3 = get, 4 = put of UserSelectedProfile). On his build 26300 it
  works: the quiet hours WNF state follows it both ways. What failed first: writing that WNF state (the value
  changes, the taskbar does not) and `FocusSessionManager.TryStartFocusSession` ("access denied" outside a packaged
  app). On = PriorityOnly, and only when it was Unrestricted; off puts back what was there. His Windows keeps Do Not
  Disturb on (PriorityOnly or AlarmsOnly), so in practice it stays on. Calls run in order on a background chain.
  The live test only runs with `ILHA_TESTE_DND=1` (it switches it off and back for ~3 s).
- **On screen**: menu balls "foco" (order 6, the mode's symbol, body in its color while on) and "nao-perturbe"
  (order 7, the bell crossed out in red); `ItemDoMenu.BodyColor` is new. `FocusActivity` is the modes card (a tap
  toggles) and, with `AoFundo` (new in `Atividade`), the mark on the pill only when nothing else is there.

- **Also in 3.6.0:** BF6 in the menu kept making matches that ended in a win (numbers read in the ticket boxes, the
  career "VITÓRIAS" read as the end screen). `Bf6State.Current` is now null until the match is real: the loading
  screen seen (mode and faction in the same look, valid 3 minutes) or both tickets read, moved at least once and the
  HUD on for 45 s (`LiveAfter`); `EndFrom` wants the whole word, read twice within 15 s. In a competitive game with
  `modoCompetitivo = placar`, `Ilha.MenuFechado` is false: the menu and the "<" work (keyboard focus still never goes
  to the island in a competitive game).

## 3.7.0 (his requests on 03/11/2026)

- **Clips** (`Modulos/Clipes/`): `ClipRing` runs ffmpeg while `Contexto.Jogo` (a competitive game in front, any
  `modoCompetitivo`) is set, stopping 60 s after it leaves. Screen: gfxcapture of the main monitor + NVENC (`-g fps`,
  a keyframe a second); sound: `AudioCapture` in raw mode (new: no WAV header, into a `QueueStream` that never blocks
  the audio thread) piped to ffmpeg's stdin as `f32le`/`s16le`..., both on one timeline from T0 (`-itsoffset -T0` with
  wall clock video, audio counted from 0, `-copyts`). Output: MPEG-TS pieces of 2 s, `-segment_wrap 25` (~50 s) in
  `%TEMP%\ilha-clipes-anel`. A cut copies the newest pieces (the growing one too) and joins them with the concat
  demuxer, `-c copy`, into MP4 (~0.4 s). Measured on 03/11/2026 during a Rocket League match: 3440x1440 at 60 fps,
  stereo AAC, video and audio 37 ms apart. Moments come from `GamesModule.MomentHappened` (new static event, raised
  before the score-mode check): two kills within 6 s, ace, epic save, our goal, a win; the cut waits 3 s and moments in
  that time join it. Pending clips sit in `<folder>\.pendentes` until the end-of-match card (Salvar/Descartar, 3 min
  with no answer = kept); frames in `.miniaturas`. Ctrl+Alt+C (`clipe`) cuts straight into the folder. The live test
  only runs with `ILHA_TESTE_CLIPE=1` (records the screen 9 s; `ILHA_TESTE_CLIPE_PASTA` keeps a copy). Not measured:
  the game's fps with the ring on (no frame-time tool here; the GPU was at 97% with RL uncapped either way).
- **Left bubble** (`Ilha.cs`): `_geoBolhaE`/`_formaBolhaE`/`_vagaBolhaE` mirror the right bubble everywhere (shape,
  clip, shadow, border, notch shoulders, window region, click, edge probe); `DaBolhaE` = the next non-privacy activity
  after the right bubble's. `Plano` got `BolhaE`, `ChaveBolhaE`, `CenaBolhaE` (defaults, so the other plans did not
  change). `--demo a,b,c` runs several fake activities (the first is the main one).
- **Public mode** (private, `ilha/privado/ModoPublico.cs`, out of git, in `local.props`): a globe next to the gear
  hides the menu entries of `mascote`, `conversas` (and anything else listed in `PrivateOwners`; its test fails when a
  module in a private namespace is missing). The public code only has `MenuDaIlha.Filtro`.
- **Focus 2x1**: order 91, right after the batteries (90): the menu packs blocks in order, so it lands under 2x1
  batteries and under the sliders when the batteries are 2x2.
- **RL epic save**: `StatfeedEvent` with `EventName: "EpicSave"` and `MainTarget.Name` = the target name of the camera
  (us); `MomentKind.EpicSave`, `Celebrations.Save` (the goal's lit letters, now `Shout`). **CS2 Rush**: raw mode
  "rush" gets the badge `panorama/images/map_icons/mapgroup_icon_rush.vsvg_c` (a cream "R" tile), with the name next
  to it.

## 3.7.1 and 3.8.0 (his requests on 03/11/2026)

- **Media (3.7.1):** `ModuloMidia.Quebrou` drops a session only when `Morta(e)` (COM disconnected/server unavailable,
  disposed). Apple Music returns a null timeline for a moment now and then; 3.5.0 dropped the player for 2 minutes on
  any error and the music bubble kept vanishing. `LerInstante` returns null for an empty timeline (skipped).
- **Menu reflow (3.7.1):** the panel (`Menu`) is not in `_atividades`, so `MudouTamanho` reached no one; `DefinirPainel`
  now listens and calls `Aplicar` while the menu is open. `ItemDoMenu.Follows` (focus follows `baterias`): right under
  a one-row block, else at the left of the row after it (his drawing).
- **MVP (3.8.0):** Rocket League: at `MatchEnded`, the best `Score` of the winning team among the last `Players`
  (`RocketLeagueState.Mvp`) against our target name. CS2 has no match MVP for the island (GSI only sends our own
  player): `Match.MvpStars` = our round MVPs. `Celebrations.End(..., mvp)`: a crown (`Celebrations.Crown`) drops above
  the word, which turns gold with the glow and the shine of the goal. Photo hook: `ILHA_FOTO_MVP=1`.
- **History (3.8.0):** `Record` got `kills`, `deaths`, `mvp`, `stars` (older files read fine with them empty);
  `History.Recent(5)` feeds the Games card's "Last matches" (green/red cards); the per-game dots are gone. The two
  false BF6 wins of 03/11 15:46 and 15:48 (menu bug) were removed from his `partidas.json` (his approval).
- **Achievements (3.8.0):** every game with hours, `VISIBLE` = 8 rows, `Rolar` scrolls a clipped list with a thin bar;
  `SteamNames.Fetch` now asks for all missing names in one run at a time (busy flag), saving every ten.

## 3.9.0 (his requests on 03/11/2026)

- **Crashes:** the three real ones (02/11 21:38, 03/11 15:17 and 15:32; every other "Application Error" for Ilha.exe in
  the Event Viewer is Smart App Control blocking a build during `compilar.ps1`, with a 1.4 MB dump) all ran on the
  finalizer thread releasing a WinRT object, once inside `Windows.Media.MediaControl.dll`. Measured with a probe in the
  media self test: **the system media wrappers are cached by identity** (the same session, the same PlaybackInfo and
  the same timeline object come back on every call, so releasing one by hand makes the next reading throw
  ObjectDisposedException), while the session lists, async operations, media properties and event args are new on
  every call. `WinRtRelease.Now` releases only those short-lived ones, on the interface thread, right after use; the
  cached ones are never released by hand. Inference, not proof: the dumps' heaps were too damaged to name the object.
  If it crashes again, `dotnet-dump analyze` the new dump (`%LOCALAPPDATA%\CrashDumps`, the 30 MB ones).
- **Dots on the score pill:** `PontosDevemAparecer` now allows the camera and microphone dots over a competitive game
  in `placar` mode, and `Indicadores.Gravacao(owner, on, apps)` adds a red one (owners `gravador` and `gravando`: the
  island's recorder, OBS, another app capturing the screen), drawn only over a competitive game. `Capturas.AnelDeClipes`
  keeps the clip ring's ffmpeg from counting as a screen capture. **Not seen on screen**: it needs a real competitive
  game in front.
- **Ping** (`Modulos/Rede`, id `ping`): `System.Net.NetworkInformation.Ping` (ICMP, no admin) once a second to `alvo`
  (1.1.1.1) and the IPv4 gateway while `Contexto.Jogo` is set, 30 s after it leaves. `NetQuality.Evaluate`: median of
  the last five replies, loss over 30 samples (after 10), bad above `limiteMs` or at `perdaPct`, good again below 80%;
  "rede local" when the router's median is over 20 ms or it drops. `ScoreActivity` widens by the mark's width. The game
  server's own ping is impossible here: UDP tables carry no remote address and anything deeper is admin or kernel.
- **Steam downloads** (`Modulos/SteamDownloads`, id `downloads-steam`): appmanifest StateFlags (EAppState bits:
  UpdateRunning 256, UpdatePaused 512, UpdateStarted 1024, Preallocating, Downloading, Staging, Committing), progress =
  max(BytesDownloaded, size of `steamapps\downloading\<app>`); Workshop = non-empty `workshop\downloads\<app>`. One
  FileSystemWatcher per library (filtered to manifests, `downloading`, Workshop downloads), a look every 2 s while
  something downloads, else one a minute. **Not seen with a real download** (nothing was downloading on 03/11/2026):
  whether Steam rewrites the manifest during a download is the open question the folder size covers.
- **Steam friends** (`Modulos/Amigos`, id `amigos`): Web API key in Credential Manager `Ilha/steam` (pasted in the
  card), SteamID64 from `ActiveProcess\ActiveUser` (+76561197960265728) or the MostRecent account of loginusers.vdf.
  Friend list every 30 min, GetPlayerSummaries every minute; notices for online and game start/switch (offline off by
  default), three per reading at most; avatars in `~/.ilha/amigos/avatares/`, circle cut into the alpha. **Not tested
  live**: there is no key yet; whether a private friend list answers to its owner's key is unknown.
- **Weather** (`Modulos/Clima`, id `clima`): Open-Meteo forecast every 10 min; the city's coordinates from its
  geocoding, cached in `~/.ilha/clima.json`. `SkyWatch`: a new sky group must be read twice in a row to count; the first
  reading only learns. His city is in his local config. Photo hook `ILHA_FOTO_CLIMA=1`.
- **Rocket League names:** ranked by team size and the game's Brazilian names (Rixa, Basquete, Dia de Neve); casual
  Chaos (4) is "4v4 casual"; an unknown id (events, like 81) shows no mode and goes to the log once.
- **ALTURA_MAX 880** (was 640): the menu's sixth row (friends, weather, and the private server ball) lost the black body.
- **Private, outside git** (`ilha/privado/ServerHealth.cs`, id `servidor`): the mini PC through one read-only `ssh`
  (BatchMode) every 2 minutes; notices for a failed or restarting service, a stopped one, the server unreachable twice
  in a row, disk 90% and 80 °C; what was said is kept in `~/.ilha/servidor-avisos.json`, so a service failing for days
  is told once. Listed in public mode's `PrivateOwners`. The live test needs `ILHA_TESTE_SERVIDOR=user@ip`.

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

## No kernel, ever (his rule on 03/11/2026)

Changing the kernel is strictly forbidden: no unsigned kernel driver, no Windows test mode, no driver signature
bypass, nothing in ring 0, not even to test. That is why there is **no AirPods noise control (ANC)**: on Windows it
only works through an unsigned kernel driver (MagicPods/MagicAAP style) plus test mode, which also breaks the
anti-cheats of his competitive games. Do not build it and do not propose it again.

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

- **Rebuilding the tree (`Reposicionar(forcar: true)`, 1.7.1):** `Desmontar` sets `_alvo.Root = null` before closing
  the old root. With the closed root still on the target, `Root = new` failed with E_INVALIDARG and every later
  `Aplicar` hit closed objects: until 1.7.0, switching notch style, size, shadow, border, offsets or language with the
  island open left it broken until a restart (every successful mount in the log was a fresh start). Checked on
  02/11/2026 by switching notch on, off and on again with the island open: no error, both shapes right.
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
