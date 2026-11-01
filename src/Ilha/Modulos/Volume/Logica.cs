using System;
using System.Collections.Generic;
using System.Linq;

namespace Ilha.Volume;

/// <summary>Um endpoint de saída como o Windows descreve (lido do IPropertyStore).</summary>
internal sealed record Endpoint(string Id, string Nome, string Aparelho, string Descricao, string Enumerador, Guid Conteiner, uint Formato);

/// <summary>
/// Uma linha da lista de saídas. Os AirPods (e qualquer fone Bluetooth com microfone) viram dois endpoints, o estéreo
/// e o Hands-Free; a linha junta os dois e troca para o estéreo. `Ids` tem todos, para marcar a atual por qualquer um.
/// </summary>
internal sealed record Saida(string Id, string Nome, string Aparelho, IReadOnlyList<string> Ids)
{
    public bool Contem(string? id) => id != null && Ids.Contains(id, StringComparer.OrdinalIgnoreCase);
}

internal enum Transicao { Nada, Conectou, Desconectou }

/// <summary>Contas e regras sem nada do sistema (é o que o `--testar volume` confere).</summary>
internal static class Logica
{
    // contêiner de tudo que está dentro do próprio PC (som da placa-mãe, saída da placa de vídeo): nunca junta
    public static readonly Guid ConteinerDoPc = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    /// <summary>Escala do Windows (0 a 1) para a porcentagem que ele mesmo mostra.</summary>
    public static int Porcentagem(float escala) =>
        float.IsFinite(escala) ? (int)MathF.Round(Math.Clamp(escala, 0f, 1f) * 100f, MidpointRounding.AwayFromZero) : 0;

    public static float Escala(int porcentagem) => Math.Clamp(porcentagem, 0, 100) / 100f;

    /// <summary>Decibéis para amplitude linear (0 dB = 1) e de volta. -inf/0 viram silêncio.</summary>
    public static float Amplitude(float db) => float.IsNegativeInfinity(db) ? 0f : MathF.Pow(10f, db / 20f);

    public static float Decibeis(float amplitude) => amplitude <= 0f ? float.NegativeInfinity : 20f * MathF.Log10(amplitude);

    /// <summary>"-12,5 dB" (como o Windows escreve no Brasil); silêncio vira "-∞ dB".</summary>
    public static string TextoDb(float db)
    {
        if (!float.IsFinite(db)) return "-∞ dB";
        var r = MathF.Round(db, 1);
        if (r == 0f) r = 0f; // sem "-0,0"
        return r.ToString("0.0", new System.Globalization.CultureInfo("pt-BR")) + " dB";
    }

    /// <summary>A rodinha: `passo`% por dente (120), andando na grade do passo para não ficar em 63% e 65%.</summary>
    public static float Rolar(float atual, int delta, int passo)
    {
        passo = Math.Clamp(passo, 1, 25);
        int pct = Porcentagem(atual);
        int dentes = delta / 120;
        if (dentes == 0) dentes = Math.Sign(delta);
        int alvo;
        if (dentes > 0) alvo = (pct / passo + dentes) * passo;
        else alvo = ((pct + passo - 1) / passo + dentes) * passo;
        return Escala(alvo);
    }

    /// <summary>Posição na barra (DIP) para escala, presa entre 0 e 1.</summary>
    public static float DaBarra(float x, float inicio, float largura) =>
        largura <= 0 ? 0f : Math.Clamp((x - inicio) / largura, 0f, 1f);

    /// <summary>Quantas ondas o alto-falante desenha (0 a 3).</summary>
    public static int Ondas(float escala, bool mudo) =>
        mudo || escala <= 0.001f ? 0 : escala < 0.34f ? 1 : escala < 0.67f ? 2 : 3;

