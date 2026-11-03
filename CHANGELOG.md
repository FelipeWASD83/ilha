# Changelog

## 4.0.1 (03/11/2026)

**Fixed**
- Steam downloads: a cancelled update no longer shows "Ready to play" (only a download that got every byte does), and
  Workshop items no longer say they finished when they were only cancelled.
- A download paused for more than 10 minutes leaves the pill (Steam can keep an update paused for days).
- Weather: changing the city while a reading was on its way kept the old city until the island restarted.
- The "Copied" notice no longer hides file names with dates (IMG_20261103_153012.jpg) as if they were keys.
- Ping: with the internet gone at the start of a match, the mark says "no reply" instead of an old ping.
- A click on a ball next to the menu closed the island instead of opening that activity: the new card is narrower
  than the menu, so the cursor ended up outside it. That card now stays open until a click outside or Esc.
- The Games card shows today's Rocket League names for matches saved before 4.0.0 ("3v3 ranqueado", an event shows
  just "Rocket League"), and no longer shows "K / D –" when there are no kills and deaths to show.

## 4.0.0 (03/11/2026)

**New**
- With the menu open, every activity running (music, timer, Claude Code, the score...) shows as a ball of its own at
  the right of the menu, one under the other, each with its own shadow and border; a click on a ball opens that
  activity's card (with "< Menu" to come back).

**Changed**
- The "Copied" notice hides anything that looks like a key, a token or a random password ("a key (hidden)"), even
  when the site it came from does not mark it as private.

**Removed**
- Steam friends. It needed a Steam Web API key, and that key is what the well-known "API key scam" uses to watch and
  cancel an account's trade offers: not worth the risk for an account with a valuable inventory. If you created a key
  for it, revoke it at steamcommunity.com/dev/revokekey.

## 3.9.0 (03/11/2026)

**New**
- The score pill carries the dots: green for the camera, orange for the microphone and a red one while something
  records the screen (the island's recorder, OBS or another app). The clip ring does not light the red one.
- Ping: while a competitive game is in front, the island pings the router and an internet host once a second; when
  the connection turns bad the score pill shows a small mark ("ping 180 · perda 3%", or "rede local" when the router
  itself is slow). It measures your connection, not the game server, which Windows does not show.
- Steam downloads: games, updates and Workshop items on the pill with the game's icon, the percentage and a bar; the
  card lists every download with the speed and the time left, and a notice says when a game is ready to play.
- Steam friends: with your Steam Web API key (pasted on the island, kept in Credential Manager), a notice when a
  friend comes online or starts a game, and a "Friends" card in the menu with who is online and playing what.
- Weather: Open-Meteo for the city in the settings. When the sky changes (clear, cloudy, fog, drizzle, rain, snow,
  storm) the collapsed island says so, "Cloudy → Rain · 24°"; the "Weather" ball in the menu opens the next hours.

**Changed**
- Rocket League modes: the ranked ones by team size (1v1, 2v2, 3v3) with the game's names (Rumble, Hoops, Snow Day,
  Dropshot, Heatseeker) and casual Chaos as "Casual 4v4". Event playlists show no name instead of "Playlist 81".

**Fixed**
- The crashes of the last days all happened while .NET's cleanup thread released Windows media objects, most likely
  while the island was using the media controls at the same time. The media module now releases the short-lived ones
  itself, right away, so the cleanup thread no longer touches them.

## 3.8.0 (03/11/2026)

**New**
- MVP: when you are the match's MVP, a crown drops above the GG or EZ and the word turns gold, glowing, with a shine
  running over it. Rocket League says who it is (the best score of the winning team); CS2 only gives your own numbers,
  so there the island shows your round MVP stars instead.
- The Games card lists the last five matches of every game, newest first: a green or red card with the game, the
  result, the mode, the score, kills and deaths, the MVP and when. Kept on this PC, so they are there after a restart.
- Achievements list every game with hours (not only the eight most played), scrolled by the mouse wheel with a thin
  bar at the side; the names of Steam games not installed arrive from the store a few at a time.

**Changed**
- The Games card no longer shows the five result dots next to each game (the last matches list replaces them).

**Fixed**
- Two false Battlefield 6 wins (from the menu bug fixed in 3.6.0) were taken out of the match history.

## 3.7.1 (03/11/2026)

**Fixed**
- The music bubble kept vanishing: Apple Music sometimes reports no timeline for a moment, and since 3.5.0 that made
  the island drop the player for two minutes. Now only a player that really went away is dropped.
- Public mode (and any menu entry coming or going) only showed after the menu was opened again.
- Focus goes to the start of the row after the batteries when they take 2x2 (under them when they are 2x1).

