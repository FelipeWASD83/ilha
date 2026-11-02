# Sounds

The island's sounds are made by `ferramentas/gerar_sons.py` (synthesized from scratch with numpy, part of this
repository and under its license). They share one family: soft tones that swell in, warm, in fifths and octaves around
F major, with a short room reverb. 48 kHz, 16 bit, stereo, all at the same loudness.

| File | Used for |
|---|---|
| `chegada.wav` | a message or notice arriving: two notes rising a fifth |
| `alerta.wav` | general alerts (also the fallback for any other kind): an open chord that blooms |
| `claude.wav` | Claude Code finishing or asking for you: a warm C and G that rise a touch |
| `conectou.wav` | a device connecting: a quick rising arpeggio |
| `erro.wav` | errors: two muted notes going down |
| `timer.wav` | pomodoro phases and timers that ended with the island closed: one chime |
| `timer-loop.wav` | a timer finishing: a small marimba melody that loops seamlessly until the alert is dismissed |

Run `python ferramentas/gerar_sons.py` to make them again (the same files every time). To use your own, put a `.wav`
with the same name in `%USERPROFILE%\.ilha\sons\` (it wins over these).
