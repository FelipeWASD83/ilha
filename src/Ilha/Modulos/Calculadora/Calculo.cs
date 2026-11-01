using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ilha.Calculadora;

/// <summary>Erro de conta com mensagem em português (nunca NaN calado).</summary>
internal sealed class ErroDeCalculo : Exception
{
    public ErroDeCalculo(string mensagem) : base(mensagem) { }
}

/// <summary>
/// O motor da calculadora, porta do calculo.js da nova aba: analisador recursivo escrito à mão, sem nada que
/// vire código. Entende + − × ÷ * / ^ (e ** como potência), parênteses, % e ! depois do número, as funções
/// sin cos tan asin acos atan sinh cosh tanh ln log (base 10) log2 sqrt cbrt abs exp (com sen, tg, raiz...),
/// π (ou pi), e, ans, vírgula ou ponto decimal e multiplicação implícita ("2π", "3(4+1)", "2sin(30)").
/// Porcentagem de calculadora de bolso: "200 + 10%" dá 220, "200 × 10%" dá 20, "50%" dá 0,5.
/// </summary>
internal static class Calculo
{
    private enum TipoFicha { Num, Nome, Op }

    private readonly record struct Ficha(TipoFicha Tipo, double Num, string Texto);

    private static readonly HashSet<string> Funcoes = new()
    {
        "sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh", "ln", "log", "log2", "sqrt", "cbrt", "abs", "exp",
    };

    private static readonly Dictionary<string, double> Constantes = new() { ["pi"] = Math.PI, ["e"] = Math.E };

    private static readonly Dictionary<string, string> Apelidos = new()
    {
        ["sen"] = "sin", ["tg"] = "tan", ["raiz"] = "sqrt", ["arcsin"] = "asin", ["arccos"] = "acos", ["arctan"] = "atan", ["asen"] = "asin",
    };

    // Milhar à brasileira só quando não há outra leitura possível: "1.234,5" e "1.234.567". O calculo.js lia
    // "1.234,5" como 1,234 × 0,5 (dois números colados viram multiplicação implícita), resultado errado e calado.
    private static readonly Regex MilharComDecimal = new(@"\G\d{1,3}(\.\d{3})+,\d+", RegexOptions.CultureInvariant);
    private static readonly Regex MilharSoPontos = new(@"\G\d{1,3}(\.\d{3}){2,}(?![\d.,])", RegexOptions.CultureInvariant);
    // English thousands where no other reading makes sense ("1,234.5", "1,234,567": comma then dot, or two comma groups),
    // in either language. A single group ("1,000") is thousands only with the English interface; in Portuguese it stays
    // 1,0 as before. A leading zero ("0,125") is always a decimal.
    private static readonly Regex MilharInglesComDecimal = new(@"\G[1-9]\d{0,2}(,\d{3})+\.\d+", RegexOptions.CultureInvariant);
    private static readonly Regex MilharInglesSoVirgulas = new(@"\G[1-9]\d{0,2}(,\d{3}){2,}(?![\d.,])", RegexOptions.CultureInvariant);
    private static readonly Regex MilharInglesUmGrupo = new(@"\G[1-9]\d{0,2},\d{3}(?![\d.,])", RegexOptions.CultureInvariant);
    private static readonly Regex Numero = new(@"\G(\d+([.,]\d+)?|[.,]\d+)(e[+-]?\d+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Nome = new(@"\G[a-zA-Z]+[0-9]?", RegexOptions.CultureInvariant);

    private static ErroDeCalculo Erro(string msg) => new(msg);

    /// <summary>Texto em fichas. Aceita os símbolos bonitos (× ÷ − π √ ∛) e os do teclado.</summary>
    private static List<Ficha> Fichas(string texto)
    {
        // espaço entre letra e dígito vira uma marca que separa fichas: sem ela "log 25" colava em "log25" e o
        // nome levava o 2 (log2 de 5); "log2 16" continua igual porque ali o espaço vem depois de um dígito
        var t = Regex.Replace(Regex.Replace(texto, @"(?<=[a-zA-Z])\s+(?=\d)", "\u0001"), @"\s+", "");
        var saida = new List<Ficha>();
        int i = 0;
        while (i < t.Length)
        {
            char c = t[i];
            if (c == '\u0001') { i++; continue; }
            Match m;
            if ((m = MilharComDecimal.Match(t, i)).Success || (m = MilharSoPontos.Match(t, i)).Success)
            {
                saida.Add(new Ficha(TipoFicha.Num, double.Parse(m.Value.Replace(".", "").Replace(',', '.'), CultureInfo.InvariantCulture), ""));
                i += m.Length;
            }
            else if ((m = MilharInglesComDecimal.Match(t, i)).Success || (m = MilharInglesSoVirgulas.Match(t, i)).Success
                     || (Lang.English && (m = MilharInglesUmGrupo.Match(t, i)).Success))
            {
                saida.Add(new Ficha(TipoFicha.Num, double.Parse(m.Value.Replace(",", ""), CultureInfo.InvariantCulture), ""));
                i += m.Length;
            }
            else if ((m = Numero.Match(t, i)).Success)
            {
                saida.Add(new Ficha(TipoFicha.Num, double.Parse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture), ""));
                i += m.Length;
            }
            else if (c is 'π' or '√' or '∛')
            {
                saida.Add(new Ficha(TipoFicha.Nome, 0, c == 'π' ? "pi" : c == '√' ? "sqrt" : "cbrt"));
                i++;
            }
            else if ((m = Nome.Match(t, i)).Success)
            {
                // "log2" leva o dígito; "sin30" não (o 3 ficaria grudado no nome), por isso só vale se o nome for conhecido
                string nome = m.Value.ToLowerInvariant();
                if (!Funcoes.Contains(nome) && !Apelidos.ContainsKey(nome) && char.IsDigit(nome[^1])) nome = nome[..^1];
                saida.Add(new Ficha(TipoFicha.Nome, 0, Apelidos.TryGetValue(nome, out var a) ? a : nome));
                i += nome.Length;
            }
            else if (c == '*' && i + 1 < t.Length && t[i + 1] == '*') { saida.Add(new Ficha(TipoFicha.Op, 0, "^")); i += 2; }
            else if ("+-−–×·*÷/:^%!()".IndexOf(c) >= 0)
            {
                string op = c switch { '−' or '–' => "-", '×' or '·' => "*", '÷' or ':' => "/", _ => c.ToString() };
                saida.Add(new Ficha(TipoFicha.Op, 0, op));
                i++;
            }
            else throw Erro(Lang.T($"Caractere que não entendi: \"{c}\"", $"Character I didn't get: \"{c}\""));
        }
        return saida;
    }