    public static bool EhHandsFree(Endpoint e) =>
        string.Equals(e.Enumerador, "BTHHFENUM", StringComparison.OrdinalIgnoreCase)
        || e.Aparelho.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
        || e.Nome.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);

    /// <summary>"AirPods Pro 3 Hands-Free" -> "AirPods Pro 3" (para casar o Hands-Free com o estéreo quando falta contêiner).</summary>
    public static string SemHandsFree(string nome)
    {
        int i = nome.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase);
        return (i < 0 ? nome : nome.Remove(i, "Hands-Free".Length)).Trim(' ', '-', '(', ')');
    }

    private static string Chave(Endpoint e) =>
        e.Conteiner != Guid.Empty && e.Conteiner != ConteinerDoPc ? "c:" + e.Conteiner : "n:" + SemHandsFree(e.Aparelho).ToLowerInvariant();

    /// <summary>
    /// Junta o Hands-Free com o estéreo do mesmo aparelho (mesmo contêiner, ou o mesmo nome sem "Hands-Free"),
    /// preferindo o estéreo. Os outros endpoints ficam um por linha, na ordem em que vieram. Hands-Free sozinho
    /// (o estéreo não está ativo) continua aparecendo.
    /// </summary>
    public static List<Saida> Juntar(IEnumerable<Endpoint> eps)
    {
        var lista = eps.ToList();
        var usados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saida = new List<Saida>();
        foreach (var e in lista)
        {
            if (usados.Contains(e.Id)) continue;
            var grupo = lista.Where(x => !usados.Contains(x.Id) && Chave(x) == Chave(e)).ToList();
            // só junta quando é o par estéreo + Hands-Free; aparelhos diferentes com o mesmo nome continuam separados
            bool par = grupo.Count > 1 && grupo.Any(EhHandsFree) && grupo.Any(x => !EhHandsFree(x));
            if (!par) grupo = new List<Endpoint> { e };
            var principal = grupo.FirstOrDefault(x => !EhHandsFree(x)) ?? e;
            foreach (var x in grupo) usados.Add(x.Id);
            saida.Add(new Saida(principal.Id, principal.Nome, principal.Aparelho, grupo.Select(x => x.Id).ToList()));
        }
        return saida;
    }

    /// <summary>Desconectado (8) ou ausente (4) para ativo = conectou; o contrário = desconectou. Desativar à mão não conta.</summary>
    public static Transicao Classificar(uint antes, uint depois)
    {
        antes &= EstadoAparelho.Todos;
        depois &= EstadoAparelho.Todos;
        bool fora(uint s) => s is EstadoAparelho.Desconectado or EstadoAparelho.Ausente;
        if (fora(antes) && depois == EstadoAparelho.Ativo) return Transicao.Conectou;
        if (antes == EstadoAparelho.Ativo && fora(depois)) return Transicao.Desconectou;
        return Transicao.Nada;
    }

    /// <summary>
    /// A janela do OSD nativo só conta se ACABOU de aparecer: não estava à vista no retrato de quando nada acontecia
    /// e agora está, pequena (o Alt+Tab e outras ilhas XAML do explorer cobrem a tela inteira) e do processo do shell.
    /// </summary>
    public static bool CandidataAoOsd(RetratoJanela? ocioso, RetratoJanela agora, int larguraMonitor, int alturaMonitor, uint pidShell)
    {
        if (agora.Pid != pidShell || pidShell == 0) return false;
        if (!agora.AVista) return false;
        if (ocioso is { AVista: true }) return false;
        long area = (long)agora.Largura * agora.Altura, tela = (long)larguraMonitor * alturaMonitor;
        return tela > 0 && area > 0 && area * 4 < tela;
    }
}

/// <summary>Uma janela de ilha XAML do explorer num instante.</summary>
internal readonly record struct RetratoJanela(IntPtr Janela, uint Pid, bool Visivel, bool Encoberta, bool Minimizada, int X, int Y, int Largura, int Altura)
{
    public bool AVista => Visivel && !Encoberta && !Minimizada && Largura > 0 && Altura > 0 && X > -16000 && Y > -16000;
}

/// <summary>
/// Quando a atividade fica na tela: cada mudança de volume renova os segundos; o aviso de aparelho dura mais; com
/// o cartão aberto ela não some (o cartão fecharia junto) e confere de novo um pouco depois.
/// </summary>
internal sealed class Exibicao
{
    public enum Modo { Volume, Conectou, Desconectou }

    public Modo Atual { get; private set; } = Modo.Volume;
    public string Aparelho { get; private set; } = "";
    public DateTime Ate { get; private set; } = DateTime.MinValue;
    public bool Ligada { get; private set; }

    /// <summary>Mudou o volume (de fora ou pela rodinha). Devolve true se o modo mudou (o layout muda).</summary>
    public bool Volume(DateTime agora, double segundos)
    {
        bool mudou = Atual != Modo.Volume;
        Atual = Modo.Volume;
        Ate = agora.AddSeconds(segundos);
        Ligada = true;
        return mudou;
    }

    public bool Aviso(DateTime agora, bool conectou, string aparelho, double segundos)
    {
        var novo = conectou ? Modo.Conectou : Modo.Desconectou;
        bool mudou = Atual != novo || Aparelho != aparelho;
        Atual = novo;
        Aparelho = aparelho;
        Ate = agora.AddSeconds(segundos);
        Ligada = true;
        return mudou;
    }

    /// <summary>O prazo chegou: desliga, menos com o cartão aberto (aí devolve o próximo instante para conferir).</summary>
    public DateTime? Conferir(DateTime agora, bool expandida)
    {
        if (!Ligada) return null;
        if (agora < Ate) return Ate;
        if (expandida) return agora.AddSeconds(1);
        Ligada = false;
        Atual = Modo.Volume;
        return null;
    }

    public void Desligar()
    {
        Ligada = false;
        Atual = Modo.Volume;
    }
}