## 3.7.0 (03/11/2026)

**New**
- Clips: while a competitive game is in front, the island keeps the last seconds of the screen and the PC's sound
  (on the graphics card, like the recorder; needs ffmpeg). A double kill or more, an ace, a win, your team's goal and
  an epic save cut the last 30 seconds by themselves (moments close together become one clip); when the match ends the
  island asks whether to keep them, with each clip's frame (a click plays it). No answer keeps them. Ctrl+Alt+C cuts one
  right away in any competitive game. Clips go to Videos\Ilha\Clips; the Games card shows the last four and opens the
  folder. Length, frame rate, quality and folder are in the settings.
- Rocket League: "WHAT A SAVE!" lit letter by letter on your epic saves, and their count with the match's numbers.
- CS2 Rush: the mode's own icon next to its name.
- Three activities at once: the pill gets a second bubble on its left, for the third activity.

**Changed**
- Focus is a 2x1 block in the menu: under the batteries, or beside them (under the sliders) when they take 2x2.

## 3.6.0 (03/11/2026)

**New**
- Focus, like the iPhone's: modes you turn on from the menu (the "Focus" ball) or that turn on by themselves on a
  schedule ("mon-fri 09:00-18:00", "all 23:30-07:00") or while an app is open (bf6.exe turns on Gaming). While a mode
  is on it can silence all of the island's sounds, turn on Windows' Do Not Disturb, let through all alerts, only the
  important ones or none, put a Fundo Vivo wallpaper on the main monitor and set the volume and the monitor's
  brightness. All of it goes back when the mode turns off, and Do Not Disturb that was already on stays on.
  - Comes with Do Not Disturb, Work, Gaming and Sleep (Sleep turns on by itself from 23:30 to 07:00); the modes are
    edited, added and ordered in the settings' new "Focus" section.
  - The mode that is on shows its symbol and name on the pill when nothing else is there, a short notice says when one
    turns on or off, and a mode turned on by hand stays on after the island restarts.
- Do Not Disturb in the menu: a ball with a bell crossed out in red that turns the Do Not Disturb mode on and off.

**Changed**
- New achievements icon: a gold trophy with handles, a star and a dark base.
- In a competitive game with the score on the pill, the menu and the "<" back to it work like outside the game.

