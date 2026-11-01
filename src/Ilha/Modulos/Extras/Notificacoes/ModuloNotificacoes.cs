using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using Vortice.DirectWrite;

namespace Ilha.Extras;

/// <summary>
/// Experimental e desligado por padrão: espelha na ilha os toasts do Windows dos apps de uma lista permitida
/// (vazia = nada). A API oficial (UserNotificationListener) só funciona com identidade de pacote assinado, então
/// aqui o caminho é ler, somente leitura, o banco onde o próprio Windows guarda as notificações. Gatilho: o
/// arquivo -wal do banco mudou (o serviço gravou alguma coisa), com espera para juntar a rajada; aí abre, lê o que
/// é mais novo que a última vista e fecha. Esquema não documentado: pode quebrar num update do Windows.
/// </summary>
[Modulo("notificacoes", "Notificações do Windows (experimental)", LigadoPorPadrao = false)]
internal sealed class ModuloNotificacoes : Modulo
{
    private ConfigNotificacoes _cfg = new();
    private FileSystemWatcher? _vigia;
    private Timer? _espera;
    private long _ultimaOrdem = -1;
    private readonly HashSet<long> _mostradas = new();
    private readonly object _trava = new();
    private bool _parado;

    public override Type? TipoConfig => typeof(ConfigNotificacoes);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigNotificacoes>(cfg);
        Aplicar();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigNotificacoes>(cfg);
        Aplicar();
    }

    public override void Parar()
    {
        _parado = true;
        Desligar();
    }

    private bool TemApps => _cfg.Apps.Any(a => !string.IsNullOrWhiteSpace(a));

    private void Aplicar()
    {
        // sem nenhum app permitido não há o que mostrar: nem vigia o banco
        if (!TemApps) { Desligar(); Log.Escrever("notificações: lista de apps vazia, nada a espelhar"); return; }
        if (_vigia != null) return;
        var arq = BancoDeNotificacoes.Arquivo;
        var pasta = Path.GetDirectoryName(arq);
        if (pasta == null || !File.Exists(arq)) { Log.Escrever("notificações: banco do Windows não encontrado"); return; }
        try
        {
            _espera = new Timer(_ => Ler());
            // só o -wal: quem escreve nele é o serviço; o -shm muda até com a nossa leitura (viraria laço)
            var w = new FileSystemWatcher(pasta, Path.GetFileName(arq) + "-wal")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            w.Changed += (_, _) => Mexeu();
            w.Created += (_, _) => Mexeu();
            w.Error += (_, e) => Log.Escrever("notificações: vigia: " + e.GetException().Message);
            w.EnableRaisingEvents = true;
            _vigia = w;
            // o que já está no banco é passado
            System.Threading.Tasks.Task.Run(() =>
            {
                try { lock (_trava) _ultimaOrdem = BancoDeNotificacoes.UltimaOrdem(arq); }
                catch (Exception e) { Log.Escrever("notificações: " + e.Message); }
            });
        }
        catch (Exception e) { Log.Escrever("notificações: não deu para vigiar: " + e.Message); Desligar(); }
    }

    private void Desligar()
    {
        _vigia?.Dispose();
        _vigia = null;
        _espera?.Dispose();
        _espera = null;
        lock (_trava) _ultimaOrdem = -1;
    }

    private void Mexeu()
    {
        if (_parado) return;
        try { _espera?.Change(Math.Max(300, _cfg.EsperaMs), Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    // roda numa thread do pool, nunca duas ao mesmo tempo (a trava)
    private void Ler()
    {
        if (_parado) return;
        List<NotificacaoLida> novas;
        lock (_trava)
        {
            if (_ultimaOrdem < 0) return;
            try { novas = BancoDeNotificacoes.Novas(BancoDeNotificacoes.Arquivo, _ultimaOrdem); }
            catch (Exception e) { Log.Escrever("notificações: leitura: " + e.Message); return; }
            if (novas.Count > 0) _ultimaOrdem = novas.Max(n => n.Ordem);
        }
        var cfg = _cfg;
        foreach (var n in Filtrar(novas, cfg.Apps, DateTime.UtcNow, _mostradas))
        {
            var toast = BancoDeNotificacoes.LerToast(n.Xml);
            if (toast == null) continue;
            string app = cfg.Nomes.FirstOrDefault(kv => n.App.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).Value ?? BancoDeNotificacoes.NomeDoApp(n.App);
            S.NaInterface(() => { if (!_parado) Mostrar(app, toast, cfg.MostrarTexto); });
        }
    }

    /// <summary>
    /// O que aparece: só apps permitidos, só o que chegou há pouco (o serviço reescreve a linha quando o toast é
    /// atualizado ou lido, e uma reescrita não é notificação nova) e cada Id uma vez só.
    /// </summary>
    public static List<NotificacaoLida> Filtrar(IEnumerable<NotificacaoLida> novas, IEnumerable<string> apps, DateTime agoraUtc, HashSet<long> mostradas)
    {
        var r = new List<NotificacaoLida>();
        foreach (var n in novas)
        {
            if (!BancoDeNotificacoes.Permitido(n.App, apps)) continue;
            if (agoraUtc - n.Chegada > TimeSpan.FromMinutes(2) || n.Chegada - agoraUtc > TimeSpan.FromMinutes(2)) continue;
            lock (mostradas)
            {
                if (!mostradas.Add(n.Id)) continue;
                if (mostradas.Count > 500) mostradas.Clear();
            }
            r.Add(n);
        }
        return r;
    }

    private void Mostrar(string app, Toast t, bool comTexto)
    {
        S.Ilha.Alertar(new Alerta
        {
            Tipo = "notificacao",
            Som = "",
            Criar = p =>
            {
                var c = p.NovaCena(new Vector2(380, 66));
                p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, tam) =>
                {
                    Icone.Retangulo(dc, 2, (tam.Y - 42) / 2, 42, 42, 12, Cor.Fraco);
                    Desenhos.Sino(dc, p.G.Fabrica, new Vector2(23, tam.Y / 2), 24, Cor.Branco);
                    float x0 = 58, l = tam.X - x0;
                    p.Texto.Desenhar(dc, app, new Vector2(x0, 2), 11.5f, Cor.Secundario, largura: l);
                    p.Texto.Desenhar(dc, t.Titulo, new Vector2(x0, 19), 15, Cor.Branco, largura: l);
                    if (comTexto && t.Texto.Length > 0)
                        p.Texto.Desenhar(dc, t.Texto, new Vector2(x0, 42), 12.5f, Cor.Secundario, FontWeight.Normal, largura: l);
                });
                return c;
            },
        });
    }

    public override string Autoteste()
    {
        var r = new Relatorio();
        const string generico = "<toast launch=\"x\"><visual><binding template=\"ToastGeneric\"><text>Fulano</text><text>oi, tudo bem?</text>" +
            "<text placement=\"attribution\">via Teste</text><image placement=\"appLogoOverride\" src=\"a.png\"/></binding></visual><audio silent=\"true\"/></toast>";
        var g = BancoDeNotificacoes.LerToast(generico);
        r.Conferir("toast genérico: título, texto, sem a atribuição", g == new Toast("Fulano", "oi, tudo bem?"), $"{g?.Titulo} / {g?.Texto}");
        const string antigo = "<toast><visual><binding template=\"ToastText02\"><text id=\"1\">Download</text><text id=\"2\">  arquivo.zip\n terminou </text></binding></visual></toast>";
        r.Conferir("modelo antigo ToastText02, espaços juntados", BancoDeNotificacoes.LerToast(antigo) == new Toast("Download", "arquivo.zip terminou"));
        const string dois = "<toast><visual><binding template=\"ToastImageAndText01\"><text>velho</text></binding><binding template=\"ToastGeneric\"><text>novo</text><text>a</text><text>b</text></binding></visual></toast>";
        r.Conferir("dois bindings: vale o ToastGeneric, textos juntados", BancoDeNotificacoes.LerToast(dois) == new Toast("novo", "a · b"));
        r.Conferir("XML quebrado, vazio ou sem texto", BancoDeNotificacoes.LerToast("<toast><visual>") == null && BancoDeNotificacoes.LerToast("") == null
            && BancoDeNotificacoes.LerToast("<toast><visual><binding template=\"ToastGeneric\"><text> </text></binding></visual></toast>") == null);
        r.Conferir("XML com DTD é recusado", BancoDeNotificacoes.LerToast("<!DOCTYPE t [<!ENTITY x \"y\">]><toast><visual><binding><text>&x;</text></binding></visual></toast>") == null);
        r.Conferir("payload UTF-8 com BOM e UTF-16", BancoDeNotificacoes.TextoDoPayload(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<', (byte)'a' }) == "<a"
            && BancoDeNotificacoes.TextoDoPayload(new byte[] { 0xFF, 0xFE, (byte)'<', 0, (byte)'a', 0 }) == "<a");
        r.Conferir("nome do app", BancoDeNotificacoes.NomeDoApp("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App") == "WindowsTerminal"
            && BancoDeNotificacoes.NomeDoApp("com.squirrel.Discord.Discord") == "Discord"
            && BancoDeNotificacoes.NomeDoApp(@"{6D809377-6AF0-444B-8957-A3773F02200E}\App\app.exe") == "app"
            && BancoDeNotificacoes.NomeDoApp("Telegram") == "Telegram");
        r.Conferir("lista vazia não deixa nada passar", !BancoDeNotificacoes.Permitido("Discord", Array.Empty<string>()) && !BancoDeNotificacoes.Permitido("Discord", new[] { " " }));
        r.Conferir("lista permitida por pedaço, sem caixa", BancoDeNotificacoes.Permitido("com.squirrel.Discord.Discord", new[] { "discord" }) && !BancoDeNotificacoes.Permitido("Telegram", new[] { "discord" }));
        var agora = new DateTime(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);
        var lista = new[]
        {
            new NotificacaoLida(10, 1, "com.squirrel.Discord.Discord", agora.AddSeconds(-3), ""),
            new NotificacaoLida(11, 2, "Telegram", agora.AddSeconds(-2), ""),
            new NotificacaoLida(12, 3, "com.squirrel.Discord.Discord", agora.AddMinutes(-30), ""),
            new NotificacaoLida(13, 1, "com.squirrel.Discord.Discord", agora, ""),
        };
        var mostradas = new HashSet<long>();
        var f = Filtrar(lista, new[] { "Discord" }, agora, mostradas);
        r.Conferir("filtro: só o permitido, recente e uma vez por Id", f.Select(x => x.Ordem).SequenceEqual(new long[] { 10 }), string.Join(",", f.Select(x => x.Ordem)));
        r.Conferir("filtro: o mesmo Id reescrito depois não volta", Filtrar(lista, new[] { "Discord" }, agora, mostradas).Count == 0);
        r.Conferir("consulta busca só toast depois da última vista", BancoDeNotificacoes.SqlNovas.Contains("[Order] > ?1") && BancoDeNotificacoes.SqlNovas.Contains("'toast'"));

        r.Tentar("banco", () =>
        {
            var arq = BancoDeNotificacoes.Arquivo;
            if (!File.Exists(arq)) { r.Sonda("banco de notificações não existe"); return; }
            var t0 = DateTime.UtcNow;
            long ultima = BancoDeNotificacoes.UltimaOrdem(arq);
            var todas = BancoDeNotificacoes.Novas(arq, 0);
            int legiveis = todas.Count(n => BancoDeNotificacoes.LerToast(n.Xml) != null);
            int apps = todas.Select(n => n.App).Distinct().Count();
            // só contagens: nada do conteúdo das notificações vai para o relatório
            r.Sonda($"banco aberto somente leitura pelo winsqlite3 em {(DateTime.UtcNow - t0).TotalMilliseconds:0} ms: {todas.Count} toasts de {apps} apps, {legiveis} com título legível, última ordem {ultima}");
        });
        return r.ToString();
    }
}

public sealed class ConfigNotificacoes
{
    /// <summary>Pedaços do id do app cujas notificações aparecem na ilha (ex.: "Discord"). Vazio = nenhuma.</summary>
    [JsonPropertyName("apps")] public List<string> Apps { get; set; } = new();
    /// <summary>Pedaço do id do app -> nome mostrado (quando o nome tirado do id fica feio).</summary>
    [JsonPropertyName("nomes")] public Dictionary<string, string> Nomes { get; set; } = new();
    /// <summary>false = só o app e o título (o texto pode ser a mensagem de alguém, na frente de quem estiver olhando).</summary>
    [JsonPropertyName("mostrarTexto")] public bool MostrarTexto { get; set; } = true;
    [JsonPropertyName("esperaMs")] public int EsperaMs { get; set; } = 1200;
}
