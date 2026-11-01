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

/// <summary>O que a ponte leu de um painel de sessão do app.</summary>
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
/// O botão do modelo do app: "Modelo: Opus 5.5 Alto · Ultracode" (lido em 01/11/2026). O esforço vai de 0 a 5:
/// 0 a 4 são Baixo, Médio, Alto, Extra e Máx com o Ultracode desligado; 5 é o Ultracode ligado.
/// </summary>
internal static class BotaoDoModelo
{
    public static readonly string[] Niveis = { "Baixo", "Médio", "Alto", "Extra", "Máx" };
    public const int ULTRACODE = 5;
    public static readonly string[] Rotulos = { "Baixo", "Médio", "Alto", "Extra", "Máx", "Ultracode" };

    public static bool EhBotao(string nome) =>
        nome.StartsWith("Modelo: ", StringComparison.Ordinal) || nome.StartsWith("Model: ", StringComparison.Ordinal);

    public static string SemAcento(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().ToLowerInvariant();
    }

    public static int IndiceDoNivel(string nome)
    {
        string n = SemAcento(nome.Trim());
        for (int k = 0; k < Niveis.Length; k++) if (SemAcento(Niveis[k]) == n) return k;
        return -1;
    }

    /// <summary>(modelo, esforço como o app mostra, nível 0 a 5, ultracode).</summary>
    public static (string Modelo, string? Esforco, int? Nivel, bool Ultracode) Ler(string nome)
    {
        int dois = nome.IndexOf(':');
        string texto = dois >= 0 ? nome[(dois + 1)..].Trim() : nome.Trim();
        var partes = texto.Split('·').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (partes.Count == 0) return ("", null, null, false);
        bool ultra = partes.Skip(1).Any(p => SemAcento(p) == "ultracode");
        string modelo = partes[0];
        string? baseNome = null;
        int? nivelBase = null;
        int esp = modelo.LastIndexOf(' ');
        if (esp > 0)
        {
            int k = IndiceDoNivel(modelo[(esp + 1)..]);
            if (k >= 0) { baseNome = modelo[(esp + 1)..]; nivelBase = k; modelo = modelo[..esp]; }
        }
        return (modelo, ultra ? "Ultracode" : baseNome, ultra ? ULTRACODE : nivelBase, ultra);
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

    public void TrocarEsforco(string alvo, int nivel, Action<RespostaPonte> pronto) => Pedir(r => Esforco(r, alvo, nivel), pronto, 16000);

    public void Enviar(string alvo, string texto, Action<RespostaPonte> pronto) => Pedir(r => Mandar(r, alvo, texto), pronto, 15000);

    private void Pedir(Func<AutomationElement, RespostaPonte> fazer, Action<RespostaPonte> pronto, int prazoMs)
    {
        Timer? prazo = null;
        var t = new Trabalho { Fazer = fazer, Pronto = r => { prazo?.Dispose(); pronto(r); } };
        BlockingCollection<Trabalho> fila;
        lock (_trava)
        {
            if (_fechada) { pronto(RespostaPonte.Falhou("A ilha está fechando.")); return; }
            fila = _fila ??= NovaThread();
            try { fila.Add(t); }
            catch (InvalidOperationException) { fila = _fila = NovaThread(); fila.Add(t); }
        }
        prazo = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref t.Feito, 1) != 0) return;
            // travou dentro da UIA: o próximo pedido sobe outra thread
            lock (_trava) { if (_fila == fila) { fila.CompleteAdding(); _fila = null; } }
            pronto(RespostaPonte.Falhou("O app do Claude demorou demais para responder."));
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
                var raiz = JanelaDoClaude(out _pidDoApp);
                r = raiz == null ? RespostaPonte.Falhou("O app do Claude não está aberto.") : t.Fazer(raiz);
            }
            catch (ElementNotAvailableException) { r = RespostaPonte.Falhou("O app do Claude mudou a tela no meio; tenta de novo."); }
            catch (Exception e) { r = RespostaPonte.Falhou("A ponte com o app falhou: " + e.Message); }
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

    private static AutomationElement? JanelaDoClaude(out int pid)
    {
        pid = 0;
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle == "Claude")
                    {
                        pid = p.Id;
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

    private const string SEM_SESSAO = "Não achei essa sessão aberta na tela do app do Claude (ou tem duas com o mesmo nome).";

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

    private static AutomationElement[] ItensDoMenu(AutomationElement menu, ControlType tipo) =>
        menu.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, tipo))
            .Cast<AutomationElement>().OrderBy(e => e.Current.BoundingRectangle.Y).ToArray();

    private static (ExpandCollapsePattern? Exp, AutomationElement? Menu) AbrirMenuDoModelo(AutomationElement raiz, Painel p)
    {
        if (p.BotaoModelo == null) return (null, null);
        var exp = (ExpandCollapsePattern)p.BotaoModelo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (exp.Current.ExpandCollapseState != ExpandCollapseState.Expanded) exp.Expand();
        string nome = p.BotaoModelo.Current.Name;
        return (exp, Esperar(() => MenuChamado(raiz, nome), 1500));
    }

    private static void FecharMenu(ExpandCollapsePattern? exp)
    {
        try { if (exp != null && exp.Current.ExpandCollapseState == ExpandCollapseState.Expanded) exp.Collapse(); }
        catch (Exception) { }
    }

    private static AutomationElement? AbrirSubmenu(AutomationElement raiz, AutomationElement menu, Func<string, bool> qual)
    {
        var item = ItensDoMenu(menu, ControlType.MenuItem).FirstOrDefault(i => qual(i.Current.Name ?? ""));
        if (item == null) return null;
        string nome = item.Current.Name;
        var ja = MenuChamado(raiz, nome);
        if (ja != null) return ja;
        var c = Centro(item.Current.BoundingRectangle);
        if (!Passar((int)c.X, (int)c.Y, _pidDoApp)) return null;
        return Esperar(() => MenuChamado(raiz, nome), 1500);
    }

    // ------------------------------------------------------------------ ações

    private static RespostaPonte Ler(AutomationElement raiz) =>
        new() { Ok = true, Paineis = Paineis(raiz).Select(p => p.Lido()).ToList() };

    private static RespostaPonte Modelo(AutomationElement raiz, string alvo, string valor)
    {
        var p = Escolher(Paineis(raiz), alvo);
        if (p?.BotaoModelo == null) return RespostaPonte.Falhou(SEM_SESSAO);
        var (exp, menu) = AbrirMenuDoModelo(raiz, p);
        if (menu == null) { FecharMenu(exp); return RespostaPonte.Falhou("O menu de modelo não abriu."); }
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
            return new RespostaPonte { Ok = false, Erro = $"O modelo {valor} não está no menu do app.", Opcoes = opcoes };
        }
        ((InvokePattern)item.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(500);
        FecharMenu(exp);
        var depois = Escolher(Paineis(raiz), alvo);
        return new RespostaPonte { Ok = true, Painel = depois?.Lido(), Opcoes = opcoes };
    }

    private static RespostaPonte Esforco(AutomationElement raiz, string alvo, int nivel)
    {
        nivel = Math.Clamp(nivel, 0, BotaoDoModelo.ULTRACODE);
        var p = Escolher(Paineis(raiz), alvo);
        if (p?.BotaoModelo == null) return RespostaPonte.Falhou(SEM_SESSAO);
        static bool EhEsforco(string n) => BotaoDoModelo.SemAcento(n).StartsWith("esforco", StringComparison.Ordinal) || n.StartsWith("Effort", StringComparison.Ordinal);
        var (exp, menu) = AbrirMenuDoModelo(raiz, p);
        if (menu == null) { FecharMenu(exp); return RespostaPonte.Falhou("O menu de modelo não abriu."); }
        var sub = AbrirSubmenu(raiz, menu, EhEsforco);
        if (sub == null) { FecharMenu(exp); return RespostaPonte.Falhou("O controle de esforço não abriu (se o mouse passou por cima do app, tenta de novo)."); }
        var radios = ItensDoMenu(sub, ControlType.RadioButton);
        var caixa = ItensDoMenu(sub, ControlType.CheckBox).FirstOrDefault(c => SemAcentoNome(c) == "ultracode");
        if (radios.Length != BotaoDoModelo.Niveis.Length)
        {
            FecharMenu(exp);
            return RespostaPonte.Falhou($"O controle de esforço do app mudou ({radios.Length} níveis); a ponte precisa ser atualizada.");
        }
        int? baseAtual = null;
        for (int k = 0; k < radios.Length; k++)
            try { if (((SelectionItemPattern)radios[k].GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected) baseAtual = k; } catch (Exception) { }
        bool ultra = caixa != null && ((TogglePattern)caixa.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
        bool querUltra = nivel == BotaoDoModelo.ULTRACODE;
        if (caixa != null && ultra != querUltra)
        {
            ((TogglePattern)caixa.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            Thread.Sleep(300);
        }
        if (!querUltra && baseAtual != nivel)
        {
            // marcar o Ultracode pode ter fechado o menu: abre de novo se precisar
            (exp, menu) = AbrirMenuDoModelo(raiz, p);
            sub = menu != null ? AbrirSubmenu(raiz, menu, EhEsforco) : null;
            if (sub == null) { FecharMenu(exp); return RespostaPonte.Falhou("O controle de esforço não abriu."); }
            var r2 = ItensDoMenu(sub, ControlType.RadioButton);
            if (nivel < r2.Length) ((InvokePattern)r2[nivel].GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }
        Thread.Sleep(400);
        FecharMenu(exp);
        Thread.Sleep(150);
        var depois = Escolher(Paineis(raiz), alvo);
        return new RespostaPonte { Ok = true, Painel = depois?.Lido() };
    }

    private static string SemAcentoNome(AutomationElement e) => BotaoDoModelo.SemAcento(e.Current.Name ?? "");

    private static RespostaPonte Mandar(AutomationElement raiz, string alvo, string texto)
    {
        var p = Escolher(Paineis(raiz), alvo);
        if (p?.Prompt == null) return RespostaPonte.Falhou(p == null ? SEM_SESSAO : "Não achei o campo de mensagem dessa sessão no app do Claude.");
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
            return RespostaPonte.Falhou("O app não aceitou o texto (o botão Enviar não apareceu).");
        }
        ((InvokePattern)enviar.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(500);
        // devolve o rascunho que já estava no campo
        if (rascunho.Trim().Length > 0) valor.SetValue(rascunho);
        return new RespostaPonte { Ok = true, Painel = p.Lido() };
    }

    // ------------------------------------------------------------------ hover sem mexer no cursor

    [StructLayout(LayoutKind.Sequential)] private struct PONTO { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PONTO p);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref PONTO p);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

    /// <summary>Move o "mouse" do app para (x, y) da tela só na cabeça dele: o cursor de verdade fica onde está.</summary>
    private static bool Passar(int x, int y, int pidDoApp)
    {
        var p = new PONTO { X = x, Y = y };
        var h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid != (uint)pidDoApp) return false;
        var c = p;
        ScreenToClient(h, ref c);
        for (int i = 0; i < 2; i++)
        {
            int cx = c.X + i * 2;
            PostMessageW(h, 0x0200, IntPtr.Zero, (IntPtr)((c.Y << 16) | (cx & 0xFFFF)));
            Thread.Sleep(40);
        }
        return true;
    }
}
