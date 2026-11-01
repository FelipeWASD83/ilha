using System;
using System.IO;
using Microsoft.Win32;

namespace Ilha;

/// <summary>
/// Iniciar com o Windows: valor "Ilha" em HKCU\...\Run (só o usuário, sem admin). Quem grava é só a cópia
/// instalada em %LOCALAPPDATA%\Programs\Ilha: a compilação da pasta do projeto muda a cada build e pode ser barrada
/// pelo Smart App Control, e aí a ilha não abriria no login.
/// </summary>
internal static class Inicio
{
    private const string CHAVE = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string NOME = "Ilha";

    public static string PastaInstalada => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ilha");

    public static bool Instalada =>
        Environment.ProcessPath is { } p && string.Equals(Path.GetDirectoryName(p)?.TrimEnd('\\'), PastaInstalada.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool Ligado()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(CHAVE);
            return k?.GetValue(NOME) is string;
        }
        catch { return false; }
    }

    /// <summary>Aplica a escolha da config. Fora da cópia instalada não grava nada (só registra no log).</summary>
    public static void Definir(bool ligado)
    {
        if (!Instalada)
        {
            Log.Escrever("iniciar com o Windows: esta não é a cópia instalada, nada gravado no Run");
            return;
        }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(CHAVE, true);
            if (k == null) return;
            if (ligado) k.SetValue(NOME, $"\"{Environment.ProcessPath}\"");
            else if (k.GetValue(NOME) != null) k.DeleteValue(NOME);
            Log.Escrever("iniciar com o Windows: " + (ligado ? "ligado" : "desligado"));
        }
        catch (Exception e) { Log.Escrever("iniciar com o Windows: " + e.Message); }
    }
}
