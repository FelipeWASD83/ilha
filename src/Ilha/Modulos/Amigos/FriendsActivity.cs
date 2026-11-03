using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ilha.Games;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Friends;

/// <summary>
/// The "Amigos" card from the menu: who is online, the ones in a game first, with their avatars (scrolled by the wheel
/// past eight rows); with no key yet, the field to paste the Steam Web API key. Never on the pill.
/// </summary>
internal sealed class FriendsActivity : Atividade
{
    private const float L = 340, ROW = 44, TOP = 30;
    private const int VISIBLE = 8;
    internal static readonly Color4 InGame = new(0.56f, 0.86f, 0.33f, 1f), OnlineBlue = new(0.40f, 0.75f, 1f, 1f);

    private readonly Servicos S;
    private readonly FriendsModule _m;
    private float _scroll;
    private bool _typing;
    private string? _notice;

    public override string Tipo => "amigos";
    public override string Nome => Lang.T("Amigos", "Friends");
    public override bool EhFerramenta => true;
    public override bool AnunciaChegada => false;

    public FriendsActivity(Servicos s, FriendsModule m)
    {
        S = s;
        _m = m;
    }

    public void Open()
    {
        _scroll = 0;
        _notice = null;
        MudouTamanho();
        S.Ilha.Expandir(this);
        _m.RefreshSoon();
    }

    public void Changed()
    {
        if (S.Ilha.Expandida == this) MudouTamanho();
    }

    private List<Friend> Online() => _m.Friends.Where(f => f.Online).OrderByDescending(f => f.Playing).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public override bool Rolar(int delta)
    {
        if (S.Ilha.Expandida != this) return false;
        int n = Online().Count;
        float max = MathF.Max(0, (n - VISIBLE) * ROW);
        float next = Math.Clamp(_scroll - delta / 120f * ROW, 0, max);
        if (next != _scroll) { _scroll = next; Redesenhar(); }
        return true;
    }

    protected override Cena Montar(Palco p, Forma f) => !_m.HasKey ? KeyCard(p) : Card(p);

