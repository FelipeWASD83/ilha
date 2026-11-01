using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Ilha.Atualizacoes;

/// <summary>
/// O que a troca vai fazer na pasta instalada. Guardar: arquivos gerenciados que existem hoje (os do pacote velho,
/// pelo pacote.json dele, mais os que o pacote novo vai sobrescrever) e vão para "anterior". Copiar: os arquivos
/// do pacote novo. O que não está em nenhuma das duas listas (logos, fotos de fones, arquivo que a pessoa pôs lá)
/// não é tocado.
/// </summary>
internal sealed class PlanoDeTroca
{
    public List<string> Guardar { get; } = new();
    public List<string> Copiar { get; } = new();
}

internal static class Troca
{
    public static PlanoDeTroca Planejar(string destino, IEnumerable<string> arquivosNovos, IEnumerable<string>? gerenciadosAntigos)
    {
        var p = new PlanoDeTroca();
        var novos = arquivosNovos.Select(Pacote.Barra).Where(Pacote.RelativoSeguro).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var guardar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in (gerenciadosAntigos ?? Array.Empty<string>()).Select(Pacote.Barra).Concat(novos))
        {
            // lista velha com caminho estranho (arquivo editado à mão, pacote antigo) não decide nada fora da pasta
            if (!Pacote.RelativoSeguro(rel) || Pacote.Protegido(rel)) continue;
            if (File.Exists(Path.Combine(destino, rel))) guardar.Add(rel);
        }
        p.Guardar.AddRange(guardar.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        foreach (var rel in novos)
        {
            // pasta protegida: o pacote só completa o que falta, nunca troca o que está lá
            if (Pacote.Protegido(rel) && File.Exists(Path.Combine(destino, rel))) continue;
            p.Copiar.Add(rel);
        }
        return p;
    }

    /// <summary>
    /// Move os gerenciados para "anterior" e copia os novos. Se qualquer passo falhar, desfaz o que fez e lança:
    /// a pasta instalada fica como estava.
    /// </summary>
    public static void Aplicar(PlanoDeTroca p, string destino, string preparacao, string anterior)
    {
        if (Directory.Exists(anterior)) ComRepeticao(() => Directory.Delete(anterior, true));
        Directory.CreateDirectory(anterior);
        // só o que foi feito de verdade entra na volta: um arquivo que ainda não saiu da pasta não pode ser apagado
        var movidos = new List<string>();
        var copiados = new List<string>();
        try
        {
            foreach (var rel in p.Guardar)
            {
                var de = Path.Combine(destino, rel);
                var para = Path.Combine(anterior, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(para)!);
                // logo depois que a ilha velha fecha, o antivírus ou o próprio Windows ainda pode segurar o .exe um instante
                ComRepeticao(() => File.Move(de, para, true));
                movidos.Add(rel);
            }
            foreach (var rel in p.Copiar)
            {
                var de = Path.Combine(preparacao, rel);
                var para = Path.Combine(destino, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(para)!);
                // entra antes de copiar: uma cópia que falhou no meio deixa arquivo pela metade, que a volta tira
                copiados.Add(rel);
                ComRepeticao(() => File.Copy(de, para, true));
            }
        }
        catch (Exception e)
        {
            try { Reverter(movidos, copiados, destino, anterior); }
            catch (Exception e2) { throw new ErroDeAtualizacao(Lang.T($"A troca falhou ({e.Message}) e a volta também ({e2.Message}). A versão anterior está em {anterior}.",
                $"Replacing failed ({e.Message}) and so did rolling back ({e2.Message}). The previous version is in {anterior}.")); }
            throw new ErroDeAtualizacao(Lang.T($"Não deu para trocar os arquivos ({e.Message}); a versão anterior foi restaurada.", $"Couldn't replace the files ({e.Message}); the previous version was restored."));
        }
    }

    /// <summary>Desfaz uma troca completa (a versão nova não abriu).</summary>
    public static void Reverter(PlanoDeTroca p, string destino, string anterior) => Reverter(p.Guardar, p.Copiar, destino, anterior);

    /// <summary>Tira os arquivos copiados e devolve os guardados em "anterior" para a pasta instalada.</summary>
    private static void Reverter(IEnumerable<string> movidos, IEnumerable<string> copiados, string destino, string anterior)
    {
        var erros = new List<string>();
        foreach (var rel in copiados)
        {
            var arq = Path.Combine(destino, rel);
            try { if (File.Exists(arq)) ComRepeticao(() => File.Delete(arq)); }
            catch (Exception e) { erros.Add($"{rel}: {e.Message}"); }
        }
        foreach (var rel in movidos)
        {
            var guardado = Path.Combine(anterior, rel);
            if (!File.Exists(guardado)) continue;
            try
            {
                var alvo = Path.Combine(destino, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(alvo)!);
                ComRepeticao(() => File.Move(guardado, alvo, true));
            }
            catch (Exception e) { erros.Add($"{rel}: {e.Message}"); }
        }
        if (erros.Count > 0) throw new IOException(string.Join("; ", erros.Take(3)));
    }

    public static void ComRepeticao(Action a, int vezes = 20, int esperaMs = 250)
    {
        for (int i = 1; ; i++)
        {
            try { a(); return; }
            // arquivo preso espera e tenta de novo; arquivo que não existe não vai aparecer esperando
            catch (Exception e) when (i < vezes && e is (IOException or UnauthorizedAccessException) and not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(esperaMs);
            }
        }
    }

    /// <summary>Copia o conteúdo de uma pasta para outra (criada vazia).</summary>
    public static void CopiarPasta(string de, string para)
    {
        if (Directory.Exists(para)) ComRepeticao(() => Directory.Delete(para, true), 8);
        Directory.CreateDirectory(para);
        foreach (var rel in Pacote.Arquivos(de))
        {
            var alvo = Path.Combine(para, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(alvo)!);
            File.Copy(Path.Combine(de, rel), alvo, true);
        }
    }
}
