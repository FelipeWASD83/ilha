using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Ilha.Focus;

namespace Ilha;

/// <summary>
/// The "Focus" section (3.6.0): the modes on the left, in the order the automations look at them, and the selected
/// mode's settings on the right. Saved into the "foco" module section, before the generated module forms run.
/// </summary>
internal sealed partial class JanelaDeConfiguracoes
{
    private List<FocusMode> _focoModos = new();
    private readonly ListBox _focoLista = new() { Width = 220, Height = 230, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
    private TextBox _focoNome = null!, _focoHorarios = null!, _focoApps = null!;
    private ComboBox _focoIcone = null!, _focoCor = null!, _focoAvisos = null!, _focoWallpaper = null!;
    private CheckBox _focoSilenciar = null!, _focoDnd = null!, _focoMudaVolume = null!, _focoMudaBrilho = null!;
    private NumericUpDown _focoVolume = null!, _focoBrilho = null!;
    private FlowLayoutPanel _focoEditor = null!;
    private bool _focoCarregando;
    private List<(string Id, string Title)> _focoWallpapers = new();

    private static readonly string[] AvisosDoFoco = { FocusAlerts.All, FocusAlerts.Important, FocusAlerts.None };

    private Panel MontarFoco()
    {
        var (s, col) = NovaSecao(Lang.T("Foco", "Focus"), Lang.T(
            "Modos como os do iPhone: ligue pelo menu da ilha ou deixe ligarem sozinhos por horário ou quando um app abre. " +
            "Enquanto um modo está ligado, ele muda o que estiver marcado aqui, e tudo volta quando ele desliga. " +
            "Se dois modos quiserem ligar ao mesmo tempo, vale o de cima.",
            "Modes like the iPhone's: turn one on from the island's menu or let it turn on by itself on a schedule or when an app opens. " +
            "While a mode is on it changes what is checked here, and everything goes back when it turns off. " +
            "When two modes want to turn on at once, the one on top wins."));
        _focoModos = LerModosDoFoco();
        try
        {
            var lib = global::Ilha.LiveWallpaper.FundoVivoBridge.Scan(global::Ilha.LiveWallpaper.FundoVivoBridge.ReadConfig());
            _focoWallpapers = lib.Select(i => (i.Id, i.Title)).OrderBy(i => i.Title, StringComparer.CurrentCulture).ToList();
        }
        catch (Exception e) { Log.Escrever("configurações: biblioteca do Fundo Vivo: " + e.Message); }

        var botoes = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(10, 0, 0, 0) };
        botoes.Controls.Add(BotaoSimples(Lang.T("Novo", "New"), (_, _) => NovoModo(), 110));
        botoes.Controls.Add(BotaoSimples(Lang.T("Apagar", "Delete"), (_, _) => ApagarModo(), 110));
        botoes.Controls.Add(BotaoSimples(Lang.T("Subir", "Up"), (_, _) => MoverModo(-1), 110));
        botoes.Controls.Add(BotaoSimples(Lang.T("Descer", "Down"), (_, _) => MoverModo(1), 110));
        foreach (Control b in botoes.Controls) b.Margin = new Padding(0, 0, 0, 6);

        _focoEditor = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(24, 0, 0, 0) };
        var topo = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        topo.Controls.Add(_focoLista);
        topo.Controls.Add(botoes);
        col.Controls.Add(topo);

