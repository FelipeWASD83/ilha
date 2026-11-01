using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;

namespace Ilha.Mascote;

/// <summary>
/// O mascote morando na ilha. O mascote é um app à parte (o acompanhante), dono do desenho, do fundo, do comportamento
/// e da config dele; a ilha é só o palco: serve o pipe \\.\pipe\ilha-acompanhante, mostra os quadros que chegam, diz
/// ao app onde a pílula está e passa o arrastar para fora. Ele entra arrastando a janela dele para cima da pílula, ou
/// pela entrada "Mascote" do menu (que abre o app, se ele estiver fechado, pelo comando da config). Nada roda sem o
/// app conectado: sem relógio, sem leitura de arquivo.
/// </summary>
[Modulo("mascote", "Mascote", LigadoPorPadrao = false)]
internal sealed partial class ModuloMascote : Modulo
{
    private const string ID_MENU = "mascote";
    private const int ARRASTO_MIN_MS = 12;

    private ConfigMascote _cfg = new();
    private AtividadeMascote? _atv;
    private ServidorAcompanhante? _srv;
    private bool _parou, _conectado, _noMenu;
    private string _palcoMandado = "";
    private long _lancouEm = long.MinValue / 2, _expandirAoEntrarAte, _ultimoArrasto;
    // só o quadro mais novo espera a interface: chegando mais rápido que ela desenha, os do meio são pulados
    private sealed record Pendente(byte[] Px, int L, int A, bool Pixelado);
    private Pendente? _quadroPendente;
    private int _quadroAgendado;

