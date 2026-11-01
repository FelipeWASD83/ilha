using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace Ilha.Extras;

/// <summary>Uma notificação lida do banco do Windows.</summary>
internal sealed record NotificacaoLida(long Ordem, long Id, string App, DateTime Chegada, string Xml);

/// <summary>Título e texto de um toast.</summary>
internal sealed record Toast(string Titulo, string Texto);

/// <summary>
/// SQLite pelo winsqlite3.dll do próprio Windows (sem pacote nem DLL nova). Somente leitura e sem conexão
/// guardada: abrir, ler e fechar a cada gatilho. No modo WAL um leitor com transação aberta impede o checkpoint
/// do serviço de notificações e o -wal dele cresceria.
/// </summary>
internal static class Sqlite
{
    private const string Dll = "winsqlite3.dll";
    [DllImport(Dll)] private static extern int sqlite3_open_v2(byte[] arquivo, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Dll)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Dll)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Dll)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr resto);
    [DllImport(Dll)] private static extern int sqlite3_bind_int64(IntPtr stmt, int i, long v);
    [DllImport(Dll)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Dll)] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Dll)] private static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Dll)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport(Dll)] private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Dll)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Dll)] private static extern IntPtr sqlite3_errmsg(IntPtr db);

    private const int SQLITE_OPEN_READONLY = 0x1, SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_OK = 0;

    private static byte[] Utf8z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>Abre só para ler, roda a consulta (parâmetros posicionais int64) e fecha. Cada linha vai para `linha`.</summary>
    public static void Consultar(string arquivo, string sql, long[] parametros, Action<Func<int, long>, Func<int, byte[]?>, Func<int, string?>> linha)
    {
        if (sqlite3_open_v2(Utf8z(arquivo), out var db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
        {
            string erro = db != IntPtr.Zero ? Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "" : "";
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
            throw new IOException("o banco não abriu: " + erro);
        }
        try
        {
            sqlite3_busy_timeout(db, 500);
            if (sqlite3_prepare_v2(db, Utf8z(sql), -1, out var st, IntPtr.Zero) != SQLITE_OK)
                throw new IOException("consulta recusada: " + Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));
            try
            {
                for (int i = 0; i < parametros.Length; i++) sqlite3_bind_int64(st, i + 1, parametros[i]);
                long Int(int c) => sqlite3_column_int64(st, c);
                byte[]? Blob(int c)
                {
                    int n = sqlite3_column_bytes(st, c);
                    var p = sqlite3_column_blob(st, c);
                    if (p == IntPtr.Zero || n <= 0) return null;
                    var b = new byte[n];
                    Marshal.Copy(p, b, 0, n);
                    return b;
                }
                string? Texto(int c) { var p = sqlite3_column_text(st, c); return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p); }
                int r;
                while ((r = sqlite3_step(st)) == SQLITE_ROW) linha(Int, Blob, Texto);
                if (r != SQLITE_DONE) throw new IOException("leitura parou: " + Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));
            }
            finally { sqlite3_finalize(st); }
        }
        finally { sqlite3_close_v2(db); }
    }
}

/// <summary>
/// O banco onde o Windows guarda as notificações (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db):
/// tabelas Notification (Order, Id, HandlerId, Type, Payload = XML do toast, ArrivalTime em FILETIME) e
/// NotificationHandler (RecordId, PrimaryId = id do app). Esquema não documentado: pode mudar num update.
/// </summary>
internal static class BancoDeNotificacoes
{
    public static string Arquivo => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    public const string SqlNovas =
        "SELECT n.[Order], n.Id, h.PrimaryId, n.ArrivalTime, n.Payload FROM Notification n " +
        "JOIN NotificationHandler h ON h.RecordId = n.HandlerId WHERE n.Type = 'toast' AND n.[Order] > ?1 ORDER BY n.[Order]";

    public static long UltimaOrdem(string arquivo)
    {
        long r = 0;
        Sqlite.Consultar(arquivo, "SELECT IFNULL(MAX([Order]), 0) FROM Notification", Array.Empty<long>(), (i, _, _) => r = i(0));
        return r;
    }

    public static List<NotificacaoLida> Novas(string arquivo, long depoisDe)
    {
        var r = new List<NotificacaoLida>();
        Sqlite.Consultar(arquivo, SqlNovas, new[] { depoisDe }, (i, b, t) =>
        {
            var bytes = b(4);
            string xml = bytes == null ? "" : TextoDoPayload(bytes);
            DateTime chegada;
            try { chegada = DateTime.FromFileTimeUtc(i(3)); } catch { chegada = DateTime.MinValue; }
            r.Add(new NotificacaoLida(i(0), i(1), t(2) ?? "", chegada, xml));
        });
        return r;
    }

    public static string TextoDoPayload(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        int ini = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(b, ini, b.Length - ini).TrimEnd('\0');
    }

    /// <summary>
    /// Título e texto do XML do toast: o binding ToastGeneric (ou o primeiro), os &lt;text&gt; em ordem, sem a linha de
    /// atribuição. Primeiro texto = título; o resto, juntado. XML ruim ou sem texto = null.
    /// </summary>
    public static Toast? LerToast(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var cfg = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var doc = new XmlDocument { XmlResolver = null };
            using (var sr = new StringReader(xml))
            using (var rd = XmlReader.Create(sr, cfg)) doc.Load(rd);
            var bindings = doc.GetElementsByTagName("binding").Cast<XmlElement>().ToList();
            var binding = bindings.FirstOrDefault(b => string.Equals(b.GetAttribute("template"), "ToastGeneric", StringComparison.OrdinalIgnoreCase)) ?? bindings.FirstOrDefault();
            if (binding == null) return null;
            var textos = binding.GetElementsByTagName("text").Cast<XmlElement>()
                .Where(t => !string.Equals(t.GetAttribute("placement"), "attribution", StringComparison.OrdinalIgnoreCase))
                .Select(t => Espacos(t.InnerText))
                .Where(t => t.Length > 0)
                .ToList();
            if (textos.Count == 0) return null;
            return new Toast(textos[0], string.Join(" · ", textos.Skip(1)));
        }
        catch (XmlException) { return null; }
    }

    private static string Espacos(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Nome para mostrar a partir do id do app: "Fabricante.App_hash!App" vira "App", "com.squirrel.Discord.Discord"
    /// vira "Discord", caminho de .exe vira o nome do .exe.
    /// </summary>
    public static string NomeDoApp(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "App";
        var s = id.Trim();
        if (s.Contains('\\') || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(s.Replace('/', '\\').Split('\\').Last());
        int ex = s.IndexOf('!');
        if (ex >= 0) s = s[..ex];
        int sub = s.IndexOf('_');
        if (sub > 0) s = s[..sub];
        var partes = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return partes.Length == 0 ? id : partes[^1];
    }

    /// <summary>O app está na lista permitida (pedaço do id, sem diferenciar maiúscula). Lista vazia = nenhum.</summary>
    public static bool Permitido(string id, IEnumerable<string> permitidos) =>
        permitidos.Any(p => !string.IsNullOrWhiteSpace(p) && id.Contains(p.Trim(), StringComparison.OrdinalIgnoreCase));
}
