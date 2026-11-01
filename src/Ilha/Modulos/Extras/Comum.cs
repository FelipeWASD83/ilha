using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Ilha.Extras;

/// <summary>Pastas do Windows pelo GUID (FOLDERID_*): o nome que o Explorador mostra muda com o idioma, o GUID não.</summary>
internal static class PastaConhecida
{
    public static readonly Guid Capturas = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    public static readonly Guid Documentos = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr caminho);

    public static string? Caminho(Guid id)
    {
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>Caminho para relatório: troca a pasta do usuário por %USERPROFILE% (o relatório pode ser colado em outro lugar).</summary>
    public static string Mascarar(string? caminho)
    {
        if (string.IsNullOrEmpty(caminho)) return "(nenhum)";
        string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return perfil.Length > 0 && caminho.StartsWith(perfil, StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%" + caminho[perfil.Length..] : caminho;
    }
}

/// <summary>Lista de processos pela foto Toolhelp: só o nome do .exe e o pid, sem abrir handle em processo nenhum.</summary>
internal static class Processos
{
    public static List<(uint Pid, string Exe)> Listar()
    {
        var r = new List<(uint, string)>();
        var snap = Nativo.CreateToolhelp32Snapshot(Nativo.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return r;
        try
        {
            var e = new Nativo.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Nativo.PROCESSENTRY32W>() };
            if (!Nativo.Process32FirstW(snap, ref e)) return r;
            do r.Add((e.th32ProcessID, e.szExeFile ?? "")); while (Nativo.Process32NextW(snap, ref e));
        }
        finally { Nativo.CloseHandle(snap); }
        return r;
    }
}

/// <summary>
/// Uma imagem já reduzida para o tamanho exato em pixels (BGRA pré-multiplicado), pronta para o Direct2D copiar 1:1.
/// Carrega e reduz fora da thread da interface (foto de 2048 px ou print de 3440 px custam dezenas de ms).
/// </summary>
internal sealed class Retrato
{
    public byte[] Dados { get; }
    public int Largura { get; }
    public int Altura { get; }

    public Retrato(byte[] dados, int largura, int altura)
    {
        Dados = dados;
        Largura = largura;
        Altura = altura;
    }

    /// <summary>Tamanho em DIP para a escala dada.</summary>
    public Vector2 TamanhoDip(float escala) => new(Largura / escala, Altura / escala);

    /// <summary>Lê o arquivo sem travar (o Recorte ou o app pode querer mexer nele) e devolve um Bitmap em memória.</summary>
    public static System.Drawing.Bitmap? AbrirBitmap(string caminho)
    {
        try
        {
            var bytes = File.ReadAllBytes(caminho);
            using var ms = new MemoryStream(bytes);
            using var img = System.Drawing.Image.FromStream(ms);
            return new System.Drawing.Bitmap(img);
        }
        catch (Exception e) { Log.Escrever($"imagem {Path.GetFileName(caminho)} não abriu: {e.Message}"); return null; }
    }

    /// <summary>
    /// Reduz para caber em maxL x maxA pixels mantendo a proporção. `recortar`: corta a margem transparente das fotos
    /// de produto (o fone fica maior no quadro) e, se a imagem não tem transparência nenhuma, apaga o fundo branco
    /// ligado às bordas (a foto da HyperX vem em JPG com fundo branco).
    /// </summary>
    public static Retrato? DeArquivo(string caminho, int maxL, int maxA, bool recortar)
    {
        using var bmp = AbrirBitmap(caminho);
        return bmp == null ? null : DeBitmap(bmp, maxL, maxA, recortar);
    }

    public static Retrato? DeBitmap(System.Drawing.Bitmap original, int maxL, int maxA, bool recortar)
    {
        if (maxL < 1 || maxA < 1) return null;
        try
        {
            var area = new System.Drawing.Rectangle(0, 0, original.Width, original.Height);
            bool temAlfa = false;
            if (recortar)
            {
                var caixa = CaixaVisivel(original, out temAlfa);
                if (caixa.Width > 0 && caixa.Height > 0) area = caixa;
            }
            float k = MathF.Min((float)maxL / area.Width, (float)maxA / area.Height);
            int l = Math.Max(1, (int)MathF.Round(area.Width * k)), a = Math.Max(1, (int)MathF.Round(area.Height * k));
            using var reduzida = new System.Drawing.Bitmap(l, a, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(reduzida))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.Clear(System.Drawing.Color.Transparent);
                using var atributos = new System.Drawing.Imaging.ImageAttributes();
                atributos.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                g.DrawImage(original, new System.Drawing.Rectangle(0, 0, l, a), area.X, area.Y, area.Width, area.Height, System.Drawing.GraphicsUnit.Pixel, atributos);
            }
            var bd = reduzida.LockBits(new System.Drawing.Rectangle(0, 0, l, a), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var dados = new byte[l * a * 4];
            for (int y = 0; y < a; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, dados, y * l * 4, l * 4);
            reduzida.UnlockBits(bd);
            if (recortar && !temAlfa) ApagarFundoBranco(dados, l, a);
            return new Retrato(dados, l, a);
        }
        catch (Exception e) { Log.Escrever("reduzir imagem: " + e.Message); return null; }
    }

    /// <summary>A menor caixa com pixels visíveis (alfa &gt; 8). Sem transparência nenhuma devolve a imagem toda.</summary>
    private static System.Drawing.Rectangle CaixaVisivel(System.Drawing.Bitmap b, out bool temAlfa)
    {
        temAlfa = false;
        var r = new System.Drawing.Rectangle(0, 0, b.Width, b.Height);
        var bd = b.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int x0 = b.Width, y0 = b.Height, x1 = -1, y1 = -1;
            var linha = new byte[b.Width * 4];
            for (int y = 0; y < b.Height; y++)
            {
                Marshal.Copy(bd.Scan0 + y * bd.Stride, linha, 0, linha.Length);
                for (int x = 0; x < b.Width; x++)
                {
                    byte alfa = linha[x * 4 + 3];
                    if (alfa < 250) temAlfa = true;
                    if (alfa <= 8) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
            if (!temAlfa || x1 < 0) return r;
            return System.Drawing.Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }
        finally { b.UnlockBits(bd); }
    }

    /// <summary>
    /// Fundo branco ligado às bordas vira transparente (preenchimento a partir da moldura); o branco de dentro do
    /// produto não é tocado. A borda do recorte fica com alfa proporcional ao quanto o pixel é branco.
    /// </summary>
    public static void ApagarFundoBranco(byte[] bgra, int l, int a)
    {
        const int LIMIAR = 236;
        var visto = new bool[l * a];
        var fila = new Queue<int>();
        bool Branco(int i) => bgra[i * 4] >= LIMIAR && bgra[i * 4 + 1] >= LIMIAR && bgra[i * 4 + 2] >= LIMIAR;
        void Semear(int x, int y)
        {
            int i = y * l + x;
            if (visto[i] || !Branco(i)) return;
            visto[i] = true;
            fila.Enqueue(i);
        }
        for (int x = 0; x < l; x++) { Semear(x, 0); Semear(x, a - 1); }
        for (int y = 0; y < a; y++) { Semear(0, y); Semear(l - 1, y); }
        while (fila.Count > 0)
        {
            int i = fila.Dequeue();
            int x = i % l, y = i / l;
            if (x > 0) Semear(x - 1, y);
            if (x < l - 1) Semear(x + 1, y);
            if (y > 0) Semear(x, y - 1);
            if (y < a - 1) Semear(x, y + 1);
        }
        for (int i = 0; i < l * a; i++)
        {
            if (!visto[i]) continue;
            // quanto mais perto do branco puro, mais transparente (borda suave em vez de serrilhada)
            int menor = Math.Min(bgra[i * 4], Math.Min(bgra[i * 4 + 1], bgra[i * 4 + 2]));
            float alfa = Math.Clamp((255 - menor) / (float)(255 - LIMIAR), 0f, 1f) * 0.5f;
            bgra[i * 4 + 3] = (byte)(alfa * 255);
            // pré-multiplicado: cinza claro com o alfa novo
            for (int c = 0; c < 3; c++) bgra[i * 4 + c] = (byte)(bgra[i * 4 + c] * alfa);
        }
    }

    /// <summary>Desenha na posição (DIP), 1:1 em pixels; com raio, recortado em retângulo arredondado.</summary>
    public unsafe void Desenhar(ID2D1DeviceContext dc, float escala, float x, float y, float raio = 0)
    {
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f * escala, 96f * escala, BitmapOptions.None);
        float x0 = MathF.Round(x * escala) / escala, y0 = MathF.Round(y * escala) / escala;
        float l = Largura / escala, a = Altura / escala;
        fixed (byte* d = Dados)
        {
            using var bmp = dc.CreateBitmap(new SizeI(Largura, Altura), (IntPtr)d, (uint)(Largura * 4), props);
            if (raio <= 0)
            {
                dc.DrawBitmap(bmp, 1f, BitmapInterpolationMode.NearestNeighbor, new Rect(x0, y0, l, a));
                return;
            }
            using var pincel = dc.CreateBitmapBrush(bmp, new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, InterpolationMode.NearestNeighbor), null);
            pincel.Transform = Matrix3x2.CreateTranslation(x0, y0);
            var rr = new RoundedRectangle(new System.Drawing.RectangleF(x0, y0, l, a), raio, raio);
            dc.FillRoundedRectangle(ref rr, pincel);
        }
    }
}