    public override Type? TipoConfig => typeof(ConfigMascote);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigMascote>(cfg);
        _atv = new AtividadeMascote
        {
            SaiuArrastando = AoSairArrastando,
            Arrastou = () => MandarCursor("arrasto", forcar: false),
            Soltou = () => MandarCursor("soltou", forcar: true),
            PedirSair = () => { _srv?.Enviar(ProtocoloAcompanhante.Simples("pedir_sair")); S.Ilha.Recolher(); },
        };
        S.Ilha.Adicionar(_atv);
        S.Ilha.EstadoMudou += AoMudarEstado;
        _srv = new ServidorAcompanhante(ProtocoloAcompanhante.PIPE,
            () => S.NaInterface(AoConectar),
            AoChegar,
            () => S.NaInterface(AoCair));
        AtualizarMenu();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigMascote>(cfg);
        AtualizarMenu();
    }

    public override void Contexto(Contexto ctx) => MandarPalco();

    public override void Parar()
    {
        _parou = true;
        try { S.Ilha.EstadoMudou -= AoMudarEstado; } catch (Exception) { }
        if (_noMenu)
        {
            try { S.Menu.Remover(ID_MENU); } catch (Exception) { }
            _noMenu = false;
        }
        _conectado = false;
        _srv?.Dispose();
        _srv = null;
        _atv?.Parar();
    }

    private void AoMudarEstado(Estado e) => MandarPalco();

    // ------------------------------------------------------------------ conexão

    private void AoConectar()
    {
        if (_parou) return;
        _conectado = true;
        MandarPalco(forcar: true);
        AtualizarMenu();
    }

    private void AoCair()
    {
        if (_parou) return;
        _conectado = false;
        _palcoMandado = "";
        _quadroPendente = null;
        _atv?.Desconectou();
        AtualizarMenu();
    }

    /// <summary>Thread do pipe: o PNG do quadro é decodificado aqui; o resto vai para a interface.</summary>
    private void AoChegar(ProtocoloAcompanhante.Recebida m)
    {
        if (m.Tipo == "quadro")
        {
            var px = ProtocoloAcompanhante.Pixels(m.Png!, out int l, out int a);
            if (px == null) return;
            Volatile.Write(ref _quadroPendente, new Pendente(px, l, a, m.Pixelado));
            if (Interlocked.Exchange(ref _quadroAgendado, 1) == 0)
                S.NaInterface(() =>
                {
                    Interlocked.Exchange(ref _quadroAgendado, 0);
                    if (_parou || !_conectado || Interlocked.Exchange(ref _quadroPendente, null) is not { } q) return;
                    _atv?.NovoQuadro(q.Px, q.L, q.A, q.Pixelado);
                    TalvezExpandir();
                });
            return;
        }
        S.NaInterface(() => { if (!_parou) Tratar(m); });
    }

    private void Tratar(ProtocoloAcompanhante.Recebida m)
    {
        if (_atv == null || !_conectado) return;
        switch (m.Tipo)
        {
            case "ola":
                if (m.Versao > ProtocoloAcompanhante.VERSAO) Log.Escrever($"mascote: o app fala a versão {m.Versao} do protocolo (a ilha, a {ProtocoloAcompanhante.VERSAO})");
                _atv.DefinirNome(m.Nome);
                MandarPalco(forcar: true);
                S.Menu.Redesenhar();
                break;
            case "entrar":
                _atv.Morar(true);
                MandarPalco();
                S.Menu.Redesenhar();
                break;
            case "sair":
                _atv.Morar(false);
                MandarPalco();
                S.Menu.Redesenhar();
                break;
            case "pairando":
                _atv.Pairando(m.Sim);
                // o mascote manda "pairando" ao começar a arrastar: a ilha confere o palco nessa hora
                MandarPalco();
                break;
            case "visivel":
                _atv.Visivel(m.Sim);
                break;
            case "recusou":
                S.Avisar(string.IsNullOrWhiteSpace(m.Motivo) ? "O mascote não quis entrar na ilha agora." : m.Motivo!);
                break;
        }
        TalvezExpandir();
    }

    /// <summary>Pedido pelo menu: abre o cartão quando ele entrar (a atividade só liga com o primeiro quadro).</summary>
    private void TalvezExpandir()
    {
        if (_atv is not { Ativa: true, Morando: true } || Environment.TickCount64 >= _expandirAoEntrarAte) return;
        // abrir o cartão apagaria o alerta na tela e a fila: tenta de novo no próximo quadro
        if (S.Ilha.EstadoAtual == Estado.Alerta) return;
        _expandirAoEntrarAte = 0;
        S.Ilha.Expandir(_atv);
    }

    // ------------------------------------------------------------------ palco

    /// <summary>Onde soltar: a pílula como está (ou, escondida, onde ela aparece), em pixels da tela.</summary>
    private System.Drawing.Rectangle RetanguloDoPalco()
    {
        var ilha = S.Ilha;
        if (ilha.EstadoAtual != Estado.Oculta)
        {
            var c = ilha.CorpoNaTela;
            return System.Drawing.Rectangle.Round(c);
        }
        Nativo.GetWindowRect(ilha.JanelaVisual, out var r);
        return Palcos.Escondida(r.Left, r.Top, r.Right - r.Left, ilha.Escala);
    }

    private bool PodeMostrar()
    {
        var ctx = S.ContextoAtual();
        return !S.Config().Pausada && !ctx.Competitivo && !ctx.TelaCheia;
    }

    private void MandarPalco(bool forcar = false)
    {
        if (_parou || !_conectado || _srv == null) return;
        string linha;
        try { linha = ProtocoloAcompanhante.Palco(RetanguloDoPalco(), PodeMostrar()); }
        catch (Exception e) { Log.Escrever("mascote: palco: " + e.Message); return; }
        if (!forcar && linha == _palcoMandado) return;
        _palcoMandado = linha;
        _srv.Enviar(linha);
    }

    // ------------------------------------------------------------------ arrastar para fora

    private void AoSairArrastando()
    {
        if (_srv == null || !_conectado) return;
        MandarCursor("arrasto_saida", forcar: true);
        // a cena do botão some: depois do arrastar que está rodando, não no meio dele
        S.NaInterface(() =>
        {
            if (_parou || _atv == null) return;
            _atv.Morar(false);
            MandarPalco();
            S.Menu.Redesenhar();
        });
    }

    private void MandarCursor(string tipo, bool forcar)
    {
        if (_srv == null || !_conectado) return;
        long agora = Environment.TickCount64;
        // o mouse manda centenas de movimentos por segundo; o soltar sempre vai
        if (!forcar && agora - _ultimoArrasto < ARRASTO_MIN_MS) return;
        _ultimoArrasto = agora;
        Nativo.GetCursorPos(out var c);
        _srv.Enviar(ProtocoloAcompanhante.Ponto(tipo, c.X, c.Y));
    }

    // ------------------------------------------------------------------ menu

    private void AtualizarMenu()
    {
        if (_parou) return;
        bool deve = _conectado || !string.IsNullOrWhiteSpace(_cfg.ComandoAbrir);
        if (deve && !_noMenu)
        {
            S.Menu.Adicionar(new ItemDoMenu
            {
                Id = ID_MENU,
                // a entrada pode nascer depois do Iniciar (quando o app conecta), fora da marca do núcleo
                Dono = Id,
                Nome = "Mascote",
                Ordem = 60,
                Abrir = AbrirPeloMenu,
                Icone = DesenharIcone,
                Detalhe = () => !_conectado ? null : _atv?.Morando == true ? "Na ilha" : "Fora da ilha",
            });
            _noMenu = true;
        }
        else if (!deve && _noMenu)
        {
            S.Menu.Remover(ID_MENU);
            _noMenu = false;
        }
        else if (_noMenu) S.Menu.Redesenhar();
    }

    private void AbrirPeloMenu()
    {
        if (_parou) return;
        if (_conectado && _srv != null)
        {
            if (_atv?.Morando == true)
            {
                _srv.Enviar(ProtocoloAcompanhante.Simples("pedir_sair"));
                S.Ilha.Recolher();
            }
            else
            {
                _expandirAoEntrarAte = Environment.TickCount64 + 15_000;
                _srv.Enviar(ProtocoloAcompanhante.Simples("pedir_entrar"));
            }
            return;
        }
        Abrir();
    }

    /// <summary>Abre o app do mascote pedindo para ele já nascer dentro da ilha.</summary>
    private void Abrir()
    {
        var cmd = Comando.Dividir(_cfg.ComandoAbrir);
        if (cmd == null)
        {
            S.Avisar("Para abrir o mascote pela ilha, ponha o comando dele em \"comandoAbrir\" (modulos.mascote na config).");
            return;
        }
        long agora = Environment.TickCount64;
        // o app leva uns segundos para abrir e conectar: um segundo clique não abre outro
        if (agora - _lancouEm < 10_000) return;
        _lancouEm = agora;
        _expandirAoEntrarAte = agora + 20_000;
        try
        {
            var (exe, args) = cmd.Value;
            string extra = (_cfg.ArgumentoNaIlha ?? "").Trim();
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = extra.Length == 0 ? args : (args.Length == 0 ? extra : args + " " + extra),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (Path.IsPathRooted(exe) && Path.GetDirectoryName(exe) is { Length: > 0 } pasta && Directory.Exists(pasta)) psi.WorkingDirectory = pasta;
            using var p = Process.Start(psi);
            Log.Escrever("mascote: app do mascote aberto pelo menu");
        }
        catch (Exception e)
        {
            _lancouEm = long.MinValue / 2;
            Log.Escrever("mascote: o app não abriu: " + e.Message);
            S.Avisar("O mascote não abriu. Confira o \"comandoAbrir\" do mascote na config.");
        }
    }

    /// <summary>Um bichinho de pixel: corpo, olhos e quatro patas, tudo em quadradinhos.</summary>
    private static void DesenharIcone(Vortice.Direct2D1.ID2D1DeviceContext dc, Palco p, System.Drawing.RectangleF r)
    {
        float u = r.Width / 12f, x = r.X, y = r.Y + u;
        var corpo = Cor.Coral;
        var olho = new Vortice.Mathematics.Color4(0.08f, 0.06f, 0.05f, 1f);
        Icone.Retangulo(dc, x + 2 * u, y + 2 * u, 8 * u, 5 * u, 0, corpo);
        Icone.Retangulo(dc, x + 1 * u, y + 4 * u, 10 * u, 2 * u, 0, corpo);
        Icone.Retangulo(dc, x + 4 * u, y + 3 * u, u, u, 0, olho);
        Icone.Retangulo(dc, x + 7 * u, y + 3 * u, u, u, 0, olho);
        foreach (float px in new[] { 2.5f, 4.5f, 6.5f, 8.5f })
            Icone.Retangulo(dc, x + px * u, y + 7 * u, u, 2 * u, 0, corpo);
    }
}

