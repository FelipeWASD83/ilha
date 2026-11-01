using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.IA;

/// <summary>
/// `Ilha.exe --testar ia`: o porte do provedor (casos do test_provedor.py que cabem aqui), os leitores de stream
/// com streams falsos das duas APIs, a troca de lado e o cancelamento contra um transporte falso (nenhuma API paga
/// é chamada), o tradutor contra um Google falso, a imagem colada e duas sondas só de leitura do Windows.
/// </summary>
internal static class TestesIA
{
    private static readonly StringBuilder _rel = new();

    private static void Ok(string nome, bool ok, string detalhe = "")
    {
        _rel.Append(ok ? "ok      " : "FALHOU  ").Append(nome);
        if (!ok && detalhe.Length > 0) _rel.Append(": ").Append(detalhe);
        _rel.Append('\n');
    }

    private static void Igual(string nome, object? esperado, object? obtido) =>
        Ok(nome, Equals(esperado, obtido), $"esperado {esperado ?? "null"}, veio {obtido ?? "null"}");

    private static void JsonIgual(string nome, string esperado, JsonNode? obtido)
    {
        string e = JsonNode.Parse(esperado)!.ToJsonString(), o = obtido?.ToJsonString() ?? "null";
        Ok(nome, JsonNode.DeepEquals(JsonNode.Parse(esperado), obtido), $"esperado {e}, veio {o}");
    }

    private static void Lanca<T>(string nome, Action a, Func<T, bool>? confere = null) where T : Exception
    {
        try { a(); Ok(nome, false, "não lançou"); }
        catch (T e) { Ok(nome, confere?.Invoke(e) ?? true, e.GetType().Name + ": " + e.Message); }
        catch (Exception e) { Ok(nome, false, "lançou " + e.GetType().Name + ": " + e.Message); }
    }

    private static void Bloco(string nome, Action a)
    {
        try { a(); }
        catch (Exception e) { Ok(nome, false, "exceção " + e.GetType().Name + ": " + e.Message); }
    }

    public static string Rodar()
    {
        _rel.Clear();
        // the messages below are asserted in Portuguese: pin the language and give back the choice at the end
        string idioma = Lang.Choice;
        Lang.Set("pt");
        try { RodarTudo(); }
        finally { Lang.Set(idioma); }
        return _rel.ToString().TrimEnd();
    }

    private static void RodarTudo()
    {
        Bloco("lado", Lado);
        Bloco("reserva", Reserva);
        Bloco("sem crédito", SemCredito);
        Bloco("disjuntor", DisjuntorTeste);
        Bloco("raciocínio", Raciocinio);
        Bloco("converter entrada", Converter);
        Bloco("corpos e resposta", CorposEResposta);
        Bloco("stream da OpenAI", StreamOpenAI);
        Bloco("stream do Claude", StreamClaude);
        Bloco("cliente", () => Task.Run(Cliente).GetAwaiter().GetResult());
        Bloco("google", () => Task.Run(GoogleTeste).GetAwaiter().GetResult());
        Bloco("textos", Textos);
        Bloco("imagem", ImagemTeste);
        Bloco("reserva do Ctrl+C", ReservaDoCtrlC);
        Bloco("sondas do Windows", Sondas);
        Bloco("textos em inglês", EmIngles);
    }

    private static void EmIngles()
    {
        Lang.Set("en");
        try
        {
            Ok("en: dinheiro", Modelos.Dinheiro(0.00042) == "$0.0004" && Modelos.Dinheiro(0.00001) == "< $0.0001" && Modelos.Dinheiro(0.123) == "$0.123", Modelos.Dinheiro(0.00042));
            Ok("en: erro HTTP", Mensagens.DeErro("openai", 401, null).StartsWith("OpenAI rejected the key (401)"), Mensagens.DeErro("openai", 401, null));
            Ok("en: chave limpa", Segredo.Limpar("key sk-proj-QWE987 end") == "key (key) end");
            Ok("en: nome do idioma", Tradutor.NomeDoIdioma("pt") == "Portuguese" && Tradutor.NomeDoIdioma("zh-HK") == "Chinese");
            Ok("en: motivo sem chave", Troca.MotivoSemChave == "no key");
        }
        finally { Lang.Set("pt"); }
    }

    private static readonly JsonObject CreditoOpenAI = (JsonObject)JsonNode.Parse(
        """{"error": {"message": "You exceeded your current quota, please check your plan and billing details.", "type": "insufficient_quota", "code": "insufficient_quota"}}""")!;
    private static readonly JsonObject CreditoClaude = (JsonObject)JsonNode.Parse(
        """{"type": "error", "error": {"type": "invalid_request_error", "message": "Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."}}""")!;

    private static JsonNode? J(string s) => JsonNode.Parse(s);

    // ── provedor ──

    private static void Lado()
    {
        bool ok = new[] { "claude-haiku-5-5", "claude-sonnet-5-5", "Claude-Opus-5-5", "claude-opus-4-8" }.All(m => Provedor.EhClaude(m) && Provedor.ProvedorDe(m) == "claude")
            && new[] { "gpt-6-luna", "gpt-6.1-sol", "o3", "ft:gpt-6-luna:org::id", "", null }.All(m => !Provedor.EhClaude(m) && Provedor.ProvedorDe(m) == "openai");
        Ok("o id decide o lado", ok);
        Ok("modelo de raciocínio pega o GPT-6", Provedor.EhModeloDeRaciocinio("gpt-6-luna") && Provedor.EhModeloDeRaciocinio("o3") && !Provedor.EhModeloDeRaciocinio("gpt-4o-mini"));
    }

    private static void Reserva()
    {
        Ok("pares da tabela", Provedor.ReservaDe("gpt-6-luna") == "claude-haiku-5-5" && Provedor.ReservaDe("claude-haiku-5-5") == "gpt-6-luna"
            && Provedor.ReservaDe("gpt-6.1-sol") == "claude-sonnet-5-5" && Provedor.ReservaDe("claude-sonnet-5-5") == "gpt-6.1-sol"
            && Provedor.ReservaDe("gpt-6-astra") == "claude-opus-5-5" && Provedor.ReservaDe("claude-opus-5-5") == "gpt-6-astra");
        Ok("fora da tabela cai no barato do outro lado", Provedor.ReservaDe("gpt-9-qualquer") == "claude-haiku-5-5" && Provedor.ReservaDe("claude-inventado-9") == "gpt-6-luna");
        Ok("id escolhido tem que ser do outro lado", Provedor.ReservaDe("gpt-6-luna", "claude-opus-5-5") == "claude-opus-5-5"
            && Provedor.ReservaDe("claude-haiku-5-5", "gpt-6-sol") == "gpt-6-sol" && Provedor.ReservaDe("gpt-6-luna", "gpt-6-sol") == "claude-haiku-5-5");
        Ok("id com data acha o par", Provedor.ReservaDe("claude-sonnet-5-5-20260101") == "gpt-6.1-sol" && Provedor.ReservaDe("claude-haiku-5-5-20260101") == "gpt-6-luna");
        Ok("pares dos outros modelos", new[] { ("claude-opus-5", "gpt-6-astra"), ("claude-sonnet-5", "gpt-6.1-sol"), ("claude-fable-5", "gpt-6-astra"),
            ("claude-opus-4-8", "gpt-6-astra"), ("claude-sonnet-4-6", "gpt-6.1-sol"), ("claude-haiku-4-5", "gpt-6-luna") }.All(x => Provedor.ReservaDe(x.Item1) == x.Item2)
            && Provedor.ReservaDe("gpt-5.6-luna") == "claude-haiku-5-5" && Provedor.ReservaDe("gpt-5.6-sol") == "claude-sonnet-5-5");
        Ok("desligar a reserva", new[] { "nenhuma", "Nenhuma", "off", "não" }.All(v => Provedor.ReservaDe("gpt-6-luna", v) == null));
        Ok("vazio e null são auto", Provedor.ReservaDe("gpt-6-luna", "") == "claude-haiku-5-5" && Provedor.ReservaDe("gpt-6-luna", null) == "claude-haiku-5-5");
    }

