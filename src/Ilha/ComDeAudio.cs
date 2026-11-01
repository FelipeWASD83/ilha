using System;

namespace Ilha;

/// <summary>
/// O MMDeviceEnumerator do Windows devolve o mesmo objeto COM para o processo inteiro. Com duas classes [ComImport]
/// para ele (uma por módulo), o .NET embrulha o objeto com a primeira que viu e o `new` da outra falha no cast. Por
/// isso ninguém declara a classe: todo mundo cria por aqui e converte para a interface do próprio módulo (QueryInterface).
/// </summary>
internal static class ComDeAudio
{
    private static readonly Type _enumerador = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;

    public static object NovoEnumerador() => Activator.CreateInstance(_enumerador)!;
}
