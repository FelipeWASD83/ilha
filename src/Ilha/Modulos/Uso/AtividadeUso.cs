using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Ilha.Uso;

/// <summary>O que o módulo sabe agora: a última leitura, o histórico de 60 s e os limites.</summary>
internal sealed class EstadoUso
{
    public const int HISTORICO = 60;

    public Amostra? Ultima { get; private set; }
    public Historico Cpu { get; } = new(HISTORICO);
    public Historico Ram { get; } = new(HISTORICO);
    public Historico Gpu { get; } = new(HISTORICO);
    public Historico Temp { get; } = new(HISTORICO);
    public Historico Descida { get; } = new(HISTORICO);
    public Historico Subida { get; } = new(HISTORICO);
    public Limites Limites { get; } = new();
    public bool TemGpu { get; set; }
    public string NomeGpu { get; set; } = "";

    public void Registrar(Amostra a, int vezes)
    {
        Ultima = a;
        Cpu.Adicionar((float)a.Cpu, vezes);
        Ram.Adicionar((float)a.Ram, vezes);
        Gpu.Adicionar((float)(a.Gpu ?? 0), vezes);
        Temp.Adicionar((float)(a.GpuTemp ?? 0), vezes);
        // a rede só é lida com a atividade na tela: sem leitura, o gráfico dela recomeça do zero na próxima vez
        if (a.Descida is double d && a.Subida is double s)
        {
            Descida.Adicionar((float)d, 1);
            Subida.Adicionar((float)s, 1);
        }
        else
        {
            Descida.Limpar();
            Subida.Limpar();
        }
    }

    public void LimparHistorico()
    {
        foreach (var h in new[] { Cpu, Ram, Gpu, Temp, Descida, Subida }) h.Limpar();
    }

    public Historico De(Medida m) => m switch
    {
        Medida.Cpu => Cpu,
        Medida.Gpu => Gpu,
        Medida.GpuTemp => Temp,
        _ => Ram,
    };

    public static double? Valor(Amostra a, Medida m) => m switch
    {
        Medida.Cpu => a.Cpu,
        Medida.Gpu => a.Gpu,
        Medida.GpuTemp => a.GpuTemp,
        _ => a.Ram,
    };

    /// <summary>O texto curto da pílula: "CPU 93%", "GPU 97%", "GPU 86 °C", "RAM 91%".</summary>
    public static string Rotulo(Medida m, Amostra a) => m switch
    {
        Medida.Cpu => "CPU " + Formato.Porcento(a.Cpu),
        Medida.Gpu => a.Gpu is double g ? "GPU " + Formato.Porcento(g) : "GPU",
        Medida.GpuTemp => a.GpuTemp is double t ? "GPU " + Formato.Graus(t) : "GPU",
        _ => "RAM " + Formato.Porcento(a.Ram),
    };

    public bool Acima(Medida m, double v) => Limites.LimiteDe(m) is double l && v > l;
}

/// <summary>
/// Uso do PC na ilha. Compacta: o que passou do limite e as barras dos últimos 12 s. Expandida: CPU, RAM, GPU e rede
/// com o gráfico do último minuto. Ferramenta: o atalho abre mesmo sem nada passando do limite.
/// </summary>
internal sealed class AtividadeUso : Atividade
{
    private readonly EstadoUso _e;
    private static readonly Color4 Trilho = new(1f, 1f, 1f, 0.07f);

    public AtividadeUso(EstadoUso e) => _e = e;

    public override string Tipo => "uso";
    public override string Nome => "Uso do PC";
    public override bool EhFerramenta => true;
    /// <summary>Estourou com o cartão já aberto pelo atalho: não repete a chegada por cima dele.</summary>
    public bool SemChegada { get; set; }
    public override bool AnunciaChegada => !SemChegada;

    /// <summary>Quantas cenas desta atividade estão na tela (pílula, bolha, cartão, chegada).</summary>
    public int Cenas { get; private set; }
    public event Action? CenasMudaram;

    public void Mostrar() => Ligar();
    public void Esconder() => Desligar();
    public void Atualizar() => Redesenhar();
    public void Refazer() => MudouTamanho();

    private Medida MotivoOuCpu => _e.Limites.Motivo ?? Medida.Cpu;

    protected override Cena Montar(Palco p, Forma f)
    {
        var c = f switch
        {
            Forma.Bolha => Bolha(p),
            Forma.Compacta => Compacta(p),
            _ => Expandida(p),
        };
        Cenas++;
        c.AoSoltar += () => { Cenas--; CenasMudaram?.Invoke(); };
        CenasMudaram?.Invoke();
        return c;
    }

    // ------------------------------------------------------------------ formas