    private static void SemCredito()
    {
        Ok("reconhece os dois lados", Provedor.FaltaDeCredito(429, CreditoOpenAI) && Provedor.FaltaDeCredito(400, CreditoClaude)
            && Provedor.FaltaDeCredito(402, J("{}")) && Provedor.FaltaDeCredito(402, null)
            && Provedor.FaltaDeCredito(402, J("""{"type": "error", "error": {"type": "billing_error", "message": "x"}}""")));
        Ok("OpenAI sem cobrança ativa vem como 401", Provedor.FaltaDeCredito(401, J("""{"error": {"code": "billing_not_active", "type": "billing_not_active", "message": "Your account is not active, please check your billing details on our website."}}"""))
            && !Provedor.FaltaDeCredito(401, J("""{"error": {"message": "Incorrect API key provided", "code": "invalid_api_key"}}""")));
        Ok("teto de gasto da organização", Provedor.FaltaDeCredito(400, J("""{"type": "error", "error": {"type": "invalid_request_error", "message": "You have reached your specified API usage limits. You will regain access on 2026-11-01 at 00:00 UTC."}}"""))
            && Provedor.FaltaDeCredito(400, J("""{"error": {"code": "billing_hard_limit_reached", "message": "x"}}""")));
        Ok("o que não é falta de crédito",
            !Provedor.FaltaDeCredito(429, J("""{"error": {"message": "Rate limit reached for gpt-6-luna on tokens per min (TPM).", "type": "tokens", "code": "rate_limit_exceeded"}}"""))
            && !Provedor.FaltaDeCredito(429, J("""{"type": "error", "error": {"type": "rate_limit_error", "message": "x"}}"""))
            && !Provedor.FaltaDeCredito(401, J("""{"error": {"message": "invalid api key"}}"""))
            && !Provedor.FaltaDeCredito(500, J("""{"error": {"message": "pane"}}"""))
            && !Provedor.FaltaDeCredito(529, J("""{"type": "error", "error": {"type": "overloaded_error", "message": "x"}}"""))
            && !Provedor.FaltaDeCredito(400, J("""{"error": {"message": "max_tokens must be positive"}}"""))
            && !Provedor.FaltaDeCredito(404, CreditoClaude) && !Provedor.FaltaDeCredito(400, null)
            && !Provedor.FaltaDeCredito(400, J("""{"error": "texto solto"}""")));
        Ok("mensagem de crédito com status errado não conta", !Provedor.FaltaDeCredito(200, CreditoOpenAI) && !Provedor.FaltaDeCredito(500, CreditoClaude));
    }

    private static void DisjuntorTeste()
    {
        double agora = 1000;
        var d = new Disjuntor(1800, () => agora);
        bool ok = !d.Aberto("openai");
        d.Abrir("openai");
        ok &= d.Aberto("openai") && !d.Aberto("claude");
        agora += 1799;
        ok &= d.Aberto("openai");
        agora += 2;
        ok &= !d.Aberto("openai") && d.MinutosRestantes("openai") == 0;
        Ok("aberto até passar o prazo", ok);
        d.Abrir("claude");
        bool m1 = d.MinutosRestantes("claude") == 30;
        agora += 1790;
        Ok("minutos restantes arredondam para cima", m1 && d.MinutosRestantes("claude") == 1);
        d.Abrir("openai");
        d.Fechar("openai");
        d.Fechar("openai");
        Ok("fechar (e fechar o que não está aberto)", !d.Aberto("openai"));
        // duas threads vencendo o prazo juntas
        var d2 = new Disjuntor(1, () => Environment.TickCount64 / 1000.0 + 10_000);
        d2.Abrir("x");
        var erros = 0;
        Parallel.For(0, 64, _ => { try { d2.Aberto("x"); } catch { Interlocked.Increment(ref erros); } });
        Ok("várias threads consultando juntas não dão erro", erros == 0);
    }

    private static void Raciocinio()
    {
        JsonIgual("haiku 5.5 none desliga", """{"thinking": {"type": "disabled"}}""", Provedor.ParametrosDeRaciocinio("claude-haiku-5-5", "none"));
        JsonIgual("sonnet 5.5 none é between_tools", """{"thinking": {"type": "between_tools"}, "output_config": {"effort": "low"}}""", Provedor.ParametrosDeRaciocinio("claude-sonnet-5-5", "none"));
        JsonIgual("opus 5.5 none vai em low", """{"output_config": {"effort": "low"}}""", Provedor.ParametrosDeRaciocinio("claude-opus-5-5", "none"));
        JsonIgual("fable none vai em low", """{"output_config": {"effort": "low"}}""", Provedor.ParametrosDeRaciocinio("claude-fable-5-1", "none"));
        Ok("pensando: adaptive com o esforço", new[] { "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1", "claude-opus-5" }
            .All(m => JsonNode.DeepEquals(Provedor.ParametrosDeRaciocinio(m, "medium"), J("""{"thinking": {"type": "adaptive"}, "output_config": {"effort": "medium"}}"""))));
        Ok("xhigh só onde existe", Json.Str(Provedor.ParametrosDeRaciocinio("claude-opus-5-5", "xhigh")["output_config"]!["effort"]) == "xhigh"
            && Json.Str(Provedor.ParametrosDeRaciocinio("claude-sonnet-4-6", "xhigh")["output_config"]!["effort"]) == "high");
        Ok("modelo antigo não recebe nada", new[] { "claude-haiku-4-5", "claude-haiku-4-5-20251001", "claude-sonnet-4-5", "claude-opus-4-5" }
            .All(m => Provedor.ParametrosDeRaciocinio(m, "high").Count == 0 && Provedor.ParametrosDeRaciocinio(m, "none").Count == 0));
        Ok("esforço estranho vira low", Json.Str(Provedor.ParametrosDeRaciocinio("claude-haiku-5-5", "turbo")["output_config"]!["effort"]) == "low");
    }