        _focoNome = new TextBox { Width = 240 };
        Linha2(col, Lang.T("Nome", "Name"), _focoNome);
        _focoIcone = Lista(FocusIcons.Names.Select(n => Lang.T(n.Pt, n.En)));
        Linha2(col, Lang.T("Símbolo", "Symbol"), _focoIcone);
        _focoCor = Lista(FocusIcons.Colors.Select(c => Lang.T(c.Pt, c.En)));
        Linha2(col, Lang.T("Cor", "Color"), _focoCor);
        _focoSilenciar = Marcar(col, Lang.T("Silenciar todos os sons da ilha", "Silence all of the island's sounds"), false);
        _focoDnd = Marcar(col, Lang.T("Ligar o Não Perturbe do Windows", "Turn on Windows' Do Not Disturb"), false,
            Lang.T("Se ele já estava ligado, continua ligado quando o modo desliga.", "If it was already on, it stays on when the mode turns off."));
        _focoAvisos = Lista(new[] { Lang.T("Todos", "All"), Lang.T("Só os importantes", "Important only"), Lang.T("Nenhum", "None") });
        Linha2(col, Lang.T("Avisos da ilha", "Island alerts"), _focoAvisos,
            Lang.T("Importantes: timer acabando, bateria baixa, o que você pediu para ver mesmo assim.", "Important: a timer ending, low battery, what you asked to see anyway."));
        _focoWallpaper = Lista(new[] { Lang.T("Não muda", "Leaves it") }.Concat(_focoWallpapers.Select(w => w.Title)));
        _focoWallpaper.Width = 280;
        Linha2(col, Lang.T("Wallpaper (Fundo Vivo, monitor principal)", "Wallpaper (Fundo Vivo, main monitor)"), _focoWallpaper,
            _focoWallpapers.Count == 0 ? Lang.T("Nenhum wallpaper encontrado na biblioteca do Fundo Vivo.", "No wallpaper found in Fundo Vivo's library.") : "");
        _focoMudaVolume = new CheckBox { Text = Lang.T("Mudar", "Change"), AutoSize = true, UseMnemonic = false };
        _focoVolume = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 70, TextAlign = HorizontalAlignment.Right };
        Linha2(col, Lang.T("Volume (%)", "Volume (%)"), Fileira(_focoMudaVolume, _focoVolume));
        _focoMudaBrilho = new CheckBox { Text = Lang.T("Mudar", "Change"), AutoSize = true, UseMnemonic = false };
        _focoBrilho = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 70, TextAlign = HorizontalAlignment.Right };
        Linha2(col, Lang.T("Brilho do monitor (%)", "Monitor brightness (%)"), Fileira(_focoMudaBrilho, _focoBrilho));
        _focoHorarios = new TextBox { Multiline = true, Width = 280, Height = 64, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
        Linha2(col, Lang.T("Horários (um por linha)", "Schedules (one per line)"), _focoHorarios,
            Lang.T("Exemplos: \"seg-sex 09:00-18:00\", \"todos 23:30-07:00\", \"sab,dom 10:00-12:00\". Passar da meia-noite vale.",
                "Examples: \"mon-fri 09:00-18:00\", \"all 23:30-07:00\", \"sat,sun 10:00-12:00\". Crossing midnight works."));
        _focoApps = new TextBox { Multiline = true, Width = 280, Height = 64, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
        Linha2(col, Lang.T("Liga com estes apps abertos", "On while these apps are open"), _focoApps,
            Lang.T("Um por linha, o nome do .exe (bf6.exe, Code.exe). No lugar da localização do iPhone, que num PC não muda.",
                "One per line, the .exe name (bf6.exe, Code.exe). Instead of the iPhone's location, which does not change on a PC."));

        _focoLista.SelectedIndexChanged += (_, _) => CarregarModo();
        foreach (var c in new Control[] { _focoNome, _focoHorarios, _focoApps }) c.TextChanged += (_, _) => GuardarModo();
        foreach (var c in new[] { _focoIcone, _focoCor, _focoAvisos, _focoWallpaper }) c.SelectedIndexChanged += (_, _) => GuardarModo();
        foreach (var c in new[] { _focoSilenciar, _focoDnd, _focoMudaVolume, _focoMudaBrilho }) c.CheckedChanged += (_, _) => GuardarModo();
        foreach (var c in new[] { _focoVolume, _focoBrilho }) c.ValueChanged += (_, _) => GuardarModo();
        ListarModos(0);
        return s;
    }

    private static ComboBox Lista(IEnumerable<string> itens)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        c.Items.AddRange(itens.Cast<object>().ToArray());
        return c;
    }

    private List<FocusMode> LerModosDoFoco()
    {
        try
        {
            if (_inicio.Modulos["foco"] is JsonObject o && o["modos"] is JsonArray a)
                return a.Deserialize<List<FocusMode>>(Config.Json) ?? FocusMode.Defaults();
        }
        catch (Exception e) { Log.Escrever("configurações: modos do foco ilegíveis: " + e.Message); }
        return FocusMode.Defaults();
    }

    private FocusMode? ModoSelecionado => _focoLista.SelectedIndex is int i && i >= 0 && i < _focoModos.Count ? _focoModos[i] : null;

    private void ListarModos(int selecionar)
    {
        _focoCarregando = true;
        _focoLista.Items.Clear();
        foreach (var m in _focoModos) _focoLista.Items.Add(m.Name);
        _focoCarregando = false;
        if (_focoModos.Count > 0) _focoLista.SelectedIndex = Math.Clamp(selecionar, 0, _focoModos.Count - 1);
        else CarregarModo();
    }

    private void CarregarModo()
    {
        if (_focoCarregando) return;
        var m = ModoSelecionado;
        _focoCarregando = true;
        foreach (var c in new Control[] { _focoNome, _focoIcone, _focoCor, _focoSilenciar, _focoDnd, _focoAvisos, _focoWallpaper, _focoMudaVolume,
                     _focoVolume, _focoMudaBrilho, _focoBrilho, _focoHorarios, _focoApps })
            c.Enabled = m != null;
        if (m != null)
        {
            _focoNome.Text = m.Nome;
            _focoIcone.SelectedIndex = Math.Max(0, Array.FindIndex(FocusIcons.Names, n => n.Id == m.Icone));
            _focoCor.SelectedIndex = Math.Max(0, Array.FindIndex(FocusIcons.Colors, c => c.Id == m.Cor));
            _focoSilenciar.Checked = m.Silenciar;
            _focoDnd.Checked = m.NaoPerturbeWindows;
            _focoAvisos.SelectedIndex = Math.Max(0, Array.IndexOf(AvisosDoFoco, FocusAlerts.Normalize(m.Avisos)));
            int w = _focoWallpapers.FindIndex(x => string.Equals(x.Id, m.Wallpaper, StringComparison.OrdinalIgnoreCase));
            if (w < 0 && m.Wallpaper.Length > 0)
            {
                // a wallpaper no longer in the library stays chosen, named by its id
                _focoWallpapers.Add((m.Wallpaper, Lang.T("(fora da biblioteca) ", "(not in the library) ") + m.Wallpaper));
                _focoWallpaper.Items.Add(_focoWallpapers[^1].Title);
                w = _focoWallpapers.Count - 1;
            }
            _focoWallpaper.SelectedIndex = w + 1;
            _focoMudaVolume.Checked = m.Volume >= 0;
            _focoVolume.Value = Math.Clamp(m.Volume < 0 ? 50 : m.Volume, 0, 100);
            _focoMudaBrilho.Checked = m.Brilho >= 0;
            _focoBrilho.Value = Math.Clamp(m.Brilho < 0 ? 50 : m.Brilho, 0, 100);
            _focoHorarios.Text = string.Join(Environment.NewLine, m.Horarios);
            _focoApps.Text = string.Join(Environment.NewLine, m.Apps);
        }
        _focoVolume.Enabled = m != null && _focoMudaVolume.Checked;
        _focoBrilho.Enabled = m != null && _focoMudaBrilho.Checked;
        _focoCarregando = false;
    }

    private void GuardarModo()
    {
        if (_focoCarregando || ModoSelecionado is not { } m) return;
        m.Nome = _focoNome.Text.Trim();
        m.Icone = FocusIcons.Names[Math.Max(0, _focoIcone.SelectedIndex)].Id;
        m.Cor = FocusIcons.Colors[Math.Max(0, _focoCor.SelectedIndex)].Id;
        m.Silenciar = _focoSilenciar.Checked;
        m.NaoPerturbeWindows = _focoDnd.Checked;
        m.Avisos = AvisosDoFoco[Math.Max(0, _focoAvisos.SelectedIndex)];
        m.Wallpaper = _focoWallpaper.SelectedIndex > 0 ? _focoWallpapers[_focoWallpaper.SelectedIndex - 1].Id : "";
        m.Volume = _focoMudaVolume.Checked ? (int)_focoVolume.Value : -1;
        m.Brilho = _focoMudaBrilho.Checked ? (int)_focoBrilho.Value : -1;
        m.Horarios = Linhas(_focoHorarios.Text);
        m.Apps = Linhas(_focoApps.Text);
        _focoVolume.Enabled = _focoMudaVolume.Checked;
        _focoBrilho.Enabled = _focoMudaBrilho.Checked;
        // the name in the list follows the box
        int i = _focoLista.SelectedIndex;
        if (i >= 0 && (string)_focoLista.Items[i] != m.Name)
        {
            _focoCarregando = true;
            _focoLista.Items[i] = m.Name;
            _focoCarregando = false;
        }
        Sujou();
    }

    private static List<string> Linhas(string texto) =>
        texto.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private void NovoModo()
    {
        string nome = Lang.T("Novo foco", "New focus");
        _focoModos.Add(new FocusMode { Id = FocusMode.NewId(nome, _focoModos.Select(m => m.Id)), Nome = nome, Icone = "coracao", Cor = "rosa" });
        ListarModos(_focoModos.Count - 1);
        Sujou();
    }

    private void ApagarModo()
    {
        int i = _focoLista.SelectedIndex;
        if (i < 0) return;
        _focoModos.RemoveAt(i);
        ListarModos(i);
        Sujou();
    }

    private void MoverModo(int delta)
    {
        int i = _focoLista.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _focoModos.Count) return;
        (_focoModos[i], _focoModos[j]) = (_focoModos[j], _focoModos[i]);
        ListarModos(j);
        Sujou();
    }

    /// <summary>Writes the modes into the "foco" section (the generated module form runs after and keeps them).</summary>
    private void ColetarFoco(Config c)
    {
        if (c.Modulos["foco"] is { } and not JsonObject) return;
        if (c.Modulos["foco"] is not JsonObject secao) c.Modulos["foco"] = secao = new JsonObject();
        secao["modos"] = JsonSerializer.SerializeToNode(_focoModos, Config.Json);
    }

    internal IReadOnlyList<FocusMode> ModosDoFoco => _focoModos;
    internal TextBox CampoNomeDoFoco => _focoNome;
    internal ListBox ListaDoFoco => _focoLista;
    internal void NovoModoDoFoco() => NovoModo();
}
