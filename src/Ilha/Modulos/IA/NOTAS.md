# The "ia" module: AI chat and translator

Id `ia`, activities `ia` (chat) and `tradutor`. Namespace `Ilha.IA`, not `Ilha.Modulos.IA`: the core has the static
class `Ilha.Modulos` and a namespace with the same name gives CS0101.

## How to use

- **Chat:** `Ctrl + Alt + J` (hotkey `ia`). Enter sends, Shift+Enter adds a line break, Ctrl+V pastes a screenshot,
  Esc closes. Clicking the model name cycles through the ones in `modelos` in the config (default GPT-6 Luna, Claude
  Sonnet 5.5, GPT-6.1 Sol); "New" clears the conversation (while answering it becomes "Stop"); "Keys" opens the keys
  screen. The mouse wheel scrolls the conversation. It keeps the last 6 exchanges, only in memory.
- **Keys:** with none, the keys screen opens in place of the chat. "Paste" opens a password field (dots) and Enter
  writes it straight into the Credential Manager (`Ilha/openai`, `Ilha/anthropic`) and tests it. "Test" does
  `GET /models` (free on both sides). "Delete" asks for a second click. One key is enough: the other one is the
  fallback.
- **Translator:** select a text in any app and press `Ctrl + Alt + T` (hotkey `tradutor`). It translates into
  Portuguese, or into English if it was already Portuguese. Enter copies the translation and closes (the focus goes
  back to the app, ready to paste); typing another text and pressing Enter translates it. Nothing selected: the field
  stays open to type or paste.

## Decisions

- **Provider ported from `contato-ia/contato/provedor.py`** (`Provedor.cs`): pairs, fallback, prices, out of credit
  (402 always; 400/401/403/429 only with the signals), 30 min circuit breaker, Claude reasoning and
  `converter_entrada`. Schema and tool conversion were left out (nothing here uses them). It is a third master copy:
  if the table changes there, it changes here. The `test_provedor.py` cases that fit are in `TestesIA.cs`.
- **Plain HttpClient + `System.Net.ServerSentEvents`** (ships with .NET 10), no SDK. OpenAI through Responses
  (`store: false`, `reasoning` only on a reasoning model), Claude through Messages with `x-api-key`.
- **Switching sides only when out of credit and only before the first text.** A server error (5xx, 529,
  `overloaded_error` in the middle of the stream) stays an error. Without the key for the requested side, the fallback
  takes over without opening the circuit breaker.
- **Cancelling:** collapsing the island, another tool taking the front, Esc in the field, a competitive game or "Stop"
  cancel the token, and the stream stops right away (tested with a fake stream that stalls). A stream silent for 90 s
  becomes an error.
- **The key** is read from the Credential Manager at the moment of each request and only goes in the header. Every
  error message goes through `Segredo.Limpar` (OpenAI returns part of the key on a 401) and a 401 never repeats the
  server text.
- **Pasted image:** longest side up to 2576 px (`ladoMaiorDaImagem`), PNG; above ~3.5 MB it becomes JPEG. One per
  message. It stays in the history and goes along with the next questions in the same conversation.
- **Translator:** up to `limiteGoogle` (1,200) characters through the unofficial Google endpoint (`client=gtx`, POST),
  above that through the AI on the cheap pair with no reasoning (`gpt-6-luna` effort `none`, fallback
  `claude-haiku-5-5` with `thinking: disabled`), with an anti-injection instruction. Without an AI key, the large text
  goes to Google in chunks of 4,500 that rebuild the exact text. Text above `limiteTradutorIA` (12,000) is cut. If one
  engine fails, the other takes over.
- **Selection:** UI Automation first (IUIAutomation2 with ~450 ms timeouts, its own MTA thread, a total deadline of
  600 ms; vtable checked against `UIAutomationClient.h` from SDK 26100 and by the self test probe). Fallback: Ctrl+C
  through SendInput after the hotkey keys are released, waits for the sequence number to change (300 ms) and restores
  what was there: every format that could be read, copied before the Ctrl+C (including private ones, like After
  Effects', HTML and RTF); if that fails, text, image and files. **Never with a competitive game in front; never Ctrl+C
  in a terminal** (Ctrl+C with no selection would kill the running program).

## Not tested (can only be checked on the PC, with the real keys)

- Real calls to both APIs (only fake servers). The exact text of the out of credit error comes from the
  documentation: if the chat shows an error instead of switching sides, that is the first suspect.
- The screen on the real island (photos, clicks, mouse wheel), the global hotkey and the Entrada focus.
- Reading the selection in real apps (Brave, Word, VS Code, Discord, Notepad). In VS Code, Ctrl+C with no selection
  copies the whole line; in its integrated terminal (which the island does not recognize as a terminal) it interrupts
  the program.
- The real Google Translate.
- The Ctrl+C goes into the Windows clipboard history (Win+V). Restoring every format only has a self test with an
  in-memory DataObject: with the real After Effects, Office and Photoshop it was not checked. A format that does not
  come in an HGLOBAL (IStorage, metafile) is still left out.