    private static int Marcas(JsonArray arr) => arr.OfType<JsonObject>().Sum(m => (m["content"] as JsonArray)?.OfType<JsonObject>().Count(b => b.ContainsKey("cache_control")) ?? (m.ContainsKey("cache_control") ? 1 : 0));

    private static void Converter()
    {
        var (s, m) = Provedor.ConverterEntrada("Seja breve.", J("""[{"role": "user", "content": "oi"}, {"role": "developer", "content": "Agora é segunda-feira."}]"""));
        JsonIgual("instruções viram system", """[{"type": "text", "text": "Seja breve.", "cache_control": {"type": "ephemeral"}}]""", s);
        JsonIgual("a hora vai depois do cache", """[{"role": "user", "content": [{"type": "text", "text": "oi", "cache_control": {"type": "ephemeral"}}, {"type": "text", "text": "Agora é segunda-feira."}]}]""", m);

        (_, m) = Provedor.ConverterEntrada("", J("""[{"role": "user", "content": "um"}, {"role": "assistant", "content": "dois"}, {"role": "user", "content": "três"}, {"role": "developer", "content": "nota"}]"""));
        Ok("histórico alterna e o cache fica na última fala dele",
            string.Join(",", m.Select(x => Json.Str(x!["role"]))) == "user,assistant,user" && Marcas(m) == 1
            && m[2]!["content"]![0]!.AsObject().ContainsKey("cache_control") && !m[2]!["content"]![1]!.AsObject().ContainsKey("cache_control"));

        (s, m) = Provedor.ConverterEntrada("", J("""[{"role": "developer", "content": "ficha"}, {"role": "user", "content": "um"}, {"role": "assistant", "content": "dois"}, {"role": "developer", "content": "nota da vez"}]"""));
        Ok("nota depois do assistente fica depois do cache", Json.Str(s[0]!["text"]) == "ficha" && m.Count == 3
            && JsonNode.DeepEquals(m[1]!["content"], J("""[{"type": "text", "text": "dois", "cache_control": {"type": "ephemeral"}}]"""))
            && JsonNode.DeepEquals(m[2]!["content"], J("""[{"type": "text", "text": "nota da vez"}]""")) && Marcas(m) == 1);

        (s, m) = Provedor.ConverterEntrada("Personagem.", J("""[{"role": "developer", "content": "Memória."}, {"role": "system", "content": "Regras."}, {"role": "user", "content": "oi"}]"""));
        Ok("developer do começo vai para o system", Json.Str(s[0]!["text"]) == "Personagem.\n\nMemória.\n\nRegras." && m.Count == 1);

        (_, m) = Provedor.ConverterEntrada("", J("""[{"role": "user", "content": "a"}, {"role": "assistant", "content": "b"}, {"role": "developer", "content": "nota do meio"}, {"role": "user", "content": "c"}]"""));
        Ok("developer depois do assistente espera a próxima fala", string.Join(",", m[2]!["content"]!.AsArray().Select(b => Json.Str(b!["text"]))) == "nota do meio,c");

        (_, m) = Provedor.ConverterEntrada("", J("""[{"role": "assistant", "content": "oi, sou eu"}]"""));
        Ok("não começa nem termina pelo assistente", m.Count == 3 && Json.Str(m[0]!["content"]![0]!["text"]) == Provedor.Abertura && Json.Str(m[2]!["content"]![0]!["text"]) == Provedor.Continue);

        (s, m) = Provedor.ConverterEntrada("Personagem.", J("""[{"role": "developer", "content": "Servidor em volta."}, {"role": "developer", "content": "Agora é segunda, 14:05. Puxe assunto."}]"""));
        Ok("conversa sem fala não põe a nota no cache", Json.Str(s[0]!["text"]) == "Personagem.\n\nServidor em volta." && m.Count == 1
            && Json.Str(m[0]!["content"]![0]!["text"]) == Provedor.Abertura && m[0]!["content"]![0]!.AsObject().ContainsKey("cache_control")
            && JsonNode.DeepEquals(m[0]!["content"]![1], J("""{"type": "text", "text": "Agora é segunda, 14:05. Puxe assunto."}""")));

        Ok("entrada vazia ainda tem uma mensagem", new[] { J("[]"), JsonValue.Create(""), null, J("""[{"role": "user", "content": "   "}]""") }
            .All(e => { var (_, mm) = Provedor.ConverterEntrada("regra", e); return mm.Count == 1 && Json.Str(mm[0]!["role"]) == "user"; }));

        (_, m) = Provedor.ConverterEntrada("", JsonValue.Create("traduza isto"));
        Ok("texto solto", Json.Str(m[0]!["content"]![0]!["text"]) == "traduza isto");

        (_, m) = Provedor.ConverterEntrada("", J("""[{"role": "user", "content": "a"}, {"role": "user", "content": "b"}, {"role": "assistant", "content": "c"}, {"role": "user", "content": "d"}]"""));
        Ok("mesmo papel seguido vira uma mensagem", m.Count == 3 && string.Join(",", m[0]!["content"]!.AsArray().Select(b => Json.Str(b!["text"]))) == "a,b");

        (_, m) = Provedor.ConverterEntrada("", J("""[{"role": "user", "content": [{"type": "input_text", "text": "olha"}, {"type": "input_image", "image_url": "data:image/png;base64,QUJD", "detail": "low"}, {"type": "input_image", "image_url": "https://exemplo.com/a.jpg"}, {"type": "input_image", "image_url": "ftp://nao"}]}]"""));
        var bl = m[0]!["content"]!.AsArray();
        Ok("partes e imagens", bl.Count == 3 && Json.Str(bl[0]!["text"]) == "olha"
            && JsonNode.DeepEquals(bl[1], J("""{"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": "QUJD"}}"""))
            && JsonNode.DeepEquals(bl[2]!["source"], J("""{"type": "url", "url": "https://exemplo.com/a.jpg"}""")) && bl[2]!.AsObject().ContainsKey("cache_control"));

        (_, m) = Provedor.ConverterEntrada("", J("""[{"role": "user", "content": "a"}, {"role": "assistant", "content": "b", "phase": "final_answer"}, {"role": "user", "content": "c"}, {"type": "reasoning", "summary": []}]"""));
        Ok("campos que só a OpenAI conhece somem", m.Count == 3 && !m[1]!.AsObject().ContainsKey("phase"));

        var entrada = J("""[{"role": "user", "content": [{"type": "input_image", "image_url": "data:image/png;base64,QUJD"}]}, {"role": "developer", "content": "nota"}]""");
        string antes = entrada!.ToJsonString();
        Provedor.ConverterEntrada("regra", entrada);
        Igual("não mexe na entrada", antes, entrada.ToJsonString());

        (s, m) = Provedor.ConverterEntrada("regra", J("""[{"role": "user", "content": "a"}, {"role": "assistant", "content": "b"}, {"role": "user", "content": "c"}]"""));
        Igual("um ponto de cache nas mensagens e um no system", 2, Marcas(m) + s.OfType<JsonObject>().Count(b => b.ContainsKey("cache_control")));
    }

