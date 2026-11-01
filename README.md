# Ilha

The iPhone Dynamic Island on Windows: a black pill floating at the top of the main monitor that grows, shrinks and
changes shape to show what is going on. It runs all the time without weighing the PC down (almost no processor and
about 125 MB of memory with everything on) and only takes the keyboard when you click it.

**Version 1.0.0:** the interface speaks Portuguese and English, following the Windows language, with an option in
Settings. The documentation is in English. Version 0.4 brought the main menu with every tool, the "< Menu" button and
Esc, the border that shows up on dark backgrounds, the time on the pill, the color picker, the file shelf, quick
notes, the per-app mixer, the recording indicator, the Claude limit and the air conditioner that only speaks up when
it changes.

## How to use

- **Install:** `powershell -ExecutionPolicy Bypass -File .\ferramentas\instalar.ps1` (in a regular terminal). It goes
  to `%LOCALAPPDATA%\Programs\Ilha`, gets an "Ilha" shortcut in the Start menu and starts with Windows. Running it again
  updates it; `-Desinstalar` removes everything (the settings stay).
- Once it is open, the island icon shows up next to the clock.
- **Icon menu (right click):** pause the island, docked to the edge (notch style) or floating, start with Windows,
  demo, **Settings...** (the window with everything), open config.json, open the Ilha folder (logs and measurements),
  check for updates and quit. Left click on the icon opens and closes the island.
- **Language:** the interface follows the Windows display language (Portuguese when Windows is in Portuguese, English
  otherwise). To force one, open **Settings...** and pick **Language: Automatic / Português / English**, or set
  `language` in `config.json` to `"auto"`, `"pt"` or `"en"`. The change applies right away: the modules restart and
  the icon menu is rebuilt in the new language.
- **Demo:** in the menu, turns on and off a 1 minute timer, a song, Claude Code working, PC usage, GPT answering and
  the animation gallery (all fake), shows an alert, or "Tour all the states" runs a tour by itself. From the command line:
  `Ilha.exe --demo claude` (or `timer`, `midia`, `uso`, `ia`, `galeria`), with `--expandir` to open the card.
- **Global hotkey:** `Ctrl + Alt + Space` opens the island; press it again to close it. If another program already
  uses it, the island takes `Ctrl + Shift + Space` and says so in the icon balloon. With nothing going on, it opens the
  **main menu**.
- **Mouse:** hovering gives a preview (it grows a little); clicking expands it with the controls; moving the mouse
  away collapses it after 600 ms (`atrasoRecolherMs`), and so does clicking outside.
- **Menu and back:** every open card has "< Menu" at the top. After you click the island, Esc goes back to the menu
  and, on the menu, collapses it. The island only listens to the keyboard after a click on it (Esc is never captured
  in the rest of Windows).
- **Dragging files:** drop files on the island to keep them on the shelf. With the island hidden, hold the file near
  the top center of the screen and the pill shows up to receive it.

## What it does

- **With nothing to show**, it stays as a small empty pill, over any window.
- **It hides** only in competitive games and in video or other full screen content (the programs in
  `appsDeConteudo`: browsers, players, PowerPoint). A regular full screen game keeps the island. When a video hides it,
  touching the top edge of the screen with the mouse, near the middle, brings it back.
- **Something new shows up expanded** for a few seconds and then turns into the compact pill.
- **Two things at once:** the most important one on the pill, the other in a bubble next to it.
- **Alerts** (headphones connected, the timer finished, Claude finished) open the island for a few seconds.
- **Competitive mode:** with a game from the list in focus (CS2, Rocket League, Valorant...), nothing shows up except
  the timer and its alert. Detection is only by the name of the program in focus, without touching the game (safe for
  anti-cheat).
- **Windows Do Not Disturb:** while it is on, the island does not open by itself for alerts, only for the timer
  finishing.

## Modules

Each one is turned on and off in the settings window (or in `config.json` → `modulos` → id → `ligado`).

| Module | What it does | Hotkey |
|---|---|---|
| Media | what is playing, cover art, visualizer, controls, drag to seek, mouse wheel = volume | |
| Volume | level when it changes; in the card, switch the audio output and the mixer (volume and mute per app) | |
| Privacy | green dot (camera) and orange dot (microphone) with who is using them | |
| Timer | timer, stopwatch and pomodoro; type `25m`, `1h30`, `90s` | `Ctrl + Alt + R` |
| PC usage | shows up when the processor, graphics card or memory is maxed out; card with everything | `Ctrl + Alt + U` |
| Claude Code | shows up only while working or asking for attention; notice when done, approve or deny permission | |
| Color picker | magnifier, exact pixel color in hex, RGB and HSL, history | `Ctrl + Alt + P` |
| Shelf | files and folders kept on the island to drag out later (only the path, nothing is copied) | |
| Quick note | one line with date and time in the Obsidian inbox (`arquivo` in the config) | `Ctrl + Alt + N` |
| Recording | red dot and elapsed time when OBS is recording or an app captures the screen | |
| Claude limit | session and weekly usage and when it resets (from the Claude Code status line) | |
| Calculator | math, units and live exchange rates | `Ctrl + Alt + K` |
| AI | chat with GPT and Claude | `Ctrl + Alt + J` |
| Translator | translates the selected text in any program | `Ctrl + Alt + T` |
| Air | the air conditioner through the `ar-do-quarto` server, in the menu; tells you when the air changes | `Ctrl + Alt + A` |
| Headphones | photo and battery of the headphones when they connect | |
| Screenshot | thumbnail of the screenshot, draggable into another program | |
| Render | notice when After Effects or Media Encoder finishes | |
| Notifications | Windows notifications on the island (experimental, off) | |
| Updater | looks for a new version in the GitHub releases | |

