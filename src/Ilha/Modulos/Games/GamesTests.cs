using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ilha.Games;

internal sealed partial class GamesModule
{
    private static StringBuilder _report = new();

    private static void Ok(string name, bool ok, string detail = "") =>
        _report.AppendLine((ok ? "ok      " : "FALHOU  ") + name + (detail.Length > 0 ? $" ({detail})" : ""));

    public override string Autoteste()
    {
        _report = new StringBuilder();
        TestCs2();
        TestRocketLeague();
        TestBf6Logic();
        TestBf6Photos();
        TestSetupAndHistory();
        TestCelebrationWords();
        TestArt();
        TestPlaytimeAndAce();
        TestTrophies();
        return _report.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ CS2

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static string Cs2Payload(string me, string player, string team, int k, int d, string phase, int round, int ct, int t,
        string roundPhase = "live", string win = "", string mode = "competitive") =>
        $$$"""
        {"provider":{"name":"Counter-Strike: Global Offensive","appid":730,"steamid":"{{{me}}}"},
         "map":{"mode":"{{{mode}}}","name":"de_dust2","phase":"{{{phase}}}","round":{{{round}}},"team_ct":{"score":{{{ct}}}},"team_t":{"score":{{{t}}}}},
         "round":{"phase":"{{{roundPhase}}}"{{{(win.Length > 0 ? ",\"win_team\":\"" + win + "\"" : "")}}}},
         "player":{"steamid":"{{{player}}}","team":"{{{team}}}","activity":"playing","match_stats":{"kills":{{{k}}},"deaths":{{{d}}},"assists":1,"mvps":2,"score":30}},
         "phase_countdowns":{"phase":"live","phase_ends_in":"95.4"},
         "auth":{"token":"ilha"}}
        """;

    private static void TestCs2()
    {
        var s = new Cs2State();
        var now = new DateTime(2026, 11, 3, 12, 0, 0);
        var menu = Json("""{"provider":{"steamid":"1"},"player":{"steamid":"1","activity":"menu"}}""");
        Ok("cs2: no menu não tem partida", s.Apply(menu, now).Count == 0 && s.Current == null);
        s.Apply(Json(Cs2Payload("1", "1", "T", 3, 1, "warmup", 0, 0, 0)), now);
        var x = s.Apply(Json(Cs2Payload("1", "1", "T", 0, 0, "live", 0, 0, 0)), now);
        Ok("cs2: o fim do aquecimento zera as contas sem contar abate", x.Count == 0 && s.Current?.Kills == 0, string.Join(",", x));
        x = s.Apply(Json(Cs2Payload("1", "1", "T", 2, 0, "live", 0, 0, 0)), now);
        Ok("cs2: dois abates viram dois momentos", x.Count(m => m.Kind == MomentKind.Kill) == 2);
        x = s.Apply(Json(Cs2Payload("1", "99", "CT", 9, 9, "live", 1, 0, 1, "over", "T")), now);
        Ok("cs2: rodada ganha (pelo nosso lado, mesmo vendo outro jogador)", x.Any(m => m.Kind == MomentKind.Round && m.Ours) && s.Current?.Kills == 2);
        var m1 = s.Current!;
        Ok("cs2: nosso lado é o TR e o placar segue o lado", m1.Us == 1 && m1.Them == 0 && m1.UsLabel == Lang.T("TR", "T"), $"{m1.Us}:{m1.Them} {m1.UsLabel}");
        Ok("cs2: modo, mapa e tempo de partida contando desde o fim do aquecimento", m1.Mode == Lang.T("Competitivo", "Competitive") && m1.Map == "Dust II"
            && m1.ClockUp && Math.Abs((m1.ClockAt(now.AddSeconds(5)) ?? 0) - 5) < 0.01, $"{m1.Mode} {m1.Map} {m1.ClockAt(now.AddSeconds(5))}");
        x = s.Apply(Json(Cs2Payload("1", "1", "T", 20, 9, "gameover", 24, 11, 13)), now);
        Ok("cs2: fim de jogo com vitória", x.Any(m => m.Kind == MomentKind.End && m.Outcome == Outcome.Win) && s.Current?.Outcome == Outcome.Win);
        Ok("cs2: abates que chegam todos de uma vez (2 para 20) não animam", x.All(m => m.Kind != MomentKind.Kill) && s.Current?.Kills == 20);
        Ok("cs2: o fim só conta uma vez", s.Apply(Json(Cs2Payload("1", "1", "T", 20, 9, "gameover", 24, 11, 13)), now).All(m => m.Kind != MomentKind.End));
        s.Apply(menu, now);
        Ok("cs2: voltar ao menu encerra", s.Current == null);
        var dm = new Cs2State();
        dm.Apply(Json(Cs2Payload("1", "1", "CT", 0, 0, "live", 0, 0, 0, mode: "deathmatch")), now);
        var e = dm.Apply(Json(Cs2Payload("1", "1", "CT", 10, 3, "gameover", 0, 0, 0, mode: "deathmatch")), now);
        Ok("cs2: mata-mata termina sem vitória nem derrota", e.Any(m => m.Kind == MomentKind.End && m.Outcome == null));
        Ok("cs2: nomes de modo e mapa", Cs2State.ModeName("scrimcomp2v2") == "Wingman" && Cs2State.MapName("cs_office") == "Office" && Cs2State.MapName("de_newmap") == "Newmap");
    }

    // ------------------------------------------------------------------ Rocket League

    private static string RlState(int blue, int orange, int myDemos, int secs, bool target = true, int playlist = 11, bool ot = false)
    {
        string data = $$$"""
            {"MatchGuid":"A1","Players":[
              {"Name":"Player1","TeamNum":0,"Goals":1,"Assists":0,"Saves":2,"Shots":3,"Demos":{{{myDemos}}},"Score":250},
              {"Name":"Mate","TeamNum":0,"Goals":0,"Demos":0},{"Name":"Rival","TeamNum":1,"Goals":0,"Demos":0},{"Name":"Rival2","TeamNum":1,"Demos":0}],
             "Game":{"Teams":[{"Name":"Azul","TeamNum":0,"Score":{{{blue}}}},{"Name":"Laranja","TeamNum":1,"Score":{{{orange}}}}],
              "PlaylistId":{{{playlist}}},"TimeSeconds":{{{secs}}},"bOvertime":{{{(ot ? "true" : "false")}}},"bReplay":false,"bHasWinner":false,
              "Arena":"Stadium_P","bHasTarget":{{{(target ? "true" : "false")}}},"Target":{"Name":"Player1","Shortcut":1,"TeamNum":0} } }
            """;
        // the game sends Data as a JSON string inside the envelope (seen on 03/11/2026)
        return JsonSerializer.Serialize(new { Event = "UpdateState", Data = data });
    }

    private static void TestRocketLeague()
    {
        var s = new RocketLeagueState();
        var now = DateTime.Now;
        var training = JsonSerializer.Serialize(new { Event = "UpdateState", Data = """{"Players":[{"Name":"Player1","TeamNum":0}],"Game":{"PlaylistId":9,"Teams":[],"bHasTarget":true,"Target":{"Name":"Player1","TeamNum":0}}}""" });
        s.ApplyMessage(training, now);
        Ok("rl: treino livre não vira partida", s.Current == null);
        s.ApplyMessage(RlState(0, 0, 0, 300), now);
        var m = s.Current;
        Ok("rl: partida com modo, arena e nosso time", m != null && m.Mode == Lang.T("2v2 ranqueado", "Ranked 2v2") && m.Map == "DFH Stadium" && m.UsLabel == "Azul",
            $"{m?.Mode} {m?.Map} {m?.UsLabel}");
        var x = s.ApplyMessage(RlState(1, 0, 1, 250), now);
        Ok("rl: gol nosso e demolição", x.Any(k => k.Kind == MomentKind.Goal && k.Ours) && x.Any(k => k.Kind == MomentKind.Kill), string.Join(",", x));
        x = s.ApplyMessage(RlState(1, 1, 1, 200), now);
        Ok("rl: gol deles", x.Count == 1 && x[0].Kind == MomentKind.Goal && !x[0].Ours);
        Ok("rl: placar e números", s.Current?.Us == 1 && s.Current?.Them == 1 && s.Current.Extras.Any(e => e.Value == "2"));
        string Feed(string who, string ev) => JsonSerializer.Serialize(new { Event = "StatfeedEvent",
            Data = JsonSerializer.Serialize(new { EventName = ev, Type = "Save", MainTarget = new { Name = who, TeamNum = 0 } }) });
        var save = s.ApplyMessage(Feed("Player1", "EpicSave"), now);
        var theirs = s.ApplyMessage(Feed("Outro", "EpicSave"), now);
        var plain = s.ApplyMessage(Feed("Player1", "Save"), now);
        Ok("rl: defesa épica nossa conta e anima, a dos outros e a defesa comum não",
            save.Count == 1 && save[0].Kind == MomentKind.EpicSave && theirs.Count == 0 && plain.Count == 0
            && s.Current!.Extras.Any(e => e.Name == Lang.T("Def. épicas", "Epic saves") && e.Value == "1"),
            string.Join(", ", s.Current?.Extras.Select(e => $"{e.Name} {e.Value}") ?? Array.Empty<string>()));
        s.ApplyMessage(RlState(2, 1, 1, 12, ot: true), now);
        Ok("rl: prorrogação conta para cima", s.Current?.ClockUp == true && s.Current.Stage == Lang.T("Prorrogação", "Overtime"));
        x = s.ApplyMessage("""{"Event":"MatchEnded","Data":"{\"MatchGuid\":\"A1\",\"WinnerTeamNum\":0}"}""", now);
        Ok("rl: vitória no fim", x.Any(k => k.Kind == MomentKind.End && k.Outcome == Outcome.Win));
        Ok("rl: MVP é a maior pontuação do time que ganhou (e o nosso jogador foi)", s.Current?.Mvp == true
            && RocketLeagueState.Mvp(new[] { ("A", 0, 300), ("B", 0, 500), ("C", 1, 900) }, 0) == "B"
            && RocketLeagueState.Mvp(new[] { ("A", 0, 300) }, 1) == null);
        string dir = Path.Combine(Path.GetTempPath(), "ilha-historico-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "partidas.json");
            // a file of 3.7.x: no kills, deaths or MVP
            File.WriteAllText(file, """[{"game":"cs2","mode":"Rush","map":"Rush 001","outcome":"win","us":6,"them":2,"end":"2026-11-03T15:38:48-03:00"}]""");
            var h = new History(file);
            h.Add(new Match { Game = "rl", Mode = "Duplas", Us = 3, Them = 1, Outcome = Outcome.Win, Kills = 2, Deaths = 1, Mvp = true });
            var again = new History(file).Recent(5);
            Ok("jogos: histórico antigo continua lendo e a partida nova guarda abates, mortes e MVP, a mais nova primeiro",
                again.Count == 2 && again[0].Game == "rl" && again[0].Kills == 2 && again[0].Deaths == 1 && again[0].Mvp == true
                && again[1].Game == "cs2" && again[1].Kills == null && again[1].Read == Outcome.Win);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        s.ApplyMessage("""{"Event":"MatchDestroyed","Data":"{\"MatchGuid\":\"A1\"}"}""", now);
        Ok("rl: sair da partida encerra", s.Current == null);
        var buf = new StringBuilder("{\"Event\":\"A\",\"Data\":\"{\\\"x\\\":\\\"}\\\"}\"}\n{\"Event\":\"B\",\"Da");
        var parts = RocketLeagueState.Split(buf);
        Ok("rl: separa as mensagens e guarda o pedaço incompleto", parts.Count == 1 && parts[0].Contains("\"A\"") && buf.ToString().TrimStart().StartsWith("{\"Event\":\"B\""));
        Ok("rl: nomes de playlist e arena", RocketLeagueState.PlaylistName(63) == Lang.T("Heatseeker ranqueado", "Ranked Heatseeker")
            && RocketLeagueState.PlaylistName(13) == Lang.T("3v3 ranqueado", "Ranked 3v3") && RocketLeagueState.PlaylistName(4) == Lang.T("4v4 casual", "Casual 4v4")
            && RocketLeagueState.PlaylistName(28) == Lang.T("Rixa ranqueada", "Ranked Rumble") && RocketLeagueState.PlaylistName(81) == ""
            && RocketLeagueState.ArenaName("cs_p") == "Champions Field" && RocketLeagueState.ArenaName("EuroStadium_Night_P") == "Mannfield");
    }

    // ------------------------------------------------------------------ BF6

    private static Line L(params string[] words) => new(words.Select((w, i) => new Word(w, i * 100, 0, 90, 20)).ToList());

    private static void TestBf6Logic()
    {
        Ok("bf6: modo da tela de carregamento", Bf6Reader.ModeFrom(new[] { L("CONQUISTA") }) == "Conquista" && Bf6Reader.ModeFrom(new[] { L("MEM", "10600", "CONQUISTA") }) == "Conquista"
            && Bf6Reader.ModeFrom(new[] { L("lixo") }) == null);
        Ok("bf6: mapa é a linha maiúscula mais alta", Bf6Reader.MapFrom(new[] { L("LAGO", "GROOM") }) == "Lago Groom");
        Ok("bf6: tela de vitória e derrota", Bf6Reader.EndFrom(new[] { L("VITÓRIA") }) == Outcome.Win && Bf6Reader.EndFrom(new[] { L("DEFEAT") }) == Outcome.Loss);
        Ok("bf6: nome com erro de OCR ainda é o nome", Bf6Reader.SameName("Jogador_um", "Jogador_um") && Bf6Reader.SameName("JogadorI_um", "Jogador_um") && Bf6Reader.SameName("Jogad0r_um", "Jogador_um")
            && Bf6Reader.SameName("Rivalsegundol@", "Rivalsegundo10") && !Bf6Reader.SameName("Outro_time23", "Jogador_um"));

        var s = new Bf6State();
        var t0 = new DateTime(2026, 11, 3, 12, 56, 0);
        var none = new List<Line>();
        s.Apply(new Bf6Frame(null, null, "Conquista", "Lago Groom", none, null) { Faction = "nato" }, t0, "Jogador_um");
        s.Apply(new Bf6Frame(894, null, null, null, none, null), t0.AddSeconds(1), "Jogador_um");
        Ok("bf6: um número lido uma vez só ainda não vale", s.Current?.Us == null && s.Current?.Mode == "Conquista" && s.Current.Map == "Lago Groom");
        s.Apply(new Bf6Frame(894, 268, null, null, none, null), t0.AddSeconds(2), "Jogador_um");
        s.Apply(new Bf6Frame(984, 268, null, null, none, null), t0.AddSeconds(3), "Jogador_um");
        Ok("bf6: dois iguais seguidos valem, um erro solto não", s.Current?.Us == 894 && s.Current?.Them == 268, $"{s.Current?.Us}:{s.Current?.Them}");
        var k1 = s.Apply(new Bf6Frame(880, 260, null, null, new List<Line> { L("Jogador_um", "Rival", "Alvo"), L("Colega23", "Rival_dois06") }, null), t0.AddSeconds(4), "Jogador_um");
        var k2 = s.Apply(new Bf6Frame(880, 260, null, null, new List<Line> { L("JogadorI_um", "Rival", "Alv0") }, null), t0.AddSeconds(5), "Jogador_um");
        var d1 = s.Apply(new Bf6Frame(880, 260, null, null, new List<Line> { L("Rival99", "Jogador_um") }, null), t0.AddSeconds(6), "Jogador_um");
        Ok("bf6: abate no feed conta uma vez mesmo lido diferente", k1.Count(m => m.Kind == MomentKind.Kill) == 1 && k2.Count == 0 && s.Current?.Kills == 1);
        Ok("bf6: morte no feed", d1.Count(m => m.Kind == MomentKind.Death) == 1 && s.Current?.Deaths == 1);
        for (int i = 0; i < 80; i++) s.Apply(new Bf6Frame(30 + (i < 1 ? 1 : 0), 0 + (i < 1 ? 1 : 0), null, null, none, null), t0.AddSeconds(10 + i), "Jogador_um");
        var end = s.Apply(new Bf6Frame(null, null, null, null, none, null), t0.AddSeconds(120), "Jogador_um");
        Ok("bf6: o placar some no fim e quem estava na frente ganhou", end.Any(m => m.Kind == MomentKind.End && m.Outcome == Outcome.Win) && s.Current == null, string.Join(",", end));

        // the menu (03/11/2026): numbers that never move where the tickets go, and the career "VITÓRIAS"
        Ok("bf6: \"VITÓRIAS\" do menu não é tela de vitória", Bf6Reader.EndFrom(new[] { L("VITÓRIAS", "128") }) == null
            && Bf6Reader.EndFrom(new[] { L("DERROTAS") }) == null && Bf6Reader.EndFrom(new[] { L("VITÓRIA") }) == Outcome.Win);
        var menu = new Bf6State();
        var anyMoment = new List<Moment>();
        for (int i = 0; i < 240; i++)
            anyMoment.AddRange(menu.Apply(new Bf6Frame(12, 34, i % 30 == 0 ? "Conquista" : null, null, none, i % 5 == 0 ? Outcome.Win : null),
                t0.AddSeconds(i * 0.7), "Jogador_um"));
        Ok("bf6: no menu não abre partida nem conta vitória", menu.Current == null && anyMoment.Count == 0, string.Join(",", anyMoment));
        var late = new Bf6State();
        for (int i = 0; i < 40; i++) late.Apply(new Bf6Frame(700 - i / 4, 650 - i / 6, null, null, none, null), t0.AddSeconds(i), "Jogador_um");
        bool before = late.Current == null;
        for (int i = 40; i < 60; i++) late.Apply(new Bf6Frame(700 - i / 4, 650 - i / 6, null, null, none, null), t0.AddSeconds(i), "Jogador_um");
        Ok("bf6: entrou sem ver a tela de carregamento, vale depois de 45 s com o placar andando", before && late.Current != null);
        var once = late.Apply(new Bf6Frame(null, null, null, null, none, Outcome.Loss), t0.AddSeconds(61), "Jogador_um");
        var twice = late.Apply(new Bf6Frame(null, null, null, null, none, Outcome.Loss), t0.AddSeconds(64), "Jogador_um");
        Ok("bf6: tela de fim só vale lida duas vezes", !once.Any(m => m.Kind == MomentKind.End) && twice.Any(m => m.Kind == MomentKind.End && m.Outcome == Outcome.Loss));
    }

    /// <summary>
    /// The real OCR on photos of a real match (ILHA_TESTE_BF6 = a folder with bf6-HHMMSS.png at 3440x1440; not in the
    /// repository: they show other players' names).
    /// </summary>
    private static void TestBf6Photos()
    {
        string? dir = Environment.GetEnvironmentVariable("ILHA_TESTE_BF6");
        if (dir == null || !Directory.Exists(dir)) { Ok("bf6: fotos de partida pulado (ILHA_TESTE_BF6 sem pasta)", true); return; }
        // whose kills to look for: ILHA_TESTE_BF6_NOME, else the Steam name (the photos are his)
        string name = Environment.GetEnvironmentVariable("ILHA_TESTE_BF6_NOME") ?? GameSetup.SteamName() ?? "";
        ScreenText ocr;
        try { ocr = new ScreenText(); }
        catch (Exception e) { Ok("bf6: OCR do Windows", false, e.Message); return; }
        Ok("bf6: OCR do Windows em " + ocr.Language, true);
        var expected = new Dictionary<string, (int? Us, int? Them)>
        {
            ["bf6-125652"] = (881, 268), ["bf6-125703"] = (874, 265), ["bf6-125714"] = (866, 262), ["bf6-125736"] = (847, 255),
            ["bf6-125758"] = (828, 250), ["bf6-125810"] = (820, 248),
        };
        int right = 0, wrong = 0, read = 0;
        string? mode = null, map = null, faction = null;
        (byte[] Px, int W, int H)? emblem = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        var feedKills = new List<string>();
        foreach (var f in Directory.GetFiles(dir, "bf6-*.png").OrderBy(x => x))
        {
            var (img, w, h) = ScreenText.Load(f);
            byte[]? Grab(Rectangle r) => r.Right <= w && r.Bottom <= h && r.X >= 0 && r.Y >= 0 ? ScreenText.Crop(img, w, r) : null;
            var fr = Bf6Reader.Read(Grab, new Rectangle(0, 0, w, h), ocr, slow: true);
            frames++;
            mode ??= fr.Mode;
            map ??= fr.Map;
            faction ??= fr.Faction;
            emblem ??= fr.Emblem;
            foreach (var l in fr.Feed) if (l.Words.Count > 1 && Bf6Reader.SameName(l.Words[0].Text.Trim('>', '▶'), name)) feedKills.Add(l.Text);
            string stem = Path.GetFileNameWithoutExtension(f);
            if (fr.Us != null) read++;
            if (!expected.TryGetValue(stem, out var e)) continue;
            if (fr.Us == e.Us && fr.Them == e.Them) right++;
            else if (fr.Us != null && fr.Us != e.Us || fr.Them != null && fr.Them != e.Them) wrong++;
        }
        long ms = sw.ElapsedMilliseconds;
        // the cost of one regular look on the real screen (no HUD there: the worst case, every mask is tried)
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var band = Bf6Reader.Band(screen);
        var look = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            var copy = ScreenText.Grab(band);
            byte[]? Live(Rectangle r) => copy == null ? null : ScreenText.Crop(copy, band.Width, new Rectangle(r.X - band.X, r.Y - band.Y, r.Width, r.Height));
            Bf6Reader.Read(Live, screen, ocr, slow: false);
        }
        Ok($"bf6: uma olhada na tela leva {look.ElapsedMilliseconds / 10} ms (sem placar na tela, o pior caso; uma a cada 700 ms)", true);
        Ok("bf6: modo e mapa lidos nas fotos", mode == "Conquista" && map == "Lago Groom", $"{mode}, {map}");
        // the emblem: a light symbol on a dark square, so the middle is brighter than the corners
        bool emblemOk = false;
        if (emblem is { } em)
        {
            float Lum(int x, int y) { int i = (y * em.W + x) * 4; return (em.Px[i] + em.Px[i + 1] + em.Px[i + 2]) / 3f; }
            float mid = 0, corner = 0;
            for (int d = -3; d <= 3; d++) { mid += Lum(em.W / 2 + d, em.H / 2 - 12); corner += Lum(5 + d + 3, 5); }
            emblemOk = mid > corner + 60;
        }
        Ok("bf6: facção e emblema da tela de carregamento", faction == "nato" && emblemOk, $"{faction}, emblema {(emblem == null ? "nenhum" : $"{emblem.Value.W}x{emblem.Value.H}")}");
        if (emblem is { } keep && Environment.GetEnvironmentVariable("ILHA_TESTE_EMBLEMA") is { } outFile)
        {
            using var bmp = new Bitmap(keep.W, keep.H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (int y = 0; y < keep.H; y++) for (int x = 0; x < keep.W; x++) { int i = (y * keep.W + x) * 4; bmp.SetPixel(x, y, Color.FromArgb(255, keep.Px[i + 2], keep.Px[i + 1], keep.Px[i])); }
            bmp.Save(outFile);
        }
        Ok("bf6: placar certo nas fotos conferidas", right >= 4 && wrong == 0, $"{right} de {expected.Count} certas, {wrong} erradas");
        Ok("bf6: abate dele lido no feed", feedKills.Count > 0, string.Join(" | ", feedKills));
        Ok($"bf6: {frames} fotos em {ms} ms ({(frames > 0 ? ms / frames : 0)} ms cada, com a leitura lenta)", true);
    }

    // ------------------------------------------------------------------ setup, history, words

    private static void TestSetupAndHistory()
    {
        string cfg = GameSetup.Cs2Cfg(47681);
        Ok("cs2: o arquivo da integração tem a porta, o token e o relógio", cfg.Contains("http://127.0.0.1:47681") && cfg.Contains("\"token\" \"ilha\"") && cfg.Contains("phase_countdowns"));
        string ini = "[TAGame.MatchStatsExporter_TA]\r\nPort=49123\r\nPacketSendRate=0\r\n";
        Ok("rl: liga a Stats API só quando está desligada", GameSetup.WithRate(ini)?.Contains("PacketSendRate=10") == true
            && GameSetup.WithRate("PacketSendRate=30") == null && GameSetup.WithRate("Port=1")?.Contains("[TAGame.MatchStatsExporter_TA]") == true);

        string file = Path.Combine(Path.GetTempPath(), $"ilha-teste-partidas-{Environment.ProcessId}.json");
        try
        {
            var h = new History(file);
            foreach (var o in new[] { Outcome.Win, Outcome.Loss, Outcome.Win, Outcome.Win, Outcome.Draw, Outcome.Loss })
                h.Add(new Match { Game = "cs2", Us = 1, Them = 0, Outcome = o });
            h.Add(new Match { Game = "rl", Outcome = Outcome.Win });
            h.Add(new Match { Game = "rl" });
            var again = new History(file);
            var last = again.Last("cs2");
            Ok("histórico: as últimas 5 de cada jogo, da mais velha para a mais nova", last.Count == 5 && last[0] == Outcome.Loss && last[^1] == Outcome.Loss
                && again.Last("rl").Count == 1, string.Join(",", last));
        }
        finally { try { File.Delete(file); } catch (Exception) { } }
    }

    private static void TestPlaytimeAndAce()
    {
        string vdf = "\"UserLocalConfigStore\"\n{\n \"Software\"\n {\n  \"Valve\"\n  {\n   \"Steam\"\n   {\n    \"apps\"\n    {\n" +
                     "     \"730\"\n     {\n      \"LastPlayed\"\t\t\"1\"\n      \"Playtime\"\t\t\"15822\"\n     }\n" +
                     "     \"440\"\n     {\n      \"Playtime\"\t\t\"0\"\n     }\n    }\n   }\n  }\n }\n \"other\"\n {\n  \"Playtime\" \"9\"\n }\n}\n";
        var read = Playtime.Read(vdf);
        Ok("horas: Playtime de cada app da Steam (minutos), só dentro de apps", read.Count == 1 && read[0] == (730, 15822), string.Join(",", read));
        // the session going on now counts on top of the saved hours, until Steam writes its own bigger number
        var steamNow = new Dictionary<int, int> { [730] = 599 };
        var live = new Dictionary<int, (int Baseline, int Extra)> { [730] = (599, 3) };
        bool during = Playtime.WithLive(steamNow, live, 730)[730] == 602;
        steamNow[730] = 603;
        bool written = Playtime.WithLive(steamNow, live, 0)[730] == 603 && !live.ContainsKey(730);
        var other = new Dictionary<int, (int Baseline, int Extra)> { [570] = (0, 2) };
        bool fresh = Playtime.WithLive(new Dictionary<int, int>(), other, 570)[570] == 2;
        Ok("horas: sessão ao vivo soma por cima da Steam e sai quando a Steam grava (conquista durante o jogo)", during && written && fresh);
        Ok("horas: jogo da Steam rodando agora (registro)", true, Playtime.RunningSteamApp() is int run && run > 0 ? $"app {run}" : "nenhum");
        Ok("horas: formato", GamesOverview.Hours(15822) == (263.7).ToString("0", Lang.Culture) + " h" && GamesOverview.Hours(90) == 1.5.ToString("0.0", Lang.Culture) + " h" && GamesOverview.Hours(0) == "–",
            GamesOverview.Hours(15822));
        var real = Playtime.Steam();
        Ok("horas: arquivo da Steam deste PC lido", GameSetup.SteamFolder() == null || real.Count > 0, $"{real.Count} jogos, CS2 {(real.TryGetValue(730, out var cs) ? GamesOverview.Hours(cs) : "–")}");

        var s = new Cs2State();
        var now = DateTime.Now;
        string Payload(int roundKills, int round, string mode = "competitive") =>
            "{\"provider\":{\"steamid\":\"1\"},\"map\":{\"mode\":\"" + mode + "\",\"name\":\"de_inferno\",\"phase\":\"live\",\"round\":" + round +
            ",\"team_ct\":{\"score\":0},\"team_t\":{\"score\":0}},\"player\":{\"steamid\":\"1\",\"team\":\"CT\",\"state\":{\"round_kills\":" + roundKills +
            "},\"match_stats\":{\"kills\":" + roundKills + ",\"deaths\":0}}}";
        s.Apply(Json(Payload(0, 3)), now);
        var four = s.Apply(Json(Payload(4, 3)), now);
        var five = s.Apply(Json(Payload(5, 3)), now);
        var again = s.Apply(Json(Payload(5, 3)), now);
        Ok("ace: cinco na rodada no competitivo, uma vez só", four.All(m => m.Kind != MomentKind.Ace) && five.Count(m => m.Kind == MomentKind.Ace) == 1 && again.All(m => m.Kind != MomentKind.Ace));
        var w = new Cs2State();
        w.Apply(Json(Payload(0, 1, "scrimcomp2v2")), now);
        Ok("ace: dois no Wingman; casual não tem", w.Apply(Json(Payload(2, 1, "scrimcomp2v2")), now).Any(m => m.Kind == MomentKind.Ace));
        var cas = new Cs2State();
        cas.Apply(Json(Payload(0, 1, "casual")), now);
        Ok("ace: no casual cinco não é ace", cas.Apply(Json(Payload(5, 1, "casual")), now).All(m => m.Kind != MomentKind.Ace));
    }

    private static void TestTrophies()
    {
        var games = new[] { new GameHours("cs2", "Counter-Strike 2", 264 * 60, 730), new GameHours("x", "X", 9 * 60, null), new GameHours("y", "Y", 60 * 60, null) };
        var earned = Trophies.Earned(games);
        Ok("conquistas: por jogo nas faixas e o total", earned.Count(t => t.GameKey == "cs2") == 4 && earned.Count(t => t.GameKey == "y") == 2
            && !earned.Any(t => t.GameKey == "x") && earned.Count(t => t.Total) == 1 && earned.Any(t => t.Id == "total:100"),
            string.Join(",", earned.Select(t => t.Id)));
        Ok("conquistas: próxima meta e quanto falta", Trophies.Next(264 * 60) is { Hours: 500 } n && n.Progress > 0.05f && n.Progress < 0.1f
            && Trophies.Next(1001 * 60) == null);
        string file = Path.Combine(Path.GetTempPath(), $"ilha-teste-conquistas-{Environment.ProcessId}.json");
        try
        {
            var store = new TrophyStore(file);
            var first = store.Update(earned);
            var more = Trophies.Earned(games.Append(new GameHours("z", "Z", 11 * 60, null)));
            var second = new TrophyStore(file).Update(more);
            Ok("conquistas: a primeira vez só anota (sem chuva de avisos), depois avisa só a nova", first.Count == 0 && second.Count == 1 && second[0].Id == "jogo:z:10",
                string.Join(",", second.Select(t => t.Id)));
        }
        finally { try { File.Delete(file); } catch (Exception) { } }
    }

    private static void TestArt()
    {
        string fitted = GameArt.Fit("<svg width=\"256px\" height=\"256px\" viewBox=\"0 0 256 256\"><g width=\"3\"/></svg>");
        Ok("logos: o SVG perde o tamanho fixo só na raiz", fitted.StartsWith("<svg width=\"100%\" height=\"100%\"") && fitted.Contains("<g width=\"3\"/>"));
        Ok("logos: proporção pelo viewBox", Math.Abs(GameArt.Aspect("<svg viewBox=\"0 0 386 65\">") - 386f / 65) < 0.01 && GameArt.Aspect("<svg>") == 1);
        if (GameSetup.SteamAppFolder(730) == null) Ok("logos: CS2 não instalado, pulado", true);
        else
        {
            var svgs = new[] { GameArt.CtLogo, GameArt.TLogo, GameArt.Premier, GameArt.Rush }.Select(GameArt.Cs2Svg).ToList();
            Ok("logos: escudos do CT e do TR e os selos do Premier e do Rush lidos dos arquivos do CS2", svgs.All(s => s != null && s.StartsWith("<svg") && s.EndsWith("</svg>")),
                string.Join(", ", svgs.Select(s => s?.Length.ToString() ?? "nada")));
            try
            {
                var png = Path.Combine(Path.GetTempPath(), "ilha-selos-cs2.png");
                global::Ilha.Recorder.IconPreview.Render(png, new Action<Vortice.Direct2D1.ID2D1DeviceContext, Vortice.Direct2D1.ID2D1Factory1, System.Drawing.RectangleF>[]
                {
                    (dc, f, box) => Marks.Badge(dc, "premier", box.X - 8, box.Y + 4, 16),
                    (dc, f, box) => Marks.Badge(dc, "rush", box.X, box.Y, 24),
                });
                Ok("logos: selos desenhados em " + png, File.Exists(png));
            }
            catch (Exception e) { Ok("logos: selos: " + e.Message, false); }
        }
        foreach (var g in new[] { "cs2", "rl", "bf6" })
        {
            string? exe = GameArt.ExeOf(g);
            if (exe == null || !File.Exists(exe)) { Ok($"logos: ícone de {g} pulado (jogo não instalado)", true); continue; }
            var icon = GameArt.GameIcon(g);
            int visible = 0;
            if (icon is { } i) for (int k = 3; k < i.Px.Length; k += 4) if (i.Px[k] > 0) visible++;
            Ok($"logos: ícone de {g} tirado do .exe", icon?.Side == 128 && visible > 128 * 128 / 4, $"{visible} pixels visíveis");
        }
        Ok("bf6: facção pelo nome", Bf6Reader.FactionFrom(new[] { L("OTAN") }) == "nato" && Bf6Reader.FactionFrom(new[] { L("PAX", "ARMATA") }) == "pax"
            && Bf6Reader.FactionFrom(new[] { L("NATO") }) == "nato" && Bf6Reader.FactionFrom(new[] { L("CONQUISTA") }) == null);
        var s = new Bf6State();
        var t0 = DateTime.Now;
        s.Apply(new Bf6Frame(null, null, "Conquista", "Lago Groom", new List<Line>(), null) { Faction = "pax" }, t0, "x");
        s.Apply(new Bf6Frame(500, 400, null, null, new List<Line>(), null), t0.AddSeconds(1), "x");
        Ok("bf6: a facção vira os times da partida", s.Current?.UsLabel == "PAX" && s.Current.ThemLabel == Lang.T("OTAN", "NATO") && s.Current.UsArt == "bf6:pax" && s.Current.ThemArt == "bf6:nato");
    }

    private static void TestCelebrationWords()
    {
        Ok("jogo fechado: pela lista de processos", Running("explorer") && !Running("jogo-que-nao-existe-ilha"));
        Ok("jogo na frente sem partida: nome pelo .exe", GameOf("bf6.exe") == "bf6" && GameOf("RocketLeague.exe") == "rl"
            && Match.NameOf(GameOf("VALORANT-Win64-Shipping.exe")) == "Valorant" && Match.NameOf(GameOf("jogo-novo.exe")) == "jogo-novo");
        Ok("fim: EZ na lavada, GG na apertada e na derrota",
            Celebrations.EndWord(Outcome.Win, 13, 4) == "EZ" && Celebrations.EndWord(Outcome.Win, 13, 11) == "GG"
            && Celebrations.EndWord(Outcome.Loss, 4, 13) == "GG" && Celebrations.EndWord(Outcome.Win, 3, 0) == "EZ" && Celebrations.EndWord(Outcome.Win, 2, 1) == "GG");
        var m = new Match { Game = "cs2", Clock = 65, ClockRead = DateTime.Now };
        Ok("relógio: conta para baixo e formata", Match.Time(65) == "1:05" && Match.Time(3725) == "1:02:05" && (m.ClockAt(m.ClockRead.AddSeconds(70)) ?? -1) == 0);
    }
}
