using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ilha.Claude;

/// <summary>
/// `Ilha.exe --ponte-claude ler | esforco SESSION LEVEL | ultracode SESSION on|off`: runs the bridge to the Claude
/// app with no island window and writes what came back to ~/.ilha/ponte-claude.txt. For checking the effort selector
/// against the real app after a Claude app update. "ler" only reads; the other two change that session. With
/// `--foco` the probe takes the foreground first, like the island does when it is clicked (the Claude app is then
/// in the background, the real situation).
/// </summary>
internal static class BridgeProbe
{
    public static int Run(string[] args)
    {
        var o = new StringBuilder();
        int code = 1;
        System.Windows.Forms.Form? focus = null;
        try
        {
            if (args.Contains("--foco"))
            {
                args = args.Where(a => a != "--foco").ToArray();
                focus = new System.Windows.Forms.Form
                {
                    ShowInTaskbar = false, FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000),
                    Size = new System.Drawing.Size(1, 1),
                };
                focus.Show();
                focus.Activate();
                Nativo.SetForegroundWindow(focus.Handle);
                System.Windows.Forms.Application.DoEvents();
                o.AppendLine("foreground is the probe: " + (Nativo.GetForegroundWindow() == focus.Handle));
            }
            string action = args.Length > 0 ? args[0] : "ler";
            using var bridge = new PonteClaudeApp();
            RespostaPonte? r = null;
            using var done = new ManualResetEventSlim();
            void Got(RespostaPonte x) { r = x; done.Set(); }
            switch (action)
            {
                case "esforco" when args.Length >= 3 && int.TryParse(args[2], out int level):
                    bridge.TrocarEsforco(args[1], level, Got);
                    break;
                case "permissoes" when args.Length >= 2:
                    bridge.Permissao(args[1], args.Length >= 3 ? args[2] : null, Got);
                    break;
                case "ultracode" when args.Length >= 3:
                    bridge.DefinirUltracode(args[1], args[2] is "on" or "ligar" or "1", Got);
                    break;
                default:
                    bridge.Estado(Got);
                    break;
            }
            var until = Environment.TickCount64 + 30000;
            while (!done.Wait(50) && Environment.TickCount64 < until) System.Windows.Forms.Application.DoEvents();
            if (!done.IsSet) o.AppendLine("no answer in 30 s");
            else if (r != null)
            {
                o.AppendLine($"ok={r.Ok} error={r.Erro}");
                if (r.Painel is { } p) o.AppendLine($"panel: {Line(p)}");
                foreach (var each in r.Paineis) o.AppendLine($"panel: {Line(each)}");
                foreach (var opt in r.Opcoes ?? new()) o.AppendLine("option: " + opt);
                code = r.Ok ? 0 : 1;
            }
        }
        catch (Exception e) { o.AppendLine("exception: " + e); }
        finally { focus?.Dispose(); }
        Directory.CreateDirectory(Config.Pasta);
        File.WriteAllText(Path.Combine(Config.Pasta, "ponte-claude.txt"), o.ToString());
        Console.Write(o.ToString());
        return code;
    }

    private static string Line(PainelLido p) =>
        $"name={p.Nome} model={p.Modelo} effort={p.Esforco} level={p.Nivel} ultracode={p.Ultracode} permission={p.Permissao} focus={p.Foco}";
}