    private Cena Card(Palco p)
    {
        int n = Online().Count;
        float listH = Math.Max(1, Math.Min(n, VISIBLE)) * ROW;
        var c = p.NovaCena(new Vector2(L, TOP + listH + (_m.Error != null ? 22 : 0)));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var online = Online();
            int playing = online.Count(x => x.Playing);
            p.Texto.Desenhar(dc, Lang.T("Amigos", "Friends"), Vector2.Zero, 15, Cor.Branco);
            string sub = _m.Loaded
                ? Lang.T($"{online.Count} online · {playing} jogando", $"{online.Count} online · {playing} playing")
                : Lang.T("carregando…", "loading…");
            var ss = p.Texto.Medir(sub, 12, FontWeight.Normal);
            p.Texto.Desenhar(dc, sub, new Vector2(L - ss.X, 3), 12, Cor.Secundario, FontWeight.Normal);
            if (online.Count == 0)
            {
                p.Texto.Desenhar(dc, _m.Loaded ? Lang.T("Ninguém online agora.", "Nobody online right now.") : "", new Vector2(0, TOP + 12), 13, Cor.Secundario, FontWeight.Normal);
            }
            else
            {
                dc.PushAxisAlignedClip(new System.Drawing.RectangleF(0, TOP, L + 12, listH), AntialiasMode.Aliased);
                int first = (int)(_scroll / ROW);
                float y = TOP - (_scroll - first * ROW);
                foreach (var fr in online.Skip(first).Take(VISIBLE + 1))
                {
                    Row(dc, p, fr, y);
                    y += ROW;
                }
                dc.PopAxisAlignedClip();
                if (online.Count > VISIBLE)
                {
                    float track = listH - 8, thumb = MathF.Max(24, track * VISIBLE / online.Count);
                    float ty = TOP + 4 + (track - thumb) * (_scroll / ((online.Count - VISIBLE) * ROW));
                    Icone.Retangulo(dc, L + 6, TOP + 4, 3, track, 1.5f, new Color4(1, 1, 1, 0.08f));
                    Icone.Retangulo(dc, L + 6, ty, 3, thumb, 1.5f, new Color4(1, 1, 1, 0.45f));
                }
            }
            if (_m.Error is { } err)
                p.Texto.Desenhar(dc, err, new Vector2(0, t.Y - 16), 11.5f, Cor.Vermelho, FontWeight.Normal, largura: L);
        });
        return c;
    }

    private void Row(ID2D1DeviceContext dc, Palco p, Friend f, float y)
    {
        var color = f.Playing ? InGame : f.State == 1 ? OnlineBlue : Cor.Secundario;
        DrawAvatar(dc, f, 2, y + 5, 32, () => S.NaInterface(Redesenhar));
        // the state dot on the avatar's corner
        Icone.Circulo(dc, new Vector2(30, y + 33), 5.5f, new Color4(0, 0, 0, 1));
        Icone.Circulo(dc, new Vector2(30, y + 33), 4, color);
        p.Texto.Desenhar(dc, f.Name, new Vector2(44, y + 4), 13.5f, Cor.Branco, largura: L - 50);
        p.Texto.Desenhar(dc, f.Status, new Vector2(44, y + 22), 11.5f, color, FontWeight.Normal, largura: L - 50);
    }

    internal static void DrawAvatar(ID2D1DeviceContext dc, Friend f, float x, float y, float side, Action arrived)
    {
        if (Avatars.Get(f.Avatar, arrived) is { } px) GameArt.Draw(dc, px, x, y, side);
        else Icone.Circulo(dc, new Vector2(x + side / 2, y + side / 2), side / 2, new Color4(1, 1, 1, 0.12f));
    }

    // ------------------------------------------------------------------ the key

    private Cena KeyCard(Palco p)
    {
        var c = p.NovaCena(new Vector2(L, 118));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            p.Texto.Desenhar(dc, Lang.T("Amigos da Steam", "Steam friends"), Vector2.Zero, 15, Cor.Branco);
            p.Texto.Desenhar(dc, Lang.T("Chave da Web API (steamcommunity.com/dev/apikey)", "Web API key (steamcommunity.com/dev/apikey)"),
                new Vector2(0, 26), 12, Cor.Secundario, FontWeight.Normal, largura: L);
            Icone.Retangulo(dc, 0, 46, L, 36, 11, Cor.Fraco);
            p.Texto.DesenharCampo(dc, S.Entrada.Texto, S.Entrada.Cursor, true, new Vector2(12, 54), L - 24, 15, Cor.Branco,
                vazio: Lang.T("clique e cole a chave", "click and paste the key"), comCursor: _typing && S.Entrada.Aberta);
            string line = _notice ?? Lang.T("Enter salva · a chave vai para o Gerenciador de Credenciais", "Enter saves · the key goes to Credential Manager");
            p.Texto.Desenhar(dc, line, new Vector2(0, 92), 11.5f, _notice != null ? Cor.Vermelho : Cor.Secundario, FontWeight.Normal, largura: L);
        });
        c.Botoes.Add(new Botao { Area = new System.Drawing.RectangleF(0, 46, L, 36), Acao = Type });
        return c;
    }

    private void Type()
    {
        S.Ilha.Expandir(this);
        _typing = true;
        _notice = null;
        S.Entrada.Abrir("", _ => Redesenhar(), Save, fechou: () => { _typing = false; Redesenhar(); }, senha: true);
        Redesenhar();
    }

    private void Save(string text)
    {
        string key = text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[0-9A-Fa-f]{32}$"))
        {
            _notice = Lang.T("A chave tem 32 letras e números (0-9, A-F).", "The key is 32 letters and digits (0-9, A-F).");
            Redesenhar();
            return;
        }
        if (!Credenciais.Gravar(SteamWeb.Credential, key))
        {
            _notice = Lang.T("Não consegui guardar a chave no Gerenciador de Credenciais.", "Couldn't save the key in Credential Manager.");
            Redesenhar();
            return;
        }
        S.Entrada.Fechar();
        _typing = false;
        _m.KeyChanged();
        MudouTamanho();
    }

    /// <summary>The notice: the avatar, the name and what changed ("entrou", "está jogando CS2").</summary>
    public static Cena NoticeCard(Palco p, Servicos s, Friend f, string what, Color4 color)
    {
        var ts = p.Texto.Medir(f.Name, 15, FontWeight.Bold);
        var ws = p.Texto.Medir(what, 12.5f, FontWeight.Normal);
        float w = MathF.Min(MathF.Max(ts.X, ws.X) + 62, 360);
        var c = p.NovaCena(new Vector2(w, 44));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            DrawAvatar(dc, f, 2, 4, 36, () => { });
            p.Texto.Desenhar(dc, f.Name, new Vector2(48, 2), 15, Cor.Branco, FontWeight.Bold, largura: w - 50);
            p.Texto.Desenhar(dc, what, new Vector2(48, 23), 12.5f, color, FontWeight.Normal, largura: w - 50);
        });
        return c;
    }
}