/// <summary>Contas do palco sem janela (testadas no autoteste).</summary>
internal static class Palcos
{
    public const float ESCONDIDA_L = 220, ESCONDIDA_A = 56;

    /// <summary>A ilha escondida não tem pílula: o alvo é o topo do meio da janela dela, onde a pílula nasce.</summary>
    public static System.Drawing.Rectangle Escondida(int janX, int janY, int janL, float escala)
    {
        int l = (int)MathF.Round(ESCONDIDA_L * escala), a = (int)MathF.Round(ESCONDIDA_A * escala);
        return new System.Drawing.Rectangle(janX + (janL - l) / 2, janY, l, a);
    }
}

/// <summary>Separa o comando da config em programa e argumentos (o programa pode vir entre aspas).</summary>
internal static class Comando
{
    public static (string Exe, string Args)? Dividir(string? linha)
    {
        var s = (linha ?? "").Trim();
        if (s.Length == 0) return null;
        string exe, resto;
        if (s[0] == '"')
        {
            int fim = s.IndexOf('"', 1);
            if (fim < 0) return null;
            exe = s[1..fim];
            resto = s[(fim + 1)..];
        }
        else
        {
            int esp = s.IndexOf(' ');
            exe = esp < 0 ? s : s[..esp];
            resto = esp < 0 ? "" : s[esp..];
        }
        exe = Environment.ExpandEnvironmentVariables(exe.Trim());
        return exe.Length == 0 ? null : (exe, Environment.ExpandEnvironmentVariables(resto.Trim()));
    }
}

public sealed class ConfigMascote
{
    /// <summary>
    /// Como abrir o app do mascote quando ele está fechado (programa e argumentos, por exemplo um pythonw com o
    /// script dele; aceita %VARIAVEIS%). Vazio = a entrada do menu só aparece com o app já aberto.
    /// </summary>
    [JsonPropertyName("comandoAbrir")] public string ComandoAbrir { get; set; } = "";
    /// <summary>O argumento que a ilha acrescenta ao abrir o app pelo menu, para ele já começar dentro dela.</summary>
    [JsonPropertyName("argumentoNaIlha")] public string ArgumentoNaIlha { get; set; } = "--na-ilha";
}
