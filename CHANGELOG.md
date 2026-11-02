# Changelog

## 1.6.1 (02/11/2026)

**Changed**
- The sound and brightness numbers are a little smaller (still bold).

## 1.6.0 (02/11/2026)

**New**
- A Menu section in the settings: one box per entry of the main menu, to hide what you do not want there. A hidden
  entry only leaves the menu; the module keeps working.

**Changed**
- Chubbier numbers: the menu's time, the pill's time and the batteries' percentage are bolder, and the sound and
  brightness numbers are bigger and bold.
- The menu is more compact: smaller balls and gaps, a smaller header.
- The outline around the devices is thinner, and it is now also on the photo of the card that shows when a device
  connects (or when you open it from the menu).

## 1.5.1 (02/11/2026)

**Changed**
- Camera or microphone in use: the time now sits on a filled pill of that color, in white, instead of a ring.
- The menu is more even: the card has its old rounding back, the sound and brightness sliders are as narrow as the
  balls and line up with them, the batteries block lines up with the balls too (the batteries go under it, where the
  names are), every block has the same curve as the balls, the icons are a bit bigger, the time and the corner icons
  keep away from the corners, and there is no empty band at the bottom anymore.
- The device photos lost the gray circle: the light gray outline now follows the shape of the mouse and the headset.

## 1.5.0 (02/11/2026)