    private static void CorposEResposta()
    {
        var (s, m) = Provedor.ConverterEntrada("regra", J("""[{"role": "user", "content": "oi"}]"""));
        var c = Provedor.CorpoClaude("claude-haiku-5-5", s, m, "low", 4000, stream: true);
        Ok("corpo do Claude sem temperatura", Json.Str(c["model"]) == "claude-haiku-5-5" && c["max_tokens"]!.GetValue<int>() == 4000
            && JsonNode.DeepEquals(c["thinking"], J("""{"type": "adaptive"}""")) && JsonNode.DeepEquals(c["output_config"], J("""{"effort": "low"}"""))
            && c["stream"]!.GetValue<bool>() && new[] { "temperature", "top_p", "top_k", "tools", "tool_choice" }.All(k => !c.ContainsKey(k)));
        var t = Provedor.CorpoClaude("claude-haiku-5-5", s, m, "none", 2000);
        Ok("tradutor no Haiku: pensamento desligado", JsonNode.DeepEquals(t["thinking"], J("""{"type": "disabled"}""")) && !t.ContainsKey("output_config"));
        var o = Provedor.CorpoOpenAI("gpt-6-luna", "regra", (JsonArray)J("""[{"role": "user", "content": "oi"}]""")!, "none", 1000);
        Ok("corpo da OpenAI com store false e reasoning", o["store"]!.GetValue<bool>() == false && Json.Str(o["reasoning"]!["effort"]) == "none" && o["stream"]!.GetValue<bool>()
            && Json.Str(o["instructions"]) == "regra" && o["max_output_tokens"]!.GetValue<int>() == 1000);
        Ok("gpt-4o-mini não recebe reasoning", !Provedor.CorpoOpenAI("gpt-4o-mini", null, new JsonArray(), "low", 100).ContainsKey("reasoning"));

        var r = Provedor.LerRespostaClaude((JsonObject)J("""{"stop_reason": "end_turn", "content": [{"type": "thinking", "thinking": "", "signature": "abc"}, {"type": "text", "text": "olá"}, {"type": "text", "text": " mundo"}, {"type": "tool_use", "id": "tu_1", "name": "f", "input": {"a": 1}}], "usage": {"input_tokens": 100, "cache_read_input_tokens": 800, "cache_creation_input_tokens": 50, "output_tokens": 30}}""")!);
        Ok("lê os blocos pelo tipo", r.Texto == "olá mundo" && r.Parada == "end_turn"
            && JsonNode.DeepEquals(r.Uso, J("""{"input_tokens": 950, "output_tokens": 30, "input_tokens_details": {"cached_tokens": 800, "cache_write_tokens": 50}}""")));
        var rec = Provedor.LerRespostaClaude((JsonObject)J("""{"stop_reason": "refusal", "content": [], "stop_details": {"type": "refusal", "category": "cyber", "explanation": "não posso"}}""")!);
        Ok("guarda a explicação da recusa", rec.Parada == "refusal" && Json.Str(rec.Detalhe?["explanation"]) == "não posso"
            && Provedor.LerRespostaClaude((JsonObject)J("""{"stop_details": "texto"}""")!).Detalhe == null);
        var vazia = Provedor.LerRespostaClaude(new JsonObject());
        Ok("resposta vazia", vazia.Texto == "" && vazia.Parada == "" && vazia.Uso == null && Provedor.UsoNormalizado(null) == null);
        var uso = (JsonObject)J("""{"input_tokens": 10000, "output_tokens": 1000, "input_tokens_details": {"cached_tokens": 8000, "cache_write_tokens": 500}}""")!;
        double esperado = (1500 * 2 + 8000 * 0.10 + 500 * 2.5 + 1000 * 10) / 1e6;
        Ok("custo com cache", Math.Abs((Provedor.Custo("claude-sonnet-5-5", uso) ?? -1) - esperado) < 1e-12);
        Ok("custo de modelo desconhecido", Provedor.Custo("claude-inventado-9", (JsonObject)J("""{"input_tokens": 1}""")!) == null
            && Provedor.Custo("gpt-6-luna", null) == null && Provedor.Custo("claude-haiku-4-5-20251001", (JsonObject)J("""{"input_tokens": 1000, "output_tokens": 10}""")!) != null
            && Provedor.Custo("gpt-5-mini", (JsonObject)J("""{"input_tokens": 1000, "output_tokens": 10}""")!) != null);
    }

    // ── streams falsos ──

    private static string Sse(params string[] eventos) => string.Concat(eventos.Select(e => "data: " + e + "\r\n\r\n"));

    private static string SseClaude(params (string Tipo, string Dados)[] eventos) =>
        string.Concat(eventos.Select(e => "event: " + e.Tipo + "\n" + "data: " + e.Dados + "\n\n"));

    /// <summary>Passa o stream pelo mesmo SseParser do cliente e devolve os pedaços e o fim.</summary>
    private static (string Pedacos, ResultadoStream Fim) Ler(ILeitorStream leitor, string sse)
    {
        var sb = new StringBuilder();
        foreach (var item in SseParser.Create(new MemoryStream(Encoding.UTF8.GetBytes(sse))).Enumerate())
        {
            if (item.Data is not { Length: > 0 } d || d == "[DONE]") continue;
            sb.Append(leitor.Processar(d));
        }
        return (sb.ToString(), leitor.Fim());
    }

    private const string OpenAICompleto =
        """{"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[{"type":"reasoning","summary":[]},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Olá, mundo!"}]}],"usage":{"input_tokens":1000,"output_tokens":200,"input_tokens_details":{"cached_tokens":400}}}}""";

    private static string StreamOpenAIOk() => Sse(
        """{"type":"response.created","response":{"id":"resp_1"}}""",
        """{"type":"response.output_text.delta","delta":"Olá"}""",
        """{"type":"response.output_text.delta","delta":", mun"}""",
        """{"type":"response.output_text.delta","delta":"do!"}""",
        OpenAICompleto) + "data: [DONE]\r\n\r\n";

