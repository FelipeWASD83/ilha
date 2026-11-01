# How to write an island module

Each feature (media, volume, timer, Claude Code...) is a **module**: a folder `src/Ilha/Modulos/<Nome>/` with a class
marked with `[Modulo("id", "Nome")]` that inherits from `Modulo`. The island finds the module by itself when it opens
(reflection): **there is no registration in any file**, so a module never needs to touch a core file.

Read first: the project's `../CLAUDE.md` (architecture and decisions), `Modulo.cs`, `Servicos.cs`, `Atividades.cs`,
`Faixas.cs` (animated logos), `Lang.cs` (interface language) and `Demo.cs` (complete activity examples: timer, media,
Claude, GPT).

## The skeleton

```csharp
namespace Ilha.Exemplo;   // não Ilha.Modulos.X: Ilha.Modulos colidia com uma classe (hoje CatalogoDeModulos)

[Modulo("exemplo", "Exemplo", English = "Example")]  // LigadoPorPadrao = false se não deve ligar sozinho
internal sealed class ModuloExemplo : Modulo
{
    private ConfigExemplo _cfg = new();
    private AtividadeExemplo? _atv;

    public override Type? TipoConfig => typeof(ConfigExemplo);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigExemplo>(cfg);
        _atv = new AtividadeExemplo(S);
        S.Ilha.Adicionar(_atv);                        // a ilha passa a considerar a atividade
        S.Atalhos.Registrar("exemplo", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x45 }, () => S.Ilha.Expandir(_atv));
    }

    public override void ConfigMudou(JsonObject cfg) => _cfg = Ler<ConfigExemplo>(cfg);
    public override void Contexto(Contexto ctx) { /* competitivo, tela cheia, Não Incomodar */ }
    public override void Parar() { /* soltar eventos do sistema, timers, threads, COM */ }

    // `Ilha.exe --testar exemplo`: sem janela e SEM os Serviços (S é nulo aqui). Teste a lógica pura.
    public override string Autoteste() => ...;     // linhas "ok      ..." ou "FALHOU  ..."
}

public sealed class ConfigExemplo
{
    [JsonPropertyName("algo")]
    [Label("Algo", "Something")]
    [Help("Quantas vezes fazer algo.", "How many times to do something.")]
    public int Algo { get; set; } = 3;
}
```

The module section lives in `config.json` → `"modulos"` → `"exemplo"`, with `"ligado"` (the core handles it) and the
fields of the config class. `Salvar(_cfg)` writes it back. Never save inside `ConfigMudou`.

## Bilingual text

The interface speaks Portuguese and English (it follows the Windows language, or the `language` key in the config:
`"auto"`, `"pt"` or `"en"`). Every text the user sees has both versions:

- **Text in code** (scenes, buttons, alerts, `S.Avisar`, error messages shown on the island):
  `Lang.T("Timer acabou", "Timer finished")`. Never keep a translated string in a `static` field: build it when it
  is used, so a language change takes effect.
- **Dates and numbers shown to the user:** format with `Lang.Culture` (pt-BR or en-US), not with a fixed culture.
  Parsing what the user typed is a separate concern (keep accepting what the module already accepted).
- **Config fields in the settings window:** `[Label("pt", "en")]` for the label and `[Help("pt", "en")]` for the help
  line (they win over `[DisplayName]` and `[Description]`).
- **Module name:** `English = "..."` in `[Modulo(...)]`; the settings window shows `Nome` or `English` following the
  interface language.
- **What stays in Portuguese:** code identifiers, code comments, log lines and the config keys (JSON names).
- A language change stops every module (`Parar`), creates them again (`Iniciar`, with menu entries and hotkeys
  registered anew) and rebuilds the tray menu, so a module does not need to listen for it.

## The Services (`S`)