    /// <summary>Avalia a expressão. graus: trigonometria em graus (senão radianos); ans: o último resultado.</summary>
    public static double Avaliar(string texto, bool graus = false, double ans = 0)
    {
        if (string.IsNullOrWhiteSpace(texto)) throw Erro(Lang.T("Escreva uma conta.", "Type a calculation."));
        var a = new Analisador(Fichas(texto), graus, ans);
        return a.Tudo();
    }

    // Cada passo devolve o valor e se ele veio de um "%" (só importa para soma e subtração).
    private readonly record struct Valor(double V, bool Pct);

    private sealed class Analisador
    {
        private readonly List<Ficha> _f;
        private readonly bool _graus;
        private readonly double _ans;
        private int _p;

        public Analisador(List<Ficha> f, bool graus, double ans)
        {
            _f = f;
            _graus = graus;
            _ans = ans;
        }

        private bool EhOp(string v) => _p < _f.Count && _f[_p].Tipo == TipoFicha.Op && _f[_p].Texto == v;

        public double Tudo()
        {
            var r = Soma();
            if (_p < _f.Count)
            {
                var t = _f[_p];
                throw Erro(t.Tipo == TipoFicha.Op && t.Texto == ")"
                    ? Lang.T("Sobrou um parêntese fechando.", "An extra closing parenthesis.")
                    : Lang.T($"Não esperava \"{Mostrar(t)}\" aqui.", $"Didn't expect \"{Mostrar(t)}\" here."));
            }
            return Conferir(r.V);
        }

        private static string Mostrar(Ficha t) => t.Tipo == TipoFicha.Num ? t.Num.ToString(CultureInfo.InvariantCulture) : t.Texto;

        private Valor Soma()
        {
            var a = Termo();
            while (EhOp("+") || EhOp("-"))
            {
                string op = _f[_p++].Texto;
                var b = Termo();
                double delta = b.Pct ? a.V * b.V : b.V;
                a = new Valor(op == "+" ? a.V + delta : a.V - delta, false);
            }
            return a;
        }

        private Valor Termo()
        {
            var a = Unario();
            for (;;)
            {
                if (EhOp("*") || EhOp("/"))
                {
                    string op = _f[_p++].Texto;
                    var b = Unario();
                    if (op == "/" && b.V == 0) throw Erro(Lang.T("Divisão por zero.", "Division by zero."));
                    a = new Valor(op == "*" ? a.V * b.V : a.V / b.V, false);
                }
                else if (ComecaOperando())
                {
                    // multiplicação implícita: 2π, 3(4+1), 2sin(30)
                    var b = Unario();
                    a = new Valor(a.V * b.V, false);
                }
                else return a;
            }
        }

        private bool ComecaOperando()
        {
            if (_p >= _f.Count) return false;
            var t = _f[_p];
            return t.Tipo is TipoFicha.Num or TipoFicha.Nome || (t.Tipo == TipoFicha.Op && t.Texto == "(");
        }

