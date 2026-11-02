# Sounds

The island's default sounds come from **Interface Sounds** by Kenney (www.kenney.nl), released under
[Creative Commons Zero (CC0)](http://creativecommons.org/publicdomain/zero/1.0/): free to use, change and share,
credit not required. Downloaded on 02/11/2026, converted to mono 44.1 kHz 16 bit WAV and leveled to the same loudness.

| File | Used for | Original file |
|---|---|---|
| `chegada.wav` | a message or notice arriving | `glass_004.ogg` |
| `alerta.wav` | general alerts (also the fallback for any other kind) | `confirmation_002.ogg` |
| `timer.wav` | the timer finishing | `confirmation_004.ogg` |
| `claude.wav` | Claude Code finishing or asking for you | `question_001.ogg` |
| `conectou.wav` | a device connecting | `maximize_004.ogg` |
| `erro.wav` | errors | `error_006.ogg` |

To use your own, put a `.wav` with the same name in `%USERPROFILE%\.ilha\sons\` (it wins over these).
`ferramentas/gerar_sons.py` makes the older synthesized set instead (it overwrites these files).
