using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using Condition = System.Windows.Automation.Condition;

namespace Ilha.Claude;

/// <summary>
/// O que a ponte leu de um painel de sessão do app. <see cref="Nivel"/> is the base effort level (0..4, Low to Max)
/// and <see cref="Ultracode"/> is the separate toggle; <see cref="Esforco"/> is the app's text ("Alto · Ultracode").
/// </summary>
internal sealed record PainelLido(string? Nome, string? Modelo, string? Esforco, int? Nivel, bool Ultracode, string? Permissao, bool Foco);

internal sealed record RespostaPonte
{
    public bool Ok { get; init; }
    public string? Erro { get; init; }
    public List<PainelLido> Paineis { get; init; } = new();
    public PainelLido? Painel { get; init; }
    /// <summary>Os modelos que o menu do app mostrou (o "modelo" sempre lê, para a lista da ilha ficar certa).</summary>
    public List<string> Opcoes { get; init; } = new();

    public static RespostaPonte Falhou(string erro) => new() { Ok = false, Erro = erro };
}

/// <summary>
/// O botão do modelo do app: "Modelo: Opus 5.5 Alto · Ultracode" (lido em 01/11/2026). The effort is a base level
/// (0..4: Baixo, Médio, Alto, Extra, Máx; Low, Medium, High, Extra high, Max in English) plus Ultracode, a separate
/// on/off toggle in the same submenu.
/// </summary>
internal static class BotaoDoModelo
{
    public static readonly string[] Niveis = { "Baixo", "Médio", "Alto", "Extra", "Máx" };
    public const string ULTRACODE = "Ultracode";
    private static readonly string[] RotulosPt = { "Baixo", "Médio", "Alto", "Extra", "Máx" };
    private static readonly string[] LabelsEn = { "Low", "Medium", "High", "Extra", "Max" };
    /// <summary>Effort labels shown in the island, in the interface language (parsing the app's button uses <see cref="IndiceDoNivel"/>).</summary>
    public static string[] Rotulos => Lang.English ? LabelsEn : RotulosPt;

    // every name the app may give each level, accent-free and lowercase (Portuguese and English)
    private static readonly string[][] Apelidos =
    {
        new[] { "baixo", "low" },
        new[] { "medio", "medium" },
        new[] { "alto", "high" },
        new[] { "extra", "extra alto", "extra high", "xhigh", "x-high", "x high" },
        new[] { "max", "maximo", "maximum" },
    };

    public static bool EhBotao(string nome) =>
        nome.StartsWith("Modelo: ", StringComparison.Ordinal) || nome.StartsWith("Model: ", StringComparison.Ordinal);

    /// <summary>The model menu is named after the button; matched by prefix because the name changes when Ultracode flips.</summary>
    public static bool EhMenuDoModelo(string nome) =>
        nome.StartsWith("Modelo:", StringComparison.Ordinal) || nome.StartsWith("Model:", StringComparison.Ordinal);

    public static bool EhEsforco(string nome) =>
        SemAcento(nome).StartsWith("esforco", StringComparison.Ordinal) || SemAcento(nome).StartsWith("effort", StringComparison.Ordinal);

    /// <summary>The Ultracode toggle: "Ultracode", maybe followed by a badge ("Ultracode Beta").</summary>
    public static bool EhUltracode(string nome)
    {
        string n = SemAcento(nome.Trim());
        return n == "ultracode" || n.StartsWith("ultracode ", StringComparison.Ordinal);
    }

    /// <summary>The effort text as the app writes it: base name plus " · Ultracode" when the toggle is on.</summary>
    public static string? Juntar(string? baseNome, bool ultra) =>
        baseNome is { Length: > 0 } b ? (ultra ? b + " · " + ULTRACODE : b) : ultra ? ULTRACODE : null;

    /// <summary>The base part of an effort text ("Alto · Ultracode" gives "Alto"; "Ultracode" alone gives null).</summary>
    public static string? BaseDe(string? esforco)
    {
        if (esforco == null) return null;
        string b = esforco.Split('·')[0].Trim();
        return b.Length == 0 || EhUltracode(b) ? null : b;
    }

    /// <summary>The effort shown in the island, in the interface language ("Alto · Ultracode", "High · Ultracode").</summary>
    public static string? Rotulo(int? nivel, bool ultra, string? esforcoDoApp = null)
    {
        string? b = nivel is int k && k >= 0 && k < Rotulos.Length ? Rotulos[k] : BaseDe(esforcoDoApp);
        return Juntar(b, ultra);
    }

    public static string SemAcento(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().ToLowerInvariant();
    }

    public static int IndiceDoNivel(string nome)
    {
        string n = Regex.Replace(SemAcento(nome.Trim()), @"\s+", " ");
        for (int k = 0; k < Apelidos.Length; k++) if (Array.IndexOf(Apelidos[k], n) >= 0) return k;
        return -1;
    }