        private Valor Unario()
        {
            if (EhOp("-")) { _p++; var x = Unario(); return new Valor(-x.V, x.Pct); }
            if (EhOp("+")) { _p++; return Unario(); }
            return Potencia();
        }

        private Valor Potencia()
        {
            var b = Posfixo();
            if (EhOp("^"))
            {
                _p++;
                var e = Unario(); // 2^-3 vale; e 2^3^2 = 2^(3^2)
                double v = Math.Pow(b.V, e.V);
                if (double.IsNaN(v)) throw Erro(Lang.T("Potência sem resultado real.", "Power with no real result."));
                return new Valor(v, false);
            }
            return b;
        }

        private Valor Posfixo()
        {
            var a = Primario();
            for (;;)
            {
                if (EhOp("%")) { _p++; a = new Valor(a.V / 100, true); }
                else if (EhOp("!")) { _p++; a = new Valor(Fatorial(a.V), false); }
                else return a;
            }
        }

        private Valor Primario()
        {
            if (_p >= _f.Count) throw Erro(Lang.T("A conta parou no meio.", "The calculation stopped halfway."));
            var t = _f[_p++];
            if (t.Tipo == TipoFicha.Num) return new Valor(t.Num, false);
            if (t.Tipo == TipoFicha.Op && t.Texto == "(")
            {
                var x = Soma();
                if (!EhOp(")")) throw Erro(Lang.T("Falta fechar um parêntese.", "A parenthesis is missing its closing one."));
                _p++;
                return new Valor(x.V, false);
            }
            if (t.Tipo == TipoFicha.Nome)
            {
                if (t.Texto == "ans") return new Valor(_ans, false);
                if (Constantes.TryGetValue(t.Texto, out var k)) return new Valor(k, false);
                if (Funcoes.Contains(t.Texto))
                {
                    // sin(30), sin 30 e √9 valem; o argumento de função sem parêntese pega só um operando
                    Valor arg;
                    if (EhOp("("))
                    {
                        _p++;
                        arg = Soma();
                        if (!EhOp(")")) throw Erro(Lang.T("Falta fechar um parêntese.", "A parenthesis is missing its closing one."));
                        _p++;
                    }
                    else
                    {
                        if (!ComecaOperando() && !EhOp("-")) throw Erro(Lang.T($"\"{t.Texto}\" precisa de um valor.", $"\"{t.Texto}\" needs a value."));
                        arg = Unario();
                    }
                    return new Valor(Aplicar(t.Texto, arg.V, _graus), false);
                }
                throw Erro(Lang.T($"Não conheço \"{t.Texto}\".", $"I don't know \"{t.Texto}\"."));
            }
            throw Erro(Lang.T($"Não esperava \"{t.Texto}\" aqui.", $"Didn't expect \"{t.Texto}\" here."));
        }
    }

    private static double Conferir(double v)
    {
        if (double.IsNaN(v)) throw Erro(Lang.T("Sem resultado.", "No result."));
        if (double.IsInfinity(v)) throw Erro(Lang.T("Grande demais para a calculadora.", "Too big for the calculator."));
        return v == 0 ? 0 : v; // tira o -0
    }

    /// <summary>n! para inteiro de 0 a 170 (acima disso estoura o double).</summary>
    public static double Fatorial(double n)
    {
        if (!double.IsFinite(n) || Math.Floor(n) != n || n < 0) throw Erro(Lang.T("Fatorial só de inteiro, 0 ou mais.", "Factorial only of a whole number, 0 or more."));
        if (n > 170) throw Erro(Lang.T("Fatorial grande demais (o máximo é 170).", "Factorial too big (the maximum is 170)."));
        double r = 1;
        for (int i = 2; i <= n; i++) r *= i;
        return r;
    }

    // corta o resíduo de ponto flutuante da trigonometria (sin 180° = 1,2e-16 vira 0)
    private static double Limpar(double x) => Math.Abs(x) < 1e-14 ? 0 : x;

    private static string SoPositivo => Lang.T("O logaritmo só existe para número maior que zero.", "The logarithm only exists for numbers above zero.");

