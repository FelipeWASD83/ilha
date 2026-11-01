using System;
using System.Collections.Generic;

namespace Ilha.Ar;

/// <summary>O que o alerta compara: só ligado, modo e alvo. A sala muda sozinha e nunca conta como mudança.</summary>
internal sealed record EstadoBasico(bool? Ligado, string? Modo, double? Alvo)
{
    public static EstadoBasico De(EstadoAr e) => new(e.Ligado, e.Modo, e.Alvo);
    public static EstadoBasico De(PontoHistorico p) => new(p.Ligado, p.Modo, p.Alvo);
}

internal enum TipoMudanca { Ligou, Desligou, Ajuste }

internal sealed record MudancaAr(TipoMudanca Tipo, EstadoBasico Antes, EstadoBasico Depois, bool MudouModo, bool MudouAlvo, bool PelaIlha);

/// <summary>
/// Decide quando o estado do ar mudou de verdade (ligou, desligou, trocou o modo ou o alvo), seja pelo controle
/// remoto, pelo site, pela nova aba ou pela própria ilha. Recebe as leituras com o `t` do servidor (o ponto mais
/// novo do /api/historico ou o lido_em do /api/estado) e só olha leitura mais nova que a última processada. A
/// primeira leitura só serve de referência. Comando da ilha muda a referência na hora e, por
/// <see cref="CARENCIA_MS"/>, leitura que discorda de um campo recém-comandado é ignorada nesse campo (a LG demora a
/// refletir: a confirmação de +4 s ainda pode vir com o valor velho), a não ser que bata com o pedido.
/// </summary>
internal sealed class DetectorDeMudanca
{
    public const long CARENCIA_MS = 60_000;

    private EstadoBasico? _ref;
    private long _ultimoT = long.MinValue;
    private readonly Dictionary<string, (object Valor, long Ate)> _pedidos = new();

    public EstadoBasico? Referencia => _ref;
    public long UltimoT => _ultimoT;

    /// <summary>Esquece tudo: a próxima leitura volta a ser só referência.</summary>
    public void Zerar()
    {
        _ref = null;
        _ultimoT = long.MinValue;
        _pedidos.Clear();
    }

    /// <summary>Uma leitura do instante `t` (relógio do servidor). Devolve a mudança, ou null.</summary>
    public MudancaAr? Leitura(long t, EstadoBasico lido, long agoraMs)
    {
        if (t <= _ultimoT) return null;
        _ultimoT = t;
        foreach (var k in new List<string>(_pedidos.Keys))
            if (agoraMs >= _pedidos[k].Ate) _pedidos.Remove(k);

        bool? ligado = Filtrar("ligado", lido.Ligado, _ref?.Ligado);
        string? modo = Filtrar("modo", lido.Modo, _ref?.Modo);
        double? alvo = Filtrar("alvo", lido.Alvo, _ref?.Alvo);

        var antes = _ref;
        // campo que não veio continua com o que se sabia
        var depois = new EstadoBasico(ligado ?? antes?.Ligado, modo ?? antes?.Modo, alvo ?? antes?.Alvo);
        _ref = depois;
        return antes == null ? null : Comparar(antes, depois, pelaIlha: false);
    }

    private T? Filtrar<T>(string campo, T? lido, T? referencia)
    {
        if (lido == null || !_pedidos.TryGetValue(campo, out var p)) return lido;
        if (Igual(lido, p.Valor))
        {
            // confirmou: daqui pra frente qualquer divergência é mudança de verdade
            _pedidos.Remove(campo);
            return lido;
        }
        return referencia;
    }

    /// <summary>
    /// A ilha mandou um comando e o servidor aceitou. `lidoEm` é o da resposta (a leitura de ANTES do comando: ponto
    /// com esse `t` nunca pode desfazer o pedido). Devolve a mudança que o comando fez, ou null.
    /// </summary>
    public MudancaAr? Comandou(IReadOnlyDictionary<string, object> pedido, long? lidoEm, EstadoBasico? daResposta, long agoraMs)
    {
        if (lidoEm is long l && l > _ultimoT) _ultimoT = l;
        bool? ligado = pedido.TryGetValue("ligado", out var vl) && vl is bool b ? b : null;
        string? modo = pedido.TryGetValue("modo", out var vm) && vm is string s ? s : null;
        double? alvo = pedido.TryGetValue("alvo", out var va) && Numero(va) is double d ? d : null;
        if (ligado != null) _pedidos["ligado"] = (ligado.Value, agoraMs + CARENCIA_MS);
        if (modo != null) _pedidos["modo"] = (modo, agoraMs + CARENCIA_MS);
        if (alvo != null) _pedidos["alvo"] = (alvo.Value, agoraMs + CARENCIA_MS);
        if (ligado == null && modo == null && alvo == null) return null;

        var antes = _ref;
        var base_ = antes ?? daResposta ?? new EstadoBasico(null, null, null);
        _ref = new EstadoBasico(ligado ?? base_.Ligado, modo ?? base_.Modo, alvo ?? base_.Alvo);
        // sem referência ainda não dá pra saber o que era (a resposta já vem com o pedido aplicado)
        return antes == null ? null : Comparar(antes, _ref, pelaIlha: true);
    }