    /// <summary>(modelo, esforço como o app mostra, nível base 0 a 4, ultracode).</summary>
    public static (string Modelo, string? Esforco, int? Nivel, bool Ultracode) Ler(string nome)
    {
        int dois = nome.IndexOf(':');
        string texto = dois >= 0 ? nome[(dois + 1)..].Trim() : nome.Trim();
        var partes = texto.Split('·').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (partes.Count == 0) return ("", null, null, false);
        bool ultra = partes.Skip(1).Any(EhUltracode);
        string modelo = partes[0];
        string? baseNome = null;
        int? nivelBase = null;
        var palavras = modelo.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // two-word names first ("Extra high"), so "high" alone is not read as High
        for (int n = Math.Min(2, palavras.Length - 1); n >= 1 && nivelBase == null; n--)
        {
            string fim = string.Join(' ', palavras[^n..]);
            int k = IndiceDoNivel(fim);
            if (k >= 0) { baseNome = fim; nivelBase = k; modelo = string.Join(' ', palavras[..^n]); }
        }
        return (modelo, Juntar(baseNome, ultra), nivelBase, ultra);
    }
}

/// <summary>
/// Ponte com o app desktop do Claude (aba Code) pela acessibilidade do Windows (UI Automation): lê o modelo e o
/// esforço de cada painel de sessão, troca os dois e manda mensagem, sem tecla e sem tirar o foco de onde ele está.
/// Roda numa thread MTA própria (a Microsoft manda chamar UIA fora da thread da interface), criada sob demanda e
/// encerrada depois de 30 s sem pedido; cada pedido tem prazo, e uma thread que travou é abandonada.
/// Os submenus do app só abrem por hover: um WM_MOUSEMOVE postado na janela dele abre sem mexer no cursor de verdade.
/// </summary>
internal sealed class PonteClaudeApp : IDisposable
{
    private sealed class Trabalho
    {
        public required Func<AutomationElement, RespostaPonte> Fazer;
        public required Action<RespostaPonte> Pronto;
        public int Feito;
    }

    private BlockingCollection<Trabalho>? _fila;
    private readonly object _trava = new();
    private bool _fechada;

    // ------------------------------------------------------------------ pedidos (qualquer thread; a resposta chega numa thread do pool)

    public void Estado(Action<RespostaPonte> pronto) => Pedir(Ler, pronto, 12000);

    public void TrocarModelo(string alvo, string modelo, Action<RespostaPonte> pronto) => Pedir(r => Modelo(r, alvo, modelo), pronto, 20000);

    /// <summary>Sets the base effort level (0..4); never touches the Ultracode toggle.</summary>
    public void TrocarEsforco(string alvo, int nivel, Action<RespostaPonte> pronto) => Pedir(r => Esforco(r, alvo, nivel), pronto, 16000);

    /// <summary>Turns the Ultracode toggle on or off (reads it first and only flips it when it differs).</summary>
    public void DefinirUltracode(string alvo, bool ligar, Action<RespostaPonte> pronto) => Pedir(r => Ultracode(r, alvo, ligar), pronto, 18000);

    public void Enviar(string alvo, string texto, Action<RespostaPonte> pronto) => Pedir(r => Mandar(r, alvo, texto), pronto, 15000);

