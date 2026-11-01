using System;
using System.Collections.Generic;

// não é Ilha.Modulos.Midia: o núcleo tem a classe estática Ilha.Modulos e o namespace colidiria com ela
namespace Ilha.Midia;

/// <summary>O que o player disse da linha do tempo: a posição vale no instante `Atualizado` (o SMTC não manda a posição andando).</summary>
internal readonly record struct Instante(TimeSpan Inicio, TimeSpan Fim, TimeSpan Posicao, DateTimeOffset Atualizado, bool Tocando, double Taxa)
{
    public bool TemDuracao => Fim > Inicio;
    public TimeSpan Duracao => TemDuracao ? Fim - Inicio : TimeSpan.Zero;
}

/// <summary>
/// Linha do tempo da sessão escolhida: extrapola a posição enquanto toca e segura a posição pedida por um arraste
/// até o player confirmar (TimelinePropertiesChanged perto do alvo) ou passar o tempo-limite.
/// </summary>
internal sealed class Linha
{
    public static readonly TimeSpan Tolerancia = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LimiteSegurar = TimeSpan.FromSeconds(3);

    private (TimeSpan Alvo, DateTimeOffset Pedido)? _segura;

    public Instante Atual { get; private set; }
    public bool Segurando => _segura != null;

    public Linha() { }
    public Linha(Instante i) => Atual = i;

    /// <summary>Posição agora: Position + (agora - LastUpdatedTime) × taxa, só tocando, limitada ao fim.</summary>
    public static TimeSpan Extrapolar(Instante i, DateTimeOffset agora)
    {
        var p = i.Posicao;
        // LastUpdatedTime zerado (ano 1601 ou 0001) = o player não diz quando: não dá para andar
        if (i.Tocando && i.Atualizado.Year > 2000)
        {
            var dt = agora - i.Atualizado;
            if (dt > TimeSpan.Zero) p += TimeSpan.FromTicks((long)(dt.Ticks * (i.Taxa > 0 ? i.Taxa : 1)));
        }
        return Limitar(i, p);
    }

    private static TimeSpan Limitar(Instante i, TimeSpan p)
    {
        if (i.TemDuracao) return p < i.Inicio ? i.Inicio : p > i.Fim ? i.Fim : p;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }

    public TimeSpan Posicao(DateTimeOffset agora)
    {
        if (_segura is { } s)
        {
            if (agora - s.Pedido > LimiteSegurar) _segura = null;
            else
            {
                var p = s.Alvo;
                if (Atual.Tocando) p += TimeSpan.FromTicks((long)((agora - s.Pedido).Ticks * (Atual.Taxa > 0 ? Atual.Taxa : 1)));
                return Limitar(Atual, p);
            }
        }
        return Extrapolar(Atual, agora);
    }

    /// <summary>De 0 a 1 (0 sem duração conhecida).</summary>
    public double Fracao(DateTimeOffset agora)
    {
        if (!Atual.TemDuracao) return 0;
        return Math.Clamp((Posicao(agora) - Atual.Inicio).Ticks / (double)Atual.Duracao.Ticks, 0, 1);
    }

    /// <summary>O arraste soltou: mostra o alvo já, até o player confirmar.</summary>
    public void Pedir(TimeSpan alvo, DateTimeOffset agora) => _segura = (Limitar(Atual, alvo), agora);

    /// <summary>O player recusou o pedido (TryChangePlaybackPositionAsync devolveu false).</summary>
    public void Recusado() => _segura = null;

    /// <summary>Chegou um TimelinePropertiesChanged. Solta o alvo segurado se a posição nova bate com ele.</summary>
    public void Recebeu(Instante novo, DateTimeOffset agora)
    {
        TimeSpan? esperado = _segura != null ? Posicao(agora) : null;
        Atual = novo;
        if (esperado is { } e && (Extrapolar(novo, agora) - e).Duration() <= Tolerancia) _segura = null;
    }

