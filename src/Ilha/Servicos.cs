using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Ilha;

/// <summary>
/// Sons curtos por tipo ("chegada", "alerta", "timer", "claude", "conectou", "erro"). Arquivo substituível: o de
/// ~/.ilha/sons/&lt;tipo&gt;.wav vale mais que o que vem com a ilha (sons/&lt;tipo&gt;.wav). Mudo no modo competitivo e no
/// Não Incomodar, menos o que for importante (o timer acabando).
/// </summary>
internal sealed class Sons
{
    private readonly Func<Config> _cfg;
    private readonly Func<Contexto> _ctx;
    private readonly List<Windows.Media.Playback.MediaPlayer> _tocando = new();

    public Sons(Func<Config> cfg, Func<Contexto> ctx)
    {
        _cfg = cfg;
        _ctx = ctx;
    }

    public static string? Arquivo(string tipo)
    {
        foreach (var pasta in new[] { Path.Combine(Config.Pasta, "sons"), Path.Combine(AppContext.BaseDirectory, "sons") })
        {
            var a = Path.Combine(pasta, tipo + ".wav");
            if (File.Exists(a)) return a;
        }
        return null;
    }

    public void Tocar(string tipo, bool importante = false)
    {
        var cfg = _cfg();
        var ctx = _ctx();
        if (!cfg.Sons.Ligados || cfg.Sons.Volume <= 0) return;
        if (cfg.Pausada) return;
        if (ctx.Competitivo && !importante) return;
        if (ctx.NaoPerturbe && cfg.RespeitarNaoPerturbe && !importante) return;
        var arq = Arquivo(tipo) ?? Arquivo("alerta");
        if (arq == null) return;
        try
        {
            var mp = new Windows.Media.Playback.MediaPlayer
            {
                Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(arq)),
                Volume = Math.Clamp(cfg.Sons.Volume, 0, 1),
                AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Alerts,
            };
            mp.MediaEnded += (s, _) => Programa.NaInterface(() => { _tocando.Remove(mp); mp.Dispose(); });
            mp.MediaFailed += (s, e) => Programa.NaInterface(() => { Log.Escrever($"som {tipo} falhou: {e.ErrorMessage}"); _tocando.Remove(mp); mp.Dispose(); });
            _tocando.Add(mp);
            mp.Play();
        }
        catch (Exception e) { Log.Escrever($"som {tipo}: {e.Message}"); }
    }
}

/// <summary>
/// Atalhos globais dos módulos (calculadora, tradutor, IA, timer...), além do atalho principal da ilha. Cada um tem
/// um nome; o "atalhos" da config pode trocar a combinação de qualquer um. RegisterHotKey, sem gancho de teclado.
/// </summary>
internal sealed class Atalhos : IDisposable
{
    private readonly IntPtr _janela;
    private readonly Func<Config> _cfg;
    private readonly Dictionary<int, (string Nome, Action Acao)> _porId = new();
    private readonly Dictionary<string, (Atalho Padrao, Action Acao)> _pedidos = new();
    private readonly Dictionary<string, string> _donos = new();
    private int _proximo = 0x5000;

    /// <summary>O módulo que está ligando agora (o núcleo marca); os atalhos pedidos ficam em nome dele.</summary>
    public string? Dono { get; set; }

    /// <summary>Nome e combinação padrão de cada atalho pedido (a tela de configurações mostra).</summary>
    public IReadOnlyDictionary<string, Atalho> Padroes
    {
        get
        {
            var r = new Dictionary<string, Atalho>(StringComparer.OrdinalIgnoreCase);
            foreach (var (nome, v) in _pedidos) r[nome] = v.Padrao;
            return r;
        }
    }

    /// <summary>Solta o atalho (a combinação fica livre para outros programas).</summary>
    public void Remover(string nome)
    {
        foreach (var (id, v) in new Dictionary<int, (string, Action)>(_porId))
            if (v.Item1 == nome) { Nativo.UnregisterHotKey(_janela, id); _porId.Remove(id); }
        _pedidos.Remove(nome);
        _donos.Remove(nome);
    }