    private static void StreamOpenAI()
    {
        var (p, f) = Ler(new LeitorOpenAI(), StreamOpenAIOk());
        Ok("OpenAI: deltas e texto oficial", p == "Olá, mundo!" && f.Texto == "Olá, mundo!" && f.Incompleta == null, $"{p} / {f.Texto}");
        double esperado = (600 * 0.10 + 400 * 0.01 + 200 * 0.50) / 1e6;
        Ok("OpenAI: custo do uso (cache lido mais barato)", Math.Abs((Provedor.Custo("gpt-6-luna", f.Uso) ?? -1) - esperado) < 1e-12);
        var (_, inc) = Ler(new LeitorOpenAI(), Sse("""{"type":"response.output_text.delta","delta":"a"}""",
            """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}}"""));
        Ok("OpenAI: incompleta pelo teto", inc.Incompleta == "max_output_tokens" && inc.Texto == "a");
        Lanca<SemCreditoErro>("OpenAI: crédito acabou antes do texto vira troca",
            () => Ler(new LeitorOpenAI(), Sse("""{"type":"error","code":"insufficient_quota","message":"You exceeded your current quota"}""")), e => e.Lado == "openai");
        Lanca<ErroIA>("OpenAI: crédito acabou depois do texto é só erro", () => Ler(new LeitorOpenAI(), Sse(
            """{"type":"response.output_text.delta","delta":"meia resposta"}""",
            """{"type":"response.failed","response":{"error":{"code":"insufficient_quota","message":"You exceeded your current quota"}}}""")), e => e is not SemCreditoErro);
        Lanca<ErroIA>("OpenAI: erro de servidor no stream não troca", () => Ler(new LeitorOpenAI(), Sse(
            """{"type":"error","code":"server_error","message":"The server had an error"}""")), e => e is not SemCreditoErro);
        Lanca<ErroIA>("OpenAI: stream cortado sem completed", () => Ler(new LeitorOpenAI(), Sse("""{"type":"response.output_text.delta","delta":"a"}""")), e => e.Message.Contains("caiu"));
        // separadores de 2 e de 4 bytes e evento quebrado no meio do pacote: o SseParser junta
        var (p2, _) = Ler(new LeitorOpenAI(), "data: {\"type\":\"response.output_text.delta\",\"delta\":\"x\"}\n\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"y\"}\r\n\r\n: comentário\n\n" + Sse(OpenAICompleto));
        Igual("OpenAI: separadores misturados e comentário", "xy", p2);
    }

