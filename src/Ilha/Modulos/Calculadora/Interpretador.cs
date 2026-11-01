using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ilha.Calculadora;

internal enum TipoResultado { Vazio, Numero, Unidade, Moeda, Erro }

/// <summary>O que a calculadora mostra para o que está escrito.</summary>
internal sealed class Resultado
{
    public TipoResultado Tipo { get; init; }
    /// <summary>A linha grande: "= 1.234,5", "= 6,21371 mi", "= R$ 520,00".</summary>
    public string Principal { get; init; } = "";
    /// <summary>A linha pequena de baixo (o erro, a conversão por extenso, a cotação usada).</summary>
    public string Detalhe { get; init; } = "";
    /// <summary>O que o Enter copia: o número sem milhar, para colar em qualquer lugar ("1234,5").</summary>
    public string Copia { get; init; } = "";
    public double Valor { get; init; } = double.NaN;
    /// <summary>Conversão de moeda sem cotação nenhuma na mão: a calculadora precisa buscar.</summary>
    public bool PrecisaCotacao { get; init; }
    public string? MoedaDe { get; init; }
    /// <summary>A moeda (que não é o real) cuja cotação a conta usou: a hora mostrada é a dela.</summary>
    public string? MoedaRef { get; init; }

    public bool Ok => Tipo is TipoResultado.Numero or TipoResultado.Unidade or TipoResultado.Moeda;

    public static Resultado Falha(string msg) => new() { Tipo = TipoResultado.Erro, Detalhe = msg };
}

/// <summary>
/// Lê o campo da calculadora inteiro: conta pura ("2+3×4"), conversão de unidade ("10 km em mi", "32 f em c",
/// "1 GiB para GB") ou de moeda ("100 usd em brl", "R$ 50 em dólar", "100 euros", que vira reais). O número antes
/// da unidade pode ser uma conta ("2×5 kg em lb"). Sem unidade nenhuma, é conta.
/// </summary>
internal static class Interpretador
{
    /// <summary>Um destino possível: unidade ou moeda.</summary>
    internal sealed record Alvo(Unidade? Unidade, Moeda? Moeda, string Apelido)
    {
        public string Norm { get; } = Normal(Apelido);
        /// <summary>Tem símbolo ("R$", "US$", "€"): vale também na frente do número.</summary>
        public bool Simbolo { get; } = Apelido.Any(ch => !char.IsLetter(ch));
        public string Sigla => Unidade?.SiglaTela ?? Moeda!.Id;
    }

