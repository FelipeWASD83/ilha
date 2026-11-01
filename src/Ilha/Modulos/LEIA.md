# Como escrever um módulo da ilha

Cada funcionalidade (mídia, volume, timer, Claude Code...) é um **módulo**: uma pasta `src/Ilha/Modulos/<Nome>/`
com uma classe marcada com `[Modulo("id", "Nome")]` que herda de `Modulo`. A ilha acha o módulo sozinha ao abrir
(reflexão): **não existe registro em arquivo nenhum**, então um módulo nunca precisa mexer em arquivo do núcleo.

Leia antes: `../CLAUDE.md` do projeto (arquitetura e decisões), `Modulo.cs`, `Servicos.cs`, `Atividades.cs`,
`Faixas.cs` (logos animadas) e `Demo.cs` (exemplos completos de atividades: timer, mídia, Claude, GPT).

## O esqueleto

```csharp
namespace Ilha.Modulos.Exemplo;

[Modulo("exemplo", "Exemplo")]                       // LigadoPorPadrao = false se não deve ligar sozinho
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

public sealed class ConfigExemplo { [JsonPropertyName("algo")] public int Algo { get; set; } = 3; }
```

A seção do módulo fica em `config.json` → `"modulos"` → `"exemplo"`, com `"ligado"` (o núcleo cuida) e os campos
da classe de config. `Salvar(_cfg)` grava de volta. Nunca salvar dentro de `ConfigMudou`.

## Os Serviços (`S`)

| | |
|---|---|
| `S.Ilha` | `Adicionar(atividade)`, `Alertar(alerta)`, `Expandir(atividade)`, `Recolher()`, `Expandida` |
| `S.Palco` | monta cenas: `NovaCena`, `Desenho` (Direct2D), `Bloco`, `Logo` (logo animada), `Texto` (DirectWrite), `Faixas` |
| `S.Sons` | `Tocar("chegada"/"alerta"/"timer"/"claude"/"conectou"/"erro", importante)`; já fica mudo no competitivo e no Não Incomodar, menos o importante |
| `S.Atalhos` | `Registrar(nome, padrão, ação)`: atalho global (RegisterHotKey); o Felipe troca em `atalhosModulos` na config |
| `S.Entrada` | campo de texto (teclado): `Abrir(inicial, mudou, enter, fechou, imagem, multilinha, senha)`, `Texto`, `Cursor`, `Fechar()`, evento `Mudou`; desenhar com `S.Palco.Texto.DesenharCampo(...)` |
| `S.Indicadores` | câmera e microfone em uso (`Definir(...)`); a ilha desenha as bolinhas sozinha |
| `S.ContextoAtual()` | `Competitivo`, `TelaCheia`, `D3DExclusivo`, `NaoPerturbe`, `Desktop`, `Processo` |
| `S.Config()` | config global (somente leitura) |
| `S.Avisar(texto)` | balão do ícone da bandeja |
| `S.NaInterface(ação)` | volta para a thread da interface (eventos de COM, WinRT, threads e timers de sistema chegam em outra) |
| `Credenciais` (estática) | `Gravar/Ler/Existe/Apagar(nome)` no Gerenciador de Credenciais (`Ilha/<nome>`). Chave nunca em arquivo, log ou mensagem de erro |

## Atividades (o que aparece na ilha)

Herdam de `Atividade` (ver `Demo.cs`). `Tipo` decide a prioridade (`claude`, `ia`, `timer`, `midia`, `uso`; o
Felipe ordena em `prioridades`). `Ligar()`/`Desligar()` quando começa e termina (algo novo aparece expandindo
sozinho). `Montar(palco, forma)` devolve a cena de cada forma: `Compacta` (até ~24 DIP de altura, a pílula tem 36),
`Bolha` (até 24×24, a bolinha ao lado), `Expandida` (cartão, até ~380×300), `Alerta`. Conteúdo mudou sem mudar de
tamanho: `Redesenhar()`. Mudou o layout: `MudouTamanho()`. Botões: `cena.Botoes.Add(new Botao { Area, Acao })`
(DIP, a partir do canto da cena). `PermitidaNoCompetitivo` (só o timer). `EhFerramenta` (calculadora, tradutor,
IA: abre pelo atalho sem estar "em andamento"). `LarguraCompactaMinima`. `Alertar(new Alerta { Tipo, Som, Importante, Criar })`.

Visual: pílula preta pura; texto branco (`Cor.Branco`, `Cor.Secundario`), acento da atividade (`Cor.Laranja`
timer, `Cor.Verde`, `Cor.Coral` Claude). Segoe UI Variable (já é a da `Escrita`). Nada de fonte serifada.

## Regras (sem exceção)

- **Só mexa na pasta do seu módulo** (`src/Ilha/Modulos/<Nome>/`) e, se precisar, num script novo em `ferramentas/`.
  Não edite arquivo do núcleo, o `.csproj`, o `CLAUDE.md` nem o `README.md`: outros agentes trabalham ao mesmo
  tempo. Precisa de algo no núcleo ou de um pacote NuGet? **Não faça**: escreva no relatório final ("pedidos ao
  núcleo") e contorne com o que já existe (P/Invoke, COM, WinRT do sistema, Vortice).
- **Nunca abra a ilha** (`Ilha.exe` sem argumento, `--demo`, `--roteiro`, `--medir`, `--autoteste`): tem uma ilha
  instalada rodando no PC do Felipe e ele pode estar usando. Teste com `Ilha.exe --testar <id>` (sem janela) e com
  sondas pequenas de console/PowerShell que só leem. Nada de laço ou processo deixado rodando.
- **Compilar só por** `powershell -ExecutionPolicy Bypass -File .\ferramentas\compilar.ps1` (na raiz do seu
  worktree): ele confere o Smart App Control e recompila com outro carimbo se barrar.
- **Desempenho**: a ilha roda sempre, inclusive com jogo aberto. Evento do sistema em vez de polling; polling só
  quando inevitável, com intervalo largo, e só enquanto a atividade estiver visível. Nada de redesenho por relógio
  sem mudança. Animação contínua só no compositor (`StartAnimation`), nunca por timer do app.
- **Anticheat**: nada de injeção, hook (`SetWindowsHookEx`), leitura de memória de outro processo nem `OpenProcess`
  com direitos além de `PROCESS_QUERY_LIMITED_INFORMATION`.
- **Repositório público**: nada pessoal no código (caminho com nome de usuário, nome de pasta privada, chave,
  e-mail). Caminhos pessoais vão para a config, não para o código. Imagem de marca de terceiros fora do git.
- Estilo: igual ao núcleo. Nomes em português, `Nullable` ligado, `using` explícitos (sem ImplicitUsings), comentário
  só onde a intenção não é óbvia, sem travessão.
- No fim: `--testar <id>` passando, compilação limpa, **commit no seu worktree** (mensagem em português dizendo o
  que e por quê, terminando com a linha `Co-Authored-By` do Claude) e o relatório com o nome do ramo.