    private static string StreamClaudeOk() => SseClaude(
        ("message_start", """{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":100,"cache_read_input_tokens":800,"cache_creation_input_tokens":50,"output_tokens":1}}}"""),
        ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"pensando"}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"abc"}}"""),
        ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
        ("ping", """{"type":"ping"}"""),
        ("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Oi, "}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"tudo bem?"}}"""),
        ("content_block_stop", """{"type":"content_block_stop","index":1}"""),
        ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":30}}"""),
        ("message_stop", """{"type":"message_stop"}"""));

    private static void StreamClaude()
    {
        var (p, f) = Ler(new LeitorClaude(), StreamClaudeOk());
        Ok("Claude: só o texto (o pensamento não aparece)", p == "Oi, tudo bem?" && f.Texto == "Oi, tudo bem?" && f.Incompleta == null, p);
        Ok("Claude: uso normalizado com o cache", JsonNode.DeepEquals(f.Uso, J("""{"input_tokens": 950, "output_tokens": 30, "input_tokens_details": {"cached_tokens": 800, "cache_write_tokens": 50}}""")), f.Uso?.ToJsonString() ?? "");
        var (_, cort) = Ler(new LeitorClaude(), SseClaude(
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"meio"}}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"max_tokens"},"usage":{"output_tokens":9}}"""),
            ("message_stop", """{"type":"message_stop"}""")));
        Ok("Claude: max_tokens vira incompleta", cort.Incompleta == "max_output_tokens" && cort.Texto == "meio");
        Lanca<ErroIA>("Claude: sobrecarga no meio do stream é erro, não troca", () => Ler(new LeitorClaude(), SseClaude(
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"meio"}}"""),
            ("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""))), e => e is not SemCreditoErro && e.Message.Contains("529"));
        Lanca<ErroIA>("Claude: sobrecarga antes do texto também não troca", () => Ler(new LeitorClaude(), SseClaude(
            ("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""))), e => e is not SemCreditoErro);
        Lanca<SemCreditoErro>("Claude: crédito acabou antes do texto vira troca", () => Ler(new LeitorClaude(), SseClaude(
            ("error", """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API."}}"""))), e => e.Lado == "claude");
        Lanca<ErroIA>("Claude: stream cortado", () => Ler(new LeitorClaude(), SseClaude(
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"a"}}"""))), e => e.Message.Contains("caiu"));
    }

    // ── cliente contra um transporte falso ──

    private sealed class Falso : HttpMessageHandler
    {
        public readonly List<(string Url, string Corpo, string Cabecalhos)> Pedidos = new();
        public Func<HttpRequestMessage, string, HttpResponseMessage> Resposta = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            string corpo = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
            Pedidos.Add((req.RequestUri!.ToString(), corpo, req.Headers.ToString()));
            return Resposta(req, corpo);
        }
    }

    private static HttpResponseMessage Http(int status, string corpo, string tipo = "application/json") =>
        new((HttpStatusCode)status) { Content = new StringContent(corpo, Encoding.UTF8, tipo) };

    private static PedidoIA Pedido(string modelo = "gpt-6-luna") => new()
    {
        Modelo = modelo, Esforco = "low", Instrucoes = "regra", MaxSaida = 500,
        Itens = (JsonArray)J("""[{"role": "user", "content": "oi"}, {"role": "developer", "content": "Agora é sábado."}]""")!,
    };

    /// <summary>Stream que manda um pedaço e depois fica parado até ser cancelado (o stream de verdade, devagar).</summary>
    private sealed class Lento : Stream
    {
        private byte[]? _primeiro = Encoding.UTF8.GetBytes(Sse("""{"type":"response.output_text.delta","delta":"come"}"""));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_primeiro != null) { int n = Math.Min(buffer.Length, _primeiro.Length); _primeiro.AsSpan(0, n).CopyTo(buffer.Span); _primeiro = n == _primeiro.Length ? null : _primeiro[n..]; return n; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private static async Task Cliente()
    {
        var chaves = new Dictionary<string, string?> { ["openai"] = "sk-proj-teste-OPENAI-0000000000", ["anthropic"] = "sk-ant-teste-CLAUDE-0000000000" };
        double agora = 0;

        // 1. OpenAI sem crédito → Claude responde, o disjuntor abre, o pedido seguinte já vai direto
        var f = new Falso();
        f.Resposta = (req, _) => req.RequestUri!.Host.Contains("openai")
            ? Http(429, CreditoOpenAI.ToJsonString())
            : Http(200, StreamClaudeOk(), "text/event-stream");
        var cli = new ClienteIA(n => chaves[n], f, new Disjuntor(1800, () => agora));
        var pedacos = new StringBuilder();
        string? trocouPara = null;
        var r = await cli.Responder(Pedido(), t => pedacos.Append(t), m => trocouPara = m, CancellationToken.None);
        Ok("sem crédito na OpenAI: o Claude responde", r.Texto == "Oi, tudo bem?" && r.Modelo == "claude-haiku-5-5" && r.Trocou?.Motivo == "sem crédito"
            && trocouPara == "claude-haiku-5-5" && pedacos.ToString() == "Oi, tudo bem?", $"{r.Texto} {r.Modelo} {r.Trocou}");
        Ok("o lado sem crédito fica de molho", cli.Disjuntor.Aberto("openai") && !cli.Disjuntor.Aberto("claude"));
        var corpoClaude = Json.Objeto(f.Pedidos[1].Corpo)!;
        Ok("pedido do Claude: cabeçalhos, stream, system e a nota depois do cache",
            f.Pedidos[1].Cabecalhos.Contains("x-api-key: sk-ant-teste") && f.Pedidos[1].Cabecalhos.Contains("anthropic-version: 2023-06-01")
            && corpoClaude["stream"]!.GetValue<bool>() && Json.Str(corpoClaude["system"]![0]!["text"]) == "regra"
            && Json.Str(corpoClaude["messages"]![0]!["content"]![1]!["text"]) == "Agora é sábado." && !corpoClaude.ContainsKey("temperature"));
        var corpoOpenAI = Json.Objeto(f.Pedidos[0].Corpo)!;
        Ok("pedido da OpenAI: Bearer, store false, esforço", f.Pedidos[0].Cabecalhos.Contains("Bearer sk-proj-teste") && corpoOpenAI["store"]!.GetValue<bool>() == false
            && Json.Str(corpoOpenAI["reasoning"]!["effort"]) == "low" && corpoOpenAI["input"]!.AsArray().Count == 2);
        int antes = f.Pedidos.Count;
        r = await cli.Responder(Pedido(), null, null, CancellationToken.None);
        Ok("com o disjuntor aberto vai direto para a reserva", f.Pedidos.Count == antes + 1 && f.Pedidos[^1].Url.Contains("anthropic") && r.Trocou?.Motivo.StartsWith("sem crédito, de molho") == true);
        agora += 1801;
        f.Resposta = (req, _) => req.RequestUri!.Host.Contains("openai") ? Http(200, StreamOpenAIOk(), "text/event-stream") : Http(500, "{}");
        r = await cli.Responder(Pedido(), null, null, CancellationToken.None);
        Ok("passado o prazo, o lado volta a ser tentado", r.Modelo == "gpt-6-luna" && r.Trocou == null && r.Texto == "Olá, mundo!" && r.Custo > 0);

        // 2. erro de servidor no meio do stream do Claude: erro, sem trocar de lado
        f = new Falso();
        f.Resposta = (req, _) => req.RequestUri!.Host.Contains("anthropic")
            ? Http(200, SseClaude(("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
                ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"meio"}}"""),
                ("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""")), "text/event-stream")
            : Http(200, StreamOpenAIOk(), "text/event-stream");
        cli = new ClienteIA(n => chaves[n], f, new Disjuntor());
        try { await cli.Responder(Pedido("claude-sonnet-5-5"), null, null, CancellationToken.None); Ok("sobrecarga no meio não troca de lado", false, "não lançou"); }
        catch (SemCreditoErro) { Ok("sobrecarga no meio não troca de lado", false, "virou falta de crédito"); }
        catch (ErroIA) { Ok("sobrecarga no meio não troca de lado", f.Pedidos.Count == 1 && !cli.Disjuntor.Aberto("claude")); }

        // 2b. 5xx antes do stream também não troca
        f.Resposta = (req, _) => req.RequestUri!.Host.Contains("anthropic") ? Http(529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""") : Http(200, StreamOpenAIOk(), "text/event-stream");
        f.Pedidos.Clear();
        try { await cli.Responder(Pedido("claude-sonnet-5-5"), null, null, CancellationToken.None); Ok("529 não troca de lado", false, "não lançou"); }
        catch (ErroIA e) { Ok("529 não troca de lado", f.Pedidos.Count == 1 && e is not SemCreditoErro && e.Message.Contains("529")); }

        // 3. sem a chave do principal: a reserva assume sem abrir o disjuntor
        f = new Falso();
        f.Resposta = (_, _) => Http(200, StreamOpenAIOk(), "text/event-stream");
        cli = new ClienteIA(n => n == "openai" ? chaves["openai"] : null, f, new Disjuntor());
        r = await cli.Responder(Pedido("claude-sonnet-5-5"), null, null, CancellationToken.None);
        Ok("sem chave do principal: a reserva do outro lado responde", r.Modelo == "gpt-6.1-sol" && r.Trocou?.Motivo == "sem chave" && !cli.Disjuntor.Aberto("claude"));

        // 4. sem chave nenhuma
        cli = new ClienteIA(_ => null, new Falso(), new Disjuntor());
        try { await cli.Responder(Pedido(), null, null, CancellationToken.None); Ok("sem chave nenhuma dá erro claro", false, "não lançou"); }
        catch (ErroIA e) { Ok("sem chave nenhuma dá erro claro", e.Message.Contains("Sem chave")); }

        // 5. a chave nunca aparece na mensagem de erro (a OpenAI devolve parte dela no 401)
        f = new Falso();
        f.Resposta = (_, _) => Http(401, """{"error":{"message":"Incorrect API key provided: sk-proj-abc123XYZ********************wxyz. You can find your API key at https://platform.openai.com.","code":"invalid_api_key"}}""");
        cli = new ClienteIA(n => chaves[n], f, new Disjuntor());
        try { await cli.Responder(Pedido(), null, null, CancellationToken.None); Ok("401 sem a chave na mensagem", false, "não lançou"); }
        catch (ErroIA e) { Ok("401 sem a chave na mensagem", !e.Message.Contains("sk-") && e.Message.Contains("401") && f.Pedidos.Count == 1, e.Message); }
        f.Resposta = (_, _) => Http(400, """{"error":{"message":"bad thing with sk-proj-ZZZZ1234abcd inside"}}""");
        try { await cli.Responder(Pedido(), null, null, CancellationToken.None); }
        catch (ErroIA e) { Ok("400 com algo parecido com chave é limpo", !e.Message.Contains("sk-proj") && e.Message.Contains("(chave)"), e.Message); }

        // 6. cancelar para o stream na hora
        f = new Falso();
        f.Resposta = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Lento()) };
        cli = new ClienteIA(n => chaves[n], f, new Disjuntor());
        using (var cts = new CancellationTokenSource())
        {
            var recebeu = new TaskCompletionSource();
            var tarefa = cli.Responder(Pedido(), _ => { recebeu.TrySetResult(); cts.Cancel(); }, null, cts.Token);
            var relogio = System.Diagnostics.Stopwatch.StartNew();
            try { await tarefa.WaitAsync(TimeSpan.FromSeconds(5)); Ok("cancelar para o stream", false, "terminou sem cancelar"); }
            catch (OperationCanceledException) { Ok("cancelar para o stream", recebeu.Task.IsCompleted && relogio.ElapsedMilliseconds < 2000, relogio.ElapsedMilliseconds + " ms"); }
            catch (TimeoutException) { Ok("cancelar para o stream", false, "não parou em 5 s"); }
        }

        // 7. stream que fica mudo: o vigia desiste sozinho
        f = new Falso();
        f.Resposta = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Lento()) };
        cli = new ClienteIA(n => chaves[n], f, new Disjuntor()) { EsperaParado = TimeSpan.FromMilliseconds(300) };
        try { await cli.Responder(Pedido(), null, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); Ok("stream parado vira erro", false, "não lançou"); }
        catch (ErroIA e) { Ok("stream parado vira erro", e.Message.Contains("parou"), e.Message); }

        // 8. testar a chave sem gastar geração (GET /models)
        f = new Falso();
        f.Resposta = (req, _) => req.Method == HttpMethod.Get ? Http(200, """{"data":[{"id":"a"},{"id":"b"}]}""") : Http(500, "{}");
        cli = new ClienteIA(n => chaves[n], f, new Disjuntor());
        var (ok1, m1) = await cli.TestarChave("openai", CancellationToken.None);
        var (ok2, _) = await cli.TestarChave("claude", CancellationToken.None);
        Ok("testar chave pelo GET /models", ok1 && ok2 && m1.Contains("2 modelo") && f.Pedidos.All(p => p.Corpo == "") && f.Pedidos[1].Url.Contains("anthropic.com/v1/models"), m1);
        f.Resposta = (_, _) => Http(401, """{"error":{"message":"Incorrect API key provided: sk-proj-abc123********wxyz"}}""");
        var (ok3, m3) = await cli.TestarChave("openai", CancellationToken.None);
        Ok("testar chave recusada não mostra a chave", !ok3 && !m3.Contains("sk-"), m3);
    }

    // ── tradutor ──

    private static async Task GoogleTeste()
    {
        using (var req = Google.Pedido("Olá & tchau", "auto", "en"))
        {
            string corpo = await req.Content!.ReadAsStringAsync();
            Ok("pedido do Google: POST com o texto no corpo", req.Method == HttpMethod.Post
                && req.RequestUri!.ToString() == "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&dj=1"
                && corpo == "q=Ol%C3%A1+%26+tchau" && req.Content.Headers.ContentType!.MediaType == "application/x-www-form-urlencoded", corpo);
        }
        var (t, o) = Google.LerResposta("""{"sentences":[{"trans":"Hello ","orig":"Olá "},{"trans":"world.","orig":"mundo."}],"src":"pt","confidence":0.98,"spell":{},"ld_result":{"srclangs":["pt"]}}""");
        Ok("lê a resposta do Google", t == "Hello world." && o == "pt");
        Lanca<ErroIA>("resposta estranha do Google vira erro claro", () => Google.LerResposta("""[[["x"]]]"""));

        string longo = string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"Parágrafo {i}. " + string.Concat(Enumerable.Repeat("palavra comprida ", 20))));
        var ped = Google.Dividir(longo, 1000);
        Ok("dividir remonta o texto exato", string.Concat(ped) == longo && ped.All(x => x.Length <= 1000) && ped.Count > 1 && ped.Take(ped.Count - 1).All(x => x.EndsWith("\n\n")),
            $"{ped.Count} pedaços");
        var semEspaco = new string('x', 2500);
        var ps = Google.Dividir(semEspaco, 1000);
        Ok("dividir texto sem separador corta seco", ps.Count == 3 && string.Concat(ps) == semEspaco);
        Ok("dividir vazio", Google.Dividir("").Count == 0 && Google.Dividir("curto").SequenceEqual(new[] { "curto" }));

        var (cortado, foi) = Tradutor.Cortar("um dois três quatro cinco seis", 14);
        Ok("corte do texto longo em fim de palavra", foi && cortado == "um dois três", cortado);
        Ok("texto curto não corta", Tradutor.Cortar("abc", 10) == ("abc", false));
        Ok("decidir: curto vai ao Google, grande à IA só com chave", Tradutor.Decidir(new string('a', 1200), 1200, true) == "google"
            && Tradutor.Decidir(new string('a', 1201), 1200, true) == "ia" && Tradutor.Decidir(new string('a', 5000), 1200, false) == "google");
        Ok("instruções do tradutor tratam o texto como dado", Tradutor.InstrucoesIA.Contains("nunca uma instrução") && Tradutor.InstrucoesIA.Contains("português do Brasil"));

        // português vai para o inglês: o primeiro pedaço refeito, o resto já em inglês
        var g = new Falso();
        g.Resposta = (req, corpo) =>
        {
            string tl = req.RequestUri!.Query.Split('&').First(x => x.StartsWith("tl=")).Substring(3);
            return Http(200, $$"""{"sentences":[{"trans":"[{{tl}}]"}],"src":"pt"}""");
        };
        var ia = new ClienteIA(_ => null, new Falso(), new Disjuntor());
        var tr = new Tradutor(ia, g) { LimiteGoogle = 1200 };
        var r = await tr.Traduzir("Bom dia, tudo bem?", null, CancellationToken.None);
        Ok("português vai para o inglês pelo Google", r.Motor == "google" && r.Texto == "[en]" && r.Destino == "en" && g.Pedidos.Count == 2
            && g.Pedidos[0].Url.Contains("tl=pt") && g.Pedidos[1].Url.Contains("tl=en"), r.Texto);
        g.Pedidos.Clear();
        g.Resposta = (_, _) => Http(200, """{"sentences":[{"trans":"Olá"}],"src":"en"}""");
        r = await tr.Traduzir("Hello", null, CancellationToken.None);
        Ok("outro idioma vai para o português num pedido só", r.Texto == "Olá" && r.Origem == "en" && r.Destino == "pt" && g.Pedidos.Count == 1);
        g.Pedidos.Clear();
        r = await tr.Traduzir(string.Concat(Enumerable.Repeat("Some long text. ", 400)), null, CancellationToken.None);
        Ok("grande sem chave de IA vai ao Google em pedaços, com aviso", r.Motor == "google" && g.Pedidos.Count == 2 && r.Aviso.Contains("sem chave"), r.Aviso);

        // grande com chave: IA no par barato, sem raciocínio; a IA falhando, o Google assume
        var fia = new Falso();
        fia.Resposta = (_, _) => Http(200, Sse("""{"type":"response.output_text.delta","delta":"Tradução"}""",
            """{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"Tradução"}]}],"usage":{"input_tokens":3000,"output_tokens":2000}}}"""), "text/event-stream");
        ia = new ClienteIA(n => n == "openai" ? "sk-teste-0000000000000000" : null, fia, new Disjuntor());
        tr = new Tradutor(ia, g);
        string grande = string.Concat(Enumerable.Repeat("This is a long text. ", 100));
        var vistos = new StringBuilder();
        r = await tr.Traduzir(grande, x => vistos.Append(x), CancellationToken.None);
        var corpoIA = Json.Objeto(fia.Pedidos[0].Corpo)!;
        Ok("grande com chave vai à IA, luna sem raciocínio", r.Motor == "ia" && r.Texto == "Tradução" && vistos.ToString() == "Tradução"
            && Json.Str(corpoIA["model"]) == "gpt-6-luna" && Json.Str(corpoIA["reasoning"]!["effort"]) == "none"
            && Json.Str(corpoIA["input"]![0]!["content"]) == grande && Json.Str(corpoIA["instructions"]) == Tradutor.InstrucoesIA && r.Custo > 0);
        fia.Resposta = (_, _) => Http(500, "{}");
        g.Pedidos.Clear();
        r = await tr.Traduzir(grande, null, CancellationToken.None);
        Ok("a IA falhando, o Google assume e avisa", r.Motor == "google" && r.Aviso.Contains("Usei o Google") && g.Pedidos.Count >= 1, r.Aviso);
        Ok("nomes dos idiomas", Tradutor.NomeDoIdioma("en") == "inglês" && Tradutor.NomeDoIdioma("pt-PT") == "português" && Tradutor.NomeDoIdioma("xx") == "xx");
    }

    private static void Textos()
    {
        Ok("limpa chave de texto", Segredo.Limpar("chave sk-ant-api03-AbC_123-xyz e sk-proj-QWE987 fim") == "chave (chave) e (chave) fim", Segredo.Limpar("chave sk-ant-api03-AbC_123-xyz e sk-proj-QWE987 fim"));
        Ok("não estraga texto comum", Segredo.Limpar("o desk-top e o risk-free") == "o desk-top e o risk-free");
        Igual("tira Markdown da tela", "Título\nTexto com negrito e mais.\n- item\nfim", Limpeza.ParaTela("## Título\nTexto com **negrito** e __mais__.\n* item\n```\nfim"));
        Ok("rótulos dos modelos", Modelos.Rotulo("gpt-6-luna") == "GPT-6 Luna" && Modelos.Rotulo("claude-haiku-5-5") == "Claude Haiku 5.5"
            && Modelos.Rotulo("claude-opus-4-8") == "Claude Opus 4.8" && Modelos.Rotulo("gpt-7-nova") == "GPT-7 Nova", Modelos.Rotulo("gpt-7-nova"));
        Ok("o 6.1 Sol não recebe none", Modelos.Esforco("gpt-6.1-sol", "none") == "low" && Modelos.Esforco("gpt-6-luna", "none") == "none");
        Ok("dinheiro", Modelos.Dinheiro(0.00042) == "US$ 0,0004" && Modelos.Dinheiro(0.00001) == "< US$ 0,0001" && Modelos.Dinheiro(0.123) == "US$ 0,123" && Modelos.Dinheiro(null) == "",
            Modelos.Dinheiro(0.00042));
    }

    private static void ImagemTeste()
    {
        Ok("ajusta o print do ultrawide para 2576", ImagemColada.Ajustar(3440, 1440, 2576) == (2576, 1078) && ImagemColada.Ajustar(800, 600, 2576) == (800, 600)
            && ImagemColada.Ajustar(1080, 1920, 1000) == (563, 1000));
        using var bmp = new System.Drawing.Bitmap(3000, 1000);
        using (var gr = System.Drawing.Graphics.FromImage(bmp))
        {
            gr.Clear(System.Drawing.Color.DarkSlateBlue);
            gr.FillEllipse(System.Drawing.Brushes.Orange, 100, 100, 800, 800);
        }
        var img = ImagemColada.Preparar(bmp, 2576);
        var dados = Convert.FromBase64String(img.DataUrl[(img.DataUrl.IndexOf(',') + 1)..]);
        Ok("imagem colada vira PNG em base64 no tamanho certo", img.DataUrl.StartsWith("data:image/png;base64,") && img.Largura == 2576 && img.Altura == 859
            && dados.Length == img.Bytes && dados[0] == 0x89 && dados[1] == (byte)'P' && img.MiniL == 128 && img.Miniatura.Length == img.MiniL * img.MiniA * 4,
            $"{img.Largura}x{img.Altura} {img.Bytes} bytes");
        var (_, m) = Provedor.ConverterEntrada("", new JsonArray { new JsonObject { ["role"] = "user", ["content"] = new JsonArray {
            new JsonObject { ["type"] = "input_text", ["text"] = "o que é isso?" }, new JsonObject { ["type"] = "input_image", ["image_url"] = img.DataUrl } } } });
        Ok("o provedor manda a imagem colada como bloco do Claude", Json.Str(m[0]!["content"]![1]!["source"]!["media_type"]) == "image/png"
            && Json.Str(m[0]!["content"]![1]!["source"]!["data"]).Length == img.DataUrl.Length - "data:image/png;base64,".Length);
    }

    // só DataObject em memória: a área de transferência de verdade não é tocada
    private static void ReservaDoCtrlC()
    {
        byte[] quadros = { 7, 0, 255, 42, 9 };
        var fluxo = new MemoryStream(quadros) { Position = 3 };
        var origem = new System.Windows.Forms.DataObject();
        origem.SetData("After Effects Keyframes", false, fluxo);
        origem.SetData(System.Windows.Forms.DataFormats.UnicodeText, false, "oi");
        var c = Selecao.Copiar(origem);
        var copia = c == null ? null : ((System.Windows.Forms.IDataObject)c).GetData("After Effects Keyframes", false) as MemoryStream;
        Ok("guarda formato próprio inteiro, em cópia própria", copia != null && !ReferenceEquals(copia, fluxo) && copia.Position == 0
            && copia.ToArray().SequenceEqual(quadros));
        Ok("guarda o texto junto", c != null && c.ContainsText() && c.GetText() == "oi");
        var corrida = Selecao.Copiar(origem, prazoMs: 0);
        Ok("passado o prazo, guarda só o básico", corrida != null && corrida.ContainsText() && corrida.GetText() == "oi"
            && !((System.Windows.Forms.IDataObject)corrida).GetDataPresent("After Effects Keyframes", false));
        Ok("nada para guardar vira null", Selecao.Copiar(new System.Windows.Forms.DataObject()) == null && Selecao.Copiar(null) == null);
    }

    private static void Sondas()
    {
        string uia = Selecao.SondaUia();
        Ok("sonda: IUIAutomation2 aceita e devolve os timeouts (confere a vtable)", uia == "ok", uia);
        Ok("sonda: número de sequência da área de transferência", Selecao.Sequencia() != 0);
        Ok("janela nula não conta como terminal", !Selecao.EhTerminal(IntPtr.Zero));
    }
}
