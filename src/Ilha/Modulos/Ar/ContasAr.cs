using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vortice.Mathematics;

// Ilha.Ar e não Ilha.Modulos.Ar: Ilha.Modulos já é a classe estática do núcleo
namespace Ilha.Ar;

/// <summary>Faixa de temperatura de um modo, como o servidor do ar manda em capacidades.temperatura.</summary>
internal sealed record Faixa(double Min, double Max, double Passo);

/// <summary>O que o ar aceita (sai do perfil da LG, montado pelo servidor). A tela nunca supõe nada fora disto.</summary>
internal sealed class Capacidades
{
    public List<string> Modos = new();
    public Dictionary<string, Faixa> Temperatura = new();
    public List<string> Vento = new();
    /// <summary>The AC lets its display light be turned on and off (capacidades.visor).</summary>
    public bool Visor;

    /// <summary>Muda quando o layout do cartão precisa mudar (outra quantidade de modos ou de vento).</summary>
    public string Assinatura => string.Join(",", Modos) + "|" + string.Join(",", Vento) + (Visor ? "|visor" : "");
}

/// <summary>O estado normalizado do ar (campo "estado" do GET /api/estado, já com as pendências do servidor por cima).</summary>
internal sealed class EstadoAr
{
    public bool? Ligado;
    public string? Modo;
    public double? Alvo;
    public double? Sala;
    public string? Vento;
    public bool? Visor;
    public bool Erro;

    public EstadoAr Copia() => (EstadoAr)MemberwiseClone();
}

internal sealed class Falha
{
    public string Campo = "";
    public string Mensagem = "";
    public long Em;
}

/// <summary>O corpo do GET /api/estado (e da resposta do POST /api/comando, que é o mesmo).</summary>
internal sealed class ResumoAr
{
    public string Situacao = "ok";
    public string Mensagem = "";
    public long? PausadoAte;
    public string NomeAparelho = Lang.T("Ar do quarto", "Bedroom AC");
    public Capacidades? Capacidades;
    public EstadoAr? Estado;
    public HashSet<string> Pendente = new();
    public List<Falha> Falhas = new();
    public long? LidoEm;
    public int? ChamadasNaHora, Teto;
    public bool Simulado;
}

/// <summary>Um ponto do GET /api/historico (o servidor lê o ar a cada 15 min, sem gastar chamada a mais).</summary>
internal sealed record PontoHistorico(long T, bool? Ligado, string? Modo, double? Alvo, double? Sala);

/// <summary>O que foi colado na tela da chave: o link do mostrar-link.ps1, só a chave ou só o endereço.</summary>
internal sealed record EntradaAr(string? Endereco, string? Chave, string? Erro);

/// <summary>
/// As contas e os textos do ar, PUROS (sem rede, sem tela, sem relógio escondido): o parser das respostas do
/// servidor do ar-do-quarto, a temperatura de 1 em 1 grau, os nomes dos modos e do vento, a montagem do corpo dos
/// comandos e a leitura do link. Os nomes de modo e de vento são os do site do ar (os mesmos da nova aba).
/// </summary>
internal static class ContasAr
{
    // 32 bytes em base64 url-safe dão 43 caracteres; a folga é a mesma do site e da nova aba
    private static readonly Regex ChaveValida = new("^[A-Za-z0-9_-]{16,256}$", RegexOptions.CultureInvariant);

    // (name pt, action pt, name en, action en)
    private static readonly Dictionary<string, (string Nome, string Acao, string NameEn, string ActionEn)> Modos = new()
    {
        ["COOL"] = ("Gelar", "Gelando", "Cool", "Cooling"),
        ["HEAT"] = ("Aquecer", "Aquecendo", "Heat", "Heating"),
        ["FAN"] = ("Ventilar", "Ventilando", "Fan", "Fan only"),
        ["AIR_DRY"] = ("Desumidificar", "Desumidificando", "Dry", "Drying"),
        ["AUTO"] = ("Automático", "No automático", "Auto", "On auto"),
        ["AIR_CLEAN"] = ("Purificar", "Purificando", "Purify", "Purifying"),
        ["AROMA"] = ("Aroma", "No aroma", "Aroma", "Aroma on"),
        ["ENERGY_SAVING"] = ("Econômico", "No econômico", "Eco", "On eco"),
    };

