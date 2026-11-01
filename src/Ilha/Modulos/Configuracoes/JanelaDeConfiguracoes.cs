using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Ilha.Configuracoes;

namespace Ilha;

/// <summary>
/// A tela de configurações: Geral, Sons, Atalhos, Jogos competitivos, Prioridades e Módulos (com os campos de cada
/// módulo montados por reflexão da classe de config dele). Janela comum na thread da interface, sem ser modal: a
/// ilha continua respondendo com ela aberta. Salvar grava o config.json e o núcleo relê sozinho.
/// </summary>
internal sealed class JanelaDeConfiguracoes : Form
{
    private static JanelaDeConfiguracoes? _aberta;

    /// <summary>
    /// Abre a tela (ou traz para a frente a que já está aberta). `atual` dá a config que vale agora (a janela edita
    /// uma cópia); `salvar` grava (normalmente c => c.Salvar()); `modulos` vem de <see cref="ModulosDaConfig"/>.
    /// `atalhosDosModulos` (nome e combinação padrão de cada atalho registrado) é opcional: sem ele, a aba Atalhos
    /// mostra o principal e os que já foram trocados na config.
    /// </summary>
    public static void Mostrar(Func<Config> atual, Action<Config> salvar,
        IReadOnlyList<(string id, string nome, Type? tipoConfig, bool ligado)> modulos,
        IReadOnlyDictionary<string, Atalho>? atalhosDosModulos = null)
    {
        if (_aberta is { IsDisposed: false })
        {
            if (_aberta.WindowState == FormWindowState.Minimized) _aberta.WindowState = FormWindowState.Normal;
            _aberta.Activate();
            return;
        }
        _aberta = new JanelaDeConfiguracoes(atual, salvar, modulos, atalhosDosModulos);
        _aberta.FormClosed += (_, _) => _aberta = null;
        _aberta.Show();
        _aberta.Activate();
    }

    /// <summary>A lista de módulos para a tela: id, nome, classe de config e se está ligado na config dada.</summary>
    public static List<(string id, string nome, Type? tipoConfig, bool ligado)> ModulosDaConfig(Config c)
    {
        var r = new List<(string, string, Type?, bool)>();
        foreach (var m in CatalogoDeModulos.Descobrir())
        {
            Type? tipo = null;
            try
            {
                // sem rodar o construtor do módulo (que pode criar timer, COM...): o TipoConfig é só um typeof
                tipo = ((Modulo)RuntimeHelpers.GetUninitializedObject(m.Tipo)).TipoConfig;
            }
            catch { }
            bool ligado = m.Info.LigadoPorPadrao;
            if (c.Modulos[m.Info.Id] is JsonObject s && s["ligado"] is JsonValue v && v.TryGetValue(out bool b)) ligado = b;
            r.Add((m.Info.Id, m.Info.Nome, tipo, ligado));
        }
        return r;
    }

    // ------------------------------------------------------------------ aparência

    private static readonly Color Fundo = Color.White;
    private static readonly Color Lateral = Color.FromArgb(243, 244, 246);
    private static readonly Color Texto = Color.FromArgb(31, 35, 40);
    private static readonly Color Secundario = Color.FromArgb(95, 102, 112);
    private static readonly Color Acento = Color.FromArgb(37, 99, 235);
    private static readonly Color Linha = Color.FromArgb(229, 231, 235);

    private static string Familia(bool display)
    {
        string nome = display ? "Segoe UI Variable Display" : "Segoe UI Variable Text";
        using var f = new Font(nome, 10f);
        return f.Name == nome ? nome : "Segoe UI";
    }

    private readonly Font _fonte = new(Familia(false), 10f);
    private readonly Font _fonteForte = new(Familia(false), 10f, FontStyle.Bold);
    private readonly Font _fontePequena = new(Familia(false), 9f);
    private readonly Font _fonteTitulo = new(Familia(true), 17f, FontStyle.Bold);
    private readonly Font _fonteMarca = new(Familia(true), 15f, FontStyle.Bold);

    // ------------------------------------------------------------------ estado

