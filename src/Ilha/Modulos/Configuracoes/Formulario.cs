using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace Ilha.Configuracoes;

/// <summary>Um campo da config de um módulo com o controle que edita ele.</summary>
internal sealed class CampoGerado
{
    public required PropertyInfo Propriedade;
    public required string Rotulo;
    public string Descricao = "";
    public required Control Editor;
    /// <summary>Lê o valor do controle (já no tipo da propriedade).</summary>
    public required Func<object?> Ler;
}

/// <summary>
/// Monta o formulário da config de um módulo por reflexão: bool vira caixa de marcar, número vira campo numérico,
/// texto vira caixa de texto, enum vira lista e lista de texto vira caixa com um item por linha. O que não dá
/// para editar assim (objeto, dicionário) fica de fora e é mantido como está no config.json.
/// </summary>
internal static class Formulario
{
    private static readonly Type[] Inteiros = { typeof(int), typeof(long), typeof(short), typeof(byte), typeof(uint), typeof(ushort) };
    private static readonly Type[] Fracionarios = { typeof(float), typeof(double), typeof(decimal) };

    public static IEnumerable<PropertyInfo> Propriedades(Type tipo) =>
        tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetSetMethod() != null && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

    public static bool Editavel(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(bool) || t == typeof(string) || t.IsEnum || Inteiros.Contains(t) || Fracionarios.Contains(t) || EhListaDeTexto(t);
    }

    private static bool EhListaDeTexto(Type t) =>
        t == typeof(string[]) || t.IsGenericType && t.GetGenericArguments() is [var a] && a == typeof(string)
            && (t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(IList<>)
                || t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) || t.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    public static string NomeJson(PropertyInfo p) => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;

    /// <summary>"[DisplayName]" se tiver; senão o nome do JSON separado: "procurarSozinha" vira "Procurar sozinha".</summary>
    public static string Rotulo(PropertyInfo p)
    {
        var dn = p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
        if (!string.IsNullOrWhiteSpace(dn)) return dn;
        return Humanizar(NomeJson(p));
    }

    public static string Humanizar(string nome)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < nome.Length; i++)
        {
            char c = nome[i];
            if (c is '_' or '-') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(nome[i - 1])) sb.Append(' ');
            sb.Append(sb.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return sb.ToString().Trim();
    }

    /// <summary>A config do módulo a partir da seção dele (o que faltar vem do padrão da classe).</summary>
    public static object Instanciar(Type tipo, JsonObject? secao)
    {
        if (secao != null)
        {
            try { if (secao.Deserialize(tipo, Config.Json) is { } o) return o; }
            catch (Exception e) { Log.Escrever($"configurações: seção ilegível para {tipo.Name}: {e.Message}"); }
        }
        return Activator.CreateInstance(tipo)!;
    }

    /// <summary>Os controles dos campos editáveis, com o valor atual.</summary>
    public static List<CampoGerado> Gerar(Type tipo, object instancia, System.Drawing.Font? fonte = null)
    {
        var campos = new List<CampoGerado>();
        foreach (var p in Propriedades(tipo))
        {
            if (!Editavel(p.PropertyType)) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            object? valor = p.GetValue(instancia);
            string descricao = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
            CampoGerado campo;
            if (t == typeof(bool))
            {
                var cb = new CheckBox { Text = Rotulo(p), Checked = valor is true, AutoSize = true, UseMnemonic = false };
                campo = new CampoGerado { Propriedade = p, Rotulo = Rotulo(p), Editor = cb, Ler = () => cb.Checked };
            }
            else if (Inteiros.Contains(t) || Fracionarios.Contains(t))
            {
                bool inteiro = Inteiros.Contains(t);
                var range = p.GetCustomAttribute<RangeAttribute>();
                decimal atual = valor == null ? 0 : Convert.ToDecimal(valor);
                decimal min = range != null ? Convert.ToDecimal(range.Minimum) : inteiro ? (t == typeof(byte) || t == typeof(uint) || t == typeof(ushort) ? 0 : -1_000_000) : -1_000_000;
                decimal max = range != null ? Convert.ToDecimal(range.Maximum) : 1_000_000;
                var n = new NumericUpDown
                {
                    DecimalPlaces = inteiro ? 0 : 3, Increment = inteiro ? 1 : 0.1m,
                    Minimum = Math.Min(min, atual), Maximum = Math.Max(max, atual), Value = atual, Width = 120,
                    ThousandsSeparator = false, TextAlign = HorizontalAlignment.Right,
                };
                campo = new CampoGerado { Propriedade = p, Rotulo = Rotulo(p), Editor = n, Ler = () => Convert.ChangeType(n.Value, t, System.Globalization.CultureInfo.InvariantCulture) };
            }
            else if (t.IsEnum)
            {
                var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
                foreach (var nome in Enum.GetNames(t)) c.Items.Add(nome);
                c.SelectedItem = valor?.ToString() ?? Enum.GetNames(t).FirstOrDefault();
                campo = new CampoGerado { Propriedade = p, Rotulo = Rotulo(p), Editor = c, Ler = () => Enum.Parse(t, (string)(c.SelectedItem ?? Enum.GetNames(t)[0])) };
            }
            else if (t == typeof(string))
            {
                var tb = new TextBox { Text = valor as string ?? "", Width = 320 };
                campo = new CampoGerado { Propriedade = p, Rotulo = Rotulo(p), Editor = tb, Ler = () => tb.Text };
            }
            else
            {
                var itens = (valor as IEnumerable<string>)?.ToList() ?? new List<string>();
                var tb = new TextBox
                {
                    Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Width = 320, Height = 96,
                    Text = string.Join(Environment.NewLine, itens),
                };
                if (descricao.Length == 0) descricao = "Um item por linha.";
                campo = new CampoGerado
                {
                    Propriedade = p, Rotulo = Rotulo(p), Editor = tb,
                    Ler = () =>
                    {
                        var linhas = tb.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                        return t == typeof(string[]) ? linhas.ToArray() : linhas;
                    },
                };
            }
            campo.Descricao = descricao;
            if (fonte != null) campo.Editor.Font = fonte;
            campos.Add(campo);
        }
        return campos;
    }

    /// <summary>Os campos que ficaram de fora do formulário (só no config.json).</summary>
    public static List<string> NaoEditaveis(Type tipo) => Propriedades(tipo).Where(p => !Editavel(p.PropertyType)).Select(NomeJson).ToList();

    /// <summary>Passa o que está nos controles para a instância.</summary>
    public static void Aplicar(IEnumerable<CampoGerado> campos, object instancia)
    {
        foreach (var c in campos) c.Propriedade.SetValue(instancia, c.Ler());
    }

    /// <summary>
    /// A seção nova do módulo: parte da original (chaves que a classe não conhece ficam), põe por cima o que a
    /// classe serializa e o "ligado".
    /// </summary>
    public static JsonObject Mesclar(JsonObject? original, Type? tipo, object? instancia, bool ligado)
    {
        var r = original?.DeepClone() as JsonObject ?? new JsonObject();
        if (tipo != null && instancia != null && JsonSerializer.SerializeToNode(instancia, tipo, Config.Json) is JsonObject novo)
            foreach (var kv in novo) r[kv.Key] = kv.Value?.DeepClone();
        r["ligado"] = ligado;
        return r;
    }
}
