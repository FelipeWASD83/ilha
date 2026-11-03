using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ilha.Sports;

internal sealed partial class SportsModule
{
    // the shape ESPN sent on 03/11/2026, cut to what the island reads
    private const string Soccer = """
        {"leagues":[{"name":"Brazilian Serie A"}],"events":[
         {"id":"1","name":"Mirassol at Chapecoense","date":"2026-11-03T22:30Z",
          "status":{"displayClock":"67'","type":{"state":"in","shortDetail":"67'"}},
          "competitions":[{"date":"2026-11-03T22:30Z","status":{"displayClock":"67'","type":{"state":"in","shortDetail":"67'"}},
           "competitors":[
            {"homeAway":"home","order":0,"score":"2","team":{"id":"9318","displayName":"Chapecoense","shortDisplayName":"Chapecoense","abbreviation":"CHA","logo":"https://a.espncdn.com/i/teamlogos/soccer/500/9318.png"}},
            {"homeAway":"away","order":1,"score":"1","team":{"id":"9169","displayName":"Mirassol","shortDisplayName":"Mirassol","abbreviation":"MIR","logo":"https://a.espncdn.com/i/teamlogos/soccer/500/9169.png"}}]}]},
         {"id":"2","name":"Coritiba at Internacional","date":"2026-11-04T00:30Z","status":{"type":{"state":"pre","shortDetail":"Scheduled"}},
          "competitions":[{"competitors":[{"homeAway":"home","score":"0","team":{"id":"1936","displayName":"Internacional","abbreviation":"INT"}},
            {"homeAway":"away","score":"0","team":{"id":"3456","displayName":"Coritiba","abbreviation":"CFC"}}]}]},
         {"id":"3","broken":true}]}
        """;

    private const string Race = """
        {"events":[{"id":"9","name":"Mexico City Grand Prix","date":"2026-10-30T18:30Z","competitions":[
          {"type":{"abbreviation":"FP1"},"status":{"type":{"state":"post"}},"competitors":[{"order":1,"athlete":{"displayName":"Lando Norris"}}]},
          {"type":{"abbreviation":"Race"},"date":"2026-11-01T20:00Z","status":{"type":{"state":"in","shortDetail":"Lap 34"}},"competitors":[
            {"order":2,"athlete":{"displayName":"Kimi Antonelli"}},{"order":1,"athlete":{"displayName":"Charles Leclerc"}},{"order":3,"athlete":{"displayName":"Frederik Vesti"}}]}]}]}
        """;

    // a tournament the way ESPN sends tennis: every match inside the groupings (the doubles and the TBD final are skipped)
    private const string Tennis = """
        {"events":[{"id":"t1","name":"Paris Masters","groupings":[
          {"grouping":{"slug":"mens-singles"},"competitions":[
            {"id":"m1","date":"2026-11-03T13:00Z","status":{"type":{"state":"in","shortDetail":"3rd Set"}},"round":{"displayName":"Round 2"},"competitors":[
              {"homeAway":"home","order":1,"athlete":{"displayName":"Joao Fonseca","shortName":"J. Fonseca"},"linescores":[{"value":6,"winner":true},{"value":4,"winner":false},{"value":2}]},
              {"homeAway":"away","order":2,"athlete":{"displayName":"Pablo Carreno Busta","shortName":"P. Carreno Busta"},"linescores":[{"value":3,"winner":false},{"value":6,"winner":true},{"value":1}]}]},
            {"id":"m2","date":"2026-10-30T13:00Z","status":{"type":{"state":"post","shortDetail":"Final"}},"competitors":[
              {"homeAway":"home","athlete":{"displayName":"A"}},{"homeAway":"away","athlete":{"displayName":"B"}}]},
            {"id":"m3","date":"2026-11-03T18:00Z","status":{"type":{"state":"pre"}},"competitors":[
              {"homeAway":"home","athlete":{"displayName":"TBD"}},{"homeAway":"away","athlete":{"displayName":"TBD"}}]}]},
          {"grouping":{"slug":"mens-doubles"},"competitions":[
            {"id":"d1","date":"2026-11-03T13:00Z","status":{"type":{"state":"in"}},"competitors":[
              {"homeAway":"home","athlete":{"displayName":"C"}},{"homeAway":"away","athlete":{"displayName":"D"}}]}]}]}]}
        """;

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Ok(string name, bool ok, string detail = "") => r.AppendLine((ok ? "ok      " : "FALHOU  ") + name + (detail.Length > 0 ? $" ({detail})" : ""));

