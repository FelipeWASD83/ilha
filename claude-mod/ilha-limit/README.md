# ilha-limit (Claude Code mod)

Keeps the island's **Claude limit** card up to date from every Claude Code session, the desktop app included.

The island reads `%USERPROFILE%\.ilha\uso-claude.json`. Until now only `ferramentas/statusline_uso.py` wrote it, and
it runs only where Claude Code draws a status line (the terminal): sessions in the desktop app never updated the card.
This mod uses Claude Code's own measurement (`session.measure`, the same 5-hour and weekly figures the status line
gets) and writes them to that file after every reply that moves a window by a whole point, plus once when a session
starts.

It writes the same format and follows the same rules as the status line script: only `five_hour` and `seven_day`, a
reading from an idle session never replaces a newer one, usage inside the same window only goes up, an expired window
is dropped, and nothing is written when the island's config has `modulos.limite.ligado` set to `false`. Both writers
can run at the same time.

## Install

Point Claude Code at this folder through `CLAUDE_CODE_PLUGIN_DIRS`, in the `env` block of `~/.claude/settings.json`
(the desktop app reads it from there; separate several folders with `;` on Windows):

```json
"env": { "CLAUDE_CODE_PLUGIN_DIRS": "C:/path/to/ilha/claude-mod/ilha-limit" }
```

New sessions load it. For a single terminal session: `claude --plugin-dir <this folder>`.

## Check

```
claude plugin validate claude-mod/ilha-limit
claude plugin test claude-mod/ilha-limit
```