/// <summary>Arquivo que outro programa ainda está escrevendo: espera ele soltar (abrir negando escrita só passa quando ninguém escreve).</summary>
internal static class Arquivo
{
    public static bool EsperarFechar(string caminho, TimeSpan limite, CancellationToken parar = default)
    {
        var fim = DateTime.UtcNow + limite;
        long tamanhoAntes = -1;
        while (DateTime.UtcNow < fim && !parar.IsCancellationRequested)
        {
            try
            {
                using (var f = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // aberto sem ninguém escrevendo e com o mesmo tamanho de antes: pronto
                    if (f.Length > 0 && f.Length == tamanhoAntes) return true;
                    tamanhoAntes = f.Length;
                }
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { tamanhoAntes = -1; }
            catch (UnauthorizedAccessException) { tamanhoAntes = -1; }
            Thread.Sleep(150);
        }
        return false;
    }
}

/// <summary>Ícones desenhados para quando não há foto (fone, controle, Bluetooth genérico, sino, render).</summary>
internal static class Desenhos
{
    public static void Fone(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float lado, Color4 cor)
    {
        float u = lado / 24f;
        using var b = dc.CreateSolidColorBrush(cor);
        using var traco = f.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Vector2(c.X - 8 * u, c.Y + 3 * u), FigureBegin.Hollow);
            s.AddArc(new ArcSegment { Point = new Vector2(c.X + 8 * u, c.Y + 3 * u), Size = new Size(8 * u, 8.5f * u), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Large });
            s.EndFigure(FigureEnd.Open);
            s.Close();
        }
        dc.DrawGeometry(g, b, 2f * u, traco);
        Icone.Retangulo(dc, c.X - 10 * u, c.Y + 1 * u, 5 * u, 9 * u, 2 * u, cor);
        Icone.Retangulo(dc, c.X + 5 * u, c.Y + 1 * u, 5 * u, 9 * u, 2 * u, cor);
    }

    public static void Controle(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float lado, Color4 cor)
    {
        float u = lado / 24f;
        Icone.Retangulo(dc, c.X - 10 * u, c.Y - 5 * u, 20 * u, 11 * u, 5.5f * u, cor);
        Icone.Circulo(dc, new Vector2(c.X - 7 * u, c.Y + 5 * u), 3.5f * u, cor);
        Icone.Circulo(dc, new Vector2(c.X + 7 * u, c.Y + 5 * u), 3.5f * u, cor);
        var furo = new Color4(0f, 0f, 0f, 1f);
        Icone.Retangulo(dc, c.X - 7.5f * u, c.Y - 1.5f * u, 5 * u, 1.6f * u, 0.5f * u, furo);
        Icone.Retangulo(dc, c.X - 5.8f * u, c.Y - 3.2f * u, 1.6f * u, 5 * u, 0.5f * u, furo);
        Icone.Circulo(dc, new Vector2(c.X + 4.5f * u, c.Y - 0.5f * u), 1.1f * u, furo);
        Icone.Circulo(dc, new Vector2(c.X + 7f * u, c.Y - 2.5f * u), 1.1f * u, furo);
    }

    public static void Bluetooth(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float lado, Color4 cor)
    {
        float u = lado / 24f, e = 1.9f * u;
        Vector2 P(float x, float y) => new(c.X + x * u, c.Y + y * u);
        Icone.Linha(dc, P(0, -10), P(0, 10), e, cor, f);
        Icone.Linha(dc, P(0, -10), P(5.5f, -4.5f), e, cor, f);
        Icone.Linha(dc, P(5.5f, -4.5f), P(-5.5f, 5), e, cor, f);
        Icone.Linha(dc, P(0, 10), P(5.5f, 4.5f), e, cor, f);
        Icone.Linha(dc, P(5.5f, 4.5f), P(-5.5f, -5), e, cor, f);
    }

    public static void Sino(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float lado, Color4 cor)
    {
        float u = lado / 24f;
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Vector2(c.X - 8 * u, c.Y + 5 * u), FigureBegin.Filled);
            s.AddLine(new Vector2(c.X - 6.5f * u, c.Y + 2.5f * u));
            s.AddLine(new Vector2(c.X - 6.5f * u, c.Y - 2 * u));
            s.AddArc(new ArcSegment { Point = new Vector2(c.X + 6.5f * u, c.Y - 2 * u), Size = new Size(6.5f * u, 7 * u), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small });
            s.AddLine(new Vector2(c.X + 6.5f * u, c.Y + 2.5f * u));
            s.AddLine(new Vector2(c.X + 8 * u, c.Y + 5 * u));
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        using var b = dc.CreateSolidColorBrush(cor);
        dc.FillGeometry(g, b);
        Icone.Circulo(dc, new Vector2(c.X, c.Y + 8 * u), 2.4f * u, cor);
        Icone.Circulo(dc, new Vector2(c.X, c.Y - 9.3f * u), 1.4f * u, cor);
    }

    /// <summary>Arco de três quartos (gira no compositor: girador de "renderizando").</summary>
    public static void Arco(ID2D1DeviceContext dc, ID2D1Factory1 f, Vector2 c, float r, float espessura, Color4 cor)
    {
        using var fundo = dc.CreateSolidColorBrush(new Color4(cor.R, cor.G, cor.B, 0.22f));
        dc.DrawEllipse(new Ellipse(c, r, r), fundo, espessura);
        using var b = dc.CreateSolidColorBrush(cor);
        using var g = f.CreatePathGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Vector2(c.X, c.Y - r), FigureBegin.Hollow);
            s.AddArc(new ArcSegment { Point = new Vector2(c.X + r, c.Y), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small });
            s.EndFigure(FigureEnd.Open);
            s.Close();
        }
        using var traco = f.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        dc.DrawGeometry(g, b, espessura, traco);
    }
}

/// <summary>Relatório do autoteste no formato do núcleo ("ok" / "FALHOU"; "sonda" é informação, não conta como falha).</summary>
internal sealed class Relatorio
{
    private readonly StringBuilder _r = new();

    public void Conferir(string nome, bool ok, string detalhe = "") =>
        _r.AppendLine((ok ? "ok      " : "FALHOU  ") + nome + (detalhe.Length > 0 ? $" ({detalhe})" : ""));

    public void Sonda(string texto) => _r.AppendLine("sonda   " + texto);

    public void Tentar(string nome, Action a)
    {
        try { a(); }
        catch (Exception e) { Sonda($"{nome}: {e.GetType().Name}: {e.Message}"); }
    }

    public override string ToString() => _r.ToString().TrimEnd();
}
