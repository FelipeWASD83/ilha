using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ilha.Atualizacoes;

public sealed class ConfigAtualizador
{
    [DisplayName("Repositório no GitHub (dono/nome)")]
    [JsonPropertyName("repositorio")] public string Repositorio { get; set; } = "FelipeWASD83/ilha";

    [DisplayName("Procurar atualização sozinha (no máximo uma vez por dia)")]
    [JsonPropertyName("procurarSozinha")] public bool ProcurarSozinha { get; set; } = true;
}

/// <summary>Quando foi a última procura automática e quais versões já falharam aqui (para não oferecer de novo sozinha).</summary>
internal sealed class EstadoDoAtualizador
{
    [JsonPropertyName("ultimaAutomatica")] public DateTime? UltimaAutomatica { get; set; }
    [JsonPropertyName("falharam")] public List<string> Falharam { get; set; } = new();
}

/// <summary>
/// Procura versão nova uma vez por dia (nunca com jogo competitivo na frente), mostra "Ilha X disponível" com os
/// botões Atualizar e Depois, e faz a troca pelo <see cref="Atualizador"/>. Só a cópia instalada procura sozinha.
/// </summary>
[Modulo("atualizador", "Atualizador")]
internal sealed partial class ModuloAtualizador : Modulo
{
    private ConfigAtualizador _cfg = new();
    private Atualizador? _at;
    private AtividadeAtualizacao? _atv;
    private readonly System.Windows.Forms.Timer _relogio = new();
    private readonly System.Windows.Forms.Timer _depoisDeAbrir = new() { Interval = 45_000 };
    private bool _pendente;
    private bool _procurando;
    private static ModuloAtualizador? _vivo;

    private static string ArquivoDeEstado => Path.Combine(Atualizador.Pasta, "estado.json");

    public override Type? TipoConfig => typeof(ConfigAtualizador);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigAtualizador>(cfg);
        _at = new Atualizador(_cfg.Repositorio);
        Atualizador.Principal = _at;
        _vivo = this;
        var atv = new AtividadeAtualizacao { AoAtualizar = Atualizar };
        atv.AoDispensar = atv.Fechar;
        _atv = atv;
        _at.Progresso += p => atv.Baixando(p);
        _at.Trocando += atv.TrocandoArquivos;
        S.Ilha.Adicionar(atv);

