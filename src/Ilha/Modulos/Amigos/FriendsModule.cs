using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Ilha.Friends;

/// <summary>
/// Steam friends' activity (3.9.0, his request): with the Web API key he pastes on the island, the friend list every
/// 30 minutes and their state every minute. A friend coming online, starting a game or switching games gets a notice
/// (going offline only if turned on); the "Amigos" menu entry opens who is online. The first reading only learns.
/// </summary>
[Modulo("amigos", "Amigos da Steam", English = "Steam friends")]
internal sealed class FriendsModule : Modulo
{
    private ConfigAmigos _cfg = new();
    private FriendsActivity? _activity;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 60_000 };
    private List<string> _ids = new();
    private DateTime _idsAt = DateTime.MinValue;
    private Dictionary<string, Friend> _known = new();
    private bool _stopped, _busy, _seeded;

    internal IReadOnlyCollection<Friend> Friends => _known.Values;
    internal bool HasKey { get; private set; }
    internal bool Loaded { get; private set; }
    internal string? Error { get; private set; }

    public override Type? TipoConfig => typeof(ConfigAmigos);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigAmigos>(cfg);
        HasKey = Credenciais.Existe(SteamWeb.Credential);
        _activity = new FriendsActivity(S, this);
        S.Ilha.Adicionar(_activity);
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "amigos", Nome = Lang.T("Amigos", "Friends"), Ordem = 63,
            Abrir = () => _activity?.Open(),
            Icone = (dc, p, r) => Icon(dc, p.G.Fabrica, r, Cor.Branco),
            Detalhe = () => HasKey && Loaded ? Lang.T($"{_known.Values.Count(f => f.Online)} online", $"{_known.Values.Count(f => f.Online)} online") : null,
        });
        _tick.Tick += (_, _) => Poll();
        _tick.Start();
        Poll();
    }

    public override void ConfigMudou(JsonObject cfg) => _cfg = Ler<ConfigAmigos>(cfg);

    public override void Parar()
    {
        _stopped = true;
        _tick.Stop();
        _tick.Dispose();
    }

    internal void KeyChanged()
    {
        HasKey = Credenciais.Existe(SteamWeb.Credential);
        _ids.Clear();
        _idsAt = DateTime.MinValue;
        Error = null;
        Poll();
    }

    internal void RefreshSoon() => Poll();

    private void Poll()
    {
        if (_stopped || _busy || !HasKey) return;
        string? key = Credenciais.Ler(SteamWeb.Credential);
        if (key is not { Length: > 0 }) { HasKey = false; return; }
        _busy = true;
        bool listDue = _ids.Count == 0 || DateTime.UtcNow - _idsAt > TimeSpan.FromMinutes(30);
        var ids = _ids;
        _ = Task.Run(async () =>
        {
            List<Friend>? got = null;
            List<string>? newIds = null;
            string? error = null;
            try
            {
                if (listDue)
                {
                    string? me = SteamWeb.MySteamId();
                    if (me == null) throw new InvalidOperationException(Lang.T("Não achei a conta da Steam neste PC.", "Couldn't find the Steam account on this PC."));
                    newIds = await SteamWeb.FriendIds(key, me).ConfigureAwait(false);
                    ids = newIds;
                }
                got = ids.Count == 0 ? new List<Friend>() : await SteamWeb.Summaries(key, ids).ConfigureAwait(false);
            }
            catch (SteamWeb.ApiException e) when (e.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                error = listDue
                    ? Lang.T("A Steam recusou: confira a chave ou deixe a lista de amigos pública.", "Steam refused: check the key or make the friend list public.")
                    : Lang.T("A Steam recusou a chave.", "Steam refused the key.");
            }
            catch (Exception e) { error = Lang.T("Steam fora do ar ou sem internet: ", "Steam unreachable: ") + e.Message; }
            S.NaInterface(() =>
            {
                _busy = false;
                if (_stopped) return;
                if (error != null)
                {
                    if (Error != error) Log.Escrever("amigos: " + error);
                    Error = error;
                    _activity?.Changed();
                    return;
                }
                Error = null;
                if (newIds != null) { _ids = newIds; _idsAt = DateTime.UtcNow; }
                Apply(got!);
            });
        });
    }

    private void Apply(List<Friend> now)
    {
        var events = _seeded ? Changes(_known, now, _cfg) : new List<(Friend, string, bool)>();
        if (!_seeded) Log.Escrever($"amigos: {now.Count} amigos, {now.Count(f => f.Online)} online");
        _seeded = true;
        Loaded = true;
        _known = now.Where(f => f.Id.Length > 0).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        // a burst (Steam waking up after the PC slept) is not news: three notices at most per reading
        foreach (var (f, what, game) in events.Take(3))
        {
            var friend = f;
            S.Ilha.Alertar(new Alerta
            {
                Tipo = "amigos", Som = "chegada", Importante = true, Segundos = 3.5f,
                Criar = p => FriendsActivity.NoticeCard(p, S, friend, what, game ? FriendsActivity.InGame : FriendsActivity.OnlineBlue),
            });
        }
        _activity?.Changed();
        S.Menu.Redesenhar();
    }

    /// <summary>What is news between two readings: came online, started or switched games, went offline (if asked).</summary>
    internal static List<(Friend F, string What, bool Game)> Changes(IReadOnlyDictionary<string, Friend> before, IEnumerable<Friend> now, ConfigAmigos cfg)
    {
        var r = new List<(Friend, string, bool)>();
        foreach (var f in now)
        {
            if (cfg.Ignorar.Any(x => string.Equals(x.Trim(), f.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (!before.TryGetValue(f.Id, out var b)) continue;
            if (f.Playing && (b.GameId != f.GameId || !b.Playing))
            {
                if (cfg.AvisarJogo) r.Add((f, Lang.T("Começou a jogar ", "Started playing ") + f.Game, true));
            }
            else if (f.Online && !b.Online)
            {
                if (cfg.AvisarOnline) r.Add((f, Lang.T("Entrou na Steam", "Came online"), false));
            }
            else if (!f.Online && b.Online && cfg.AvisarOffline) r.Add((f, Lang.T("Saiu da Steam", "Went offline"), false));
        }
        return r;
    }

    /// <summary>Two people, the one in front a little bigger.</summary>
    internal static void Icon(ID2D1DeviceContext dc, ID2D1Factory1 f, System.Drawing.RectangleF r, Color4 color)
    {
        float s = r.Width / 24f;
        var back = new Color4(color.R, color.G, color.B, color.A * 0.55f);
        Icone.Circulo(dc, new Vector2(r.X + 16 * s, r.Y + 7.5f * s), 3.2f * s, back);
        Icone.Retangulo(dc, r.X + 11.5f * s, r.Y + 12.5f * s, 9 * s, 7 * s, 3.5f * s, back);
        Icone.Circulo(dc, new Vector2(r.X + 9 * s, r.Y + 8.5f * s), 3.8f * s, color);
        Icone.Retangulo(dc, r.X + 3.5f * s, r.Y + 14 * s, 11 * s, 7.5f * s, 4 * s, color);
    }

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Ok(string n, bool ok, string d = "") => r.AppendLine((ok ? "ok      " : "FALHOU  ") + n + (d.Length > 0 ? $" ({d})" : ""));
        using var list = JsonDocument.Parse("""{"friendslist":{"friends":[{"steamid":"76561198000000001","relationship":"friend"},{"steamid":"76561198000000002"}]}}""");
        Ok("amigos: lista de amigos", SteamWeb.ParseFriendIds(list.RootElement).Count == 2);
        using var sum = JsonDocument.Parse("""{"response":{"players":[{"steamid":"1","personaname":"Ana","personastate":1,"avatarmedium":"https://x/a.jpg","gameid":"730","gameextrainfo":"Counter-Strike 2"},{"steamid":"2","personaname":"Bia","personastate":0,"avatarmedium":""}]}}""");
        var fs = SteamWeb.ParseSummaries(sum.RootElement);
        Ok("amigos: estado dos amigos", fs.Count == 2 && fs[0].Playing && fs[0].Game == "Counter-Strike 2" && !fs[1].Online);
        var before = new Dictionary<string, Friend>
        {
            ["1"] = new("1", "Ana", 1, null, null, ""),
            ["2"] = new("2", "Bia", 0, null, null, ""),
            ["3"] = new("3", "Caio", 1, "730", "Counter-Strike 2", ""),
            ["4"] = new("4", "Duda", 1, null, null, ""),
        };
        var now = new List<Friend>
        {
            new("1", "Ana", 1, "730", "Counter-Strike 2", ""),
            new("2", "Bia", 1, null, null, ""),
            new("3", "Caio", 1, "252950", "Rocket League", ""),
            new("4", "Duda", 0, null, null, ""),
            new("5", "Edu", 1, null, null, ""),
        };
        var cfg = new ConfigAmigos();
        var ch = Changes(before, now, cfg);
        Ok("amigos: começou a jogar, entrou, trocou de jogo; saiu e o novo na lista não avisam",
            ch.Count == 3 && ch.Any(c => c.F.Name == "Ana" && c.Game) && ch.Any(c => c.F.Name == "Bia" && !c.Game) && ch.Any(c => c.F.Name == "Caio" && c.What.Contains("Rocket")),
            string.Join("; ", ch.Select(c => c.F.Name + ": " + c.What)));
        cfg.AvisarOffline = true;
        cfg.Ignorar = new List<string> { "ana" };
        var ch2 = Changes(before, now, cfg);
        Ok("amigos: ignorados e saída quando ligada", ch2.All(c => c.F.Name != "Ana") && ch2.Any(c => c.F.Name == "Duda"));
        Ok("amigos: conta do loginusers (a mais recente)", SteamWeb.FromLoginUsers("\"users\"\n{\n\"76561198000000009\"\n{\n\"MostRecent\"\t\"0\"\n}\n\"76561198000000010\"\n{\n\"MostRecent\"\t\"1\"\n}\n}") == "76561198000000010");
        var px = Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray();
        Avatars.Round(px, 8);
        Ok("amigos: avatar redondo (canto transparente, meio cheio)", px[3] == 0 && px[(4 * 8 + 4) * 4 + 3] == 255);
        string? me = SteamWeb.MySteamId();
        Ok("amigos: conta da Steam deste PC", me != null, me != null ? "achada" : "não achada");
        Ok("amigos: chave da Web API", true, Credenciais.Existe(SteamWeb.Credential) ? "guardada" : "ainda não colada");
        return r.ToString().TrimEnd();
    }
}

public sealed class ConfigAmigos
{
    [Label("Avisar quando um amigo entrar", "Notify when a friend comes online")]
    [JsonPropertyName("avisarOnline")] public bool AvisarOnline { get; set; } = true;

    [Label("Avisar quando um amigo começar a jogar", "Notify when a friend starts a game")]
    [JsonPropertyName("avisarJogo")] public bool AvisarJogo { get; set; } = true;

    [Label("Avisar quando um amigo sair", "Notify when a friend goes offline")]
    [JsonPropertyName("avisarOffline")] public bool AvisarOffline { get; set; }

    [Label("Não avisar sobre", "Don't notify about")]
    [Help("Nomes da Steam, um por linha.", "Steam names, one per line.")]
    [JsonPropertyName("ignorar")] public List<string> Ignorar { get; set; } = new();
}
