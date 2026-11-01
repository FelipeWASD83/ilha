using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Ilha.Claude;

/// <summary>
/// Vigia ~/.claude/sessions/*.json (um arquivo por sessão aberta, com status busy, idle ou waiting) por
/// FileSystemWatcher. É o que mostra que ele parou por Esc ou erro (o Stop não roda) e quando espera permissão.
/// Formato não documentado: tudo é opcional, e arquivo ilegível é ignorado.
/// </summary>
internal sealed class VigiaDasSessoes : IDisposable
{
    private readonly string _pasta;
    private readonly Action<List<ArquivoDeSessao>, List<string>> _mudou;
    private readonly FileSystemWatcher? _vigia;
    private readonly Timer _espera;
    private readonly object _trava = new();
    // arquivo -> sessão, para saber qual sessão sumiu quando o arquivo é apagado
    private readonly Dictionary<string, string> _conhecidos = new(StringComparer.OrdinalIgnoreCase);
    private bool _fechado;

    /// <param name="mudou">Numa thread do sistema: os arquivos lidos agora e as sessões cujo arquivo sumiu.</param>
    public VigiaDasSessoes(string pasta, Action<List<ArquivoDeSessao>, List<string>> mudou)
    {
        _pasta = pasta;
        _mudou = mudou;
        _espera = new Timer(_ => Ler(), null, Timeout.Infinite, Timeout.Infinite);
        if (Directory.Exists(pasta))
        {
            _vigia = new FileSystemWatcher(pasta, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _vigia.Changed += (_, _) => Agendar();
            _vigia.Created += (_, _) => Agendar();
            _vigia.Deleted += (_, _) => Agendar();
            _vigia.Renamed += (_, _) => Agendar();
            _vigia.Error += (_, _) => Agendar();
            _vigia.EnableRaisingEvents = true;
        }
        _espera.Change(0, Timeout.Infinite);
    }

    private void Agendar()
    {
        if (!_fechado) _espera.Change(80, Timeout.Infinite);
    }

    private void Ler()
    {
        var lidos = new List<ArquivoDeSessao>();
        var sumiram = new List<string>();
        lock (_trava)
        {
            if (_fechado) return;
            var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] arquivos;
            try { arquivos = Directory.GetFiles(_pasta, "*.json"); }
            catch (IOException) { arquivos = Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { arquivos = Array.Empty<string>(); }
            foreach (var a in arquivos)
            {
                string texto;
                try
                {
                    using var fs = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    texto = sr.ReadToEnd();
                }
                // sendo gravado agora: continua valendo o que já se sabia dele, e a próxima mudança relê
                catch (IOException) { vistos.Add(a); Agendar(); continue; }
                catch (UnauthorizedAccessException) { vistos.Add(a); continue; }
                var s = ArquivoDeSessao.Ler(texto);
                if (s == null && _conhecidos.ContainsKey(a)) { vistos.Add(a); continue; }
                // arquivo que ficou para trás de um processo que já fechou (ficaria "busy" para sempre)
                if (s == null || s.Pid > 0 && !Vivo(s.Pid)) continue;
                vistos.Add(a);
                _conhecidos[a] = s.SessaoId;
                lidos.Add(s);
            }
            foreach (var (a, id) in new Dictionary<string, string>(_conhecidos))
            {
                if (vistos.Contains(a)) continue;
                _conhecidos.Remove(a);
                sumiram.Add(id);
            }
        }
        _mudou(lidos, sumiram);
    }

    private static bool Vivo(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    public void Dispose()
    {
        lock (_trava) _fechado = true;
        if (_vigia != null) { _vigia.EnableRaisingEvents = false; _vigia.Dispose(); }
        _espera.Dispose();
    }
}