        var bra = League.Find("bra.1")!;
        var ev = Espn.Parse(bra, Soccer);
        var g = ev.FirstOrDefault();
        Ok("esportes: jogo ao vivo lido (estado, minuto, placar, escudo); o quebrado é pulado",
            ev.Count == 2 && g!.State == EventState.Live && g.Detail == "67'" && g.Home?.Name == "Chapecoense" && g.Home.Score == 2 && g.Away?.Score == 1
            && g.Home.Logo.EndsWith("9318.png") && ev[1].State == EventState.Pre, string.Join(" | ", ev.Select(e => e.Name)));
        var f1 = Espn.Parse(League.Find("f1")!, Race).Single();
        Ok("esportes: corrida pega a sessão ao vivo e os pilotos em ordem", f1.Session == "Race" && f1.State == EventState.Live
            && f1.Competitors[0].Name == "Charles Leclerc" && f1.Competitors[1].Name == "Kimi Antonelli", string.Join(", ", f1.Competitors.Select(c => c.Name)));
        var cha = g!.Home!;
        Ok("esportes: time seguido pelo nome, sem acento nem maiúscula, ou pela sigla",
            Espn.Follows(cha, new[] { "chapecoense" }) && Espn.Follows(cha, new[] { "CHA" }) && !Espn.Follows(cha, new[] { "Mirassol" })
            && Espn.Follows(new Competitor("", "Atlético-MG", "Atlético-MG", "CAM", null, "", true, 0), new[] { "atletico mg" }));
        Ok("esportes: piloto pelo sobrenome", Espn.Follows(f1.Competitors[0], new[] { "Leclerc" }) && !Espn.Follows(f1.Competitors[0], new[] { "Norris" }));
        Ok("esportes: endereço da ESPN com e sem dia", Espn.Url(bra) == "https://site.api.espn.com/apis/site/v2/sports/soccer/bra.1/scoreboard"
            && Espn.Url(bra, new DateTime(2026, 11, 5)).EndsWith("?dates=20261105"));
        Ok("esportes: todas as ligas da configuração existem", new ConfigEsportes().Ligas.All(k => League.Find(k) != null));
        var tennis = Espn.Parse(League.Find("atp")!, Tennis, new DateTime(2026, 11, 3));
        Ok("esportes: tênis vira uma partida por jogo de simples, placar em sets ganhos (duplas e TBD ficam de fora)",
            tennis.Count == 1 && tennis[0].State == EventState.Live && tennis[0].Home?.Score == 1 && tennis[0].Away?.Score == 1
            && tennis[0].Name == "J. Fonseca x P. Carreno Busta" && Espn.Follows(tennis[0].Home!, new[] { "Fonseca" }),
            string.Join(" | ", tennis.Select(e => $"{e.Name} {e.Home?.Score}:{e.Away?.Score}")));
        Ok("esportes: esporte de cada liga (para o ícone do menu)", bra.Sport == "soccer" && League.Find("f1")!.Sport == "racing"
            && League.Find("atp")!.Sport == "tennis" && League.Find("nba")!.Sport == "basketball" && League.Find("nfl")!.Sport == "football");
        {
            var saved = new Dictionary<string, List<SportEvent>>(_today);
            _today.Clear();
            string idle = IconSport();
            _today["f1"] = new List<SportEvent> { f1 };
            string race = IconSport();
            _today["atp"] = tennis;
            string both = IconSport();
            _today.Clear();
            foreach (var (k, v) in saved) _today[k] = v;
            Ok("esportes: ícone do menu segue o que está ao vivo (bola sem nada, carro na corrida)", idle == "soccer" && race == "racing"
                && (both is "racing" or "tennis"), $"{idle}, {race}, {both}");
        }
        try
        {
            var png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ilha-icones-esportes.png");
            global::Ilha.Recorder.IconPreview.Render(png, new Action<Vortice.Direct2D1.ID2D1DeviceContext, Vortice.Direct2D1.ID2D1Factory1, System.Drawing.RectangleF>[]
            {
                (dc, f, box) => global::Ilha.Games.GamesOverview.Icon(dc, f, box, Cor.Branco),
                (dc, f, box) => SportIcons.Draw(dc, f, box, "soccer"),
                (dc, f, box) => SportIcons.Draw(dc, f, box, "racing"),
                (dc, f, box) => SportIcons.Draw(dc, f, box, "tennis"),
                (dc, f, box) => SportIcons.Draw(dc, f, box, "basketball"),
                (dc, f, box) => SportIcons.Draw(dc, f, box, "football"),
                (dc, f, box) => global::Ilha.Games.TrophyRoom.MenuIcon(dc, f, box),
            });
            Ok("esportes: ícones do menu (jogos e os cinco esportes) desenhados em " + png, System.IO.File.Exists(png));
        }
        catch (Exception e) { Ok("esportes: ícones do menu: " + e.Message, false); }

        // the real thing, when the network answers (not a failure when it does not: the test may run offline)
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var real = Espn.Fetch(bra, null, cts.Token).GetAwaiter().GetResult();
            Ok($"esportes: ESPN respondeu agora ({real.Count} jogos do Brasileirão no placar de hoje)", true,
                string.Join(" | ", real.Take(3).Select(e => $"{e.Name} {SportDraw.Status(e)}")));
        }
        catch (Exception e) { Ok("esportes: ESPN fora do ar ou sem rede, pulado", true, e.Message); }
        return r.ToString().TrimEnd();
    }
}