    /// <summary>Solta todos os atalhos de um módulo (ele foi desligado).</summary>
    public void RemoverDoDono(string dono)
    {
        foreach (var (nome, d) in new Dictionary<string, string>(_donos))
            if (d == dono) Remover(nome);
    }

    public Atalhos(IntPtr janela, Func<Config> cfg)
    {
        _janela = janela;
        _cfg = cfg;
    }

    /// <summary>Pede um atalho. Devolve o que ficou valendo (null = ocupado por outro programa ou desligado).</summary>
    public Atalho? Registrar(string nome, Atalho padrao, Action acao)
    {
        _pedidos[nome] = (padrao, acao);
        if (Dono != null) _donos[nome] = Dono;
        return Aplicar(nome);
    }

    private Atalho? Aplicar(string nome)
    {
        foreach (var (id, v) in new Dictionary<int, (string, Action)>(_porId))
            if (v.Item1 == nome) { Nativo.UnregisterHotKey(_janela, id); _porId.Remove(id); }
        var (padrao, acao) = _pedidos[nome];
        var a = _cfg().AtalhosModulos.TryGetValue(nome, out var trocado) ? trocado : padrao;
        if (a.Tecla == 0) return null;
        int novo = _proximo++;
        if (!Nativo.RegisterHotKey(_janela, novo, a.Mods | Nativo.MOD_NOREPEAT, a.Tecla))
        {
            Log.Escrever($"atalho {nome} ({Teclas.Nome(a)}) ocupado por outro programa");
            return null;
        }
        _porId[novo] = (nome, acao);
        Log.Escrever($"atalho {nome}: {Teclas.Nome(a)}");
        return a;
    }

    /// <summary>A config mudou: refaz todos (pega combinações trocadas).</summary>
    public void Reaplicar()
    {
        foreach (var nome in new List<string>(_pedidos.Keys)) Aplicar(nome);
    }

    /// <summary>Chamado pela janela quando chega um WM_HOTKEY; devolve true se era de um módulo.</summary>
    public bool Disparou(int id)
    {
        if (!_porId.TryGetValue(id, out var v)) return false;
        try { v.Acao(); } catch (Exception e) { Log.Escrever($"atalho {v.Nome}: {e}"); }
        return true;
    }

    public void Dispose()
    {
        foreach (var id in _porId.Keys) Nativo.UnregisterHotKey(_janela, id);
        _porId.Clear();
    }
}

/// <summary>
/// Chaves e senhas no Gerenciador de Credenciais do Windows (genéricas, "Ilha/&lt;nome&gt;"). Nunca em arquivo nem em log.
/// Quem digita é o Felipe, numa tela da própria ilha.
/// </summary>
internal static class Credenciais
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWriteW(ref CREDENTIAL c, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr p);
    private const uint CRED_TYPE_GENERIC = 1, CRED_PERSIST_LOCAL_MACHINE = 2;

    private static string Alvo(string nome) => "Ilha/" + nome;

    public static bool Gravar(string nome, string valor)
    {
        var bytes = Encoding.Unicode.GetBytes(valor);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC, TargetName = Alvo(nome), CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob, Persist = CRED_PERSIST_LOCAL_MACHINE, UserName = Environment.UserName,
            };
            return CredWriteW(ref c, 0);
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    public static string? Ler(string nome)
    {
        if (!CredReadW(Alvo(nome), CRED_TYPE_GENERIC, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            return c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2);
        }
        finally { CredFree(p); }
    }

    public static bool Existe(string nome) => Ler(nome) is { Length: > 0 };

    public static void Apagar(string nome) => CredDeleteW(Alvo(nome), CRED_TYPE_GENERIC, 0);
}

/// <summary>Câmera e microfone em uso (o módulo de privacidade preenche; a ilha desenha as bolinhas verde e laranja).</summary>
internal sealed class Indicadores
{
    public bool Camera { get; private set; }
    public bool Microfone { get; private set; }
    public string CameraApps { get; private set; } = "";
    public string MicrofoneApps { get; private set; } = "";
    public event Action? Mudou;