    private static readonly Dictionary<string, (string Pt, string En)> Ventos = new()
    {
        ["SLOW"] = ("Mínimo", "Lowest"), ["SLOW_LOW"] = ("Mínimo-baixo", "Very low"), ["LOW"] = ("Baixo", "Low"),
        ["LOW_MID"] = ("Médio-baixo", "Medium-low"), ["MID"] = ("Médio", "Medium"), ["MID_HIGH"] = ("Médio-alto", "Medium-high"),
        ["HIGH"] = ("Alto", "High"), ["POWER"] = ("Turbo", "Turbo"),
        ["AUTO"] = ("Automático", "Auto"), ["BREEZE"] = ("Brisa", "Breeze"), ["NATURE"] = ("Natural", "Natural"),
    };

    public static readonly string[] OrdemModos = { "COOL", "HEAT", "AIR_DRY", "FAN", "AUTO", "AIR_CLEAN", "ENERGY_SAVING", "AROMA" };
    public static readonly string[] NiveisVento = { "SLOW", "SLOW_LOW", "LOW", "LOW_MID", "MID", "MID_HIGH", "HIGH", "POWER" };
    public static readonly string[] EspeciaisVento = { "AUTO", "NATURE", "BREEZE" };

    /// <summary>Os únicos campos que a ilha manda. O turbo fica de fora de propósito (nenhuma tela liga o turbo).</summary>
    public static readonly string[] CamposDeComando = { "ligado", "modo", "alvo", "vento", "visor" };

    public static string ProblemaChave => Lang.T(
        "O mini PC recusou a chave. Rode o implantar\\mostrar-link.ps1 do ar-do-quarto e cole o link novo aqui.",
        "The mini PC rejected the key. Run implantar\\mostrar-link.ps1 from ar-do-quarto and paste the new link here.");
    public static string ProblemaRede => Lang.T(
        "O mini PC não respondeu. Confira se este PC está na rede de casa e se o mini PC está ligado.",
        "The mini PC didn't respond. Check that this PC is on the home network and the mini PC is on.");
    public static string ProblemaResposta => Lang.T(
        "O servidor do ar mandou uma resposta que não deu pra entender.",
        "The AC server sent a response that couldn't be understood.");

    // ------------------------------------------------------------------ miúdos de JSON