    private readonly Func<Config> _atual;
    private readonly Action<Config> _salvar;
    private readonly Config _inicio;
    private readonly Panel _conteudo = new() { Dock = DockStyle.Fill, BackColor = Fundo };
    private readonly Panel _lateral = new() { Dock = DockStyle.Left, Width = 210, BackColor = Lateral, Padding = new Padding(12, 18, 12, 12) };
    private readonly Panel _rodape = new() { Dock = DockStyle.Bottom, Height = 62, BackColor = Fundo, Padding = new Padding(24, 12, 24, 14) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Secundario };
    private readonly System.Windows.Forms.Timer _limparStatus = new() { Interval = 5000 };
    private readonly List<(Button Botao, Panel Secao)> _secoes = new();
    private bool _sujo;
    private bool _montando = true;
    private Windows.Media.Playback.MediaPlayer? _teste;

    // Geral
    private CheckBox _colada = null!, _sombra = null!, _dividir = null!, _ociosa = null!, _naoPerturbe = null!, _iniciar = null!;
    private NumericUpDown _distancia = null!, _tamanho = null!, _deslX = null!, _deslY = null!, _alerta = null!;
    // Sons
    private CheckBox _sonsLigados = null!;
    private TrackBar _volume = null!;
    // Atalhos
    private CaixaDeAtalho _atalhoPrincipal = null!;
    private readonly List<(string Nome, Atalho? Padrao, CaixaDeAtalho Caixa)> _atalhos = new();
    // Jogos e prioridades
    private readonly ListBox _jogos = new() { Width = 380, Height = 260, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly ListBox _prioridades = new() { Width = 260, Height = 200, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly List<string> _listaPrioridades = new();
    // Módulos
    private readonly List<ModuloNaTela> _modulos = new();

    private sealed class ModuloNaTela
    {
        public required string Id;
        public required CheckBox Ligado;
        public Type? Tipo;
        public object? Instancia;
        public List<CampoGerado> Campos = new();
        /// <summary>O valor de cada campo quando a tela abriu ou no último salvar: no salvar só vai o que saiu dele.</summary>
        public List<object?> Iniciais = new();
    }

    private JanelaDeConfiguracoes(Func<Config> atual, Action<Config> salvar,
        IReadOnlyList<(string id, string nome, Type? tipoConfig, bool ligado)> modulos,
        IReadOnlyDictionary<string, Atalho>? atalhosDosModulos)
    {
        _atual = atual;
        _salvar = salvar;
        _inicio = Copiar(atual());

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Configurações da Ilha";
        Font = _fonte;
        ForeColor = Texto;
        BackColor = Fundo;
        ClientSize = new Size(900, 640);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        KeyPreview = true;
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "icone.ico");
            if (File.Exists(ico)) Icon = new Icon(ico);
        }
        catch { }

        MontarLateral();
        MontarRodape();
        AdicionarSecao("Geral", MontarGeral());
        AdicionarSecao("Sons", MontarSons());
        AdicionarSecao("Atalhos", MontarAtalhos(atalhosDosModulos));
        AdicionarSecao("Jogos competitivos", MontarJogos());
        AdicionarSecao("Prioridades", MontarPrioridades());
        AdicionarSecao("Módulos", MontarModulos(modulos));

        // a ordem importa no Dock: o que preenche entra primeiro
        Controls.Add(_conteudo);
        Controls.Add(_rodape);
        Controls.Add(_lateral);
        Escolher(0);
        ResumeLayout(false);
        PerformLayout();

        Vigiar(this);
        _montando = false;
        _limparStatus.Tick += (_, _) => { _limparStatus.Stop(); _status.Text = ""; };
    }