        // garante o sinal mesmo se o núcleo ainda não chama o AbriuBem (roda quando a fila de mensagens começar)
        S.NaInterface(Atualizador.AbriuBem);
        LerResultado();
        _depoisDeAbrir.Tick += (_, _) =>
        {
            _depoisDeAbrir.Stop();
            // o executor grava o resultado depois que esta ilha já abriu: lê de novo e limpa o que ele deixou
            LerResultado();
            Atualizador.LimparSobras();
        };
        _depoisDeAbrir.Start();
        _relogio.Tick += (_, _) => { _relogio.Stop(); ProcurarSozinha(); };
        Agendar();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigAtualizador>(cfg);
        if (_at != null) _at.Repositorio = _cfg.Repositorio;
        Agendar();
    }

    public override void Contexto(Contexto ctx)
    {
        if (_pendente && !ctx.Competitivo)
        {
            // saiu do jogo: procura daqui a pouco, não no mesmo instante da troca de janela
            _pendente = false;
            _relogio.Stop();
            _relogio.Interval = 30_000;
            _relogio.Start();
        }
    }

    public override void Parar()
    {
        _relogio.Stop();
        _relogio.Dispose();
        _depoisDeAbrir.Stop();
        _depoisDeAbrir.Dispose();
        _atv?.Fechar();
        if (Atualizador.Principal == _at) Atualizador.Principal = null;
        if (_vivo == this) _vivo = null;
    }

    /// <summary>"Procurar atualização" do menu da bandeja. False se o módulo estiver desligado.</summary>
    public static bool ProcurarAgora()
    {
        if (_vivo == null) return false;
        _vivo.ProcurarManual();
        return true;
    }

    // ------------------------------------------------------------------ estado

    private static EstadoDoAtualizador LerEstado()
    {
        try { return JsonSerializer.Deserialize<EstadoDoAtualizador>(File.ReadAllText(ArquivoDeEstado)) ?? new(); }
        catch { return new(); }
    }

    private static void GravarEstado(EstadoDoAtualizador e)
    {
        try
        {
            Directory.CreateDirectory(Atualizador.Pasta);
            File.WriteAllText(ArquivoDeEstado, JsonSerializer.Serialize(e, Config.Json));
        }
        catch (Exception ex) { Log.Escrever("atualizador: não gravou o estado: " + ex.Message); }
    }

    private void LerResultado()
    {
        JsonNode? r;
        try
        {
            if (!File.Exists(Atualizador.Resultado)) return;
            r = JsonNode.Parse(File.ReadAllText(Atualizador.Resultado));
            File.Delete(Atualizador.Resultado);
        }
        catch { return; }
        if (r == null) return;
        bool ok = r["ok"]?.GetValue<bool>() == true;
        string versao = r["versao"]?.GetValue<string>() ?? "?";
        string de = r["de"]?.GetValue<string>() ?? "?";
        string motivo = r["motivo"]?.GetValue<string>() ?? "";
        if (ok)
        {
            Log.Escrever($"atualizador: atualizada de {de} para {versao}");
            S.Avisar($"Ilha atualizada para a {versao}.");
            return;
        }
        Log.Escrever($"atualizador: a {versao} não deu certo ({motivo})");
        S.Avisar($"A atualização para a {versao} não deu certo ({motivo}). A ilha continua na {Atualizador.VersaoAtual}.");
        var e = LerEstado();
        if (!e.Falharam.Contains(versao)) { e.Falharam.Add(versao); GravarEstado(e); }
    }

    // ------------------------------------------------------------------ procurar

    private void Agendar()
    {
        _relogio.Stop();
        if (!_cfg.ProcurarSozinha || !Inicio.Instalada) return;
        var ultima = LerEstado().UltimaAutomatica ?? DateTime.MinValue;
        var espera = ultima == DateTime.MinValue ? TimeSpan.Zero : ultima.AddHours(24) - DateTime.UtcNow;
        // nunca logo ao abrir: a ilha sobe com o Windows junto de tudo o mais
        if (espera < TimeSpan.FromMinutes(3)) espera = TimeSpan.FromMinutes(3);
        _relogio.Interval = (int)Math.Min(int.MaxValue, espera.TotalMilliseconds);
        _relogio.Start();
    }

    private async void ProcurarSozinha()
    {
        if (_at == null || _atv == null || !_cfg.ProcurarSozinha || !Inicio.Instalada) return;
        if (S.ContextoAtual().Competitivo) { _pendente = true; return; }
        if (_procurando || _at.Instalando || _atv.Ativa) { Agendar(); return; }
        _procurando = true;
        try
        {
            var v = await _at.Procurar();
            var e = LerEstado();
            e.UltimaAutomatica = DateTime.UtcNow;
            GravarEstado(e);
            if (v == null) Log.Escrever($"atualizador: já está na mais nova ({Atualizador.VersaoAtual})");
            else if (e.Falharam.Contains(v)) Log.Escrever($"atualizador: a {v} já falhou aqui; não ofereço sozinha");
            else if (!S.ContextoAtual().Competitivo) Mostrar(v);
            else _pendente = true;
        }
        catch (ErroDeAtualizacao ex) { Log.Escrever("atualizador: " + ex.Message); }
        catch (Exception ex) { Log.Escrever("atualizador: erro ao procurar: " + ex); }
        finally
        {
            _procurando = false;
            Agendar();
        }
    }

    private void Mostrar(string versao)
    {
        if (_at?.Ultima == null || _atv == null) return;
        Log.Escrever($"atualizador: versão {versao} disponível");
        bool jaAtiva = _atv.Ativa;
        _atv.Disponivel(versao, Atualizador.VersaoAtual, _at.Ultima.Resumo());
        if (jaAtiva) S.Ilha.Expandir(_atv);
    }

    private async void ProcurarManual()
    {
        if (_at == null || _procurando) return;
        _procurando = true;
        try
        {
            var v = await _at.Procurar(forcar: true);
            if (v != null) Mostrar(v);
            else S.Avisar($"A ilha já está na versão mais nova ({Atualizador.VersaoAtual}).");
        }
        catch (ErroDeAtualizacao ex) { S.Avisar(ex.Message); }
        catch (Exception ex) { Log.Escrever("atualizador: erro ao procurar: " + ex); S.Avisar("Não deu para procurar atualização (detalhes no ilha.log)."); }
        finally { _procurando = false; }
    }

    private async void Atualizar()
    {
        if (_at == null || _atv == null || _at.Instalando) return;
        _atv.Baixando(0);
        try
        {
            await _at.Instalar();
        }
        catch (ErroDeAtualizacao ex)
        {
            Log.Escrever("atualizador: " + ex.Message);
            _atv.Erro(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Escrever("atualizador: erro ao instalar: " + ex);
            _atv.Erro("Erro inesperado (detalhes no ilha.log).");
        }
    }
}