    public bool Algum => Camera || Microfone;

    public void Definir(bool camera, string cameraApps, bool microfone, string microfoneApps)
    {
        if (camera == Camera && microfone == Microfone && cameraApps == CameraApps && microfoneApps == MicrofoneApps) return;
        Camera = camera;
        Microfone = microfone;
        CameraApps = cameraApps;
        MicrofoneApps = microfoneApps;
        Mudou?.Invoke();
    }
}

/// <summary>
/// Campo de texto da ilha. A ilha nunca vira janela ativa; quando um módulo precisa de teclado (calculadora, tradutor,
/// IA, timer), uma janela invisível com uma caixa de texto de verdade recebe o foco: acento, colar, seleção e IME
/// funcionam. O módulo desenha o texto na cena (Texto e Cursor); Esc ou clicar em outro lugar fecha e o foco volta
/// para a janela de antes.
/// </summary>
internal sealed class Entrada : IDisposable
{
    private sealed class Invisivel : Form
    {
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Nativo.WS_EX_TOOLWINDOW; return cp; }
        }
    }

    private readonly Invisivel _form;
    private readonly TextBox _caixa;
    private IntPtr _antes;
    private Action<string>? _mudou, _enter;
    private Action? _fechou;
    private Action<System.Drawing.Image>? _imagem;
    private bool _multilinha;
    private bool _soTeclas;

    public bool Aberta { get; private set; }
    /// <summary>Aberta só para a ilha ouvir o teclado (o Esc), sem campo de módulo nenhum.</summary>
    public bool SoTeclas => Aberta && _soTeclas;
    /// <summary>
    /// Esc com a ilha focada: o núcleo decide. true = ele tratou e a ilha continua com o teclado (voltou para o
    /// menu); false = fecha e devolve o foco para a janela de antes.
    /// </summary>
    public Func<bool>? AoEsc;
    public string Texto => _caixa.Text;
    public int Cursor => _caixa.SelectionStart;
    public int Selecao => _caixa.SelectionLength;
    public bool Senha { get; private set; }
    /// <summary>Qualquer mudança de texto ou de cursor (o módulo redesenha).</summary>
    public event Action? Mudou;
    /// <summary>Setas para cima e para baixo, Page Up, Page Down e Tab (histórico, lista...); o texto não muda.</summary>
    public event Action<Keys>? Tecla;

    public Entrada()
    {
        _form = new Invisivel
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000), Size = new System.Drawing.Size(40, 20),
        };
        _caixa = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, AcceptsTab = false };
        _form.Controls.Add(_caixa);
        _caixa.TextChanged += (_, _) => Avisar();
        _caixa.KeyUp += (_, _) => Avisar();
        _caixa.MouseUp += (_, _) => Avisar();
        _caixa.KeyDown += AoTeclar;
        _form.Deactivate += (_, _) => { if (Aberta) Fechar(devolverFoco: false); };
    }

    private void Avisar()
    {
        if (!Aberta) return;
        _mudou?.Invoke(_caixa.Text);
        Mudou?.Invoke();
    }

    private void AoTeclar(object? s, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            bool fica = false;
            try { fica = AoEsc?.Invoke() ?? false; } catch (Exception ex) { Log.Escrever("Esc: " + ex); }
            // true: o núcleo tratou (voltou para o menu, que já passou para o modo teclado, ou só fechou um alerta e o
            // campo do módulo continua como estava)
            if (Aberta && !fica) Fechar();
            return;
        }
        // só ouvindo o Esc: o resto do teclado não faz nada
        if (_soTeclas) { e.SuppressKeyPress = true; return; }
        if (e.KeyCode is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Tab && !(_multilinha && e.KeyCode is Keys.Up or Keys.Down))
        {
            if (Tecla != null) { e.SuppressKeyPress = true; e.Handled = true; Tecla(e.KeyCode); return; }
        }
        if (e.KeyCode == Keys.Enter && !(_multilinha && e.Shift))
        {
            e.SuppressKeyPress = true;
            _enter?.Invoke(_caixa.Text);
            return;
        }
        if (e.Control && e.KeyCode == Keys.V && _imagem != null && Clipboard.ContainsImage())
        {
            e.SuppressKeyPress = true;
            var img = Clipboard.GetImage();
            if (img != null) _imagem(img);
        }
    }

    /// <summary>Abre o campo. `enter` recebe o texto ao apertar Enter (Shift+Enter quebra linha se multilinha).</summary>
    public void Abrir(string inicial, Action<string> mudou, Action<string> enter, Action? fechou = null,
        Action<System.Drawing.Image>? imagem = null, bool multilinha = false, bool senha = false)
    {
        // do modo teclado para um campo: o foco já é nosso, só troca (sem piscar)
        if (Aberta && !_soTeclas) Fechar(devolverFoco: false);
        _soTeclas = false;
        _caixa.ReadOnly = false;
        _mudou = mudou;
        _enter = enter;
        _fechou = fechou;
        _imagem = imagem;
        _multilinha = multilinha;
        Senha = senha;
        _caixa.Multiline = multilinha;
        _caixa.UseSystemPasswordChar = senha;
        _caixa.Text = inicial;
        _caixa.SelectionStart = inicial.Length;
        // de um campo para outro: a janela da frente já é a invisível da ilha; a de antes continua a original
        var frente = Nativo.GetForegroundWindow();
        if (frente != _form.Handle) _antes = frente;
        bool jaAberta = Aberta;
        Aberta = true;
        if (jaAberta) { _caixa.Focus(); Avisar(); return; }
        _form.Show();
        _form.Activate();
        Nativo.SetForegroundWindow(_form.Handle);
        _caixa.Focus();
        Avisar();
    }

    /// <summary>
    /// A ilha pega o teclado (o clique nela): só para o Esc funcionar. Não faz nada se um campo de módulo já estiver
    /// aberto. Fechar devolve o foco para a janela onde ele estava.
    /// </summary>
    public void Focar()
    {
        if (Aberta) return;
        var frente = Nativo.GetForegroundWindow();
        if (frente != _form.Handle) _antes = frente;
        _soTeclas = true;
        _caixa.ReadOnly = true;
        _caixa.Text = "";
        Aberta = true;
        _form.Show();
        _form.Activate();
        Nativo.SetForegroundWindow(_form.Handle);
        _caixa.Focus();
    }

    /// <summary>O campo do módulo fecha (ele recebe o "fechou"), mas a ilha continua com o teclado para o Esc.</summary>
    public void ModoTeclado()
    {
        if (!Aberta || _soTeclas) return;
        var f = _fechou;
        _fechou = null;
        _mudou = null;
        _enter = null;
        _imagem = null;
        _soTeclas = true;
        _caixa.ReadOnly = true;
        _caixa.Text = "";
        f?.Invoke();
        Mudou?.Invoke();
    }

    /// <summary>Troca senha ou multilinha com o campo aberto, sem piscar o foco.</summary>
    public void Trocar(bool senha, bool multilinha)
    {
        Senha = senha;
        _multilinha = multilinha;
        _caixa.UseSystemPasswordChar = senha;
        _caixa.Multiline = multilinha;
        Avisar();
    }

    public void Definir(string texto)
    {
        _caixa.Text = texto;
        _caixa.SelectionStart = texto.Length;
    }

    public void Fechar(bool devolverFoco = true)
    {
        if (!Aberta) return;
        Aberta = false;
        _soTeclas = false;
        _caixa.ReadOnly = false;
        _form.Hide();
        if (devolverFoco && _antes != IntPtr.Zero) Nativo.SetForegroundWindow(_antes);
        var f = _fechou;
        _fechou = null;
        _mudou = null;
        _enter = null;
        _imagem = null;
        f?.Invoke();
        Mudou?.Invoke();
    }

    public void Dispose()
    {
        _form.Dispose();
    }
}
