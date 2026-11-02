using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.CapsLock;

/// <summary>
/// Caps Lock on the island: a short alert when it turns on (and off), for keyboards without the light (the Huntsman V3
/// HE TKL dropped its indicator lights). There is no system event for it without a keyboard hook, which anti-cheats
/// dislike, so the toggle state is read with GetKeyState every 200 ms (it follows the real state even from a thread
/// with no window; checked on 02/11/2026).
/// </summary>
[Modulo("capslock", "Caps Lock", English = "Caps Lock")]
internal sealed class ModuloCapsLock : Modulo
{
    private const int VK_CAPITAL = 0x14;
    private const int POLL_MS = 200;

    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);

    private ConfigCapsLock _cfg = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = POLL_MS };
    private readonly CapsWatch _watch = new();
    private Alerta? _shown;
    private bool _competitive, _stopped;

    public override Type? TipoConfig => typeof(ConfigCapsLock);

    private static bool IsOn() => (GetKeyState(VK_CAPITAL) & 1) != 0;

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigCapsLock>(cfg);
        _competitive = S.ContextoAtual().Competitivo;
        _watch.Feed(IsOn());
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    public override void ConfigMudou(JsonObject cfg) => _cfg = Ler<ConfigCapsLock>(cfg);

    public override void Contexto(Contexto ctx) => _competitive = ctx.Competitivo;

    public override void Parar()
    {
        _stopped = true;
        _poll.Stop();
        _poll.Dispose();
        if (_shown != null) S.Ilha.FecharAlerta(_shown);
        _shown = null;
    }

    private void Poll()
    {
        if (_stopped || _watch.Feed(IsOn()) is not bool on) return;
        Log.Escrever("caps lock " + (on ? "ligou" : "desligou"));
        if (_competitive || (!on && !_cfg.AoDesligar)) return;
        // pressing it again quickly replaces the alert instead of queueing a second one
        if (_shown != null) S.Ilha.FecharAlerta(_shown);
        var a = new Alerta
        {
            Tipo = "capslock",
            Som = "",
            // a reaction to his own key press: it shows even with Do Not Disturb on (his PC reads "alarms only" all the time)
            Importante = true,
            Segundos = on ? 1.4f : 1.1f,
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(W, H));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) => Draw(p, dc, t, on));
                return c;
            },
        };
        _shown = a;
        S.Ilha.Alertar(a);
    }

    private const float W = 236, H = 40;

    private static void Draw(Palco p, ID2D1DeviceContext dc, Vector2 t, bool on)
    {
        var center = new Vector2(t.Y / 2, t.Y / 2);
        Icone.Circulo(dc, center, 16, on ? Cor.Verde : Cor.Fraco);
        CapsIcon(dc, p.G.Fabrica, center, on ? Cor.Branco : Cor.Secundario);
        var name = "Caps Lock";
        var m = p.Texto.Medir(name, 15, FontWeight.SemiBold);
        p.Texto.Desenhar(dc, name, new Vector2(t.Y + 6, (t.Y - m.Y) / 2), 15, Cor.Branco, FontWeight.SemiBold);
        string state = on ? Lang.T("ligado", "on") : Lang.T("desligado", "off");
        var ms = p.Texto.Medir(state, 13, FontWeight.Medium);
        p.Texto.Desenhar(dc, state, new Vector2(t.X - ms.X - 6, (t.Y - ms.Y) / 2), 13, on ? Cor.Verde : Cor.Secundario, FontWeight.Medium);
    }

    /// <summary>The ⇪ key symbol: a hollow arrow pointing up with a bar under it.</summary>
    internal static void CapsIcon(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, Color4 cor)
    {
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            Vector2 P(float x, float y) => new(c.X + x, c.Y + y);
            s.BeginFigure(P(0, -8.5f), FigureBegin.Hollow);
            s.AddLine(P(7, -1.5f));
            s.AddLine(P(3.4f, -1.5f));
            s.AddLine(P(3.4f, 3.5f));
            s.AddLine(P(-3.4f, 3.5f));
            s.AddLine(P(-3.4f, -1.5f));
            s.AddLine(P(-7, -1.5f));
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using var b = dc.CreateSolidColorBrush(cor);
        using var stroke = f.CreateStrokeStyle(new StrokeStyleProperties { LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        dc.DrawGeometry(g, b, 1.7f, stroke);
        Icone.Linha(dc, new Vector2(c.X - 3.4f, c.Y + 7), new Vector2(c.X + 3.4f, c.Y + 7), 1.7f, cor, f);
    }

    public override string Autoteste()
    {
        var sb = new StringBuilder();
        void Check(bool ok, string name) => sb.AppendLine((ok ? "ok      " : "FALHOU  ") + name);
        var w = new CapsWatch();
        Check(w.Feed(true) == null, "a primeira leitura não avisa (a ilha abrindo com o Caps Lock já ligado)");
        Check(w.Feed(true) == null, "sem mudança, sem aviso");
        Check(w.Feed(false) == false && w.Feed(true) == true, "desligou e ligou avisam, cada um uma vez");
        Check(w.Feed(true) == null, "ligado de novo na leitura seguinte não repete");
        bool now = IsOn();
        Check(true, "lido agora: Caps Lock " + (now ? "ligado" : "desligado"));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>The toggle seen by polling: null while nothing changed (and on the first reading), else the new state.</summary>
internal sealed class CapsWatch
{
    private bool? _last;

    public bool? Feed(bool on)
    {
        var before = _last;
        _last = on;
        return before == null || before == on ? null : on;
    }
}

public sealed class ConfigCapsLock
{
    [Label("Avisar também quando desliga", "Also show when it turns off")]
    [JsonPropertyName("aoDesligar")] public bool AoDesligar { get; set; } = true;
}