    private Cena Bolha(Palco p)
    {
        var c = p.NovaCena(new Vector2(24, 24));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var m = MotivoOuCpu;
            Barras(dc, 1, 4, 22, 16, _e.De(m).Ultimos(6), 6, 100, v => CorDe(m, v));
        });
        return c;
    }

    private Cena Compacta(Palco p)
    {
        var c = p.NovaCena(new Vector2(150, 24));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var m = MotivoOuCpu;
            string texto = _e.Ultima is Amostra a ? EstadoUso.Rotulo(m, a) : "Uso do PC";
            bool acima = _e.Ultima is Amostra b && EstadoUso.Valor(b, m) is double v && _e.Acima(m, v);
            var tam = p.Texto.Medir(texto, 14, FontWeight.SemiBold, tabular: true);
            p.Texto.Desenhar(dc, texto, new Vector2(0, (t.Y - tam.Y) / 2), 14, acima ? Cor.Vermelho : Cor.Branco, FontWeight.SemiBold, tabular: true, largura: t.X - 60);
            Barras(dc, t.X - 54, 4, 54, 16, _e.De(m).Ultimos(12), 12, 100, x => CorDe(m, x));
        });
        return c;
    }

    private const float L = 340, GL = 150, GX = L - GL, LINHA = 42, LINHA_GPU = 58, TOPO = 26;

    private Cena Expandida(Palco p)
    {
        bool gpu = _e.TemGpu;
        float altura = TOPO + LINHA * 3 + (gpu ? LINHA_GPU : 0) - 4;
        var c = p.NovaCena(new Vector2(L, altura));
        p.Desenho(c, Vector2.Zero, c.Tamanho, (dc, t) =>
        {
            var F = p.G.Fabrica;
            p.Texto.Desenhar(dc, "Uso do PC", new Vector2(0, 0), 13, Cor.Secundario);
            if (_e.Limites.Ligada && _e.Ultima is Amostra am && _e.Limites.Motivo is Medida mot)
            {
                string r = EstadoUso.Rotulo(mot, am);
                bool acima = EstadoUso.Valor(am, mot) is double vm && _e.Acima(mot, vm);
                var tr = p.Texto.Medir(r, 13);
                p.Texto.Desenhar(dc, r, new Vector2(t.X - tr.X, 0), 13, acima ? Cor.Vermelho : Cor.Secundario);
            }

            var a = _e.Ultima;
            float y = TOPO;

            // CPU
            Rotulo(p, dc, "CPU", y);
            Valor(p, dc, a is Amostra a1 ? Formato.Porcento(a1.Cpu) : "...", y, a is Amostra a2 && _e.Acima(Medida.Cpu, a2.Cpu));
            Barras(dc, GX, y + 3, GL, 32, _e.Cpu.Ultimos(EstadoUso.HISTORICO), EstadoUso.HISTORICO, 100, v => CorDe(Medida.Cpu, v));
            y += LINHA;

            // RAM
            Rotulo(p, dc, a is Amostra r1 ? "RAM · " + Formato.Porcento(r1.Ram) : "RAM", y);
            Valor(p, dc, a is Amostra r2 && r2.RamTotal > 0 ? Formato.Par(r2.RamUsada, r2.RamTotal) : "...", y, a is Amostra r3 && _e.Acima(Medida.Ram, r3.Ram));
            Barras(dc, GX, y + 3, GL, 32, _e.Ram.Ultimos(EstadoUso.HISTORICO), EstadoUso.HISTORICO, 100, v => CorDe(Medida.Ram, v));
            y += LINHA;

            // GPU (só com placa NVIDIA)
            if (gpu)
            {
                Rotulo(p, dc, _e.NomeGpu.Length > 0 ? "GPU · " + NomeCurto(_e.NomeGpu) : "GPU", y);
                float x = 0;
                if (a is Amostra g && g.Gpu is double uso)
                {
                    string s = Formato.Porcento(uso);
                    Valor(p, dc, s, y, _e.Acima(Medida.Gpu, uso));
                    x = p.Texto.Medir(s, 17, FontWeight.SemiBold, true).X;
                }
                else { Valor(p, dc, "...", y, false); x = p.Texto.Medir("...", 17, FontWeight.SemiBold, true).X; }
                if (a is Amostra g2 && g2.GpuTemp is double temp)
                {
                    p.Texto.Desenhar(dc, "·", new Vector2(x + 6, y + 14), 17, Cor.Secundario);
                    p.Texto.Desenhar(dc, Formato.Graus(temp), new Vector2(x + 18, y + 14), 17, _e.Acima(Medida.GpuTemp, temp) ? Cor.Vermelho : Cor.Branco, FontWeight.SemiBold, tabular: true);
                }
                if (a is Amostra g3 && g3.VramUsada is ulong vu && g3.VramTotal is ulong vt && vt > 0)
                    p.Texto.Desenhar(dc, "VRAM " + Formato.Par(vu, vt), new Vector2(0, y + 38), 12, Cor.Secundario, FontWeight.Normal, tabular: true, largura: GX - 8);
                Barras(dc, GX, y + 3, GL, 48, _e.Gpu.Ultimos(EstadoUso.HISTORICO), EstadoUso.HISTORICO, 100, v => CorDe(Medida.Gpu, v));
                y += LINHA_GPU;
            }

            // rede: descida em azul, subida por cima em branco, na mesma escala
            Rotulo(p, dc, "Rede", y);
            float vy = y + 14;
            Icone.Triangulo(dc, F, new Vector2(0, vy + 7), new Vector2(9, vy + 7), new Vector2(4.5f, vy + 14), Cor.Azul);
            string desce = a is Amostra { Descida: double n1 } ? Formato.Taxa(n1) : "...";
            p.Texto.Desenhar(dc, desce, new Vector2(13, vy + 1), 15, Cor.Branco, FontWeight.SemiBold, tabular: true);
            float x2 = 13 + MathF.Max(p.Texto.Medir(desce, 15, FontWeight.SemiBold, true).X, 72) + 12;
            Icone.Triangulo(dc, F, new Vector2(x2, vy + 14), new Vector2(x2 + 9, vy + 14), new Vector2(x2 + 4.5f, vy + 7), Cor.Secundario);
            p.Texto.Desenhar(dc, a is Amostra { Subida: double n2 } ? Formato.Taxa(n2) : "...", new Vector2(x2 + 13, vy + 1), 15, Cor.Branco, FontWeight.SemiBold, tabular: true, largura: MathF.Max(10, GX - x2 - 21));
            float teto = MathF.Max(64 * 1024, MathF.Max(_e.Descida.Maximo(EstadoUso.HISTORICO), _e.Subida.Maximo(EstadoUso.HISTORICO)));
            Barras(dc, GX, y + 3, GL, 32, _e.Descida.Ultimos(EstadoUso.HISTORICO), EstadoUso.HISTORICO, teto, _ => Cor.Azul);
            Barras(dc, GX, y + 3, GL, 32, _e.Subida.Ultimos(EstadoUso.HISTORICO), EstadoUso.HISTORICO, teto, _ => Cor.Secundario, fundo: false, fina: true);
        });
        return c;
    }

    // ------------------------------------------------------------------ desenho

    private static void Rotulo(Palco p, ID2D1DeviceContext dc, string texto, float y) =>
        p.Texto.Desenhar(dc, texto, new Vector2(0, y), 12, Cor.Secundario, FontWeight.Normal, largura: GX - 8);

    private static void Valor(Palco p, ID2D1DeviceContext dc, string texto, float y, bool acima) =>
        p.Texto.Desenhar(dc, texto, new Vector2(0, y + 14), 17, acima ? Cor.Vermelho : Cor.Branco, FontWeight.SemiBold, tabular: true, largura: GX - 8);

    /// <summary>"NVIDIA GeForce RTX 3060" vira "RTX 3060".</summary>
    public static string NomeCurto(string nome) => nome.Replace("NVIDIA ", "").Replace("GeForce ", "").Trim();

    private Color4 CorDe(Medida m, float v) => _e.Acima(m, v) ? Cor.Vermelho : Cor.Verde;

    /// <summary>
    /// Barras do mais velho (esquerda) para o mais novo (direita), alinhadas à direita: com pouco histórico o gráfico
    /// enche da direita para a esquerda, como o do Gerenciador de Tarefas.
    /// </summary>
    private static void Barras(ID2D1DeviceContext dc, float x, float y, float l, float a, float[] valores, int casas, float teto,
        Func<float, Color4> cor, bool fundo = true, bool fina = false)
    {
        if (fundo) Icone.Retangulo(dc, x, y, l, a, 3, Trilho);
        float passo = l / casas;
        float largura = MathF.Max(0.8f, passo * (fina ? 0.45f : 0.72f));
        for (int i = 0; i < valores.Length; i++)
        {
            float v = valores[i];
            float h = teto <= 0 ? 0 : Math.Clamp(v / teto, 0, 1) * (a - 2);
            if (h < 1.2f) h = 1.2f;
            float bx = x + l - (valores.Length - i) * passo + (passo - largura) / 2;
            Icone.Retangulo(dc, bx, y + a - 1 - h, largura, h, MathF.Min(1f, largura / 2), cor(v));
        }
    }
}
