using System;

// não é Ilha.Modulos.Midia: o núcleo tem a classe estática Ilha.Modulos e o namespace colidiria com ela
namespace Ilha.Midia;

/// <summary>
/// As 4 barras do visualizador a partir das últimas 1024 amostras (mono): janela de Hann, FFT, energia por banda e
/// uma normalização que acompanha o volume (o pico decai devagar), para a barra reagir à música e não ao volume.
/// </summary>
internal sealed class Espectro
{
    public const int N = 1024;
    public const int Bandas = 4;
    /// <summary>Limites das bandas em Hz: grave, médio-grave, médio, agudo.</summary>
    public static readonly float[] Limites = { 40f, 200f, 800f, 3200f, 12000f };
    // música tem bem mais energia no grave: cada banda acima ganha uns dB para as quatro mexerem parecido
    private static readonly float[] Compensacao = { 0f, 3f, 6f, 9f };
    private const float FAIXA_DB = 30f;
    private const float PISO_DB = -45f;
    // abaixo disto é chiado: nunca acende, por mais baixo que o pico esteja
    private const float CHAO_DB = -62f;
    private const float SILENCIO_RMS = 3e-5f;

    private static readonly double[] _hann = Hann(N);
    private readonly double[] _re = new double[N], _im = new double[N];
    private readonly float[] _suave = new float[Bandas];
    private float _pico = PISO_DB;

    public static double[] Hann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }

    /// <summary>FFT complexa radix-2 no lugar (tamanho potência de 2).</summary>
    public static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        if (n == 0 || (n & (n - 1)) != 0 || im.Length != n) throw new ArgumentException("tamanho precisa ser potência de 2");
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int tam = 2; tam <= n; tam <<= 1)
        {
            double ang = -2 * Math.PI / tam;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            int meio = tam >> 1;
            for (int i = 0; i < n; i += tam)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < meio; k++)
                {
                    int a = i + k, b = a + meio;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    /// <summary>Nível de cada banda em dB (0 dB = senoide de amplitude 1), já com a janela de Hann.</summary>
    public void NiveisDb(ReadOnlySpan<float> amostras, float taxa, Span<float> db)
    {
        if (amostras.Length != N) throw new ArgumentException($"precisa de {N} amostras");
        double somaJanela = 0;
        for (int i = 0; i < N; i++)
        {
            _re[i] = amostras[i] * _hann[i];
            _im[i] = 0;
            somaJanela += _hann[i];
        }
        Fft(_re, _im);
        double escala = 2.0 / somaJanela;
        double hzPorBin = taxa / N;
        for (int b = 0; b < Bandas; b++)
        {
            int de = Math.Max(1, (int)Math.Ceiling(Limites[b] / hzPorBin));
            int ate = Math.Min(N / 2 - 1, (int)Math.Floor(Limites[b + 1] / hzPorBin));
            double energia = 0;
            for (int k = de; k <= ate; k++)
            {
                double m = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * escala;
                energia += m * m;
            }
            db[b] = (float)(10 * Math.Log10(energia + 1e-14));
        }
    }

    /// <summary>Altura de cada barra (0 a 1). Chamado a ~30 Hz; sobe na hora e desce suave.</summary>
    public void Analisar(ReadOnlySpan<float> amostras, float taxa, Span<float> saida)
    {
        double q = 0;
        foreach (var a in amostras) q += a * a;
        float rms = (float)Math.Sqrt(q / Math.Max(1, amostras.Length));
        Span<float> db = stackalloc float[Bandas];
        bool silencio = rms < SILENCIO_RMS;
        if (!silencio) NiveisDb(amostras, taxa, db);

        float maior = float.MinValue;
        for (int b = 0; b < Bandas && !silencio; b++) maior = MathF.Max(maior, db[b] + Compensacao[b]);
        // o pico decai ~7,5 dB/s a 30 Hz: acompanha a música ficando mais baixa sem pular a cada batida
        _pico = MathF.Max(silencio ? float.MinValue : maior, _pico - 0.25f);
        _pico = MathF.Max(_pico, PISO_DB);

        for (int b = 0; b < Bandas; b++)
        {
            float chao = MathF.Max(_pico - FAIXA_DB, CHAO_DB);
            float alvo = silencio ? 0f : Math.Clamp((db[b] + Compensacao[b] - chao) / (_pico - chao), 0f, 1f);
            _suave[b] = alvo >= _suave[b] ? alvo : MathF.Max(alvo, _suave[b] - 0.09f);
            saida[b] = _suave[b];
        }
    }

    public void Zerar()
    {
        Array.Clear(_suave);
        _pico = PISO_DB;
    }
}

internal enum TipoAmostra { Float, Inteiro }

/// <summary>Formato do mix do dispositivo (WAVEFORMATEX ou WAVEFORMATEXTENSIBLE).</summary>
internal readonly record struct FormatoAudio(TipoAmostra Tipo, int Bits, int Canais, int Taxa, int Bloco)
{
    public bool Suportado => Canais > 0 && Bloco >= Canais * Bits / 8 &&
        (Tipo == TipoAmostra.Float ? Bits == 32 : Bits is 16 or 24 or 32);

    public override string ToString() => $"{(Tipo == TipoAmostra.Float ? "float" : "pcm")}{Bits} {Canais}ch {Taxa} Hz";

    /// <summary>Lê os bytes de um WAVEFORMATEX(TENSIBLE).</summary>
    public static FormatoAudio? Ler(ReadOnlySpan<byte> wf)
    {
        if (wf.Length < 16) return null;
        int tag = BitConverter.ToUInt16(wf[..2]);
        int canais = BitConverter.ToUInt16(wf[2..4]);
        int taxa = (int)BitConverter.ToUInt32(wf[4..8]);
        int bloco = BitConverter.ToUInt16(wf[12..14]);
        int bits = BitConverter.ToUInt16(wf[14..16]);
        if (tag == 0xFFFE)
        {
            // WAVEFORMATEXTENSIBLE: o tipo de verdade são os 2 primeiros bytes do SubFormat (offset 24)
            if (wf.Length < 40) return null;
            tag = BitConverter.ToUInt16(wf[24..26]);
        }
        TipoAmostra tipo;
        if (tag == 3) tipo = TipoAmostra.Float;
        else if (tag == 1) tipo = TipoAmostra.Inteiro;
        else return null;
        return new FormatoAudio(tipo, bits, canais, taxa, bloco);
    }

    /// <summary>Converte `quadros` quadros intercalados para mono (média dos canais), de -1 a 1. Devolve quantos escreveu.</summary>
    public int ParaMono(ReadOnlySpan<byte> dados, int quadros, Span<float> destino)
    {
        int n = Math.Min(quadros, Math.Min(destino.Length, dados.Length / Math.Max(1, Bloco)));
        int bytesAmostra = Bits / 8;
        float div = 1f / Canais;
        for (int f = 0; f < n; f++)
        {
            int baseQ = f * Bloco;
            float soma = 0;
            for (int c = 0; c < Canais; c++)
            {
                var s = dados.Slice(baseQ + c * bytesAmostra, bytesAmostra);
                soma += Tipo == TipoAmostra.Float
                    ? BitConverter.ToSingle(s)
                    : Bits switch
                    {
                        16 => BitConverter.ToInt16(s) / 32768f,
                        24 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                        _ => BitConverter.ToInt32(s) / 2147483648f,
                    };
            }
            destino[f] = soma * div;
        }
        return n;
    }
}