    private static Config Copiar(Config c) =>
        JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(c, Config.Json), Config.Json) ?? new Config();

    // ------------------------------------------------------------------ peças

    private void MontarLateral()
    {
        var marca = new Label { Text = "Ilha", Font = _fonteMarca, AutoSize = true, Location = new Point(12, 16), ForeColor = Texto };
        var versao = new Label { Text = "versão " + Atualizacao.Versao, Font = _fontePequena, AutoSize = true, Location = new Point(14, 48), ForeColor = Secundario };
        _lateral.Controls.Add(marca);
        _lateral.Controls.Add(versao);
    }

    private void MontarRodape()
    {
        var salvar = new Button
        {
            Text = "Salvar", Width = 110, Height = 36, FlatStyle = FlatStyle.Flat, BackColor = Acento, ForeColor = Color.White,
            Font = _fonteForte, Anchor = AnchorStyles.Right | AnchorStyles.Top, Cursor = Cursors.Hand,
        };
        salvar.FlatAppearance.BorderSize = 0;
        salvar.Click += (_, _) => Salvar();
        var fechar = new Button
        {
            Text = "Fechar", Width = 100, Height = 36, FlatStyle = FlatStyle.Flat, BackColor = Fundo, ForeColor = Texto,
            Anchor = AnchorStyles.Right | AnchorStyles.Top, Cursor = Cursors.Hand,
        };
        fechar.FlatAppearance.BorderColor = Linha;
        fechar.Click += (_, _) => Close();
        _status.Location = new Point(24, 22);
        _rodape.Controls.Add(_status);
        _rodape.Controls.Add(salvar);
        _rodape.Controls.Add(fechar);
        void Posicionar()
        {
            salvar.Location = new Point(_rodape.ClientSize.Width - _rodape.Padding.Right - salvar.Width, 12);
            fechar.Location = new Point(salvar.Left - 10 - fechar.Width, 12);
        }
        _rodape.Resize += (_, _) => Posicionar();
        _rodape.Paint += (_, e) => { using var p = new Pen(Linha); e.Graphics.DrawLine(p, 0, 0, _rodape.Width, 0); };
        Posicionar();
        AcceptButton = null;
    }

    private void AdicionarSecao(string nome, Panel secao)
    {
        int i = _secoes.Count;
        var b = new Button
        {
            Text = nome, TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat, Height = 38, Width = 186,
            Location = new Point(12, 86 + i * 42), BackColor = Lateral, ForeColor = Texto, Cursor = Cursors.Hand,
            Padding = new Padding(10, 0, 0, 0), UseMnemonic = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 234, 238);
        b.Click += (_, _) => Escolher(i);
        _lateral.Controls.Add(b);
        secao.Visible = false;
        _conteudo.Controls.Add(secao);
        _secoes.Add((b, secao));
    }

    private void Escolher(int i)
    {
        for (int j = 0; j < _secoes.Count; j++)
        {
            var (b, s) = _secoes[j];
            bool sel = j == i;
            s.Visible = sel;
            b.BackColor = sel ? Fundo : Lateral;
            b.ForeColor = sel ? Acento : Texto;
            b.Font = sel ? _fonteForte : _fonte;
        }
    }

    /// <summary>Uma seção rolável com uma coluna (FlowLayoutPanel de cima para baixo).</summary>
    private (Panel Secao, FlowLayoutPanel Coluna) NovaSecao(string titulo, string descricao)
    {
        var secao = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Fundo, Padding = new Padding(32, 24, 24, 24) };
        var coluna = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(32, 24), BackColor = Fundo, Margin = Padding.Empty,
        };
        coluna.Controls.Add(new Label { Text = titulo, Font = _fonteTitulo, AutoSize = true, Margin = new Padding(0, 0, 0, 2), UseMnemonic = false });
        if (descricao.Length > 0) coluna.Controls.Add(Descricao(descricao, 18));
        secao.Controls.Add(coluna);
        return (secao, coluna);
    }

    private Label Descricao(string texto, int depois = 10) => new()
    {
        Text = texto, AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = Secundario, Font = _fontePequena,
        Margin = new Padding(0, 0, 0, depois), UseMnemonic = false,
    };

    private CheckBox Marcar(FlowLayoutPanel col, string texto, bool valor, string descricao = "")
    {
        var cb = new CheckBox { Text = texto, Checked = valor, AutoSize = true, Margin = new Padding(0, 8, 0, descricao.Length > 0 ? 0 : 6), UseMnemonic = false };
        col.Controls.Add(cb);
        if (descricao.Length > 0) col.Controls.Add(new Label
        {
            Text = descricao, AutoSize = true, MaximumSize = new Size(580, 0), ForeColor = Secundario, Font = _fontePequena,
            Margin = new Padding(20, 0, 0, 6), UseMnemonic = false,
        });
        return cb;
    }

    /// <summary>Rótulo à esquerda e o controle à direita, numa linha.</summary>
    private void Linha2(FlowLayoutPanel col, string rotulo, Control editor, string descricao = "")
    {
        var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 6, 0, descricao.Length > 0 ? 0 : 6) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = rotulo, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 8, 0), UseMnemonic = false, MaximumSize = new Size(250, 0) }, 0, 0);
        editor.Margin = new Padding(0);
        t.Controls.Add(editor, 1, 0);
        col.Controls.Add(t);
        if (descricao.Length > 0) col.Controls.Add(Descricao(descricao, 6));
    }

    private NumericUpDown Numero(FlowLayoutPanel col, string rotulo, decimal valor, decimal min, decimal max, int casas, decimal passo, string descricao = "")
    {
        var n = new NumericUpDown
        {
            DecimalPlaces = casas, Increment = passo, Minimum = min, Maximum = max, Width = 110,
            Value = Math.Clamp(valor, min, max), TextAlign = HorizontalAlignment.Right,
        };
        Linha2(col, rotulo, n, descricao);
        return n;
    }

    private Button BotaoSimples(string texto, EventHandler acao, int largura = 0)
    {
        var b = new Button { Text = texto, AutoSize = largura == 0, Width = largura > 0 ? largura : 80, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Fundo, Cursor = Cursors.Hand, UseMnemonic = false, Margin = new Padding(0, 0, 8, 0) };
        b.FlatAppearance.BorderColor = Linha;
        b.Click += acao;
        return b;
    }

    private static FlowLayoutPanel Fileira(params Control[] itens)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
        f.Controls.AddRange(itens);
        return f;
    }

    // ------------------------------------------------------------------ seções

    private Panel MontarGeral()
    {
        var c = _inicio;
        var (s, col) = NovaSecao("Geral", "Posição, tamanho e comportamento da ilha.");
        _colada = Marcar(col, "Colada na borda de cima (estilo notch)", c.Colada, "Desligado, a ilha flutua um pouco abaixo da borda, como no iPhone.");
        _distancia = Numero(col, "Distância do topo (pontos)", (decimal)c.DistanciaDoTopo, 0, 200, 0, 1, "Só no modo flutuante.");
        _tamanho = Numero(col, "Tamanho", (decimal)c.Tamanho, 0.5m, 3m, 2, 0.05m, "1 é o tamanho normal; vale por cima da escala do Windows.");
        _deslX = Numero(col, "Deslocamento horizontal (pontos)", c.DeslocamentoX, -4000, 4000, 0, 1);
        _deslY = Numero(col, "Deslocamento vertical (pontos)", c.DeslocamentoY, -2000, 2000, 0, 1, "Para tirar a ilha do centro de cima da tela.");
        _sombra = Marcar(col, "Sombra em volta da ilha", c.Sombra);
        _dividir = Marcar(col, "Dividir quando tem duas coisas acontecendo", c.Dividir, "A segunda vira uma bolinha ao lado da pílula.");
        _ociosa = Marcar(col, "Mostrar a ilha vazia na área de trabalho", c.OciosaNaAreaDeTrabalho);
        _naoPerturbe = Marcar(col, "Respeitar o Não Incomodar do Windows", c.RespeitarNaoPerturbe, "Sem chegada nem alerta, menos o alarme do timer.");
        _alerta = Numero(col, "Tempo de um alerta na tela (segundos)", (decimal)c.AlertaSegundos, 1, 60, 1, 0.5m);
        _iniciar = Marcar(col, "Iniciar com o Windows", c.IniciarComWindows, "Só vale na ilha instalada (Menu Iniciar), não na cópia de desenvolvimento.");
        return s;
    }

    private Panel MontarSons()
    {
        var c = _inicio;
        var (s, col) = NovaSecao("Sons", "Sons curtos quando algo chega. Ficam mudos no jogo competitivo e no Não Incomodar, menos o alarme do timer.");
        _sonsLigados = Marcar(col, "Sons ligados", c.Sons.Ligados);
        _volume = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 260, Value = (int)Math.Round(Math.Clamp(c.Sons.Volume, 0, 1) * 100), AutoSize = true };
        var pct = new Label { AutoSize = true, Text = _volume.Value + "%", Margin = new Padding(8, 12, 12, 0), Width = 48 };
        _volume.ValueChanged += (_, _) => pct.Text = _volume.Value + "%";
        var testar = BotaoSimples("Testar", (_, _) => TocarTeste());
        testar.Margin = new Padding(0, 6, 0, 0);
        var fileira = Fileira(_volume, pct, testar);
        Linha2(col, "Volume", fileira);
        col.Controls.Add(new Label { Text = "Sons personalizados", Font = _fonteForte, AutoSize = true, Margin = new Padding(0, 18, 0, 2) });
        col.Controls.Add(Descricao("Um .wav com o nome do tipo na pasta de sons personalizados vale mais que o da ilha: " +
            "alerta.wav, chegada.wav, timer.wav, claude.wav, conectou.wav e erro.wav.", 8));
        col.Controls.Add(Fileira(BotaoSimples("Abrir a pasta de sons personalizados", (_, _) => AbrirPastaDeSons())));
        return s;
    }

    private void TocarTeste()
    {
        var arq = Sons.Arquivo("alerta") ?? Sons.Arquivo("chegada");
        if (arq == null) { MostrarStatus("Não achei nenhum som para testar."); return; }
        try
        {
            _teste?.Dispose();
            _teste = new Windows.Media.Playback.MediaPlayer
            {
                Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(arq)),
                Volume = _volume.Value / 100.0,
                AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Alerts,
            };
            _teste.Play();
        }
        catch (Exception e) { MostrarStatus("O som não tocou: " + e.Message); }
    }

    private void AbrirPastaDeSons()
    {
        var pasta = Path.Combine(Config.Pasta, "sons");
        try
        {
            Directory.CreateDirectory(pasta);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pasta}\"") { UseShellExecute = true });
        }
        catch (Exception e) { MostrarStatus("Não abriu a pasta: " + e.Message); }
    }

    private Panel MontarAtalhos(IReadOnlyDictionary<string, Atalho>? padroes)
    {
        var c = _inicio;
        var (s, col) = NovaSecao("Atalhos", "Clique na caixa e aperte a combinação. Backspace deixa sem atalho; Esc desfaz. " +
            "Uma combinação já usada por outro programa não chega aqui e não vai funcionar.");
        _atalhoPrincipal = new CaixaDeAtalho { Valor = c.Atalho };
        var padraoPrincipal = new Atalho();
        Linha2(col, "Abrir e fechar a ilha", Fileira(_atalhoPrincipal,
            BotaoSimples("Padrão", (_, _) => { _atalhoPrincipal.Valor = padraoPrincipal; Sujou(); })));

        var nomes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (padroes != null) foreach (var k in padroes.Keys) nomes.Add(k);
        foreach (var k in c.AtalhosModulos.Keys) nomes.Add(k);
        if (nomes.Count > 0)
            col.Controls.Add(new Label { Text = "Dos módulos", Font = _fonteForte, AutoSize = true, Margin = new Padding(0, 18, 0, 6) });
        foreach (var nome in nomes)
        {
            Atalho? padrao = padroes != null && padroes.TryGetValue(nome, out var p) ? p : null;
            var valor = c.AtalhosModulos.TryGetValue(nome, out var trocado) ? trocado : padrao ?? new Atalho { Mods = 0, Tecla = 0 };
            var caixa = new CaixaDeAtalho { Valor = valor };
            var itens = new List<Control> { caixa };
            if (padrao != null) itens.Add(BotaoSimples("Padrão", (_, _) => { caixa.Valor = padrao; Sujou(); }));
            Linha2(col, Formulario.Humanizar(nome), Fileira(itens.ToArray()), padrao != null ? $"Padrão: {Teclas.Nome(padrao)}" : "");
            _atalhos.Add((nome, padrao, caixa));
        }
        if (padroes == null)
            col.Controls.Add(Descricao("Os atalhos de módulo aparecem aqui quando o módulo registra um atalho ou quando ele já foi trocado na config.", 6));
        return s;
    }

    private Panel MontarJogos()
    {
        var (s, col) = NovaSecao("Jogos competitivos", "Com um destes na frente, a ilha só mostra o timer: nada de alerta, som nem animação por cima do jogo.");
        foreach (var j in _inicio.JogosCompetitivos) _jogos.Items.Add(j);
        var novo = new TextBox { Width = 260, PlaceholderText = "nome do .exe, como cs2.exe" };
        void Adicionar()
        {
            var t = novo.Text.Trim().Trim('"');
            if (t.Length == 0) return;
            t = Path.GetFileName(t);
            if (!_jogos.Items.Cast<string>().Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) { _jogos.Items.Add(t); Sujou(); }
            novo.Clear();
        }
        novo.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Adicionar(); } };
        var add = BotaoSimples("Adicionar", (_, _) => Adicionar());
        var tirar = BotaoSimples("Remover o selecionado", (_, _) =>
        {
            if (_jogos.SelectedIndex < 0) return;
            int i = _jogos.SelectedIndex;
            _jogos.Items.RemoveAt(i);
            if (_jogos.Items.Count > 0) _jogos.SelectedIndex = Math.Min(i, _jogos.Items.Count - 1);
            Sujou();
        });
        _jogos.Margin = new Padding(0, 4, 0, 8);
        col.Controls.Add(_jogos);
        col.Controls.Add(Fileira(novo, add));
        col.Controls.Add(Fileira(tirar));
        col.Controls.Add(Descricao("O nome do .exe pode ir com ou sem \".exe\"; maiúscula não importa. O Gerenciador de Tarefas mostra o nome em Detalhes.", 6));
        return s;
    }

    private static readonly Dictionary<string, string> NomesDosTipos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["volume"] = "Volume", ["claude"] = "Claude Code", ["ia"] = "IA", ["timer"] = "Timer", ["midia"] = "Mídia",
        ["uso"] = "Uso do PC", ["privacidade"] = "Câmera e microfone", ["atualizacao"] = "Atualização", ["mascote"] = "Mascote",
    };

    private static string NomeDoTipo(string t) => NomesDosTipos.TryGetValue(t, out var n) ? n : Formulario.Humanizar(t);

    /// <summary>Sobe (delta -1) ou desce (+1) um item. False se não dá.</summary>
    public static bool Mover(List<string> lista, int i, int delta)
    {
        int j = i + delta;
        if (i < 0 || i >= lista.Count || j < 0 || j >= lista.Count) return false;
        (lista[i], lista[j]) = (lista[j], lista[i]);
        return true;
    }

    private void RefazerPrioridades(int selecionar)
    {
        _prioridades.BeginUpdate();
        _prioridades.Items.Clear();
        for (int i = 0; i < _listaPrioridades.Count; i++) _prioridades.Items.Add($"{i + 1}.  {NomeDoTipo(_listaPrioridades[i])}");
        _prioridades.EndUpdate();
        if (selecionar >= 0 && selecionar < _prioridades.Items.Count) _prioridades.SelectedIndex = selecionar;
    }

    private Panel MontarPrioridades()
    {
        var (s, col) = NovaSecao("Prioridades", "Quando tem mais de uma coisa acontecendo, a de cima fica na frente da ilha. Alerta sempre passa por cima de todas.");
        _listaPrioridades.AddRange(_inicio.Prioridades);
        RefazerPrioridades(-1);
        var subir = BotaoSimples("Subir", (_, _) => { int i = _prioridades.SelectedIndex; if (Mover(_listaPrioridades, i, -1)) { RefazerPrioridades(i - 1); Sujou(); } }, 90);
        var descer = BotaoSimples("Descer", (_, _) => { int i = _prioridades.SelectedIndex; if (Mover(_listaPrioridades, i, 1)) { RefazerPrioridades(i + 1); Sujou(); } }, 90);
        var botoes = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(12, 0, 0, 0) };
        subir.Margin = new Padding(0, 0, 0, 8);
        botoes.Controls.Add(subir);
        botoes.Controls.Add(descer);
        _prioridades.Margin = Padding.Empty;
        col.Controls.Add(Fileira(_prioridades, botoes));
        col.Controls.Add(Descricao("O que não está na lista fica depois de todos.", 6));
        return s;
    }

    private Panel MontarModulos(IReadOnlyList<(string id, string nome, Type? tipoConfig, bool ligado)> modulos)
    {
        var (s, col) = NovaSecao("Módulos", "Ligue e desligue cada parte da ilha e ajuste o que cada uma tem de configuração. Vale assim que salvar.");
        foreach (var (id, nome, tipo, ligado) in modulos)
        {
            var cartao = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 12), BackColor = Fundo, MinimumSize = new Size(620, 0),
            };
            cartao.Paint += (_, e) =>
            {
                using var p = new Pen(Linha);
                e.Graphics.DrawRectangle(p, 0, 0, cartao.Width - 1, cartao.Height - 1);
            };
            // "fones": [] escrito à mão: o Coletar deixa a seção como está, então nem o ligado dá para mudar daqui
            var naoObjeto = _inicio.Modulos[id] is { } and not JsonObject;
            var cb = new CheckBox { Text = nome, Checked = ligado, AutoSize = true, Font = _fonteForte, UseMnemonic = false, Margin = new Padding(0, 0, 0, 0), Enabled = !naoObjeto };
            cartao.Controls.Add(cb);
            cartao.Controls.Add(new Label { Text = id, AutoSize = true, ForeColor = Secundario, Font = _fontePequena, Margin = new Padding(20, 0, 0, 4), UseMnemonic = false });
            var m = new ModuloNaTela { Id = id, Ligado = cb, Tipo = tipo };
            if (tipo != null)
            {
                try
                {
                    if (naoObjeto) throw new InvalidOperationException("a seção não é um objeto JSON");
                    m.Instancia = Formulario.Instanciar(tipo, _inicio.Modulos[id] as JsonObject);
                    m.Campos = Formulario.Gerar(tipo, m.Instancia, _fonte);
                    m.Iniciais = Formulario.Valores(m.Campos);
                    foreach (var campo in m.Campos)
                    {
                        if (campo.Editor is CheckBox cbc)
                        {
                            cbc.Margin = new Padding(20, 6, 0, campo.Descricao.Length > 0 ? 0 : 4);
                            cartao.Controls.Add(cbc);
                        }
                        else
                        {
                            var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(20, 6, 0, campo.Descricao.Length > 0 ? 0 : 4) };
                            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
                            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                            t.Controls.Add(new Label { Text = campo.Rotulo, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 4, 8, 0), MaximumSize = new Size(230, 0), UseMnemonic = false }, 0, 0);
                            campo.Editor.Margin = Padding.Empty;
                            t.Controls.Add(campo.Editor, 1, 0);
                            cartao.Controls.Add(t);
                        }
                        if (campo.Descricao.Length > 0)
                            cartao.Controls.Add(new Label { Text = campo.Descricao, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Secundario, Font = _fontePequena, Margin = new Padding(20, 0, 0, 4), UseMnemonic = false });
                    }
                    var fora = Formulario.NaoEditaveis(tipo);
                    if (fora.Count > 0)
                        cartao.Controls.Add(new Label { Text = "Só no config.json: " + string.Join(", ", fora), AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Secundario, Font = _fontePequena, Margin = new Padding(20, 6, 0, 2), UseMnemonic = false });
                }
                catch (Exception e)
                {
                    Log.Escrever($"configurações: campos do módulo {id}: {e.Message}");
                    m.Campos.Clear();
                    m.Iniciais.Clear();
                    m.Instancia = null;
                    cartao.Controls.Add(new Label
                    {
                        Text = "Não deu para montar os campos deste módulo (" + e.Message + "). A tela não mexe na seção dele: corrija no config.json.",
                        AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Secundario, Font = _fontePequena, Margin = new Padding(20, 4, 0, 2), UseMnemonic = false,
                    });
                }
            }
            _modulos.Add(m);
            col.Controls.Add(cartao);
        }
        if (modulos.Count == 0) col.Controls.Add(Descricao("Nenhum módulo encontrado.", 6));
        return s;
    }

    // ------------------------------------------------------------------ salvar

    /// <summary>A config que a tela representa: parte da que vale agora e põe por cima só o que a tela edita.</summary>
    internal Config Coletar()
    {
        var c = Copiar(_atual());
        c.Colada = _colada.Checked;
        c.DistanciaDoTopo = (float)_distancia.Value;
        c.Tamanho = (float)_tamanho.Value;
        c.DeslocamentoX = (int)_deslX.Value;
        c.DeslocamentoY = (int)_deslY.Value;
        c.Sombra = _sombra.Checked;
        c.Dividir = _dividir.Checked;
        c.OciosaNaAreaDeTrabalho = _ociosa.Checked;
        c.RespeitarNaoPerturbe = _naoPerturbe.Checked;
        c.AlertaSegundos = (float)_alerta.Value;
        c.IniciarComWindows = _iniciar.Checked;

        c.Sons.Ligados = _sonsLigados.Checked;
        c.Sons.Volume = _volume.Value / 100.0;

        c.Atalho = _atalhoPrincipal.Valor;
        foreach (var (nome, padrao, caixa) in _atalhos)
        {
            var v = caixa.Valor;
            if (padrao != null && v.Mods == padrao.Mods && v.Tecla == padrao.Tecla) c.AtalhosModulos.Remove(nome);
            else c.AtalhosModulos[nome] = v;
        }

        c.JogosCompetitivos = _jogos.Items.Cast<string>().ToList();
        c.Prioridades = new List<string>(_listaPrioridades);

        foreach (var m in _modulos)
        {
            // seção que não é objeto (agora ou quando a tela abriu, com o ligado travado) fica como está
            if (c.Modulos[m.Id] is { } and not JsonObject || _inicio.Modulos[m.Id] is { } and not JsonObject) continue;
            var original = c.Modulos[m.Id] as JsonObject;
            object? inst = null;
            if (m.Tipo != null && m.Instancia != null)
            {
                // parte da seção de agora, não da abertura da tela: o módulo pode ter gravado o estado dele nesse meio-tempo
                try { inst = Formulario.Instanciar(m.Tipo, original); }
                catch (Exception e) { Log.Escrever($"configurações: módulo {m.Id} fica como está no config.json: {e.Message}"); }
                if (inst != null) Formulario.AplicarMudados(m.Campos, m.Iniciais, inst);
            }
            c.Modulos[m.Id] = Formulario.Mesclar(original, inst == null ? null : m.Tipo, inst, m.Ligado.Checked);
        }
        return c;
    }

    internal void Salvar()
    {
        try
        {
            _salvar(Coletar());
            // a janela fica aberta: o próximo salvar compara com o que acabou de ir, senão desfazer uma mudança não grava
            foreach (var m in _modulos) m.Iniciais = Formulario.Valores(m.Campos);
            _sujo = false;
            MostrarStatus("Salvo. A ilha já está usando.");
        }
        catch (Exception e)
        {
            Log.Escrever("configurações: não salvou: " + e);
            MostrarStatus("Não deu para salvar: " + e.Message);
        }
    }

    private void MostrarStatus(string texto)
    {
        _status.Text = texto;
        _limparStatus.Stop();
        _limparStatus.Start();
    }

    private void Sujou()
    {
        if (_montando) return;
        _sujo = true;
    }

    /// <summary>Marca a tela como mudada em qualquer edição (para perguntar antes de fechar sem salvar).</summary>
    private void Vigiar(Control raiz)
    {
        foreach (Control c in raiz.Controls)
        {
            switch (c)
            {
                case CheckBox cb: cb.CheckedChanged += (_, _) => Sujou(); break;
                case NumericUpDown n: n.ValueChanged += (_, _) => Sujou(); break;
                case TrackBar tb: tb.ValueChanged += (_, _) => Sujou(); break;
                case ComboBox co: co.SelectedIndexChanged += (_, _) => Sujou(); break;
                case CaixaDeAtalho ca: ca.Mudou += Sujou; break;
                case TextBox t when t.Parent != null && _modulos.Any(m => m.Campos.Any(x => x.Editor == t)): t.TextChanged += (_, _) => Sujou(); break;
            }
            Vigiar(c);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || !_sujo || e.CloseReason == CloseReason.WindowsShutDown) return;
        var r = MessageBox.Show(this, "Salvar as mudanças antes de fechar?", "Configurações da Ilha", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) e.Cancel = true;
        else if (r == DialogResult.Yes) Salvar();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; Salvar(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _teste?.Dispose();
            _limparStatus.Dispose();
            _fonte.Dispose();
            _fonteForte.Dispose();
            _fontePequena.Dispose();
            _fonteTitulo.Dispose();
            _fonteMarca.Dispose();
        }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ autoteste

    /// <summary>Monta a janela sem mostrar (o autoteste do módulo de configurações usa).</summary>
    internal static JanelaDeConfiguracoes ParaTeste(Func<Config> atual, IReadOnlyList<(string id, string nome, Type? tipoConfig, bool ligado)> modulos,
        IReadOnlyDictionary<string, Atalho>? atalhos, Action<Config>? salvar = null) => new(atual, salvar ?? (_ => { }), modulos, atalhos);

    /// <summary>Foto de cada aba sem mostrar a janela na tela (para conferir o visual no desenvolvimento).</summary>
    internal List<string> Fotografar(string pasta)
    {
        var feitos = new List<string>();
        Directory.CreateDirectory(pasta);
        // o WinForms só desenha controle visível: a janela "aparece" fora da tela, sem foco, sem barra de tarefas e transparente
        _semAtivar = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-30000, -30000);
        Opacity = 0;
        Visible = true;
        for (int i = 0; i < _secoes.Count; i++)
        {
            Escolher(i);
            PerformLayout();
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            var arq = Path.Combine(pasta, $"configuracoes-{i + 1}.png");
            bmp.Save(arq, System.Drawing.Imaging.ImageFormat.Png);
            feitos.Add(arq);
        }
        Escolher(0);
        Visible = false;
        return feitos;
    }

    private bool _semAtivar;
    protected override bool ShowWithoutActivation => _semAtivar;

    internal NumericUpDown CampoTamanho => _tamanho;
    internal CheckBox CampoColada => _colada;
    internal List<string> ListaDePrioridades => _listaPrioridades;
    internal ListBox ListaDeJogos => _jogos;
    internal IEnumerable<(string Id, CheckBox Ligado, List<CampoGerado> Campos)> ModulosNaTela => _modulos.Select(m => (m.Id, m.Ligado, m.Campos));
    internal IEnumerable<(string Nome, CaixaDeAtalho Caixa)> CaixasDeAtalho => _atalhos.Select(a => (a.Nome, a.Caixa));
}