Hotkeys are changed in the settings window (`atalhosModulos` in the JSON); if another program already uses one, the
island tells you.

**Keys (AI and air):** you type them yourself, on the island (or in the settings window). They go to the Windows
Credential Manager (`Ilha/openai`, `Ilha/anthropic`, `Ilha/ar-do-quarto`) and never to a file.

**Claude limit:** the island reads what the Claude Code status line receives. In `~/.claude/settings.json`,
`statusLine.command` becomes `python "<island folder>/ferramentas/statusline_uso.py"`, and the status line you already
had goes to `modulos.limite.statuslineAnterior` in the island's `config.json` (the script calls it and passes on
whatever it shows). It only writes the limits to `%USERPROFILE%\.ilha\uso-claude.json`. Without the status line
running, the module shows that it has no data.

**Approving Claude Code permissions from the island:** it needs the hook in `~/.claude/settings.json`, under `hooks`:

```json
"PermissionRequest": [ { "matcher": "*", "hooks": [ { "type": "command",
  "command": "python \"<island folder>\\ferramentas\\hook_permissao.py\"", "timeout": 120 } ] } ]
```

Without the island open, or with it hidden (paused, competitive game, Do Not Disturb, full screen), the hook exits
right away and the regular Claude Code dialog goes on. The island waits for the click for `esperaSegundos` (default
45).

## Updates

"Check for updates" (or by itself, at most every 6 hours) looks at the repository releases. When it finds one, the
island shows it; click install and it downloads, checks the SHA-256, swaps the files and reopens. If the new version
does not open properly, it goes back to the previous one by itself. What it records lives in
`%USERPROFILE%\.ilha\atualizacao\` (`checagem.json`, `estado.json`, `resultado.json`, `atualizacao.log` and the copy
of the previous version in `anterior\`). The settings are never deleted.

## Settings

`%USERPROFILE%\.ilha\config.json` (icon menu, "Settings..." or "Open config.json"). Once saved, it applies right away.
If there is a typo, the island tells you and keeps the previous settings.

| Key | What it does |
|---|---|
| `language` | interface language: `"auto"` (default, follows Windows), `"pt"` or `"en"` |
| `colada` | `false` (default) floats; `true` docks to the top edge, notch style |
| `distanciaDoTopo` | distance from the top edge in floating mode (default 10) |
| `tamanho` | scale of the whole island (1 = normal, from 0.5 to 3) |
| `deslocamentoX`, `deslocamentoY` | pushes the island sideways or down |
| `sombra` | turns the shadow on or off |
| `atalho` | `mods` (1 Alt, 2 Ctrl, 4 Shift, 8 Win, added up) and `tecla` (key code; 32 = space) |
| `jogosCompetitivos` | list of `.exe` files for competitive mode |
| `prioridades` | order between activities (an alert always goes on top) |
| `dividir` | show the second activity in the bubble |
| `pilulaVazia` | the empty pill when there is nothing to show, over any window (formerly `ociosaNaAreaDeTrabalho`) |
| `appsDeConteudo` | `.exe` files whose full screen is content (video, browser, presentation) and hides the island |
| `respeitarNaoPerturbe` | obey Do Not Disturb and Focus sessions |
| `alertaSegundos` | how long an alert stays open |
| `atrasoRecolherMs` | how long to wait after the mouse leaves before collapsing (default 600) |
| `bordaAdaptativa` | the light border that shows up on a dark background (on by default) |
| `relogio` | the time in the middle of the compact pill (on by default) |
| `mola` | `amortecimento` (below 1 it overshoots and comes back) and `periodoMs` (speed) |
| `desenharNaGpu` | draw text on the graphics card (uses about 110 MB more; applies after reopening) |
| `pausada` | island paused |
| `iniciarComWindows` | open together with Windows (only works for the installed island) |

## Images and animations

The Claude and GPT logos, the videos of their animations and the headphone photos (for the "headphones connected"
animation) belong to third parties and **are not in this repository**. The island works without them (the image space
stays empty). The animations come from the videos in `videos\` through `python ferramentas\preparar_logos.py`, which
also draws the dots; the source of each headphone photo is in `src/Ilha/imagens/fones/FONTES.md`.

## For developers

Always build through `ferramentas\compilar.ps1` (it checks whether Smart App Control accepted the program). The self
tests of every module run without a window: `Ilha.exe --testar` (or `--testar <id>`). `Ilha.exe --teste-mouse` moves
the real cursor to reproduce the hover cases (only with nobody using the PC). How to write a module:
`src/Ilha/Modulos/README.md`. Publishing a version: `ferramentas\publicar-versao.ps1` (builds the package with the
SHA-256 and creates the release; it checks that the `.csproj` version matches the top of `CHANGELOG.md`). Details,
decisions and measurements are in `CLAUDE.md`.

**Private modules:** an optional `src/Ilha/local.props`, ignored by git, can add the sources of private modules to the
build on one PC. The public build (`ferramentas\compilar.ps1 -Publico`, which `publicar-versao.ps1` uses) skips it, so
the release only has what is in this repository.
