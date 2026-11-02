# Changelog

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