    /// <summary>
    /// A leitura com os campos ainda na carência trocados pelo que foi pedido (a não ser que já batam): o mesmo filtro
    /// da <see cref="Leitura"/>, sem mexer em nada. É o que o menu mostra, para o ícone não voltar ao estado de antes
    /// com a confirmação atrasada da LG.
    /// </summary>
    public EstadoBasico ComPedidos(EstadoBasico lido, long agoraMs)
    {
        T? Campo<T>(string campo, T? valor)
        {
            if (valor == null || !_pedidos.TryGetValue(campo, out var p) || agoraMs >= p.Ate || Igual(valor, p.Valor)) return valor;
            return p.Valor is T v ? v : valor;
        }
        bool? ligado = Campo("ligado", lido.Ligado);
        string? modo = Campo("modo", lido.Modo);
        double? alvo = lido.Alvo;
        if (alvo != null && _pedidos.TryGetValue("alvo", out var pa) && agoraMs < pa.Ate && !Igual(alvo.Value, pa.Valor))
            alvo = Numero(pa.Valor) ?? alvo;
        return new EstadoBasico(ligado, modo, alvo);
    }

    /// <summary>
    /// Ligou ou desligou sempre conta. Modo e alvo só contam com o ar ligado antes e depois (com ele desligado nada
    /// muda no quarto), e o alvo não conta no ventilar (não tem temperatura). Campo desconhecido não é mudança.
    /// </summary>
    public static MudancaAr? Comparar(EstadoBasico antes, EstadoBasico depois, bool pelaIlha)
    {
        if (antes.Ligado is bool a && depois.Ligado is bool d && a != d)
            return new MudancaAr(d ? TipoMudanca.Ligou : TipoMudanca.Desligou, antes, depois, false, false, pelaIlha);
        if (antes.Ligado != true || depois.Ligado != true) return null;
        bool modo = antes.Modo != null && depois.Modo != null && antes.Modo != depois.Modo;
        bool alvo = antes.Alvo is double x && depois.Alvo is double y && Math.Abs(x - y) > 0.01 && depois.Modo != "FAN";
        return modo || alvo ? new MudancaAr(TipoMudanca.Ajuste, antes, depois, modo, alvo, pelaIlha) : null;
    }

    private static bool Igual(object lido, object pedido) => lido switch
    {
        double x => Numero(pedido) is double y && Math.Abs(x - y) < 0.01,
        _ => Equals(lido, pedido),
    };

    private static double? Numero(object? v) => v switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        _ => null,
    };
}

/// <summary>
/// O alerta dos comandos da própria ilha. Eles saem do cartão, e alerta com um cartão aberto (o do ar ou o menu)
/// seria desenhado por cima dele; então fica guardado e sai quando a ilha recolhe, um só para a leva inteira: do
/// estado de antes do primeiro comando ao de agora ("Ar em 24°, era 22°" depois de dois cliques no +). Ligar e
/// desligar de novo antes de fechar não alerta nada. Guardado há mais de <see cref="PRAZO_MS"/> é velho e some.
/// </summary>
internal sealed class AlertaAdiado
{
    public const long PRAZO_MS = 30_000;

    private EstadoBasico? _antes;
    private long _ultimo;

    public bool Vazio => _antes == null;

    public void Guardar(MudancaAr m, long agoraMs)
    {
        _antes ??= m.Antes;
        _ultimo = agoraMs;
    }

    public void Limpar() => _antes = null;

    /// <summary>A mudança da leva até `agora` (o que o detector tem como referência), ou null. Sempre esvazia.</summary>
    public MudancaAr? Soltar(EstadoBasico? agora, long agoraMs)
    {
        var antes = _antes;
        _antes = null;
        if (antes == null || agora == null || agoraMs - _ultimo > PRAZO_MS) return null;
        return DetectorDeMudanca.Comparar(antes, agora, pelaIlha: true);
    }
}