    // "em", "para", "pra", "in", "into", "to" entre espaços (sem consumir o espaço: "10 in in cm" tem dois), ou uma seta / "="
    private static readonly Regex Separador = new(@"(?<=\s)(?:em|para|pra|into|in|to)(?=\s)|->|→|=>|=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly List<Alvo> Todos = MontarAlvos();

    private static List<Alvo> MontarAlvos()
    {
        var l = new List<Alvo>();
        foreach (var c in Unidades.Categorias)
            foreach (var u in c.Unidades)
                // o id não entra: "ms" é o id do m/s e também a sigla do milissegundo
                foreach (var a in new[] { u.Sigla }.Concat(u.SiglaEn != null ? new[] { u.SiglaEn } : Array.Empty<string>()).Concat(u.Apelidos).Distinct())
                    l.Add(new Alvo(u, null, a));
        foreach (var m in Cambio.Moedas)
            foreach (var a in new[] { m.Id }.Concat(m.Apelidos).Distinct())
                l.Add(new Alvo(null, m, a));
        // o mais comprido primeiro: "km" antes de "m", "dólar canadense" antes de "dólar"; a ordem da lista desempata
        return l.Select((a, i) => (a, i)).OrderByDescending(x => x.a.Apelido.Length).ThenBy(x => x.i).Select(x => x.a).ToList();
    }

    /// <summary>Minúsculas e sem acento, letra por letra (o tamanho do texto não muda).</summary>
    internal static string Normal(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            string d = char.ToLowerInvariant(c).ToString().Normalize(NormalizationForm.FormD);
            sb.Append(d.Length > 0 && char.IsLetter(d[0]) ? d[0] : char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>O texto inteiro é o nome de uma unidade ou moeda? Primeiro como escrito (MB e Mb diferem), depois sem caixa nem acento.</summary>
    internal static Alvo? AcharAlvo(string texto)
    {
        string t = texto.Trim();
        if (t.Length == 0) return null;
        foreach (var a in Todos) if (a.Apelido == t) return a;
        string n = Normal(t);
        foreach (var a in Todos) if (a.Norm == n) return a;
        return null;
    }

    private static bool Letra(char c) => char.IsLetter(c);

    /// <summary>
    /// "10 km", "2×5kg", "R$ 50", "km" (vale 1): a quantidade e o que ela é. Tenta do apelido mais comprido para o
    /// mais curto até sobrar uma conta que fecha. erro: o motivo da conta não fechar, se foi isso.
    /// </summary>
    internal static bool LerQuantidade(string s, bool graus, double ans, out double valor, out Alvo? alvo, out string? erro)
    {
        valor = double.NaN;
        alvo = null;
        erro = null;
        s = s.Trim();
        if (s.Length == 0) return false;
        string n = Normal(s);
        for (int passo = 0; passo < 2; passo++)
        {
            foreach (var a in Todos)
            {
                string ap = passo == 0 ? a.Apelido : a.Norm;
                string alvoTexto = passo == 0 ? s : n;
                // no fim: "10 km"
                if (alvoTexto.EndsWith(ap, StringComparison.Ordinal))
                {
                    string resto = s[..^ap.Length].TrimEnd();
                    bool fronteira = resto.Length == 0 || !(Letra(ap[0]) && Letra(s[s.Length - ap.Length - 1]));
                    if (fronteira && Valor(resto, graus, ans, out valor, ref erro)) { alvo = a; return true; }
                }
                // no começo, só moeda com símbolo: "R$ 50", "US$ 10", "€ 20"
                if (a.Moeda != null && a.Simbolo && alvoTexto.StartsWith(ap, StringComparison.Ordinal))
                {
                    string resto = s[ap.Length..].TrimStart();
                    if (resto.Length > 0 && Valor(resto, graus, ans, out valor, ref erro)) { alvo = a; return true; }
                }
            }
        }
        return false;
    }

    private static bool Valor(string resto, bool graus, double ans, out double valor, ref string? erro)
    {
        valor = double.NaN;
        if (resto.Length == 0) { valor = 1; return true; }
        try
        {
            valor = Calculo.Avaliar(resto, graus, ans);
            return true;
        }
        catch (ErroDeCalculo e)
        {
            erro ??= e.Message;
            return false;
        }
    }

    public static Resultado Interpretar(string texto, bool graus, double ans, Cotacoes? cot)
    {
        string t = (texto ?? "").Trim();
        if (t.Length == 0) return new Resultado { Tipo = TipoResultado.Vazio };
        // "2+2=" é só o costume da calculadora de bolso
        if (t.EndsWith('=') && t.Length > 1 && !t.EndsWith("=>")) t = t[..^1].TrimEnd();

        // conversão: da última ocorrência do separador para a primeira, até os dois lados fazerem sentido
        var seps = Separador.Matches(t).Cast<Match>().Reverse().ToList();
        Resultado? motivo = null;
        foreach (var m in seps)
        {
            var destino = AcharAlvo(t[(m.Index + m.Length)..]);
            if (destino == null) continue;
            string esquerda = t[..m.Index];
            if (!LerQuantidade(esquerda, graus, ans, out var v, out var origem, out var erro))
            {
                motivo ??= Resultado.Falha(erro ?? Lang.T($"Faltou dizer de que unidade converter para {destino.Sigla}.", $"Missing the unit to convert to {destino.Sigla} from."));
                continue;
            }
            return Converter(v, origem!, destino, cot);
        }

        // "100 usd" sozinho vira reais
        if (seps.Count == 0 && LerQuantidade(t, graus, ans, out var vm, out var am, out _) && am!.Moeda != null)
            return Converter(vm, am, new Alvo(null, Cambio.Achar("BRL"), "brl"), cot);

        try
        {
            double r = Calculo.Avaliar(t, graus, ans);
            return new Resultado { Tipo = TipoResultado.Numero, Valor = r, Principal = "= " + Calculo.Formatar(r), Copia = Calculo.ParaDigitar(r) };
        }
        catch (ErroDeCalculo e)
        {
            if (motivo != null) return motivo;
            // "10 km" sem destino: diz como pedir em vez de "Não conheço km"
            if (LerQuantidade(t, graus, ans, out _, out var a, out _) && a!.Unidade != null)
                return Resultado.Falha(Lang.T($"Para converter, diga o destino: \"{t} em ...\"", $"To convert, say the target: \"{t} in ...\""));
            return Resultado.Falha(e.Message);
        }
    }

    private static Resultado Converter(double v, Alvo de, Alvo para, Cotacoes? cot)
    {
        if (de.Unidade != null && para.Unidade != null)
        {
            if (de.Unidade.Categoria != para.Unidade.Categoria)
            {
                string a = de.Unidade.SiglaTela, ca = de.Unidade.Categoria.NomeTela.ToLowerInvariant();
                string b = para.Unidade.SiglaTela, cb = para.Unidade.Categoria.NomeTela.ToLowerInvariant();
                return Resultado.Falha(Lang.T($"Não dá para converter {a} ({ca}) em {b} ({cb}).", $"Can't convert {a} ({ca}) to {b} ({cb})."));
            }
            double r = Unidades.Converter(v, de.Unidade, para.Unidade);
            if (!double.IsFinite(r)) return Resultado.Falha(Lang.T("Grande demais para a calculadora.", "Too big for the calculator."));
            string txt = Unidades.Formatar(r);
            string de1 = $"{Unidades.Formatar(v)} {de.Unidade.SiglaTela}";
            return new Resultado
            {
                Tipo = TipoResultado.Unidade, Valor = r, Principal = $"= {txt} {para.Unidade.SiglaTela}",
                Detalhe = Lang.T($"{de1} em {para.Unidade.NomeTela}", $"{de1} in {para.Unidade.NomeTela}"),
                // sem o separador de milhar da língua da tela (ponto em português, vírgula em inglês)
                Copia = txt.Contains('e') ? txt : txt.Replace(Calculo.Tela.NumberGroupSeparator, ""),
            };
        }
        if (de.Moeda != null && para.Moeda != null)
        {
            if (cot == null)
                return new Resultado { Tipo = TipoResultado.Erro, Detalhe = Lang.T("Buscando a cotação...", "Fetching the exchange rate..."), PrecisaCotacao = true, MoedaDe = de.Moeda.Id };
            double r = Cambio.Converter(v, de.Moeda.Id, para.Moeda.Id, cot);
            if (!double.IsFinite(r))
            {
                var falta = cot.Real.ContainsKey(de.Moeda.Id) ? para.Moeda : de.Moeda;
                string nome = falta.NomeTela.ToLowerInvariant(), fonte = Cambio.NomeDaFonte(cot.Fonte);
                return Resultado.Falha(Lang.T($"Sem cotação de {nome} agora ({fonte}).", $"No {nome} rate right now ({fonte})."));
            }
            // a cotação de 1 unidade da moeda que não é o real, para conferir de olho
            var refm = de.Moeda.Id != "BRL" ? de.Moeda : para.Moeda;
            string uma = refm.Id == "BRL" ? "" : $"1 {refm.Id} = R$ {Cambio.FormatarMoeda(cot.Real[refm.Id], "BRL")}";
            return new Resultado
            {
                Tipo = TipoResultado.Moeda, Valor = r, MoedaDe = de.Moeda.Id, MoedaRef = refm.Id == "BRL" ? null : refm.Id,
                Principal = $"= {para.Moeda.Simbolo} {Cambio.FormatarMoeda(r, para.Moeda.Id)}",
                Detalhe = uma, Copia = Cambio.FormatarMoeda(r, para.Moeda.Id, milhar: false),
            };
        }
        return Resultado.Falha(Lang.T($"Não dá para converter {de.Sigla} em {para.Sigla}.", $"Can't convert {de.Sigla} to {para.Sigla}."));
    }
}