    private void Pedir(Func<AutomationElement, RespostaPonte> fazer, Action<RespostaPonte> pronto, int prazoMs)
    {
        Timer? prazo = null;
        var t = new Trabalho { Fazer = fazer, Pronto = r => { prazo?.Dispose(); pronto(r); } };
        BlockingCollection<Trabalho> fila;
        lock (_trava)
        {
            if (_fechada) { pronto(RespostaPonte.Falhou(Lang.T("A ilha está fechando.", "The island is closing."))); return; }
            fila = _fila ??= NovaThread();
            try { fila.Add(t); }
            catch (InvalidOperationException) { fila = _fila = NovaThread(); fila.Add(t); }
        }
        prazo = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref t.Feito, 1) != 0) return;
            // travou dentro da UIA: o próximo pedido sobe outra thread
            lock (_trava) { if (_fila == fila) { fila.CompleteAdding(); _fila = null; } }
            pronto(RespostaPonte.Falhou(Lang.T("O app do Claude demorou demais para responder.", "The Claude app took too long to respond.")));
        }, null, prazoMs, Timeout.Infinite);
    }

    private BlockingCollection<Trabalho> NovaThread()
    {
        var fila = new BlockingCollection<Trabalho>();
        var th = new Thread(() => Laco(fila)) { IsBackground = true, Name = "ilha: ponte com o app do Claude" };
        th.SetApartmentState(ApartmentState.MTA);
        th.Start();
        return fila;
    }

    private void Laco(BlockingCollection<Trabalho> fila)
    {
        // coordenadas em pixel de verdade: o WM_MOUSEMOVE e a UIA precisam falar a mesma língua
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }
        while (true)
        {
            Trabalho? t;
            try
            {
                if (!fila.TryTake(out t, 30000))
                {
                    // 30 s sem pedido: a thread sai (um pedido que entrou nesse meio tempo ainda é atendido)
                    lock (_trava)
                    {
                        if (fila.Count == 0 || fila.IsAddingCompleted) { if (_fila == fila) _fila = null; break; }
                    }
                    continue;
                }
            }
            catch (InvalidOperationException) { break; }
            RespostaPonte r;
            try
            {
                var raiz = JanelaDoClaude(out _pidDoApp, out _janelaDoApp);
                r = raiz == null ? RespostaPonte.Falhou(Lang.T("O app do Claude não está aberto.", "The Claude app isn't open.")) : t.Fazer(raiz);
            }
            catch (ElementNotAvailableException) { r = RespostaPonte.Falhou(Lang.T("O app do Claude mudou a tela no meio; tenta de novo.", "The Claude app changed its screen midway; try again.")); }
            catch (Exception e) { r = RespostaPonte.Falhou(Lang.T("A ponte com o app falhou: ", "The bridge to the app failed: ") + e.Message); }
            if (Interlocked.Exchange(ref t.Feito, 1) == 0) t.Pronto(r);
        }
        lock (_trava) { if (_fila == fila) _fila = null; }
    }

    public void Dispose()
    {
        lock (_trava)
        {
            _fechada = true;
            _fila?.CompleteAdding();
            _fila = null;
        }
    }

    // ------------------------------------------------------------------ a árvore do app

    [ThreadStatic] private static int _pidDoApp;
    [ThreadStatic] private static IntPtr _janelaDoApp;

    private static AutomationElement? JanelaDoClaude(out int pid, out IntPtr janela)
    {
        pid = 0;
        janela = IntPtr.Zero;
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle == "Claude")
                    {
                        pid = p.Id;
                        janela = p.MainWindowHandle;
                        return AutomationElement.FromHandle(p.MainWindowHandle);
                    }
                }
                catch (InvalidOperationException) { }
            }
        }
        return null;
    }

    private sealed class Painel
    {
        public string? Nome;
        public Rect Cabecalho = Rect.Empty;
        public Rect Area = Rect.Empty;
        public string? Modelo, Esforco, Permissao;
        public int? Nivel;
        public bool Ultracode, Foco;
        public AutomationElement? BotaoModelo, Prompt;

        public PainelLido Lido() => new(Nome, Modelo, Esforco, Nivel, Ultracode, Permissao, Foco);
    }

    private static readonly Regex Renomear = new(@"^(.+), (renomear sess.o|rename session)$", RegexOptions.CultureInvariant);

    private static AutomationElement[] DoTipo(AutomationElement raiz, params ControlType[] tipos)
    {
        Condition c = tipos.Length == 1
            ? new PropertyCondition(AutomationElement.ControlTypeProperty, tipos[0])
            : new OrCondition(tipos.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray());
        var achados = raiz.FindAll(TreeScope.Descendants, c);
        var r = new AutomationElement[achados.Count];
        achados.CopyTo(r, 0);
        return r;
    }

    private static bool Valido(Rect r) => !r.IsEmpty && !double.IsInfinity(r.X) && !double.IsNaN(r.X);
    private static Point Centro(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private static Rect AreaDoPainel(AutomationElement cabecalho, List<Point> outros, AutomationElement raiz)
    {
        // sobe do cabeçalho até o maior bloco que não engole o cabeçalho de outro painel (qualquer arranjo da tela dividida)
        var area = cabecalho.Current.BoundingRectangle;
        var a = TreeWalker.ControlViewWalker.GetParent(cabecalho);
        while (a != null && !Automation.Compare(a, raiz))
        {
            var r = a.Current.BoundingRectangle;
            if (Valido(r))
            {
                if (outros.Any(o => r.Contains(o))) break;
                area = r;
            }
            a = TreeWalker.ControlViewWalker.GetParent(a);
        }
        return area;
    }

    private static List<Painel> Paineis(AutomationElement raiz)
    {
        var botoes = DoTipo(raiz, ControlType.Button);
        // primeira pergunta depois de um tempo: o Chromium ainda está montando a árvore de acessibilidade
        var fim = Environment.TickCount64 + 4000;
        while (!botoes.Any(b => Renomear.IsMatch(b.Current.Name ?? "") || BotaoDoModelo.EhBotao(b.Current.Name ?? "")) && Environment.TickCount64 < fim)
        {
            Thread.Sleep(300);
            botoes = DoTipo(raiz, ControlType.Button);
        }
        var cabecalhos = new List<(string Nome, Rect R, AutomationElement E)>();
        foreach (var b in botoes)
        {
            var m = Renomear.Match(b.Current.Name ?? "");
            if (!m.Success) continue;
            var r = b.Current.BoundingRectangle;
            if (Valido(r)) cabecalhos.Add((m.Groups[1].Value, r, b));
        }
        var paineis = new List<Painel>();
        foreach (var c in cabecalhos)
        {
            var outros = cabecalhos.Where(o => o.E != c.E).Select(o => Centro(o.R)).ToList();
            paineis.Add(new Painel { Nome = c.Nome, Cabecalho = c.R, Area = AreaDoPainel(c.E, outros, raiz) });
        }
        if (paineis.Count == 0) paineis.Add(new Painel());

        Painel? DoPainel(Rect r)
        {
            var ponto = Centro(r);
            foreach (var p in paineis) if (Valido(p.Area) && p.Area.Contains(ponto)) return p;
            // sem bloco que contenha: o cabeçalho mais perto em cima e à esquerda
            Painel? dono = null;
            foreach (var p in paineis)
            {
                if (!Valido(p.Cabecalho)) { dono ??= p; continue; }
                if (p.Cabecalho.X <= r.X + 40 && p.Cabecalho.Y <= r.Y + 10)
                    if (dono == null || !Valido(dono.Cabecalho) || p.Cabecalho.X > dono.Cabecalho.X || (p.Cabecalho.X == dono.Cabecalho.X && p.Cabecalho.Y > dono.Cabecalho.Y)) dono = p;
            }
            return dono;
        }

        foreach (var b in botoes)
        {
            var r = b.Current.BoundingRectangle;
            if (!Valido(r) || b.Current.IsOffscreen) continue;
            string n = b.Current.Name ?? "";
            if (BotaoDoModelo.EhBotao(n))
            {
                var p = DoPainel(r);
                if (p == null || p.BotaoModelo != null) continue;
                var (modelo, esforco, nivel, ultra) = BotaoDoModelo.Ler(n);
                p.Modelo = modelo; p.Esforco = esforco; p.Nivel = nivel; p.Ultracode = ultra; p.BotaoModelo = b;
            }
            else if (n.StartsWith("Ignorar permiss", StringComparison.Ordinal) || n.StartsWith("Modo", StringComparison.Ordinal))
            {
                var p = DoPainel(r);
                if (p != null && p.Permissao == null) p.Permissao = n;
            }
        }
        foreach (var c in DoTipo(raiz, ControlType.Edit))
        {
            if (c.Current.Name != "Prompt" || c.Current.IsOffscreen) continue;
            var p = DoPainel(c.Current.BoundingRectangle);
            if (p != null && p.Prompt == null) { p.Prompt = c; p.Foco = c.Current.HasKeyboardFocus; }
        }
        return paineis;
    }

    /// <summary>Só o painel com esse nome: mandar mensagem ou trocar modelo na sessão errada é pior que falhar.</summary>
    private static Painel? Escolher(List<Painel> paineis, string alvo)
    {
        var achados = paineis.Where(p => p.Nome != null && string.Equals(p.Nome, alvo, StringComparison.OrdinalIgnoreCase)).ToList();
        // duas sessões com o mesmo nome na tela: não dá para saber qual é, então nenhuma
        return achados.Count == 1 ? achados[0] : null;
    }

    private static string SEM_SESSAO => Lang.T("Não achei essa sessão aberta na tela do app do Claude (ou tem duas com o mesmo nome).", "Couldn't find this session open in the Claude app (or there are two with the same name).");

    private static T? Esperar<T>(Func<T?> f, int ms) where T : class
    {
        var fim = Environment.TickCount64 + ms;
        while (true)
        {
            var r = f();
            if (r != null || Environment.TickCount64 >= fim) return r;
            Thread.Sleep(60);
        }
    }

    private static AutomationElement? MenuChamado(AutomationElement raiz, string nome) =>
        DoTipo(raiz, ControlType.Menu).FirstOrDefault(m => m.Current.Name == nome);

    /// <summary>The open model menu: by the button's exact name, else by the "Modelo:"/"Model:" prefix (the name changes when Ultracode flips).</summary>
    private static AutomationElement? MenuDoModelo(AutomationElement raiz, string? nome)
    {
        var menus = DoTipo(raiz, ControlType.Menu);
        return (nome != null ? menus.FirstOrDefault(m => m.Current.Name == nome) : null)
            ?? menus.FirstOrDefault(m => BotaoDoModelo.EhMenuDoModelo(m.Current.Name ?? ""));
    }

    private static AutomationElement[] ItensDoMenu(AutomationElement menu, ControlType tipo) =>
        menu.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, tipo))
            .Cast<AutomationElement>().OrderBy(e => e.Current.BoundingRectangle.Y).ToArray();

    private static (ExpandCollapsePattern? Exp, AutomationElement? Menu) AbrirMenuDoModelo(AutomationElement raiz, Painel p)
    {
        if (p.BotaoModelo == null) return (null, null);
        var exp = (ExpandCollapsePattern)p.BotaoModelo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (exp.Current.ExpandCollapseState != ExpandCollapseState.Expanded) exp.Expand();
        string? nome = null;
        try { nome = p.BotaoModelo.Current.Name; } catch (Exception) { }
        return (exp, Esperar(() => MenuDoModelo(raiz, nome), 1500));
    }

    private static void FecharMenu(ExpandCollapsePattern? exp)
    {
        try { if (exp != null && exp.Current.ExpandCollapseState == ExpandCollapseState.Expanded) exp.Collapse(); }
        catch (Exception) { }
    }

    /// <summary>
    /// Opens a submenu. First the item's MSAA default action (a click for Chromium, which opens the submenu wherever the
    /// real cursor is). Then the old hover paths: a WM_MOUSEMOVE posted to the app's render widget, then through the
    /// window under the point. The hover alone failed on 01/11/2026 whenever the real cursor was over the island (the
    /// app got the move and then lost the pointer again). <paramref name="achouItem"/> tells "the item is not in the
    /// menu" apart from "it did not open".
    /// </summary>
    private static AutomationElement? AbrirSubmenu(AutomationElement raiz, AutomationElement menu, Func<string, bool> qual, out bool achouItem)
    {
        var item = ItensDoMenu(menu, ControlType.MenuItem).FirstOrDefault(i => qual(i.Current.Name ?? ""));
        achouItem = item != null;
        if (item == null) return null;
        string nome = item.Current.Name;
        var ja = MenuChamado(raiz, nome);
        if (ja != null) return ja;
        if (MsaaClicarItem(_janelaDoApp, nome))
        {
            // the click alone only focuses the trigger; the Right arrow on the focused trigger opens the submenu
            Thread.Sleep(150);
            var porClique = MenuChamado(raiz, nome);
            if (porClique != null) return porClique;
            if (TeclaNoRenderizador(_janelaDoApp, 0x27))
            {
                var porTecla = Esperar(() => MenuChamado(raiz, nome), 1500);
                if (porTecla != null) return porTecla;
            }
        }
        var c = Centro(item.Current.BoundingRectangle);
        int x = (int)c.X, y = (int)c.Y;
        if (PassarNoRenderizador(_janelaDoApp, x, y))
        {
            var sub = Esperar(() => MenuChamado(raiz, nome), 1200);
            if (sub != null) return sub;
        }
        if (PassarPeloPonto(x, y, _pidDoApp)) return Esperar(() => MenuChamado(raiz, nome), 1200);
        return null;
    }

    private static AutomationElement? AbrirSubmenu(AutomationElement raiz, AutomationElement menu, Func<string, bool> qual) =>
        AbrirSubmenu(raiz, menu, qual, out _);

    // ------------------------------------------------------------------ the effort submenu

    private static string ESFORCO_NAO_ABRIU => Lang.T("Deixe o app do Claude visível para trocar o esforço.", "Keep the Claude app visible to change the effort.");
    private static string ESFORCO_SUMIU => Lang.T("O menu do app não tem mais o item de esforço; a ponte precisa ser atualizada.", "The app's menu no longer has the effort item; the bridge needs an update.");

    /// <summary>The Ultracode toggle: TogglePattern on any control type (CheckBox, Button for role=switch, MenuItem).</summary>
    private sealed class Interruptor
    {
        public AutomationElement? Elemento;
        public TogglePattern? Toggle;
        public Accessibility.IAccessible? Msaa;
        public int MsaaFilho;

        public bool? Ligado()
        {
            try
            {
                if (Toggle != null) return Toggle.Current.ToggleState == ToggleState.On;
                if (Msaa != null) return Msaa.get_accState(MsaaFilho) is int st && (st & (0x10 | 0x08)) != 0; // CHECKED | PRESSED
            }
            catch (Exception) { }
            return null;
        }

        public void Alternar()
        {
            if (Toggle != null) Toggle.Toggle();
            else Msaa?.accDoDefaultAction(MsaaFilho);
        }
    }

    private static Interruptor? AcharUltracode(AutomationElement sub, AutomationElement? menu)
    {
        var comToggle = new AndCondition(
            new PropertyCondition(AutomationElement.IsTogglePatternAvailableProperty, true),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
        foreach (var onde in new[] { sub, menu })
        {
            if (onde == null) continue;
            foreach (AutomationElement e in onde.FindAll(TreeScope.Descendants, comToggle))
            {
                if (!BotaoDoModelo.EhUltracode(e.Current.Name ?? "")) continue;
                try { return new Interruptor { Elemento = e, Toggle = (TogglePattern)e.GetCurrentPattern(TogglePattern.Pattern) }; }
                catch (Exception) { }
            }
        }
        // no TogglePattern: the MSAA state and default action of the same node (only if UIA shows it in the submenu)
        bool existe = sub.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
            .Any(e => BotaoDoModelo.EhUltracode(e.Current.Name ?? "") && e.Current.ControlType != ControlType.Text);
        if (!existe) return null;
        var (acc, filho) = MsaaUltracode(_janelaDoApp);
        return acc != null ? new Interruptor { Msaa = acc, MsaaFilho = filho } : null;
    }

    private static int? NivelMarcado(AutomationElement[] radios)
    {
        for (int k = 0; k < radios.Length; k++)
            try { if (((SelectionItemPattern)radios[k].GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected) return k; } catch (Exception) { }
        return null;
    }

    /// <summary>The panel read again after an action, with the Ultracode state that the submenu showed (it is the source of truth).</summary>
    private static PainelLido? Depois(AutomationElement raiz, string alvo, bool? ultra)
    {
        var lido = Escolher(Paineis(raiz), alvo)?.Lido();
        if (lido == null || ultra is not bool u || lido.Ultracode == u) return lido;
        return lido with { Ultracode = u, Esforco = BotaoDoModelo.Juntar(BotaoDoModelo.BaseDe(lido.Esforco), u) };
    }

    // ------------------------------------------------------------------ ações

    private static RespostaPonte Ler(AutomationElement raiz) =>
        new() { Ok = true, Paineis = Paineis(raiz).Select(p => p.Lido()).ToList() };

    private static RespostaPonte Modelo(AutomationElement raiz, string alvo, string valor)
    {
        var p = Escolher(Paineis(raiz), alvo);
        if (p?.BotaoModelo == null) return RespostaPonte.Falhou(SEM_SESSAO);
        var (exp, menu) = AbrirMenuDoModelo(raiz, p);
        if (menu == null) { FecharMenu(exp); return RespostaPonte.Falhou(Lang.T("O menu de modelo não abriu.", "The model menu didn't open.")); }
        var opcoes = new List<string>();
        AutomationElement? item = null;
        void Procurar(AutomationElement m)
        {
            foreach (var r in ItensDoMenu(m, ControlType.RadioButton))
            {
                string n = r.Current.Name ?? "";
                if (n.Length > 0 && !opcoes.Contains(n)) opcoes.Add(n);
                if (item == null && string.Equals(n, valor, StringComparison.OrdinalIgnoreCase)) item = r;
            }
        }
        Procurar(menu);
        if (item == null)
        {
            var mais = AbrirSubmenu(raiz, menu, n => n.StartsWith("Mais modelos", StringComparison.Ordinal) || n.StartsWith("More models", StringComparison.Ordinal));
            if (mais != null) Procurar(mais);
        }
        if (item == null)
        {
            FecharMenu(exp);
            return new RespostaPonte { Ok = false, Erro = Lang.T($"O modelo {valor} não está no menu do app.", $"The model {valor} isn't in the app's menu."), Opcoes = opcoes };
        }
        ((InvokePattern)item.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(500);
        FecharMenu(exp);
        var depois = Escolher(Paineis(raiz), alvo);
        return new RespostaPonte { Ok = true, Painel = depois?.Lido(), Opcoes = opcoes };
    }

    /// <summary>Opens the model menu and the effort submenu of the panel; on failure, the user-facing error.</summary>
    private static (ExpandCollapsePattern? Exp, AutomationElement? Menu, AutomationElement? Sub, string? Erro) AbrirEsforco(AutomationElement raiz, Painel p)
    {
        var (exp, menu) = AbrirMenuDoModelo(raiz, p);
        if (menu == null) { FecharMenu(exp); return (null, null, null, Lang.T("O menu de modelo não abriu.", "The model menu didn't open.")); }
        var sub = AbrirSubmenu(raiz, menu, BotaoDoModelo.EhEsforco, out bool achou);
        if (sub == null) { FecharMenu(exp); return (null, null, null, achou ? ESFORCO_NAO_ABRIU : ESFORCO_SUMIU); }
        return (exp, menu, sub, null);
    }

    /// <summary>Base level 0..4: Invoke on the radio of that index; the Ultracode toggle is left as it is.</summary>
    private static RespostaPonte Esforco(AutomationElement raiz, string alvo, int nivel)
    {
        nivel = Math.Clamp(nivel, 0, BotaoDoModelo.Niveis.Length - 1);
        var p = Escolher(Paineis(raiz), alvo);
        if (p?.BotaoModelo == null) return RespostaPonte.Falhou(SEM_SESSAO);
        var (exp, menu, sub, erro) = AbrirEsforco(raiz, p);
        if (sub == null) return RespostaPonte.Falhou(erro!);
        // the items carry badges ("Médio Recomendado", "Máx 3.5x ou mais de uso"): the choice is by index
        var radios = ItensDoMenu(sub, ControlType.RadioButton);
        if (radios.Length != BotaoDoModelo.Niveis.Length)
        {
            FecharMenu(exp);
            return RespostaPonte.Falhou(Lang.T($"O controle de esforço do app mudou ({radios.Length} níveis); a ponte precisa ser atualizada.", $"The app's effort control changed ({radios.Length} levels); the bridge needs an update."));
        }
        bool? ultra = AcharUltracode(sub, menu)?.Ligado();
        if (NivelMarcado(radios) != nivel)
        {
            ((InvokePattern)radios[nivel].GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Thread.Sleep(400);
        }
        FecharMenu(exp);
        Thread.Sleep(150);
        return new RespostaPonte { Ok = true, Painel = Depois(raiz, alvo, ultra) };
    }

    /// <summary>Ultracode on or off: reads the toggle and flips it only when it differs; the base level is left as it is.</summary>
    private static RespostaPonte Ultracode(AutomationElement raiz, string alvo, bool ligar)
    {
        var p = Escolher(Paineis(raiz), alvo);
        if (p?.BotaoModelo == null) return RespostaPonte.Falhou(SEM_SESSAO);
        var (exp, menu, sub, erro) = AbrirEsforco(raiz, p);
        if (sub == null) return RespostaPonte.Falhou(erro!);
        var chave = AcharUltracode(sub, menu);
        if (chave == null)
        {
            FecharMenu(exp);
            return RespostaPonte.Falhou(Lang.T("Não achei o Ultracode no menu de esforço do app; a ponte precisa ser atualizada.", "Couldn't find Ultracode in the app's effort menu; the bridge needs an update."));
        }
        bool? agora = chave.Ligado();
        if (agora != ligar)
        {
            chave.Alternar();
            Thread.Sleep(300);
            agora = chave.Ligado();
            if (agora == null)
            {
                // flipping it closed or rebuilt the menu: open it again (by prefix, the button got a new name) and read
                FecharMenu(exp);
                Thread.Sleep(150);
                var p2 = Escolher(Paineis(raiz), alvo) ?? p;
                (exp, menu, sub, _) = AbrirEsforco(raiz, p2);
                agora = sub != null ? AcharUltracode(sub, menu)?.Ligado() : null;
            }
        }
        FecharMenu(exp);
        Thread.Sleep(150);
        var depois = Depois(raiz, alvo, agora);
        if (agora is bool a && a != ligar)
            return new RespostaPonte { Ok = false, Painel = depois, Erro = Lang.T("O app não aceitou a troca do Ultracode.", "The app didn't accept the Ultracode change.") };
        return new RespostaPonte { Ok = true, Painel = depois };
    }

    private static RespostaPonte Mandar(AutomationElement raiz, string alvo, string texto)
    {
        var p = Escolher(Paineis(raiz), alvo);
        if (p?.Prompt == null) return RespostaPonte.Falhou(p == null ? SEM_SESSAO : Lang.T("Não achei o campo de mensagem dessa sessão no app do Claude.", "Couldn't find this session's message field in the Claude app."));
        var valor = (ValuePattern)p.Prompt.GetCurrentPattern(ValuePattern.Pattern);
        string rascunho = valor.Current.Value ?? "";
        var area = Valido(p.Area) ? p.Area : p.Prompt.Current.BoundingRectangle;
        valor.SetValue(texto);
        // o botão Enviar só existe com texto no campo, dentro do mesmo painel
        var enviar = Esperar(() => DoTipo(raiz, ControlType.Button).FirstOrDefault(b =>
            (b.Current.Name is "Enviar" or "Send") && !b.Current.IsOffscreen && b.Current.IsEnabled && area.Contains(Centro(b.Current.BoundingRectangle))), 2500);
        if (enviar == null)
        {
            valor.SetValue(rascunho);
            return RespostaPonte.Falhou(Lang.T("O app não aceitou o texto (o botão Enviar não apareceu).", "The app didn't accept the text (the Send button didn't show up)."));
        }
        ((InvokePattern)enviar.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(500);
        // devolve o rascunho que já estava no campo
        if (rascunho.Trim().Length > 0) valor.SetValue(rascunho);
        return new RespostaPonte { Ok = true, Painel = p.Lido() };
    }

    // ------------------------------------------------------------------ hover sem mexer no cursor

    [StructLayout(LayoutKind.Sequential)] private struct PONTO { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RET { public int Esq, Topo, Dir, Base; }
    private delegate bool EnumFilhas(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PONTO p);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref PONTO p);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr pai, EnumFilhas f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RET r);

    /// <summary>The app's Chromium render widget (Chrome_RenderWidgetHostHWND child of the main window) under the point, or the largest visible one.</summary>
    private static IntPtr Renderizador(IntPtr janela, int x, int y)
    {
        if (janela == IntPtr.Zero) return IntPtr.Zero;
        IntPtr noPonto = IntPtr.Zero, maior = IntPtr.Zero;
        long area = 0;
        var nome = new StringBuilder(64);
        EnumFilhas f = (h, _) =>
        {
            nome.Clear();
            GetClassNameW(h, nome, nome.Capacity);
            if (nome.ToString() != "Chrome_RenderWidgetHostHWND" || !IsWindowVisible(h) || !GetWindowRect(h, out var r)) return true;
            if (x >= r.Esq && x < r.Dir && y >= r.Topo && y < r.Base) { noPonto = h; return false; }
            long a = (long)(r.Dir - r.Esq) * (r.Base - r.Topo);
            if (a > area) { area = a; maior = h; }
            return true;
        };
        EnumChildWindows(janela, f, IntPtr.Zero);
        GC.KeepAlive(f);
        return noPonto != IntPtr.Zero ? noPonto : maior;
    }

    private static void Postar(IntPtr h, int x, int y)
    {
        var c = new PONTO { X = x, Y = y };
        ScreenToClient(h, ref c);
        for (int i = 0; i < 2; i++)
        {
            int cx = c.X + i * 2;
            PostMessageW(h, 0x0200, IntPtr.Zero, (IntPtr)((c.Y << 16) | (cx & 0xFFFF)));
            Thread.Sleep(40);
        }
    }

    /// <summary>
    /// A key (WM_KEYDOWN and WM_KEYUP) posted to the app's render widget: Chromium hands it to the focused element even
    /// with the app in the background. Tested on 01/11/2026 with the Right arrow on the focused effort trigger.
    /// </summary>
    private static bool TeclaNoRenderizador(IntPtr janela, int vk)
    {
        var h = Renderizador(janela, int.MinValue, int.MinValue);
        if (h == IntPtr.Zero) return false;
        // repeat count 1, scan code of the arrow (0x4D) with the extended bit; key up also has the previous and transition bits
        PostMessageW(h, 0x0100, (IntPtr)vk, (IntPtr)0x014D0001);
        Thread.Sleep(30);
        PostMessageW(h, 0x0101, (IntPtr)vk, unchecked((IntPtr)(int)0xC14D0001));
        return true;
    }

    /// <summary>Hover at (x, y) of the screen sent straight to the app's render widget, even with another window on top.</summary>
    private static bool PassarNoRenderizador(IntPtr janela, int x, int y)
    {
        var h = Renderizador(janela, x, y);
        if (h == IntPtr.Zero) return false;
        Postar(h, x, y);
        return true;
    }

    /// <summary>Old path: move o "mouse" do app para (x, y) da tela pela janela que está no ponto, só se for do app.</summary>
    private static bool PassarPeloPonto(int x, int y, int pidDoApp)
    {
        var p = new PONTO { X = x, Y = y };
        var h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid != (uint)pidDoApp) return false;
        Postar(h, x, y);
        return true;
    }

    // ------------------------------------------------------------------ MSAA fallback for the Ultracode toggle

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? obj);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(Accessibility.IAccessible pai, int inicio, int quantos,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object?[] filhos, out int obtidos);

    /// <summary>
    /// Finds the Ultracode node through MSAA (for when UIA has no TogglePattern on it). Walks the render widget's tree
    /// from the last child (Chromium puts the open menus at the end of the page), with a node and time budget.
    /// </summary>
    private static (Accessibility.IAccessible? Acc, int Filho) MsaaUltracode(IntPtr janela) => MsaaAchar(janela, EhChaveMsaa);

    /// <summary>The first MSAA node (from the end of the page) that matches, with a node and time budget.</summary>
    private static (Accessibility.IAccessible? Acc, int Filho) MsaaAchar(IntPtr janela, Func<Accessibility.IAccessible, int, bool> qual)
    {
        var rw = Renderizador(janela, int.MinValue, int.MinValue);
        if (rw == IntPtr.Zero) return (null, 0);
        var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        if (AccessibleObjectFromWindow(rw, 0xFFFFFFFC, ref iid, out var raiz) != 0 || raiz is not Accessibility.IAccessible acc) return (null, 0);
        var pilha = new Stack<Accessibility.IAccessible>();
        pilha.Push(acc);
        int visitados = 0;
        var fim = Environment.TickCount64 + 1500;
        while (pilha.Count > 0 && visitados < 6000 && Environment.TickCount64 < fim)
        {
            var no = pilha.Pop();
            visitados++;
            try
            {
                if (qual(no, 0)) return (no, 0);
                int n = no.accChildCount;
                if (n <= 0) continue;
                var filhos = new object?[n];
                if (AccessibleChildren(no, 0, n, filhos, out int obtidos) < 0) continue;
                // pushed in order, popped from the last child
                for (int i = 0; i < obtidos; i++)
                {
                    if (filhos[i] is Accessibility.IAccessible f) pilha.Push(f);
                    else if (filhos[i] is int id && qual(no, id)) return (no, id);
                }
            }
            catch (Exception) { }
        }
        return (null, 0);
    }

    /// <summary>
    /// The menu item's default action, which for Chromium is a click: the submenu trigger opens on click, so this needs
    /// no mouse at all. Only a menu item (role 0x0C) with exactly that name.
    /// </summary>
    private static bool MsaaClicarItem(IntPtr janela, string nome)
    {
        var (acc, filho) = MsaaAchar(janela, (no, f) => no.get_accName(f) is string n && n == nome && no.get_accRole(f) is int papel && papel == 0x0C);
        if (acc == null) return false;
        try { acc.accDoDefaultAction(filho); return true; }
        catch (Exception) { return false; }
    }

    private static bool EhChaveMsaa(Accessibility.IAccessible no, int filho)
    {
        if (no.get_accName(filho) is not string nome || !BotaoDoModelo.EhUltracode(nome)) return false;
        // only switch-like roles: a link or text with that name in a chat must never get the default action
        return no.get_accRole(filho) is int papel && papel is 0x2C or 0x2B or 0x0C;
    }
}
