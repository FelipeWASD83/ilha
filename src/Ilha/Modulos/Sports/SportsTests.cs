using System;
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