**Fixed**
- Battlefield 6: the menu no longer counts as a match that keeps ending in a win. A match only counts after its
  loading screen, or after the tickets of both sides moved for 45 s, and the end screen needs the whole word
  (VICTORY/DEFEAT, not the menu's "victories") read twice.

## 3.5.0 (03/11/2026)

**New**
- Charging animation: when the G733, the mouse (through G HUB) or the AirPods case starts charging, the island shows
  the device's photo with a battery filling up to its level and a lightning bolt popping over it. It can be turned off
  in the settings (Headphones and Bluetooth).
- Achievements unlock while you play: the minutes of the Steam game running now count right away (Steam itself only
  writes its hours when the game closes), and a new trophy shows up over the game too.
- Tennis (ATP and WTA) in the sports leagues: the day's singles matches, scored in sets.
- The Sports icon in the menu follows what is on: a soccer ball, an F1 car, a tennis racket, a basketball or an
  American football, by what is live now or the next game today.
- The island comes back by itself if it ever closes on its own (Windows reopens it within a second).

**Changed**
- New icons: the audio recorder is a microphone with a black mesh grille on top (same stand as before), and Games is a
  controller with grips, two sticks, the cross and four colored buttons.

**Fixed**
- AirPods (and other headphones whose name says nothing about the model) show their photo when they connect, not a
  drawn icon: the island now waits a moment for Windows to tell which model they are.
- The goal animation only plays for soccer (a basket in the NBA is not a goal).
- The island could close on its own while a game was open (it did in a CS2 Rush match). A media player whose session
  went dead was one trigger and is now dropped right away; the cause is not fully pinned down yet, so the island also
  reopens by itself when that happens.

## 3.4.0 (03/11/2026)

**New**
- Achievements: a trophy for every 10, 50, 100, 250, 500 and 1000 hours in a game (bronze, silver, gold, platinum,
  diamond, legend) and for 100, 500, 1000, 2500 and 5000 hours in all games together. Steam games use Steam's own
  count of hours; games outside Steam (Rocket League, Fortnite, Valorant and others) are counted by the island from
  now on, while they are open. What was already earned is noted quietly the first time; from then on each new trophy
  shows up with a cup in its color and its own sound.
- "Achievements" in the menu: how many are unlocked, the hours in all games with the five total medals, and the most
  played games with their six medals and a bar to the next one.
- Every game gets its icon: from its .exe when installed, else Steam's own icon from its library cache, else a logo
  downloaded once for games outside Steam. Names of Steam games no longer installed come from Steam's store.

## 3.3.0 (03/11/2026)

**New**
- Sports: live scores from ESPN's open scoreboards (no account or key). In the settings you pick the leagues
  (Brasileirão Série A and B, Copa do Brasil, Libertadores, Sul-Americana, Champions League, Premier League, La Liga,
  World Cup, Formula 1, NBA, NFL), the teams and the drivers you follow.
  - A followed team's game goes to the pill while it is live: both crests, the score and the minute, with
    "GOOOOOOOOOL!" and its sound when your team scores. A live race (with a followed driver, or any race when none is
    picked) shows the leader and your drivers' places.
  - "Sports" in the menu: what is live now, today's games, the coming games and the latest results.
  - Crests are downloaded once from ESPN's image server and kept only on this PC.

## 3.2.0 (03/11/2026)

**New**
- "Games" in the menu: the match on now (a click opens its score), and each game with its icon, the hours played
  (Steam's own count, read from its file on this PC) and the last five results.
- Ace in Counter-Strike 2 (the whole enemy team in one round: five in competitive and Premier, two in Wingman): a
  gold "ACE!" slams down, five skulls light up one after the other under it, each crossed out, and a shine runs over
  the word, with its own fanfare.

**Changed**
- The menu has five columns instead of four, so it grows less downward. The bigger blocks (sound, brightness,
  batteries) sit at the right and the balls fill around them, so entries that come and go (the lock hides two) no
  longer move the blocks or open holes in the middle.

## 3.1.1 (03/11/2026)

**Fixed**
- In live score mode the island vanished over a competitive game whenever no match was being read (menus, queue,
  loading). Now the pill stays as the game in front: its icon, its name and how long it has been open, and turns into
  the score when the match starts. The card shows the same with the last five results.
- Closing the game in the middle of a match left the match on the pill (for minutes in Battlefield 6). The island now
  checks every 2 seconds, only while a match is on, whether the game is still running, and drops the match at once.
- In live score mode the other activities (music, timer, Claude Code...) were hidden over the game. They show again,
  with the score always in front; their arrival alerts and sounds still stay quiet over the game.
- The Counter-Strike 2 match time vanished: the game did not send the round countdown it was read from. The pill now
  shows the match time counted by the island from the end of warmup, which also keeps the score centered.

**Changed**
- Square logos (the games' icons, the Battlefield 6 emblems) have rounded corners.

## 3.1.0 (03/11/2026)

**New**
- The game score shows the games' own art, read from the games installed on this PC (nothing is downloaded or
  shipped): each game's icon from its .exe; in Counter-Strike 2 the CT and T shields and the Premier badge from the
  game's files; in Battlefield 6 the NATO and PAX names and emblems, cut from the loading screen the first time each
  side shows up. Rocket League keeps blue and orange badges.
- New sounds for the score, made to sit over a game: a punchy hit with a metallic ping on a kill, a stadium horn over
  a cheering crowd on a goal, a brass fanfare with timpani and cymbal on a win, and two low notes on a loss.

**Changed**
- The kill animation: a more detailed skull drops in, squashes and wobbles, flashes red, and two red cuts slash it one
  after the other with a red ring bursting out; the count pops in after.
- The goal animation: "GOOOOOOOOOL!" lights up letter by letter from left to right, each letter flashing white
  before taking the team color, then the word blinks and a shine runs over it from left to right, with a glow behind.

## 3.0.0 (03/11/2026)

**New**
- Live game score: with a competitive game in front, the pill becomes the match score. It shows the game, our score
  and theirs in the team colors and the game clock; the card adds the mode and map, the round or stage, your numbers
  (K/D/A, MVPs and score in CS2; goals, assists, saves, demos and score in Rocket League; kills and deaths in
  Battlefield 6) and the last five results of that game.
  - Counter-Strike 2 through the game's official Game State Integration, Rocket League through its official Stats API
    (the island switches both on; they take effect the next time the game opens) and Battlefield 6 by reading the
    score off the screen (it has no integration; only Conquest is calibrated, other modes show the mode and the time).
  - Animations: a skull crossed out by a red X on a kill ("x2", "x3" for a quick streak), "GOOOOOOOOOL!" with the O's
    in your team color on a goal (a quieter "Their goal" on theirs), and a big "EZ" for a stomp or "GG" for a close
    win or a loss at the end, with the final score.
  - Sounds for your goal and for a win.
  - Finished matches are kept only on this PC, in `.ilha/partidas.json`.
- Settings: "In game" under Competitive games picks what a competitive game does: live score (the default), hide the
  island, or nothing.

**Changed**
- Battlefield 6 joined the default list of competitive games.

## 2.1.0 (03/11/2026)

**New**
- PC reactions: the collapsed island gives a short notice when something happens on the PC, each one can be turned
  off in the settings.
  - Copied: a preview of what went to the clipboard (the text in one line, a thumbnail of an image, the file name).
    What a password manager marks as private doesn't show, and with the island discreet it only says "Copied".
  - Download finished: the file's name and size (a thumbnail for images) once it gets its final name in Downloads. A
    click shows it in Explorer, and it can be dragged out of the notice into another program.
  - Drive connected or removed: pen drive or external drive, with its letter, name and size; a click opens it.
  - Internet dropped or came back (from Windows' own network status, after it held for a few seconds).
  - Microphone muted or unmuted, with the device's name.
  - Do Not Disturb turned on or off.

## 2.0.1 (02/11/2026)

**Changed**
- The Claude limit card says that only Claude Code in the terminal updates it: sessions in the desktop app don't run
  the status line, so the numbers stay still while only the app is used.

## 2.0.0 (02/11/2026)

**New**
- Recordings window: the island's own player for the latest videos and audios, a black window with rounded corners.
  The list on the left (newest first, with a thumbnail and the length), the player on the right: video plays inside it,
  audio shows its waveform lighting up as it plays. Space plays and pauses, the arrows seek and move through the list,
  Esc closes. It opens from the "Recordings" button on both recorder cards and from the "Recording saved" notice.
- While recording audio, the collapsed island turns into a live waveform with the time, like Voice Memos on the iPhone.
- Permission mode on the Claude Code card: a fourth button with the session's mode (Auto, Manual, Accept edits, Plan,
  Bypass permissions) that switches it in the Claude app.

**Changed**
- The Recording button (OBS) left the menu's top corner and became a button next to Wallpaper.
- The Claude Code card's buttons are now two by two: model and effort, then permission mode and message.

## 1.9.0 (02/11/2026)

**New**
- Fundo Vivo on the island: "Wallpaper" in the menu opens a picker like the iPhone's wallpaper picker or the Apple
  Watch's faces. The wallpapers you chose sit in a row on a black card, the middle one big with its name, the
  neighbours smaller at the sides; the mouse wheel, the arrow keys or a click on a neighbour slides the row, and a click
  on the middle one (or Enter) puts it on the monitor. With two monitors, the buttons on top pick which one, and the
  cards take that monitor's shape. "Choose" shows Fundo Vivo's whole library in a grid to mark the ones that show up.
  Needs the Fundo Vivo app.

## 1.8.1 (02/11/2026)

**Changed**
- New icons for the recorder's menu buttons: a silver camera with a blue glass lens (its small window turns red while
  recording video) and a detailed studio microphone with its grille, cradle and stand (a red dot while recording audio).

## 1.8.0 (02/11/2026)

**New**
- Recorder: two new menu buttons. "Record video" records the screen, the webcam, or the screen with the webcam in the
  bottom-right corner, with rounded corners and a soft shadow. "Record audio" records the microphone and/or the PC's
  sound. The PC's sound and the microphone are switches on each card. While it records, the pill shows a red dot and
  the time, and the island stays out of the video. Video is saved as MP4 in Videos\Ilha (full resolution, 60 frames per
  second, encoded on the graphics card), audio as WAV in Music\Ilha. Ctrl + Alt + G starts and stops the video.
  Video needs ffmpeg installed.

**Changed**
- Stronger shadows under the island, floating and in notch style.

## 1.7.1 (02/11/2026)

**Fixed**
- Switching notch style on or off with the island open broke it: it stopped updating and kept failing until it was
  closed and opened again. The same happened when changing the size, the shadow, the border, the position or the
  language in the settings. Now the island rebuilds itself in place.

## 1.7.0 (02/11/2026)

**New**
- Wired keyboards: plugging in the Logitech G213 or the Razer Huntsman V3 HE TKL shows its card with the photo and
  "Keyboard connected" (no battery, they are wired). "Wired keyboards" in the settings turns it off.
- Caps Lock on the island: a short notice with the key symbol when it turns on (green) and when it turns off, for
  keyboards without the light (the Huntsman V3 HE TKL has none). It is the new Caps Lock module, which can be turned
  off, and "Also show when it turns off" can be unchecked.
- The air conditioner card has a "Display light" switch, to turn the light on the unit's display on and off (only
  with the air on, like on the site, and only if the unit allows it).

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