**New**
- Monitor brightness: a tall slider next to the sound one, for the main monitor (over DDC/CI, the same setting as the
  monitor's own menu). Drag it or use the mouse wheel over it. A monitor that does not answer gets no slider.
- Camera and microphone in use: with the island closed, a ring around the time (green for the camera, orange for the
  microphone); when the time is not showing, a dot. In the menu, a dot right after the time.

**Changed**
- The menu is rounder: every small entry is a ball with its name under it, the bigger blocks have rounded ends, the
  gray is darker and has no lighter edge.
- The top of the menu is aligned on one line (time, date, icons), the date is shorter so it is not cut, and settings
  is a plain icon with no gray behind it. Recording moved next to settings, and Translator took its place.
- Batteries: one device is a small ball, two share a 2x1 block and three or four a 2x2 block. The photos are bigger,
  with a light gray outline instead of the gray backdrop, and the battery number has a cleaner font, centered.

**Fixed**
- The camera and microphone warning was not showing at all: Windows stopped recording that in the place the island
  read (it had nothing since mid October). It now comes from the Windows audio and camera systems directly.

## 1.4.0 (02/11/2026)

**New**
- The main menu was redone in the style of the iPhone Control Center: rounder tiles with a faint rim, a rounder card,
  and tiles of different sizes packed into the grid by themselves.
- Sound is a tall slider at the bottom: it fills white up to the volume. Drag it up or down (it moves from where it
  was, so a tap changes nothing and opens the mixer), or use the mouse wheel over it.
- A Translator tile: it opens ready to type or paste a text (from the menu there is nothing selected to read).
- The headset and mouse batteries share one tile, one device per line with its photo, name and iPhone-style battery;
  clicking a line opens that device. With a single device the tile is small.

**Changed**
- Settings left the grid and became a round button in the top-right corner, next to the date.
- Limit shows its percentage inside the ring, and the ring follows the window closest to its limit (the session or the
  week, whichever is higher).
- The empty shelf shows an empty shelf in its icon instead of the word "empty".
- The color picker shows a dot with the last color next to its code.

## 1.3.1 (02/11/2026)

**Changed**
- The headset and mouse batteries left the menu grid and moved to its bottom-right corner: the device photo in a circle
  with a gray ring (so a black device still shows on the black card) and, next to it, an iPhone-style battery with the
  number inside. It fills green, turns yellow at 50% and red at 25%, and stays green while charging. Clicking either
  one still opens the device.

## 1.3.0 (02/11/2026)

**New**
- When a timer finishes, a small melody plays in a loop until you dismiss it (OK, a click outside or "+1 min"); it
  stops by itself after 60 seconds (adjustable in the timer settings). Pomodoro phases keep a single chime.

**Changed**
- All the sounds were made again from scratch by the island's own generator (`ferramentas/gerar_sons.py`): soft tones
  that swell in, warm and in the same key, with a little room around them, all at the same loudness. They replace the
  Kenney sounds of 1.2.1.

## 1.2.1 (02/11/2026)

**Changed**
- New sound effects for every kind of alert (arrival, alert, timer, Claude Code, device connected, error): Kenney's
  Interface Sounds (CC0), all at the same loudness. Credits in `sons/CREDITS.md`.

## 1.2.0 (01/11/2026)

**New**
- The battery of the headphones and the mouse in the main menu: each connected device gets a tile with its photo
  and the percentage (the two AirPods side by side), with a dot when it is charging or low. Clicking it shows the
  device card.

**Changed**
- The G502 X Plus photo is now the straight view from above.

**Fixed**
- The Claude Code effort selector did nothing when the island was clicked: the effort submenu of the Claude app only
  opened while the real mouse was over the app. It now opens with no mouse at all, and the levels and Ultracode were
  tested on the real app through the island card.
- Every photo with a position was drawn in the corner and cut: the device photos in the cards, the app icons in the
  mixer and the pasted image in the AI chat. They are where they belong now, centered.
- A card taller than 400 points lost the bottom of its black background.

## 1.1.0 (01/11/2026)

**New**
- Low battery alerts for the headphones and the mouse: the island warns when the battery falls past 50%, 25%, 10%,
  5% and 1% (each level once per discharge; charging starts over). The levels are in the settings.
- AirPods report in steps of 10, so for them the alerts come at 50%, 20%, 10% and "under 10%". Each earbud counts on
  its own and the alert says which one is lower; an earbud charging in the case does not count.
- Logitech G502 X Plus mouse: battery read from G HUB (it has to be running), with its photo (black, purple, white or
  mint, chosen in the settings) in the alert.
- The G733 percentage now comes from G HUB when it is running, the exact number G HUB shows; without G HUB it keeps
  the estimate from the voltage.

**Changed**
- Low battery alerts go through Do Not Disturb and over full screen content (competitive games still silence them).

## 1.0.3 (01/11/2026)

**Fixed**
- The mouse pointer vanished while crossing the island: its window had no cursor of its own, so the pointer kept
  the shape of whatever was underneath (or stayed hidden if that one had hidden it). Now it is always the arrow.

## 1.0.2 (01/11/2026)

**New**
- Song change animation: the old cover and title slide out and fade while the new ones come in from the other side
  (to the left for the next song, to the right for the previous one); on the pill and in the bubble the cover also
  shrinks out and pops back in.
- Claude Code steps on the pill change smoothly: the old step goes up and fades while the new one comes from below.

## 1.0.1 (01/11/2026)

**Changed**
- The empty pill shows the time over any window; on the desktop it stays plain, with no time and no border.
- Claude Code effort: the five levels (Low, Medium, High, Extra, Max) and Ultracode as a separate switch, matching
  the new menu of the Claude app. The level is changed without the Claude app having to be in front; when the app
  does not open the menu, the island says so.

**Fixed**
- The island could vanish after closing the browser and going to the desktop: Windows kept reporting a full screen
  app that was no longer there.
- The adaptive border stopped following the background now and then, for the same reason; and a border lit for a
  dark background no longer stays on when the probe pauses.
- The effort selector no longer worked after the Claude app update that turned Ultracode into a toggle.

## 1.0.0 (01/11/2026)

**New**
- English interface: the island follows the Windows language (Portuguese when Windows is in Portuguese, English
  otherwise), with an option in Settings (Language: Automatic / Português / English).
- Documentation in English.

**Changed**
- Version 1.0.
- The main menu now also has the calculator, the AI chat, the timer and PC usage.
- The calculator and the timer understand English too ("10 km in miles", "stopwatch", "2 hours") without losing the
  Portuguese words; numbers and times follow the interface language.

**Removed**
- The mascot module from the public version.

## 0.4.2 (01/11/2026)

**Fixed**
- The Claude, GPT and dot animations were cut off now and then: on the pill and in the bubble, whatever went a little
  past the logo box was clipped at the edge of the scene, and the GPT dots went past the edge of their own frame strip.
  Now every scene has some room around it (only the pill shape clips it) and the GPT dots were redrawn at the same
  size as the Claude ones.

## 0.4.1 (01/11/2026)

**Changed**
- The empty pill stays over any regular window (Brave, Explorer, an editor), not only on the desktop. The island only
  hides in competitive games and in video or other full screen content; a regular full screen game keeps it.
- With the island hidden by a full screen video, touching the top edge with the mouse, near the middle, brings it
  back, and it stays while the mouse is nearby.
- A video going full screen is noticed within 3 seconds (before, up to 10).

## 0.4.0 (01/11/2026)

**New**
- Main menu: the island hotkey (or a click with nothing going on) opens the time, the date and a grid with the tools
  (calculator, AI, translator, timer, PC usage, air with the room temperature, sound, color picker, shelf, note,
  Claude limit and settings).
- "< Menu" button on every open card, and Esc goes back to the menu. The island only takes the keyboard when you
  click it, and gives the focus back to the previous window when it collapses.
- Thin light border around the island, shown only with a dark background behind it (not in docked mode).
- The time in the middle of the compact pill, when the activity leaves the middle free (never on the desktop).
- Color picker (Ctrl + Alt + P): magnifier under the cursor, the exact pixel color in hex (already copied), RGB and
  HSL, and the last colors to copy again.
- Shelf: drag files and folders onto the island and drag them back into any program later. It only keeps the path;
  whatever disappears is marked as unavailable.
- Quick note (Ctrl + Alt + N): one line with date and time in the inbox of the Obsidian vault.
- Mixer: volume and mute for each app that is playing, in the sound card.
- Recording: red dot and elapsed time when OBS is recording (with pause and stop from the island) or when an app
  captures the screen (Snipping Tool and others, shown as "capturing the screen").
- Claude limit: how much of the session and weekly limit has been used and when it resets, with a warning at 80% and
  95% (read from the Claude Code status line).

**Changed**
- The air conditioner left the pill: it lives in the menu and only shows up by itself, for a few seconds, when the air
  turns on, turns off, changes mode or temperature (including from the remote control). The room temperature alone
  does not trigger a notice.
- The Claude Code indicator only shows up while it works or asks for attention, without opening the card by itself.
- After the mouse leaves, the island waits 600 ms before collapsing (adjustable in the settings).
- Esc closes the settings window.

**Fixed**
- An alert that appeared under a still cursor stayed stuck on screen until the mouse left it.
- The clickable area grew before the drawing and held clicks on top of other programs for a moment.
- The notice that the mouse had left could get lost and leave the island open.
- Media and volume fought over the same Windows audio object and the visualizer could freeze.

## 0.3.0 (01/11/2026)

**New**
- Media: what is playing on Windows (Spotify, browser, players) with cover art, a visualizer in the cover color, play
  and pause, skip, drag the bar to seek and the mouse wheel changing the volume.
- Volume: the level shows up on the island when it changes, and the card switches the audio output (headphones,
  speakers, monitor).
- Camera and microphone: green dot (camera) and orange dot (microphone) on the pill, with the name of who is using
  them.
- Timer, stopwatch and pomodoro, with quick creation by typing "25m", "1h30" or "10s" (Ctrl + Alt + R).
- PC usage: shows up by itself when the processor, graphics card or memory is maxed out (Ctrl + Alt + U).
- Real Claude Code: what each session is doing, a notice when it finishes or asks something, and approving or denying
  permission requests right from the island.
- Calculator with units and live exchange rates (Ctrl + Alt + K).
- Chat with GPT and Claude (Ctrl + Alt + J) and a translator for the selected text (Ctrl + Alt + T); the keys stay in
  the Windows Credential Manager.
- Bedroom air conditioner from the island (Ctrl + Alt + A).
- Headphones: photo of the headphones and battery when they connect (AirPods, G733 and others), screenshots with a
  thumbnail you can drag into another program, a render finished notice for After Effects and Media Encoder, and
  Windows notifications (experimental, off by default).
- Sounds for alerts, timer and Claude Code (muted in competitive mode and in Do Not Disturb).
- Settings window (icon menu, "Settings..."), with each module, the hotkeys and the island options.
- Updates from the island itself: it looks for a new version in the GitHub releases, checks the file and goes back to
  the previous one by itself if the new one does not open.

**Changed**
- The icon menu got "Settings...", "Open config.json" and "Check for updates".

## 0.2.0 (01/11/2026)

**New**
- Animations of the Claude and GPT logos (the ones Felipe made): spinning loop, smoothed turn, exit turn, entry turn,
  shrinking and growing, vanishing and appearing, and Claude turning into a ball.
- Dots spinning in 3D out of the Claude ball, with motion blur and the ones in the back darker: orbit, comet, wave,
  split and ring. The same ones in white for GPT, which also got the "turning into a ball".
- The fake Claude uses the real animations: it spins in, works in a loop, turns into a ball and releases the dots
  when it thinks, turns back into the logo to work, spins out when it finishes and pulses while waiting for you.
- Fake GPT and a gallery with every animation (Demo menu).
- Installer (`ferramentas\instalar.ps1`) with a Start menu shortcut and its own icon.
- Start with Windows (on by default, option in the icon menu).

**Changed**
- Both logos show up at the same size (the Claude one gets a zoom, because it fills half of its frame).
- The tray icon is the same as the Start menu one.

**Fixed**
- A maximized window no longer counts as full screen.

## 0.1.0 (01/11/2026)

**New**
- The island: a black pill floating at the top of the main monitor, with a soft shadow, that changes shape with a
  spring and a slight overshoot, at 250 frames per second on the ultrawide.
- States: hidden, empty on the desktop, compact, compact split with a bubble next to it, expanded and alert.
- Content comes in and out with fade, scale and blur, always clipped to the island shape.
- Preview on hover, expand on click, back when the mouse leaves or on a click outside.
- Competitive mode from the game list: only the timer shows up.
- Respects Windows Do Not Disturb and Focus sessions.
- Global hotkey (Ctrl + Alt + Space, with a fallback if it is taken).
- Tray icon: pause, floating or docked (notch style), demo, settings, logs and quit.
- JSON settings that apply as soon as they are saved.
- Demo with a fake timer, music, Claude Code (with the original logo) and PC usage, plus an automatic tour through
  every state.
- Measurement of frames per second, processor and memory (`--medir`) and a window self test (`--autoteste`).
