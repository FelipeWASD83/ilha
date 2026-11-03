using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ilha.Focus;

internal static class FocusTests
{
    /// <summary>The test starts from "off" (his Windows keeps Do Not Disturb on) and puts back what was there.</summary>
    private static void WriteForTest(string profile) => WindowsDnd.Write(profile);

    public static string Run()
    {
        var r = new StringBuilder();
        void Ok(string name, bool ok, string detail = "") => r.AppendLine((ok ? "ok      " : "FALHOU  ") + name + (detail.Length > 0 ? $" ({detail})" : ""));

        // Tuesday 03/11/2026
        var tue = new DateTime(2026, 11, 3);
        var work = Schedule.Parse("seg-sex 09:00-18:00");
        Ok("foco: horário seg-sex 09:00-18:00", work != null && work.Contains(tue.AddHours(9)) && work.Contains(tue.AddHours(17.99))
            && !work.Contains(tue.AddHours(18)) && !work.Contains(tue.AddHours(8.9)) && !work.Contains(new DateTime(2026, 11, 7, 10, 0, 0)));
        var night = Schedule.Parse("todos 23:30-07:00");
        Ok("foco: horário que passa da meia-noite", night != null && night.Contains(tue.AddHours(23.6)) && night.Contains(tue.AddDays(1).AddHours(6.9))
            && !night.Contains(tue.AddHours(7)) && !night.Contains(tue.AddHours(23)));
        var friNight = Schedule.Parse("sex 22:00-02:00");
        Ok("foco: noite de sexta vale até a madrugada de sábado e não a de sexta",
            friNight != null && friNight.Contains(new DateTime(2026, 11, 7, 1, 0, 0)) && !friNight.Contains(new DateTime(2026, 11, 6, 1, 0, 0)));
        var weekend = Schedule.Parse("sab,dom 10:00-12:00");
        Ok("foco: lista de dias e nomes em inglês", weekend != null && weekend.Contains(new DateTime(2026, 11, 8, 11, 0, 0))
            && Schedule.Parse("mon-fri 9:00-17:00")?.Contains(tue.AddHours(10)) == true && Schedule.Parse("10:00-11:00")?.Contains(tue.AddHours(10.5)) == true);
        Ok("foco: linha que não lê dá nulo", Schedule.Parse("qualquer coisa") == null && Schedule.Parse("seg 25:00-26:00") == null
            && Schedule.Parse("seg 10:00-10:00") == null && Schedule.Parse("") == null);

        var modes = new List<FocusMode>
        {
            new() { Id = "a", Horarios = { "seg-sex 09:00-18:00" } },
            new() { Id = "jogo", Apps = { "BF6.exe" } },
        };
        var none = new HashSet<string>();
        Ok("foco: automação pega o modo do horário", FocusRules.Wanted(modes, tue.AddHours(10), none)?.Id == "a"
            && FocusRules.Wanted(modes, tue.AddHours(20), none) == null);
        Ok("foco: app aberto liga o modo dele (o primeiro da lista ganha)", FocusRules.Wanted(modes, tue.AddHours(20), new HashSet<string> { "bf6" })?.Id == "jogo"
            && FocusRules.Wanted(modes, tue.AddHours(10), new HashSet<string> { "bf6" })?.Id == "a");

        var important = new FocusMode { Avisos = FocusAlerts.Important };
        var quiet = new FocusMode { Avisos = FocusAlerts.None };
        Ok("foco: avisos (todos, só importantes, nenhum; o aviso do próprio foco sempre passa)",
            FocusRules.Allows(new FocusMode(), "x", false) && !FocusRules.Allows(important, "x", false) && FocusRules.Allows(important, "x", true)
            && !FocusRules.Allows(quiet, "x", true) && FocusRules.Allows(quiet, "foco", false));
        Ok("foco: id novo a partir do nome", FocusMode.NewId("Leitura", new[] { "trabalho" }) == "leitura"
            && FocusMode.NewId("Trabalho", new[] { "trabalho" }) == "trabalho-2" && FocusMode.NewId("Ação!", Array.Empty<string>()) == "acao");
        var defaults = FocusMode.Defaults();
        Ok("foco: modos de fábrica (Não Perturbe primeiro, ícones e cores que existem)", defaults[0].Id == FocusMode.DoNotDisturbId
            && defaults.All(m => FocusIcons.Names.Any(n => n.Id == m.Icone) && FocusIcons.Colors.Any(c => c.Id == m.Cor))
            && defaults.All(m => m.Horarios.All(h => Schedule.Parse(h) != null)));
        var json = System.Text.Json.JsonSerializer.Serialize(new ConfigFoco(), Config.Json);
        var back = System.Text.Json.JsonSerializer.Deserialize<ConfigFoco>(json, Config.Json);
        Ok("foco: config vai e volta do JSON", back?.Modos.Count == defaults.Count && back.Modos[3].Horarios.SequenceEqual(defaults[3].Horarios));

        try
        {
            var png = Path.Combine(Path.GetTempPath(), "ilha-icones-foco.png");
            var icons = new List<Action<Vortice.Direct2D1.ID2D1DeviceContext, Vortice.Direct2D1.ID2D1Factory1, System.Drawing.RectangleF>>
            {
                (dc, f, box) => FocusIcons.BellOff(dc, f, box, Cor.Branco),
            };
            foreach (var (id, _, _) in FocusIcons.Names)
            {
                string name = id;
                icons.Add((dc, f, box) => FocusIcons.Draw(dc, f, box, name, FocusIcons.ColorOf(defaults.FirstOrDefault(m => m.Icone == name)?.Cor ?? "rosa")));
            }
            global::Ilha.Recorder.IconPreview.Render(png, icons.ToArray());
            Ok("foco: ícones desenhados em " + png, File.Exists(png));
        }
        catch (Exception e) { Ok("foco: ícones: " + e.Message, false); }

        try { Ok("foco: Não Perturbe do Windows lido pelo QuietHoursSettings", WindowsDnd.Read().StartsWith("Microsoft.QuietHoursProfile."), WindowsDnd.Read()); }
        catch (Exception e) { Ok("foco: QuietHoursSettings: " + e.Message, false); }
        // the real thing only on purpose: it switches Windows' Do Not Disturb off and back for a few seconds
        if (Environment.GetEnvironmentVariable("ILHA_TESTE_DND") == "1")
        {
            string before = WindowsDnd.Read();
            bool wasOn = before != WindowsDnd.Off;
            try
            {
                if (wasOn) WriteForTest(WindowsDnd.Off);
                bool started = WindowsDnd.Start(out string detail);
                Thread.Sleep(1500);
                string during = WindowsDnd.Read();
                WindowsDnd.Stop();
                Thread.Sleep(1500);
                string after = WindowsDnd.Read();
                Ok("foco: liga o Não Perturbe do Windows e devolve como estava", started && during == WindowsDnd.On && after == WindowsDnd.Off,
                    $"{detail}; durante {during}, depois {after}");
            }
            finally
            {
                if (WindowsDnd.Read() != before) WriteForTest(before);
                Ok("foco: o Não Perturbe ficou como estava antes do teste", WindowsDnd.Read() == before, before);
            }
        }
        else Ok("foco: teste de verdade do Não Perturbe pulado (ILHA_TESTE_DND=1 roda)", true);
        return r.ToString().TrimEnd();
    }
}