    /// <summary>
    /// Tocar ou pausar mudou (PlaybackInfoChanged), às vezes sem o player mandar a linha nova: congela a posição
    /// extrapolada no instante da mudança, para a barra não pular de volta ao pausar.
    /// </summary>
    public void MudouEstado(bool tocando, double taxa, DateTimeOffset agora)
    {
        if (tocando == Atual.Tocando && Math.Abs(taxa - Atual.Taxa) < 1e-6) return;
        var p = Extrapolar(Atual, agora);
        if (_segura is { } s)
        {
            p = Posicao(agora);
            _segura = (p, agora);
        }
        Atual = Atual with { Posicao = p, Atualizado = agora, Tocando = tocando, Taxa = taxa };
    }
}

/// <summary>O que a mídia sabe de cada sessão do SMTC para escolher qual mostrar.</summary>
internal sealed class Rastro
{
    public required string Chave;
    public bool Tocando;
    /// <summary>Pausada, parada ou trocando de faixa: ainda aparece por alguns minutos.</summary>
    public bool Pausada;
    public DateTime TocandoDesde;
    public DateTime UltimaVezTocando;

    /// <summary>Atualiza com o estado lido agora. `ultimaAtualizacao` é o LastUpdatedTime da linha, usado quando a sessão já aparece pausada.</summary>
    public void Atualizar(bool tocando, bool pausada, DateTime agora, DateTime? ultimaAtualizacao, bool nova)
    {
        if (tocando)
        {
            if (!Tocando) TocandoDesde = agora;
            UltimaVezTocando = agora;
        }
        else if (Tocando) UltimaVezTocando = agora;
        else if (nova)
        {
            // apareceu já pausada (a ilha abriu depois): vale a última vez que o player mexeu na posição
            UltimaVezTocando = ultimaAtualizacao is { } u && u.Year > 2000 && u <= agora ? u : agora;
        }
        Tocando = tocando;
        Pausada = !tocando && pausada;
    }
}

internal static class Escolha
{
    /// <summary>
    /// Qual sessão mostrar: a que começou a tocar por último; sem nenhuma tocando, a pausada mais recente que
    /// parou há menos de `limite`. Empate fica com a atual. -1 = nenhuma.
    /// </summary>
    public static int Sessao(IReadOnlyList<Rastro> s, DateTime agora, TimeSpan limite, string? atual)
    {
        int melhor = -1;
        for (int i = 0; i < s.Count; i++)
        {
            if (!s[i].Tocando) continue;
            if (melhor < 0 || s[i].TocandoDesde > s[melhor].TocandoDesde || s[i].TocandoDesde == s[melhor].TocandoDesde && s[i].Chave == atual)
                melhor = i;
        }
        if (melhor >= 0) return melhor;
        for (int i = 0; i < s.Count; i++)
        {
            if (!s[i].Pausada || agora - s[i].UltimaVezTocando >= limite) continue;
            if (melhor < 0 || s[i].UltimaVezTocando > s[melhor].UltimaVezTocando || s[i].UltimaVezTocando == s[melhor].UltimaVezTocando && s[i].Chave == atual)
                melhor = i;
        }
        return melhor;
    }
}

internal static class Rotulo
{
    /// <summary>m:ss, ou h:mm:ss passando de uma hora.</summary>
    public static string Tempo(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        long s = (long)t.TotalSeconds;
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    /// <summary>Nome legível do app a partir do AUMID ("Spotify.exe", "SpotifyAB.SpotifyMusic_x!Spotify", "Brave").</summary>
    public static string App(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "";
        var n = aumid;
        int ex = n.LastIndexOf('!');
        if (ex >= 0 && ex < n.Length - 1)
        {
            // "Pacote_hash!App": o nome genérico depois do ! não diz nada, vale o nome do pacote
            string depois = n[(ex + 1)..];
            if (depois.Equals("App", StringComparison.OrdinalIgnoreCase))
            {
                n = n[..ex];
                int sub = n.IndexOf('_');
                if (sub > 0) n = n[..sub];
            }
            else n = depois;
        }
        int barra = n.LastIndexOfAny(new[] { '\\', '/' });
        if (barra >= 0) n = n[(barra + 1)..];
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        int ponto = n.LastIndexOf('.');
        if (ponto >= 0 && ponto < n.Length - 1) n = n[(ponto + 1)..];
        return n.Length > 0 ? char.ToUpperInvariant(n[0]) + n[1..] : n;
    }
}