    private static double Aplicar(string nome, double x, bool graus)
    {
        double rad = graus ? x * Math.PI / 180 : x;
        switch (nome)
        {
            case "sin": return Limpar(Math.Sin(rad));
            case "cos": return Limpar(Math.Cos(rad));
            case "tan":
                if (graus ? Math.Abs((x % 180 + 180) % 180 - 90) < 1e-9 : Math.Abs(Math.Cos(rad)) < 1e-14)
                    throw Erro(Lang.T("A tangente não existe nesse ângulo.", "The tangent does not exist at this angle."));
                return Limpar(Math.Tan(rad));
            case "asin":
            case "acos":
            {
                if (x < -1 || x > 1)
                    throw Erro(Lang.T($"Arco-{(nome == "asin" ? "seno" : "cosseno")} só de −1 a 1.", $"Arc{(nome == "asin" ? "sine" : "cosine")} only from −1 to 1."));
                double r = nome == "asin" ? Math.Asin(x) : Math.Acos(x);
                return Limpar(graus ? r * 180 / Math.PI : r);
            }
            case "atan": return Limpar(graus ? Math.Atan(x) * 180 / Math.PI : Math.Atan(x));
            case "sinh": return Math.Sinh(x);
            case "cosh": return Math.Cosh(x);
            case "tanh": return Math.Tanh(x);
            case "exp": return Math.Exp(x);
            case "ln": if (x <= 0) throw Erro(SoPositivo); return Math.Log(x);
            case "log": if (x <= 0) throw Erro(SoPositivo); return ArredondaPotencia(Math.Log10(x));
            case "log2": if (x <= 0) throw Erro(SoPositivo); return ArredondaPotencia(Math.Log2(x));
            case "sqrt": if (x < 0) throw Erro(Lang.T("Raiz quadrada de número negativo não existe aqui.", "No square root of a negative number here.")); return Math.Sqrt(x);
            case "cbrt": return Math.Cbrt(x);
            case "abs": return Math.Abs(x);
            default: throw Erro(Lang.T("Função desconhecida.", "Unknown function."));
        }
    }

    // log10(1000) pode sair 2,9999...; a 1e-12 de um inteiro, é o inteiro
    private static double ArredondaPotencia(double v)
    {
        double r = Math.Round(v, MidpointRounding.AwayFromZero);
        return Math.Abs(v - r) < 1e-12 ? r : v;
    }

    // ------------------------------------------------------------------ números na tela

    /// <summary>Vírgula decimal, ponto de milhar, sinal "-": o que o Intl pt-BR da nova aba escreve.</summary>
    internal static readonly NumberFormatInfo Br = new()
    {
        NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NumberGroupSizes = new[] { 3 }, NegativeSign = "-",
    };

    /// <summary>The English counterpart: decimal point, comma for thousands.</summary>
    internal static readonly NumberFormatInfo En = new()
    {
        NumberDecimalSeparator = ".", NumberGroupSeparator = ",", NumberGroupSizes = new[] { 3 }, NegativeSign = "-",
    };

    /// <summary>The number format shown to the user, following the interface language.</summary>
    internal static NumberFormatInfo Tela => Lang.English ? En : Br;

    /// <summary>O toPrecision(12) do JS: 12 algarismos significativos ("0,1 + 0,2" dá 0,3).</summary>
    internal static double Doze(double n) => double.Parse(n.ToString("G12", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// Notação científica curta (o toExponential() sem casas fixas): "1e21", "1,5e-12"; maisNoExpoente põe o "+"
    /// como o JS ("1e+21"). O valor tem até 12 algarismos (passou pelo Doze), então E11 é exato.
    /// </summary>
    internal static string Cientifica(double v, bool maisNoExpoente, string separador = ",")
    {
        string s = v.ToString("E11", CultureInfo.InvariantCulture); // -1.50000000000E-012
        int iE = s.IndexOf('E');
        string mant = s[..iE].TrimEnd('0').TrimEnd('.');
        int exp = int.Parse(s[(iE + 1)..], CultureInfo.InvariantCulture);
        string sinal = exp < 0 ? "-" : maisNoExpoente ? "+" : "";
        return mant.Replace(".", separador) + "e" + sinal + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Número para a tela: 12 algarismos no máximo, milhar com ponto, vírgula decimal e notação científica nos extremos.</summary>
    public static string Formatar(double n)
    {
        if (!double.IsFinite(n)) return "";
        if (n == 0) return "0";
        double limpo = Doze(n);
        double a = Math.Abs(limpo);
        if (a >= 1e15 || a < 1e-9) return Cientifica(limpo, maisNoExpoente: false, Tela.NumberDecimalSeparator);
        return limpo.ToString("#,##0.############", Tela);
    }

    /// <summary>O mesmo número sem milhar, para voltar a ser digitado ou colado ("1234,5").</summary>
    public static string ParaDigitar(double n)
    {
        if (!double.IsFinite(n)) return "0";
        if (n == 0) return "0";
        double limpo = Doze(n);
        double a = Math.Abs(limpo);
        // String(numero) do JS: notação científica a partir de 1e21 e abaixo de 1e-7
        if (a >= 1e21 || a < 1e-7) return Cientifica(limpo, maisNoExpoente: true, Tela.NumberDecimalSeparator);
        return ((decimal)limpo).ToString("0.############################", CultureInfo.InvariantCulture).Replace(".", Tela.NumberDecimalSeparator);
    }
}