| | |
|---|---|
| `S.Ilha` | `Adicionar(atividade)`, `Alertar(alerta)`, `Expandir(atividade)`, `Recolher()`, `Expandida` |
| `S.Palco` | builds scenes: `NovaCena`, `Desenho` (Direct2D), `Bloco`, `Logo` (animated logo), `Texto` (DirectWrite), `Faixas` |
| `S.Sons` | `Tocar("chegada"/"alerta"/"timer"/"claude"/"conectou"/"erro", importante)`; already muted in competitive mode and in Do Not Disturb, except the important ones |
| `S.Atalhos` | `Registrar(nome, padrão, ação)`: global hotkey (RegisterHotKey); the user changes it in `atalhosModulos` in the config |
| `S.Entrada` | text field (keyboard): `Abrir(inicial, mudou, enter, fechou, imagem, multilinha, senha)`, `Texto`, `Cursor`, `Fechar()`, event `Mudou`; draw it with `S.Palco.Texto.DesenharCampo(...)` |
| `S.Indicadores` | camera and microphone in use (`Definir(...)`); the island draws the dots by itself |
| `S.ContextoAtual()` | `Competitivo`, `TelaCheia`, `D3DExclusivo`, `NaoPerturbe`, `Desktop`, `Processo` |
| `S.Config()` | global config (read only) |
| `S.Avisar(texto)` | tray icon balloon |
| `S.NaInterface(ação)` | goes back to the UI thread (COM, WinRT, thread and system timer events arrive on another one) |
| `Credenciais` (static) | `Gravar/Ler/Existe/Apagar(nome)` in the Credential Manager (`Ilha/<nome>`). A key never goes into a file, a log or an error message |

## Activities (what shows up on the island)

They inherit from `Atividade` (see `Demo.cs`). `Tipo` decides the priority (`claude`, `ia`, `timer`, `midia`, `uso`;
the user orders them in `prioridades`). `Ligar()`/`Desligar()` when it starts and ends (something new shows up
expanded by itself). `Montar(palco, forma)` returns the scene for each shape: `Compacta` (up to ~24 DIP high, the
pill is 36), `Bolha` (up to 24×24, the dot next to it), `Expandida` (card, up to ~380×300), `Alerta`. Content changed
without changing size: `Redesenhar()`. Layout changed: `MudouTamanho()`. Buttons:
`cena.Botoes.Add(new Botao { Area, Acao })` (DIP, from the corner of the scene). `PermitidaNoCompetitivo` (only the
timer). `EhFerramenta` (calculator, translator, AI: opens from the hotkey without being "in progress").
`LarguraCompactaMinima`. `AnunciaChegada` (false = shows up without opening the card, like volume). `Rolar(delta)`
(mouse wheel over the island). Draggable button: `Botao.Arrastar = (x, y, soltou) => ...` (progress bar, thumbnail
to drag). `Alertar(new Alerta { Tipo, Som, Importante, Criar })`.

Look: pure black pill; white text (`Cor.Branco`, `Cor.Secundario`), the activity accent (`Cor.Laranja` timer,
`Cor.Verde`, `Cor.Coral` Claude). Segoe UI Variable (already the one in `Escrita`). No serif fonts.

## Rules (no exceptions)

- **Only touch your module's folder** (`src/Ilha/Modulos/<Nome>/`) and, if needed, a new script in `ferramentas/`.
  Do not edit core files, the `.csproj`, `CLAUDE.md`, the root `README.md` or this guide: other agents work at the same time. Need
  something in the core or a NuGet package? **Do not do it**: write it in the final report ("requests to the core")
  and work around it with what already exists (P/Invoke, COM, system WinRT, Vortice).
- **Never open the island** (`Ilha.exe` with no argument, `--demo`, `--roteiro`, `--medir`, `--autoteste`): there is
  an installed island running on Felipe's PC and he may be using it. Test with `Ilha.exe --testar <id>` (no window)
  and with small read-only console/PowerShell probes. No loop or process left running.
- **Build only through** `powershell -ExecutionPolicy Bypass -File .\ferramentas\compilar.ps1` (at the root of your
  worktree): it checks Smart App Control and rebuilds with another stamp if it is blocked.
- **Performance**: the island always runs, including with a game open. System events instead of polling; polling only
  when unavoidable, with a wide interval, and only while the activity is visible. No redrawing on a clock without a
  change. Continuous animation only in the compositor (`StartAnimation`), never through an app timer.
- **Anti-cheat**: no injection, hooks (`SetWindowsHookEx`), reading another process's memory or `OpenProcess` with
  rights beyond `PROCESS_QUERY_LIMITED_INFORMATION`.
- **Public repository**: nothing personal in the code (a path with a user name, a private folder name, a key, an
  e-mail). Personal paths go into the config, not into the code. Third party brand images stay outside git.
- Style: the same as the core. Names in Portuguese, `Nullable` on, explicit `using` (no ImplicitUsings), comments only
  where the intent is not obvious, no em dashes. User-facing text in both languages (see "Bilingual text").
- At the end: `--testar <id>` passing, a clean build, a **commit in your worktree** (message in English saying what
  and why, ending with Claude's `Co-Authored-By` line) and the report with the branch name.
