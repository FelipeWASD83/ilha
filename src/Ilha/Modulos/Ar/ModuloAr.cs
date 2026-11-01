using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.Ar;

public sealed class ConfigAr
{
    // origem do servidor do projeto ar-do-quarto (http://IP:8088); vazio = o cartão explica como configurar.
    // A chave não fica aqui: mora no Gerenciador de Credenciais ("Ilha/ar-do-quarto").
    [JsonPropertyName("endereco")] public string Endereco { get; set; } = "";
}

/// <summary>
/// O ar-condicionado pela ilha, reaproveitando o servidor do ar-do-quarto (o mesmo que o site e a nova aba usam):
/// liga e desliga, temperatura de 1 em 1 grau, modo e vento. Abre pelo menu da ilha ou pelo atalho "ar" (Ctrl+Alt+A);
/// sozinho, só aparece num alerta curto quando o estado do ar muda.
/// </summary>
[Modulo("ar", "Ar-condicionado")]
internal sealed class ModuloAr : Modulo
{
    private ConfigAr _cfg = new();
    private AtividadeAr? _atv;

    public override Type? TipoConfig => typeof(ConfigAr);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigAr>(cfg);
        _atv = new AtividadeAr(S, () => _cfg.Endereco, GuardarEndereco);
        // a atividade nunca liga (não fica na pílula), mas precisa estar na ilha para o Expandir e para os alertas
        S.Ilha.Adicionar(_atv);
        S.Atalhos.Registrar("ar", new Atalho { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = 0x41 }, () => _atv?.Alternar());
        var atv = _atv;
        S.Menu.Adicionar(new ItemDoMenu
        {
            Id = "ar",
            Nome = "Ar-condicionado",
            Abrir = atv.Abrir,
            Detalhe = atv.DetalheDoMenu,
            Icone = (dc, palco, r) => IconesAr.Aparelho(dc, palco.G.Fabrica, r, atv.Conhecido),
            Ordem = 40,
        });
        _competitivo = S.ContextoAtual().Competitivo;
        _atv.Comecar();
        if (_competitivo) _atv.PausarVigia(true);
    }

    private bool _competitivo;

    public override void Contexto(Contexto ctx)
    {
        if (ctx.Competitivo == _competitivo) return;
        _competitivo = ctx.Competitivo;
        _atv?.PausarVigia(_competitivo);
    }

    private void GuardarEndereco(string endereco)
    {
        _cfg.Endereco = endereco;
        Salvar(_cfg);
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigAr>(cfg);
        _atv?.ConfigMudou();
    }

    public override void Parar() => _atv?.Parar();

    // ------------------------------------------------------------------ autoteste

    // respostas de verdade do simulador do ar-do-quarto (perfil padrão dele), copiadas em 01/11/2026
    private const string JsonOk = """
        {"servidor": {"versao": "1.0.0", "simulado": true}, "conexao": {"situacao": "ok", "mensagem": "", "pausado_ate": null}, "aparelho": {"nome": "Ar do quarto", "modelo": "RAC_056905_WW"}, "capacidades": {"modos": ["COOL", "AIR_DRY", "FAN", "HEAT"], "temperatura": {"COOL": {"min": 18, "max": 30, "passo": 0.5}, "AIR_DRY": {"min": 18, "max": 30, "passo": 0.5}, "HEAT": {"min": 16, "max": 30, "passo": 0.5}}, "vento": ["AUTO", "NATURE", "LOW", "LOW_MID", "MID", "MID_HIGH", "HIGH"], "swing_vertical": true, "swing_horizontal": false, "economia": false, "visor": true, "sono": true, "umidade": false, "filtro": true, "turbo": true}, "estado": {"ligado": true, "modo": "COOL", "alvo": 18, "sala": 22, "vento": "HIGH", "swing_vertical": true, "swing_horizontal": null, "economia": null, "visor": false, "sono": {"ativo": false, "minutos": null}, "umidade": null, "filtro_restante": 98, "erro": false, "turbo": {"ativo": false, "fim": null, "minutos_restantes": null}, "turbo_no_maximo": true}, "pendente": {}, "falhas": [], "lido_em": 1793556669, "chamadas": {"na_hora": 4, "teto": 40}}
        """;

    private const string JsonLimite = """
        {"servidor": {"versao": "1.0.0", "simulado": true}, "conexao": {"situacao": "limite", "mensagem": "A LG pausou as chamadas por excesso. Volta sozinho às 15:41.", "pausado_ate": 1793558496}, "aparelho": {"nome": "Ar do quarto", "modelo": "RAC_056905_WW"}, "capacidades": {"modos": ["COOL", "AIR_DRY", "FAN", "HEAT"], "temperatura": {"COOL": {"min": 18, "max": 30, "passo": 0.5}, "AIR_DRY": {"min": 18, "max": 30, "passo": 0.5}, "HEAT": {"min": 16, "max": 30, "passo": 0.5}}, "vento": ["AUTO", "NATURE", "LOW", "LOW_MID", "MID", "MID_HIGH", "HIGH"], "swing_vertical": true, "swing_horizontal": false, "economia": false, "visor": true, "sono": true, "umidade": false, "filtro": true, "turbo": true}, "estado": {"ligado": false, "modo": "COOL", "alvo": 18, "sala": 21, "vento": "HIGH", "swing_vertical": true, "swing_horizontal": null, "economia": null, "visor": false, "sono": {"ativo": false, "minutos": null}, "umidade": null, "filtro_restante": 98, "erro": false, "turbo": {"ativo": false, "fim": null, "minutos_restantes": null}, "turbo_no_maximo": false}, "pendente": {"ligado": false}, "falhas": [{"campo": "ligado", "mensagem": "A LG pausou as chamadas por excesso. Volta sozinho às 15:41.", "em": 1793556697}], "lido_em": 1793556688, "chamadas": {"na_hora": 7, "teto": 40}}
        """;

    private const string JsonLendo = """
        {"servidor": {"versao": "1.0.0", "simulado": true}, "conexao": {"situacao": "lendo", "mensagem": "Lendo o ar…", "pausado_ate": null}, "aparelho": {"nome": "Ar do quarto", "modelo": null}, "capacidades": null, "estado": null, "pendente": {}, "falhas": [], "lido_em": null, "chamadas": {"na_hora": 0, "teto": 40}}
        """;

    public override string Autoteste()
    {
        var sb = new StringBuilder();
        void Conf(bool ok, string nome) => sb.AppendLine((ok ? "ok      " : "FALHOU  ") + nome);
        void Lanca(Action a, string nome)
        {
            try { a(); Conf(false, nome); }
            catch (ArgumentException) { Conf(true, nome); }
        }

        // ---- parser
        var r = ContasAr.LerResumo(JsonOk)!;
        Conf(r != null && r.Situacao == "ok" && r.Simulado && r.NomeAparelho == "Ar do quarto", "resumo: conexão ok, simulado, nome do aparelho");
        Conf(r!.Capacidades!.Modos.SequenceEqual(new[] { "COOL", "HEAT", "AIR_DRY", "FAN" }), "resumo: modos na ordem dos botões (Gelar, Aquecer, Desumidificar, Ventilar)");
        Conf(r.Capacidades.Temperatura["COOL"] == new Faixa(18, 30, 0.5) && r.Capacidades.Temperatura["HEAT"].Min == 16
             && !r.Capacidades.Temperatura.ContainsKey("FAN"), "resumo: faixas por modo (ventilar sem temperatura)");
        Conf(r.Capacidades.Vento.Count == 7 && r.Capacidades.Vento[0] == "AUTO", "resumo: as 7 opções de vento");
        var e = r.Estado!;
        Conf(e.Ligado == true && e.Modo == "COOL" && e.Alvo == 18 && e.Sala == 22 && e.Vento == "HIGH" && !e.Erro, "resumo: estado (ligado, gelando, alvo 18, sala 22, vento alto)");
        Conf(r.LidoEm == 1793556669 && r.ChamadasNaHora == 4 && r.Teto == 40 && r.Pendente.Count == 0 && r.Falhas.Count == 0, "resumo: lido_em, chamadas, pendente e falhas");

        var lim = ContasAr.LerResumo(JsonLimite)!;
        Conf(lim.Situacao == "limite" && lim.PausadoAte == 1793558496 && lim.Pendente.Contains("ligado") && lim.Estado!.Ligado == false,
            "resumo de limite: situação, pausado_ate, pendente e estado desligado");
        Conf(lim.Falhas.Count == 1 && lim.Falhas[0].Campo == "ligado" && lim.Falhas[0].Mensagem.StartsWith("A LG pausou"), "resumo de limite: a falha do comando");
        string tLim = ContasAr.TextoSituacao(lim.Situacao, lim.Mensagem, lim.PausadoAte);
        Conf(tLim == $"A LG pausou as chamadas por excesso. Volta sozinho às {ContasAr.HoraDe(1793558496)}.", "situação limite em português com a hora local");

        var lendo = ContasAr.LerResumo(JsonLendo)!;
        Conf(lendo.Situacao == "lendo" && lendo.Capacidades == null && lendo.Estado == null && lendo.LidoEm == null, "resumo ainda lendo: sem capacidades nem estado");
        Conf(ContasAr.LerResumo("[1,2]") == null && ContasAr.LerResumo("<html>") == null && ContasAr.LerResumo("") == null, "resposta que não é objeto JSON vira null");
        var torto = ContasAr.LerResumo("""{"estado": {"ligado": "sim", "alvo": "18", "modo": 3}, "capacidades": {"modos": [1, "COOL", "COOL"], "temperatura": {"COOL": {"min": 30, "max": 18}}}}""")!;
        Conf(torto.Estado!.Ligado == null && torto.Estado.Alvo == null && torto.Estado.Modo == null
             && torto.Capacidades!.Modos.SequenceEqual(new[] { "COOL" }) && torto.Capacidades.Temperatura.Count == 0, "campo com tipo errado é ignorado, não derruba");

        var h = ContasAr.LerHistorico("""{"pontos": [{"t": 200, "sala": 19.5, "alvo": 24, "ligado": true, "modo": "HEAT", "turbo": false}, {"t": 100, "sala": 25, "alvo": 18, "ligado": false, "modo": "COOL", "turbo": false}], "agora": 260}""");
        Conf(h?.Ultimo is { T: 200, Ligado: true, Modo: "HEAT", Alvo: 24, Sala: 19.5 } && h.Value.Agora == 260, "histórico: o ponto mais novo e o agora do servidor");
        Conf(ContasAr.LerHistorico("""{"pontos": [], "agora": 5}""")?.Ultimo == null, "histórico vazio: sem ponto");

        // ---- detector de mudança (o alerta curto)
        var lig22 = new EstadoBasico(true, "COOL", 22);
        var det = new DetectorDeMudanca();
        Conf(det.Leitura(100, lig22, 0) == null && det.Referencia == lig22, "detector: a primeira leitura só vira referência, sem alerta");
        var pa = new PontoHistorico(200, true, "COOL", 22, 24.5);
        var pb = new PontoHistorico(300, true, "COOL", 22, 26);
        Conf(det.Leitura(pa.T, EstadoBasico.De(pa), 0) == null && det.Leitura(pb.T, EstadoBasico.De(pb), 0) == null, "detector: só a sala mudou não é mudança");
        var m1 = det.Leitura(400, lig22 with { Alvo = 24 }, 0);
        Conf(m1 is { Tipo: TipoMudanca.Ajuste, MudouAlvo: true, MudouModo: false, PelaIlha: false } && m1.Antes.Alvo == 22 && m1.Depois.Alvo == 24,
            "detector: alvo mudado pelo controle remoto alerta");
        var m2 = det.Leitura(500, new EstadoBasico(false, "COOL", 24), 0);
        Conf(m2 is { Tipo: TipoMudanca.Desligou, PelaIlha: false }, "detector: desligado por fora alerta");
        Conf(det.Leitura(450, lig22, 0) == null && det.Leitura(500, lig22, 0) == null && det.Referencia!.Ligado == false,
            "detector: ponto fora de ordem ou com o mesmo t é ignorado");
        Conf(det.Leitura(600, new EstadoBasico(false, "HEAT", 27), 0) == null, "detector: modo e alvo mexendo com o ar desligado não alertam");
        Conf(det.Leitura(700, new EstadoBasico(null, null, null), 0) == null && det.Referencia == new EstadoBasico(false, "HEAT", 27), "detector: campo desconhecido não muda nada");

        // comando da própria ilha: alerta já na resposta, e as leituras de confirmação ficam caladas
        var mc = det.Comandou(new Dictionary<string, object> { ["ligado"] = true }, 650, new EstadoBasico(true, "HEAT", 27), 1000);
        Conf(mc is { Tipo: TipoMudanca.Ligou, PelaIlha: true } && det.Referencia!.Ligado == true, "detector: comando da ilha alerta na hora e vira referência");
        Conf(det.Leitura(700, new EstadoBasico(false, "HEAT", 27), 2000) == null && det.Referencia!.Ligado == true,
            "detector: ponto com um t já processado (lido_em da resposta mais velho que ele) não desfaz o pedido");
        Conf(det.ComPedidos(new EstadoBasico(false, "HEAT", 27), 3000) == new EstadoBasico(true, "HEAT", 27)
             && det.ComPedidos(new EstadoBasico(false, "HEAT", 27), 1000 + DetectorDeMudanca.CARENCIA_MS) == new EstadoBasico(false, "HEAT", 27),
            "menu: na carência a leitura velha fica com o pedido; passada a carência vale a leitura");
        Conf(det.Leitura(704, new EstadoBasico(false, "HEAT", 27), 5000) == null && det.Referencia!.Ligado == true,
            "detector: confirmação de +4 s ainda com o valor velho é ignorada (sem \"voltou\")");
        Conf(det.Leitura(715, new EstadoBasico(true, "HEAT", 27), 16000) == null, "detector: confirmação que bate com o pedido não duplica o alerta");
        Conf(det.Leitura(800, new EstadoBasico(false, "HEAT", 27), 20000) is { Tipo: TipoMudanca.Desligou, PelaIlha: false },
            "detector: depois de confirmado, desligar pelo controle alerta mesmo dentro dos 60 s");

        var det2 = new DetectorDeMudanca();
        det2.Leitura(100, lig22, 0);
        // a resposta do POST traz uma leitura mais nova que a última processada: o ponto dela (de antes do comando) não conta
        Conf(det2.Comandou(new Dictionary<string, object> { ["alvo"] = 25 }, 150, lig22 with { Alvo = 25 }, 0) is { MudouAlvo: true, PelaIlha: true }
             && det2.Leitura(150, lig22, 1000) == null, "detector: ponto com o mesmo t do lido_em do POST não sobrescreve o estado pedido");
        var mm = det2.Leitura(160, new EstadoBasico(true, "HEAT", 22), 3000);
        Conf(mm is { MudouModo: true, MudouAlvo: false } && det2.Referencia == new EstadoBasico(true, "HEAT", 25),
            "detector: na carência, o campo comandado fica e o outro campo mudado por fora alerta");
        Conf(det2.ComPedidos(new EstadoBasico(true, "HEAT", 22), 4000) == new EstadoBasico(true, "HEAT", 25)
             && det2.ComPedidos(new EstadoBasico(true, "HEAT", 25), 4000) == new EstadoBasico(true, "HEAT", 25),
            "menu: alvo pedido segura a leitura velha, e a que já bate passa igual");
        Conf(det2.Leitura(300, new EstadoBasico(true, "HEAT", 22), 3000 + DetectorDeMudanca.CARENCIA_MS) is { MudouAlvo: true } m3 && m3.Depois.Alvo == 22,
            "detector: passada a carência, o alvo que não pegou vira mudança de verdade");
        Conf(new DetectorDeMudanca().Comandou(new Dictionary<string, object> { ["ligado"] = true }, 10, null, 0) == null, "detector: comando sem referência não alerta (não se sabe o que era)");
        Conf(det2.Comandou(new Dictionary<string, object> { ["vento"] = "LOW" }, null, null, 0) == null, "detector: vento não é mudança que alerta");
        Conf(DetectorDeMudanca.Comparar(new EstadoBasico(true, "FAN", 20), new EstadoBasico(true, "FAN", 24), false) == null, "detector: alvo no ventilar não conta");
        det2.Zerar();
        Conf(det2.Leitura(50, new EstadoBasico(false, "COOL", 18), 0) == null, "detector: depois de zerar, a primeira leitura volta a ser calada");

        Conf(AtividadeAr.TextosDoAlerta(new MudancaAr(TipoMudanca.Ligou, new EstadoBasico(false, "COOL", 22), lig22, false, false, false), 25) == ("Ar ligado", "Gelando · 22°"),
            "alerta: ligado com o modo e o alvo");
        Conf(AtividadeAr.TextosDoAlerta(m2!, 24.5) == ("Ar desligado", "Sala com 24,5°") && AtividadeAr.TextosDoAlerta(m2!, null).Sub == "Ar do quarto", "alerta: desligado com a sala");
        Conf(AtividadeAr.TextosDoAlerta(m1!, null) == ("Ar em 24°", "Era 22° · Gelando"), "alerta: alvo novo e o de antes");
        Conf(AtividadeAr.TextosDoAlerta(mm!, null) == ("Ar no modo Aquecer", "25°"), "alerta: modo novo");

        // ---- alerta dos comandos da ilha: guardado com o cartão aberto, sai ao recolher, um por leva
        var off22 = new EstadoBasico(false, "COOL", 22);
        var lig24 = lig22 with { Alvo = 24 };
        var ad = new AlertaAdiado();
        ad.Guardar(new MudancaAr(TipoMudanca.Ligou, off22, lig22, false, false, true), 0);
        ad.Guardar(new MudancaAr(TipoMudanca.Ajuste, lig22, lig24, false, true, true), 1000);
        var sa = ad.Soltar(lig24, 2000);
        Conf(sa is { Tipo: TipoMudanca.Ligou, PelaIlha: true } && AtividadeAr.TextosDoAlerta(sa, null) == ("Ar ligado", "Gelando · 24°") && ad.Vazio,
            "adiado: ligou e subiu o alvo no cartão vira um alerta só, com o estado do fim");
        ad.Guardar(new MudancaAr(TipoMudanca.Ajuste, lig22, lig22 with { Alvo = 23 }, false, true, true), 0);
        ad.Guardar(new MudancaAr(TipoMudanca.Ajuste, lig22 with { Alvo = 23 }, lig24, false, true, true), 500);
        Conf(ad.Soltar(lig24, 600) is { } sb2 && AtividadeAr.TextosDoAlerta(sb2, null) == ("Ar em 24°", "Era 22° · Gelando"),
            "adiado: dois cliques no + mostram o de antes do primeiro");
        ad.Guardar(new MudancaAr(TipoMudanca.Ligou, off22, lig22, false, false, true), 0);
        ad.Guardar(new MudancaAr(TipoMudanca.Desligou, lig22, off22, false, false, true), 500);
        Conf(ad.Soltar(off22, 600) == null && ad.Vazio, "adiado: ligou e desligou antes de fechar não alerta");
        ad.Guardar(new MudancaAr(TipoMudanca.Ligou, off22, lig22, false, false, true), 0);
        Conf(ad.Soltar(lig22, AlertaAdiado.PRAZO_MS + 1) == null && ad.Soltar(lig22, 0) == null, "adiado: velho demais some, e vazio não alerta");

        // ---- respostas HTTP
        Conf(RespostaAr.Classificar(401, """{"erro": "chave"}""") is { Tipo: TipoResposta.Chave } c401 && c401.Problema == ContasAr.ProblemaChave, "401 vira chave recusada");
        var c400 = RespostaAr.Classificar(400, """{"erro": "A temperatura tem que ficar entre 18° e 30°."}""");
        Conf(c400.Tipo == TipoResposta.Recusado && c400.Problema == "A temperatura tem que ficar entre 18° e 30°.", "400 traz a mensagem do servidor");
        Conf(RespostaAr.Classificar(200, JsonOk).Tipo == TipoResposta.Ok, "200 com JSON é ok");
        Conf(RespostaAr.Classificar(200, "<html>").Tipo == TipoResposta.Erro, "200 sem JSON é erro (não é o servidor do ar)");
        Conf(RespostaAr.Classificar(500, """{"erro": "Erro interno do servidor."}""").Problema == "Erro interno do servidor.", "500 com a mensagem do servidor");
        Conf(RespostaAr.Classificar(404, """{"erro": "Não existe essa rota."}""").Problema == "O servidor do ar respondeu com erro (404).", "404 mostra o código");
        Conf(new RespostaAr(TipoResposta.Rede, 0, "", "").Problema == ContasAr.ProblemaRede, "rede caída em português");

        // ---- temperatura de 1 em 1 grau
        var f18 = new Faixa(18, 30, 0.5);
        Conf(ContasAr.ProximoAlvo(18.5, 1, f18) == 19 && ContasAr.ProximoAlvo(18.5, -1, f18) == 18, "18,5 vai a 19 no + e a 18 no −");
        Conf(ContasAr.ProximoAlvo(18, -1, f18) == 18 && ContasAr.ProximoAlvo(30, 1, f18) == 30 && ContasAr.ProximoAlvo(22, 1, f18) == 23, "preso à faixa nas pontas");
        Conf(ContasAr.ProximoAlvo(null, 1, f18, 22.6) == 23 && ContasAr.ProximoAlvo(null, 1, f18) == 18 && ContasAr.ProximoAlvo(20, 1, null) == null, "sem alvo parte da sala; sem faixa não muda");
        Conf(ContasAr.FaixaInteira(new Faixa(16.5, 30.5, 0.5)) == (17, 30), "faixa 16,5 a 30,5 vira 17 a 30");
        Conf(ContasAr.LimitesDoAlvo(18, f18) == (false, true) && ContasAr.LimitesDoAlvo(18.5, f18) == (true, true) && ContasAr.LimitesDoAlvo(30, f18) == (true, false), "o − e o + param nas pontas");
        Conf(ContasAr.FormatarNumero(25.5) == "25,5" && ContasAr.FormatarNumero(25) == "25" && ContasAr.FormatarNumero(-0.04) == "0" && ContasAr.FormatarGraus(null) == "", "números com vírgula");

        // ---- frases e nomes
        Conf(ContasAr.FraseStatus(e, r.Capacidades) == ("Gelando", "Sala com 22°, indo pra 18°."), "frase ligado: Gelando, sala indo pro alvo");
        var desl = e.Copia(); desl.Ligado = false;
        Conf(ContasAr.FraseStatus(desl, r.Capacidades) == ("Desligado", "Sala com 22°."), "frase desligado");
        var fan = e.Copia(); fan.Modo = "FAN";
        Conf(ContasAr.FraseStatus(fan, r.Capacidades) == ("Ventilando", "Sala com 22°."), "ventilar não mostra alvo");
        Conf(ContasAr.NomeModo("COOL") == "Gelar" && ContasAr.NomeModo("AIR_DRY") == "Desumidificar" && ContasAr.NomeVento("MID_HIGH") == "Médio-alto"
             && ContasAr.NomeVento("NATURE") == "Natural" && ContasAr.Legivel("SOME_CODE") == "Some code", "nomes de modo e vento (os do site)");
        var (niveis, especiais) = ContasAr.AgruparVentos(r.Capacidades.Vento);
        Conf(niveis.SequenceEqual(new[] { "LOW", "LOW_MID", "MID", "MID_HIGH", "HIGH" }) && especiais.SequenceEqual(new[] { "AUTO", "NATURE" }), "vento em barrinhas e pílulas");
        Conf(ContasAr.TempoDesde(1000, 1030) == "lido agora" && ContasAr.TempoDesde(1000, 1000 + 125) == "lido há 2 min"
             && ContasAr.TempoDesde(1000, 1000 + 3 * 3600) == "lido há 3 h" && ContasAr.TempoDesde(null, 5) == "sem leitura ainda", "lido há");
        Conf(ContasAr.TextoSituacao("ok", "", null) == "" && ContasAr.TextoSituacao("desconectado", "", null).Contains("sem Wi-Fi")
             && ContasAr.TextoSituacao("token_recusado", "", null).StartsWith("A LG recusou o token")
             && ContasAr.TextoSituacao("erro", "Código 2209", null) == "Erro da LG: Código 2209"
             && ContasAr.TextoSituacao("coisa_nova", "", null) == "Coisa nova.", "situações em português");
        var mesclado = ContasAr.Mesclar(desl, new Dictionary<string, object> { ["ligado"] = true, ["alvo"] = 20, ["vento"] = "LOW" })!;
        Conf(mesclado.Ligado == true && mesclado.Alvo == 20 && mesclado.Vento == "LOW" && desl.Ligado == false, "o pedido aparece por cima do estado (sem mexer no original)");

        // ---- comandos
        Conf(ContasAr.MontarComando(new Dictionary<string, object> { ["ligado"] = true }) == """{"ligado":true}""", "comando liga");
        Conf(ContasAr.MontarComando(new Dictionary<string, object> { ["alvo"] = 19, ["modo"] = "HEAT" }) == """{"modo":"HEAT","alvo":19}""", "comando modo e alvo juntos");
        Conf(ContasAr.MontarComando(new Dictionary<string, object> { ["alvo"] = 20.0, ["vento"] = "MID" }) == """{"alvo":20,"vento":"MID"}""", "alvo inteiro vai sem casa decimal");
        Lanca(() => ContasAr.MontarComando(new Dictionary<string, object> { ["turbo"] = true }), "turbo nunca sai da ilha");
        Lanca(() => ContasAr.MontarComando(new Dictionary<string, object> { ["alvo"] = 18.5 }), "alvo quebrado é recusado (a tela anda de 1 em 1)");
        Lanca(() => ContasAr.MontarComando(new Dictionary<string, object> { ["ligado"] = "sim" }), "tipo errado é recusado");
        Lanca(() => ContasAr.MontarComando(new Dictionary<string, object>()), "comando vazio é recusado");

        var fila = new FilaDeComandos();
        fila.Adicionar("ligado", true);
        var l1 = fila.Retirar();
        fila.Adicionar("alvo", 20);
        Conf(l1 != null && l1.Count == 1 && fila.Enviando && fila.Retirar() == null && fila.Locais.Count == 2, "fila: um POST por vez, o resto espera");
        fila.Terminou();
        Conf(!fila.Pendente("ligado") && fila.Pendente("alvo"), "fila: o que voltou sai do local, o que espera fica");
        var l2 = fila.Retirar();
        fila.Adicionar("alvo", 21);
        fila.Terminou();
        var l3 = fila.Retirar();
        Conf(l2 != null && (int)l2["alvo"] == 20 && l3 != null && (int)l3["alvo"] == 21 && fila.Pendente("alvo"), "fila: o valor mais novo vence e vai no próximo");
        fila.Terminou();
        Conf(fila.Locais.Count == 0 && !fila.Enviando, "fila vazia no fim");
        Lanca(() => fila.Adicionar("turbo", true), "fila não aceita turbo");

        // ---- link e chave (endereço de documentação, 192.0.2.x, e uma chave inventada)
        const string chave = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-abcd";
        var ent = ContasAr.InterpretarEntrada("http://192.0.2.10:8088/#chave=" + chave);
        Conf(ent == new EntradaAr("http://192.0.2.10:8088", chave, null), "link do mostrar-link.ps1 vira endereço e chave");
        Conf(ContasAr.InterpretarEntrada("  " + chave + " ") == new EntradaAr(null, chave, null), "só a chave");
        Conf(ContasAr.InterpretarEntrada("192.0.2.10:8088") == new EntradaAr("http://192.0.2.10:8088", null, null), "só o endereço (sem http://)");
        Conf(ContasAr.InterpretarEntrada("192.0.2.10:8088/#chave=" + chave).Chave == chave, "link sem http:// também");
        var curta = ContasAr.InterpretarEntrada("http://192.0.2.10:8088/#chave=curta");
        var semChave = ContasAr.InterpretarEntrada("http://192.0.2.10:8088/#outra=1");
        var ftp = ContasAr.InterpretarEntrada("ftp://192.0.2.10/#chave=" + chave);
        Conf(curta.Erro != null && semChave.Erro != null && ftp.Erro != null && ContasAr.InterpretarEntrada("").Erro != null, "link torto vira erro em português");
        Conf(!(ftp.Erro ?? "").Contains(chave) && !(curta.Erro ?? "").Contains("curta"), "a mensagem de erro nunca repete o que foi colado");
        Conf(ContasAr.NormalizarEndereco("http://192.0.2.10:8088/caminho?x=1") == "http://192.0.2.10:8088" && ContasAr.NormalizarEndereco("javascript:alert(1)") == null
             && ContasAr.NormalizarEndereco("http://u:p@192.0.2.10/") == null && ContasAr.NormalizarEndereco("") == null, "endereço vira origem; esquisito vira null");

        // ---- rotas: nunca /api/atualizar
        Conf(!ClienteAr.RotaPermitida(HttpMethod.Post, "/api/atualizar", "http://192.0.2.10:8088") && ClienteAr.RotaPermitida(HttpMethod.Get, "/api/estado", "http://192.0.2.10:8088")
             && ClienteAr.RotaPermitida(HttpMethod.Post, "/api/comando", "http://192.0.2.10:8088") && !ClienteAr.RotaPermitida(HttpMethod.Get, "/api/comando", "http://192.0.2.10:8088")
             && !ClienteAr.RotaPermitida(HttpMethod.Post, "/api/simulador", "http://192.0.2.10:8088") && ClienteAr.RotaPermitida(HttpMethod.Post, "/api/simulador", "http://127.0.0.1:8089"),
            "rotas: /api/atualizar barrado, /api/simulador só no local");
        var barrado = Task.Run(() => ClienteAr.Pedir("http://127.0.0.1:9", null, HttpMethod.Post, "/api/atualizar", "{}")).GetAwaiter().GetResult();
        Conf(barrado.Tipo == TipoResposta.Erro && barrado.Status == 0, "pedido a /api/atualizar nem sai da ilha");

        // ---- de verdade, contra o simulador local (nunca o servidor da casa)
        try { sb.Append(Task.Run(AoVivo).GetAwaiter().GetResult()); }
        catch (Exception ex) { Conf(false, "ao vivo: exceção " + ex.GetType().Name + ": " + ex.Message); }
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> AoVivo()
    {
        const string end = "http://127.0.0.1:8089";
        var sb = new StringBuilder();
        void Conf(bool ok, string nome) => sb.AppendLine((ok ? "ok      " : "FALHOU  ") + "ao vivo: " + nome);
        Task<RespostaAr> Get(string rota) => ClienteAr.Pedir(end, null, HttpMethod.Get, rota, null);
        Task<RespostaAr> Post(string rota, string corpo) => ClienteAr.Pedir(end, null, HttpMethod.Post, rota, corpo);
        async Task<ResumoAr?> Comando(Dictionary<string, object> campos) =>
            await Post("/api/comando", ContasAr.MontarComando(campos)) is { Tipo: TipoResposta.Ok } ok ? ContasAr.LerResumo(ok.Corpo) : null;

        var ping = await Get("/api/ping");
        if (ping.Tipo == TipoResposta.Rede)
            return "pulou   ao vivo: o simulador não está em 127.0.0.1:8089 (na pasta do ar-do-quarto: python servidor\\servidor.py --simulado --sem-chave --host 127.0.0.1 --porta 8089)\n";
        bool simulado = ping.Tipo == TipoResposta.Ok && JsonNode.Parse(ping.Corpo)?["simulado"]?.GetValue<bool>() == true;
        Conf(simulado, "127.0.0.1:8089 é o simulador (senão nada é mandado)");
        if (!simulado) return sb.ToString();

        ResumoAr? r = null;
        for (int i = 0; i < 20; i++)
        {
            var g = await Get("/api/estado");
            r = g.Tipo == TipoResposta.Ok ? ContasAr.LerResumo(g.Corpo) : null;
            if (r?.Capacidades != null && r.Estado != null) break;
            await Task.Delay(500);
        }
        Conf(r?.Capacidades != null && r.Estado != null, "GET /api/estado lido (capacidades e estado)");
        if (r?.Capacidades == null || r.Estado == null) return sb.ToString();
        var caps = r.Capacidades;
        var antes = r.Estado.Copia();

        var r1 = await Comando(new() { ["ligado"] = true });
        Conf(r1?.Estado?.Ligado == true, "POST liga");
        string? modo = caps.Modos.FirstOrDefault(m => m != r1?.Estado?.Modo && caps.Temperatura.ContainsKey(m));
        ResumoAr? r2 = modo == null ? null : await Comando(new() { ["modo"] = modo });
        Conf(modo != null && r2?.Estado?.Modo == modo, $"POST modo {ContasAr.NomeModo(modo)}");
        var faixa = ContasAr.FaixaDoModo(caps, modo);
        int? alvo = ContasAr.ProximoAlvo(r2?.Estado?.Alvo, 1, faixa, r2?.Estado?.Sala);
        if (alvo != null && r2?.Estado?.Alvo is double atual && Math.Abs(atual - alvo.Value) < 0.01) alvo = ContasAr.ProximoAlvo(atual, -1, faixa);
        ResumoAr? r3 = alvo == null ? null : await Comando(new() { ["alvo"] = alvo.Value });
        Conf(alvo != null && r3?.Estado?.Alvo is double a3 && Math.Abs(a3 - alvo.Value) < 0.01, $"POST temperatura {alvo}°");
        string? vento = caps.Vento.FirstOrDefault(v => v != r3?.Estado?.Vento);
        ResumoAr? r4 = vento == null ? null : await Comando(new() { ["vento"] = vento });
        Conf(vento != null && r4?.Estado?.Vento == vento, $"POST vento {ContasAr.NomeVento(vento)}");
        var recusado = await Post("/api/comando", """{"alvo": 99}""");
        Conf(recusado.Tipo == TipoResposta.Recusado && recusado.Problema.Contains("temperatura"), "temperatura fora da faixa volta recusada em português");
        var hist = await Get("/api/historico");
        Conf(hist.Tipo == TipoResposta.Ok && ContasAr.LerHistorico(hist.Corpo) is { Agora: not null }, "GET /api/historico lido");

        // situações forçadas no simulador: o que a cena mostra quando a LG pausa ou o ar some do Wi-Fi
        foreach (var (acao, situacao, trecho) in new[] { ("desconectar", "desconectado", "sem Wi-Fi"), ("limite", "limite", "A LG pausou") })
        {
            await Post("/api/simulador", $$"""{"acao": "{{acao}}"}""");
            ResumoAr? s = null;
            for (int i = 0; i < 10; i++)
            {
                var g = await Get("/api/estado");
                s = g.Tipo == TipoResposta.Ok ? ContasAr.LerResumo(g.Corpo) : null;
                if (s?.Situacao == situacao) break;
                await Task.Delay(400);
            }
            if (s?.Situacao != situacao && s?.ChamadasNaHora >= 30)
                sb.AppendLine($"pulou   ao vivo: situação {situacao} (o simulador chegou no teto de leitura dele; pare, apague o chamadas.json e rode de novo)");
            else
                Conf(s?.Situacao == situacao && ContasAr.TextoSituacao(s.Situacao, s.Mensagem, s.PausadoAte).Contains(trecho), $"situação {situacao} em português");
            // com a LG "pausada" o servidor não lê nada, então cada situação volta ao normal antes da próxima
            await Post("/api/simulador", """{"acao": "normal"}""");
        }

        // volta o simulador ao que era (o alvo pode ter meio grau: vai direto, sem a regra de 1 em 1 da tela)
        var volta = new JsonObject();
        if (antes.Ligado is bool lig) volta["ligado"] = lig;
        if (antes.Modo != null) volta["modo"] = antes.Modo;
        if (antes.Alvo is double av) volta["alvo"] = av;
        if (antes.Vento != null) volta["vento"] = antes.Vento;
        if (volta.Count > 0) Conf((await Post("/api/comando", volta.ToJsonString())).Tipo == TipoResposta.Ok, "simulador de volta ao estado de antes");
        return sb.ToString();
    }
}