    private static double? Numero(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.GetValueKind() != JsonValueKind.Number) return null;
        double d = v.GetValue<double>();
        return double.IsFinite(d) ? d : null;
    }

    private static long? Inteiro(JsonNode? n) => Numero(n) is double d ? (long)Math.Floor(d) : null;

    private static bool? Booleano(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;

    private static string? Texto(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static List<string> Textos(JsonNode? n)
    {
        var l = new List<string>();
        if (n is JsonArray a)
            foreach (var x in a)
                if (Texto(x) is { Length: > 0 } s && !l.Contains(s)) l.Add(s);
        return l;
    }

    // ------------------------------------------------------------------ parser

    /// <summary>O corpo do GET /api/estado. null se não for um objeto JSON (o que vem do servidor nunca derruba a ilha).</summary>
    public static ResumoAr? LerResumo(string json)
    {
        JsonNode? raiz;
        try { raiz = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (raiz is not JsonObject o) return null;

        var r = new ResumoAr();
        if (o["conexao"] is JsonObject c)
        {
            r.Situacao = Texto(c["situacao"]) is { Length: > 0 } s ? s : "ok";
            r.Mensagem = Texto(c["mensagem"]) ?? "";
            r.PausadoAte = Inteiro(c["pausado_ate"]);
        }
        if (o["aparelho"] is JsonObject ap && Texto(ap["nome"]) is { Length: > 0 } nome) r.NomeAparelho = nome;
        if (o["servidor"] is JsonObject sv) r.Simulado = Booleano(sv["simulado"]) == true;

        if (o["capacidades"] is JsonObject caps)
        {
            var cp = new Capacidades { Modos = EmOrdem(Textos(caps["modos"])), Vento = Textos(caps["vento"]), Visor = Booleano(caps["visor"]) == true };
            if (caps["temperatura"] is JsonObject temps)
                foreach (var (modo, valor) in temps)
                {
                    if (valor is not JsonObject f) continue;
                    if (Numero(f["min"]) is not double min || Numero(f["max"]) is not double max || max < min) continue;
                    double passo = Numero(f["passo"]) is double p && p > 0 ? p : 1;
                    cp.Temperatura[modo] = new Faixa(min, max, passo);
                }
            r.Capacidades = cp;
        }

        if (o["estado"] is JsonObject e)
            r.Estado = new EstadoAr
            {
                Ligado = Booleano(e["ligado"]),
                Modo = Texto(e["modo"]),
                Alvo = Numero(e["alvo"]),
                Sala = Numero(e["sala"]),
                Vento = Texto(e["vento"]),
                Visor = Booleano(e["visor"]),
                Erro = Booleano(e["erro"]) == true,
            };

        if (o["pendente"] is JsonObject pend)
            foreach (var (campo, _) in pend) r.Pendente.Add(campo);

        if (o["falhas"] is JsonArray falhas)
            foreach (var f in falhas)
                if (f is JsonObject fo && Texto(fo["mensagem"]) is { Length: > 0 } msg)
                    r.Falhas.Add(new Falha { Campo = Texto(fo["campo"]) ?? "", Mensagem = msg, Em = Inteiro(fo["em"]) ?? 0 });

        r.LidoEm = Inteiro(o["lido_em"]);
        if (o["chamadas"] is JsonObject ch)
        {
            r.ChamadasNaHora = (int?)Inteiro(ch["na_hora"]);
            r.Teto = (int?)Inteiro(ch["teto"]);
        }
        return r;
    }

    /// <summary>O GET /api/historico: o ponto mais novo e o "agora" do servidor (para saber a idade sem depender do relógio daqui).</summary>
    public static (PontoHistorico? Ultimo, long? Agora)? LerHistorico(string json)
    {
        JsonNode? raiz;
        try { raiz = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (raiz is not JsonObject o) return null;
        PontoHistorico? ultimo = null;
        if (o["pontos"] is JsonArray pontos)
            foreach (var p in pontos)
            {
                if (p is not JsonObject po || Inteiro(po["t"]) is not long t) continue;
                if (ultimo != null && t < ultimo.T) continue;
                ultimo = new PontoHistorico(t, Booleano(po["ligado"]), Texto(po["modo"]), Numero(po["alvo"]), Numero(po["sala"]));
            }
        return (ultimo, Inteiro(o["agora"]));
    }

    /// <summary>O campo "erro" de uma resposta de erro do servidor ({"erro": "..."}); "" se não tiver.</summary>
    public static string ErroDoCorpo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try { return JsonNode.Parse(json) is JsonObject o ? Texto(o["erro"]) ?? "" : ""; }
        catch (JsonException) { return ""; }
    }

    // ------------------------------------------------------------------ nomes e cores

    public static string Legivel(string? codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo)) return "";
        string t = Regex.Replace(codigo, "[_-]+", " ").Trim().ToLowerInvariant();
        return t.Length == 0 ? "" : char.ToUpperInvariant(t[0]) + t[1..];
    }

    public static string NomeModo(string? m) => m != null && Modos.TryGetValue(m, out var x) ? Lang.T(x.Nome, x.NameEn) : Legivel(m);
    public static string AcaoModo(string? m) => m != null && Modos.TryGetValue(m, out var x) ? Lang.T(x.Acao, x.ActionEn) : Legivel(m);
    public static string NomeVento(string? v) => v != null && Ventos.TryGetValue(v, out var x) ? Lang.T(x.Pt, x.En) : Legivel(v);

    /// <summary>Os modos na ordem dos botões (a do site); código desconhecido vai no fim.</summary>
    public static List<string> EmOrdem(IEnumerable<string> modos)
    {
        var lista = modos.Distinct().ToList();
        return OrdemModos.Where(lista.Contains).Concat(lista.Where(m => !OrdemModos.Contains(m))).ToList();
    }

    /// <summary>O vento separado em níveis (barrinhas, do mais fraco ao mais forte) e especiais (pílulas).</summary>
    public static (List<string> Niveis, List<string> Especiais) AgruparVentos(IEnumerable<string> vento)
    {
        var lista = vento.ToList();
        var niveis = NiveisVento.Where(lista.Contains).ToList();
        var especiais = EspeciaisVento.Where(lista.Contains).ToList();
        foreach (var v in lista) if (!niveis.Contains(v) && !especiais.Contains(v)) especiais.Add(v);
        return (niveis, especiais);
    }

    private static Color4 Hex(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);

    // as cores de cada modo do site do ar (frio, quente, neutro, seco, automático)
    public static Color4 CorModo(string? modo) => modo switch
    {
        "COOL" => Hex(0x7CC4FF),
        "HEAT" => Hex(0xFF9A4D),
        "AIR_DRY" => Hex(0x6FE0C8),
        "AUTO" => Hex(0xB9A8FF),
        _ => Hex(0xD5DDE8),
    };

    // ------------------------------------------------------------------ números e temperatura

    /// <summary>25.5 vira "25,5"; 25 vira "25"; vazio para o que não é número.</summary>
    public static string FormatarNumero(double? n)
    {
        if (n is not double v || !double.IsFinite(v)) return "";
        double r = Math.Round(v * 10, MidpointRounding.AwayFromZero) / 10;
        if (r == 0) r = 0;
        return r.ToString("0.#", Lang.Culture);
    }

    public static string FormatarGraus(double? n) => FormatarNumero(n) is { Length: > 0 } t ? t + "°" : "";

    public static Faixa? FaixaDoModo(Capacidades? caps, string? modo) =>
        caps != null && modo != null && caps.Temperatura.TryGetValue(modo, out var f) ? f : null;

    /// <summary>Os graus INTEIROS de dentro da faixa (a tela anda de 1 em 1 grau, decisão do ar-do-quarto): 16,5 a 30,5 vira 17 a 30.</summary>
    public static (int Min, int Max)? FaixaInteira(Faixa? f)
    {
        if (f == null) return null;
        int min = (int)Math.Ceiling(f.Min - 1e-9), max = (int)Math.Floor(f.Max + 1e-9);
        return min <= max ? (min, max) : null;
    }

    /// <summary>
    /// O alvo depois de um + (delta 1) ou de um − (delta −1): o próximo grau inteiro (18,5 vai a 19 no + e a 18 no −),
    /// preso à faixa. Sem alvo conhecido, parte da sala arredondada (ou do mínimo).
    /// </summary>
    public static int? ProximoAlvo(double? atual, int delta, Faixa? faixa, double? sala = null)
    {
        if (FaixaInteira(faixa) is not (int min, int max)) return null;
        int v;
        if (atual is double a && double.IsFinite(a))
            v = delta > 0 ? (int)Math.Floor(a + 1e-9) + 1 : (int)Math.Ceiling(a - 1e-9) - 1;
        else v = sala is double s && double.IsFinite(s) ? (int)Math.Round(s, MidpointRounding.AwayFromZero) : min;
        return Math.Clamp(v, min, max);
    }

    /// <summary>Se o − e o + mexem (18,5 com mínimo 18 ainda desce, para o 18).</summary>
    public static (bool Desce, bool Sobe) LimitesDoAlvo(double? alvo, Faixa? faixa)
    {
        if (FaixaInteira(faixa) is not (int min, int max)) return (false, false);
        if (alvo is not double a || !double.IsFinite(a)) return (true, true);
        return (a > min, a < max);
    }

    // ------------------------------------------------------------------ frases

    /// <summary>"Gelando" + "Sala com 25,5°, indo pra 18°" (a primeira parte vai em destaque).</summary>
    public static (string Forte, string Fraco) FraseStatus(EstadoAr? e, Capacidades? caps)
    {
        if (e == null) return (Lang.T("Sem leitura", "No reading"), "");
        string sala = e.Sala is double s ? Lang.T("Sala com ", "Room at ") + FormatarGraus(s) : "";
        string erro = Lang.T("O ar avisou um erro, confira o visor dele.", "The AC reported an error, check its display.");
        var partes = new List<string>();
        if (e.Ligado != true)
        {
            if (sala.Length > 0) partes.Add(sala + ".");
            if (e.Erro) partes.Add(erro);
            return (Lang.T("Desligado", "Off"), string.Join(" ", partes));
        }
        string alvo = FaixaDoModo(caps, e.Modo) != null && e.Alvo is double a ? FormatarGraus(a) : "";
        if (sala.Length > 0 && alvo.Length > 0) partes.Add(sala + Lang.T(", indo pra ", ", going to ") + alvo + ".");
        else if (sala.Length > 0) partes.Add(sala + ".");
        else if (alvo.Length > 0) partes.Add(Lang.T("Indo pra ", "Going to ") + alvo + ".");
        if (e.Erro) partes.Add(erro);
        return (e.Modo != null ? AcaoModo(e.Modo) : Lang.T("Ligado", "On"), string.Join(" ", partes));
    }

    /// <summary>Hora local "HH:mm" de um epoch em segundos.</summary>
    public static string HoraDe(long epoch) => DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString(Lang.T("HH:mm", "h:mm tt"), Lang.Culture);

    /// <summary>"lido agora", "lido há 2 min", "lido há 3 h".</summary>
    public static string TempoDesde(long? lidoEm, long agora)
    {
        if (lidoEm is not long l) return Lang.T("sem leitura ainda", "no reading yet");
        long seg = Math.Max(0, agora - l);
        if (seg < 60) return Lang.T("lido agora", "read just now");
        long min = seg / 60;
        if (min < 60) return Lang.T($"lido há {min} min", $"read {min} min ago");
        long h = min / 60;
        if (h < 24) return Lang.T($"lido há {h} h", $"read {h} h ago");
        long d = h / 24;
        return Lang.T($"lido há {d} " + (d == 1 ? "dia" : "dias"), $"read {d} " + (d == 1 ? "day" : "days") + " ago");
    }

    /// <summary>A situação da conexão do servidor com a LG em frase curta; "" quando está tudo certo.</summary>
    public static string TextoSituacao(string? situacao, string? mensagem, long? pausadoAte)
    {
        string c = string.IsNullOrEmpty(situacao) ? "ok" : situacao;
        string m = mensagem ?? "";
        return c switch
        {
            "ok" => "",
            "lendo" => Lang.T("Lendo o ar…", "Reading the AC…"),
            "sem_token" => Lang.T("Falta o token da LG no mini PC. Rode o implantar\\configurar-token.ps1 do ar-do-quarto.",
                "The LG token is missing on the mini PC. Run implantar\\configurar-token.ps1 from ar-do-quarto."),
            "token_recusado" => Lang.T("A LG recusou o token. Gere outro em connect-pat.lgthinq.com e rode o configurar-token.ps1.",
                "LG rejected the token. Create a new one at connect-pat.lgthinq.com and run configurar-token.ps1."),
            "termos" => Lang.T("Aceite os termos de uso no app LG ThinQ.", "Accept the terms of use in the LG ThinQ app."),
            "limite" => pausadoAte is long p
                ? Lang.T($"A LG pausou as chamadas por excesso. Volta sozinho às {HoraDe(p)}.", $"LG paused calls for too many requests. Back on its own at {HoraDe(p)}.")
                : Lang.T("A LG pausou as chamadas por excesso. Volta sozinho em meia hora.", "LG paused calls for too many requests. Back on its own in half an hour."),
            "desconectado" => Lang.T("O ar está sem Wi-Fi ou sem energia.", "The AC has no Wi-Fi or no power."),
            "sem_internet" => Lang.T("O mini PC está sem resposta da LG. Confira a internet de casa.", "The mini PC gets no answer from LG. Check the home internet."),
            "sem_aparelho" => Lang.T("Nenhum ar-condicionado nesta conta LG.", "No air conditioner on this LG account."),
            // the server's own message is Portuguese: in English only the generic text is shown
            "erro" => m.Length > 0 && !Lang.English ? "Erro da LG: " + m : Lang.T("A LG respondeu com erro.", "LG responded with an error."),
            _ => (Legivel(c) is { Length: > 0 } n ? n : Lang.T("Situação desconhecida", "Unknown status")) + (m.Length > 0 && !Lang.English ? ". " + m : "."),
        };
    }

    public static string TextoErroHttp(int status) => Lang.T($"O servidor do ar respondeu com erro ({status}).", $"The AC server responded with an error ({status}).");

    /// <summary>O estado a mostrar: o do servidor (que já traz as pendências dele) mais o que foi pedido aqui e ainda não voltou.</summary>
    public static EstadoAr? Mesclar(EstadoAr? base_, IReadOnlyDictionary<string, object> locais)
    {
        if (base_ == null) return null;
        var e = base_.Copia();
        foreach (var (campo, valor) in locais)
            switch (campo)
            {
                case "ligado" when valor is bool b: e.Ligado = b; break;
                case "modo" when valor is string s: e.Modo = s; break;
                case "alvo" when valor is double d: e.Alvo = d; break;
                case "alvo" when valor is int i: e.Alvo = i; break;
                case "vento" when valor is string v: e.Vento = v; break;
                case "visor" when valor is bool luz: e.Visor = luz; break;
            }
        return e;
    }

    // ------------------------------------------------------------------ comandos

    /// <summary>
    /// O corpo do POST /api/comando. Só ligado, modo, alvo (inteiro, de 1 em 1 grau), vento e visor: qualquer outro campo
    /// (o turbo, por exemplo) é recusado aqui, antes de sair da ilha.
    /// </summary>
    public static string MontarComando(IReadOnlyDictionary<string, object> campos)
    {
        if (campos.Count == 0) throw new ArgumentException("nada pra mandar");
        var o = new JsonObject();
        foreach (var campo in CamposDeComando)
        {
            if (!campos.TryGetValue(campo, out var valor)) continue;
            o[campo] = (campo, valor) switch
            {
                ("ligado" or "visor", bool b) => JsonValue.Create(b),
                ("modo" or "vento", string s) when s.Length > 0 => JsonValue.Create(s),
                ("alvo", int i) => JsonValue.Create(i),
                ("alvo", double d) when double.IsFinite(d) && Math.Abs(d - Math.Round(d)) < 1e-9 => JsonValue.Create((int)Math.Round(d)),
                _ => throw new ArgumentException($"valor inválido para {campo}"),
            };
        }
        var estranho = campos.Keys.FirstOrDefault(k => !CamposDeComando.Contains(k));
        if (estranho != null) throw new ArgumentException($"campo não permitido: {estranho}");
        return o.ToJsonString();
    }

    // ------------------------------------------------------------------ link e chave

    /// <summary>O endereço como origem (http://ip:porta), ou null se não for http/https com host.</summary>
    public static string? NormalizarEndereco(string? texto)
    {
        string t = (texto ?? "").Trim();
        if (t.Length == 0) return null;
        if (!Regex.IsMatch(t, "^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase)) t = "http://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(u.Host) || !string.IsNullOrEmpty(u.UserInfo)) return null;
        return u.GetLeftPart(UriPartial.Authority);
    }

    public static bool ChaveParece(string? chave) => chave != null && ChaveValida.IsMatch(chave);

    /// <summary>
    /// O que foi colado na tela da chave: o link inteiro do mostrar-link.ps1 (http://IP:8088/#chave=...) vira endereço
    /// e chave; só a chave vira chave; só o endereço vira endereço. As mensagens de erro nunca repetem o que foi colado.
    /// </summary>
    public static EntradaAr InterpretarEntrada(string? texto)
    {
        string t = (texto ?? "").Trim();
        if (t.Length == 0) return new EntradaAr(null, null, Lang.T("Cole o link que o mostrar-link.ps1 mostra (ou só a chave).", "Paste the link that mostrar-link.ps1 shows (or just the key)."));
        if (ChaveValida.IsMatch(t)) return new EntradaAr(null, t, null);

        int h = t.IndexOf('#');
        string semFragmento = h >= 0 ? t[..h] : t;
        string fragmento = h >= 0 ? t[(h + 1)..] : "";
        var endereco = NormalizarEndereco(semFragmento);
        if (endereco == null)
            return new EntradaAr(null, null, Lang.T("Isso não parece o link nem a chave. Copie de novo o que o mostrar-link.ps1 mostra (http://…:8088/#chave=…).",
                "That doesn't look like the link or the key. Copy again what mostrar-link.ps1 shows (http://…:8088/#chave=…)."));

        string? chave = null;
        foreach (var par in fragmento.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int ig = par.IndexOf('=');
            if (ig > 0 && par[..ig] == "chave") { chave = Uri.UnescapeDataString(par[(ig + 1)..].Replace('+', ' ')); break; }
        }
        if (fragmento.Length > 0 && chave == null)
            return new EntradaAr(null, null, Lang.T("Faltou a chave: o link tem que terminar em #chave=… (é o que o mostrar-link.ps1 mostra).",
                "The key is missing: the link must end in #chave=… (that's what mostrar-link.ps1 shows)."));
        if (chave != null && !ChaveValida.IsMatch(chave))
            return new EntradaAr(null, null, Lang.T("A chave do link não parece certa. Copie o link inteiro de novo.", "The key in the link doesn't look right. Copy the whole link again."));
        return new EntradaAr(endereco, chave, null);
    }
}

/// <summary>
/// A fila de comandos: um POST por vez; o que muda com um POST no ar espera e vai no próximo, com o valor mais novo de
/// cada campo. Guarda também o que foi pedido e ainda não voltou, para a tela mostrar na hora.
/// </summary>
internal sealed class FilaDeComandos
{
    private readonly Dictionary<string, object> _esperando = new();
    private readonly Dictionary<string, object> _locais = new();
    private HashSet<string> _emVoo = new();

    public bool Enviando { get; private set; }
    public IReadOnlyDictionary<string, object> Locais => _locais;
    public bool TemEsperando => _esperando.Count > 0;
    public bool Esperando(string campo) => _esperando.ContainsKey(campo);
    public bool Pendente(string campo) => _locais.ContainsKey(campo);

    public void Adicionar(string campo, object valor)
    {
        if (!ContasAr.CamposDeComando.Contains(campo)) throw new ArgumentException($"campo não permitido: {campo}");
        _esperando[campo] = valor;
        _locais[campo] = valor;
    }

    /// <summary>O próximo corpo a mandar, ou null se já tem um POST no ar ou nada esperando.</summary>
    public Dictionary<string, object>? Retirar()
    {
        if (Enviando || _esperando.Count == 0) return null;
        var lote = new Dictionary<string, object>(_esperando);
        _esperando.Clear();
        _emVoo = new HashSet<string>(lote.Keys);
        Enviando = true;
        return lote;
    }

    /// <summary>O POST voltou (bem ou mal): o que foi nele e não foi pedido de novo deixa de ser "local".</summary>
    public void Terminou()
    {
        foreach (var c in _emVoo) if (!_esperando.ContainsKey(c)) _locais.Remove(c);
        _emVoo.Clear();
        Enviando = false;
    }

    public void Limpar()
    {
        _esperando.Clear();
        _locais.Clear();
        _emVoo.Clear();
        Enviando = false;
    }
}
