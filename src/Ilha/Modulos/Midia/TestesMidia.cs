using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vortice.Mathematics;
using Windows.Media.Control;

// não é Ilha.Modulos.Midia: o núcleo tem a classe estática Ilha.Modulos e o namespace colidiria com ela
namespace Ilha.Midia;

/// <summary>`Ilha.exe --testar midia`: a lógica pura com dados gerados aqui e sondas que só leem as APIs do Windows.</summary>
internal static class TestesMidia
{
    private static StringBuilder _s = new();

    private static void Ok(bool cond, string o) => _s.Append(cond ? "ok      " : "FALHOU  ").Append(o).Append('\n');

    public static string Rodar()
    {
        _s = new StringBuilder();
        Executar("linha do tempo", TestarLinha);
        Executar("escolha da sessão", TestarEscolha);
        Executar("fft e bandas", TestarEspectro);
        Executar("formato do áudio", TestarFormato);
        Executar("cor da capa", TestarCor);
        Executar("textos e volume", TestarTextos);
        Executar("meio livre da pílula", TestarMeio);
        Executar("sondas", Sondas);
        return _s.ToString().TrimEnd('\n');
    }

    private static void Executar(string nome, Action a)
    {
        try { a(); }
        catch (Exception e) { Ok(false, $"{nome}: exceção {e}"); }
    }

    // ------------------------------------------------------------------ linha do tempo

    private static void TestarLinha()
    {
        var t0 = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var i = new Instante(TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(30), t0, true, 1.0);
        Ok(Linha.Extrapolar(i, t0.AddSeconds(5)) == TimeSpan.FromSeconds(35), "tocando: 30 s + 5 s passados = 35 s");
        Ok(Linha.Extrapolar(i with { Tocando = false }, t0.AddSeconds(5)) == TimeSpan.FromSeconds(30), "pausada não anda");
        Ok(Linha.Extrapolar(i with { Taxa = 2 }, t0.AddSeconds(5)) == TimeSpan.FromSeconds(40), "taxa 2x anda o dobro");
        Ok(Linha.Extrapolar(i, t0.AddSeconds(500)) == TimeSpan.FromSeconds(200), "para no fim (EndTime)");
        Ok(Linha.Extrapolar(i, t0.AddSeconds(-3)) == TimeSpan.FromSeconds(30), "relógio antes do LastUpdatedTime não volta");
        Ok(Linha.Extrapolar(i with { Atualizado = default }, t0.AddSeconds(5)) == TimeSpan.FromSeconds(30), "LastUpdatedTime zerado: sem extrapolar");
        Ok(Linha.Extrapolar(i with { Inicio = TimeSpan.FromSeconds(10), Posicao = TimeSpan.FromSeconds(2) }, t0) == TimeSpan.FromSeconds(10), "antes do início fica no início");
        var semDuracao = i with { Fim = TimeSpan.Zero };
        Ok(!semDuracao.TemDuracao && Linha.Extrapolar(semDuracao, t0.AddSeconds(5)) == TimeSpan.FromSeconds(35), "sem duração (ao vivo) anda sem limite");

        var l = new Linha(i);
        Ok(Math.Abs(l.Fracao(t0.AddSeconds(70)) - 0.5) < 1e-9, "fração: 100 de 200 s = 0,5");

        // arraste: segura o alvo até o player confirmar
        l.Pedir(TimeSpan.FromSeconds(150), t0.AddSeconds(10));
        Ok(l.Segurando && l.Posicao(t0.AddSeconds(11)) == TimeSpan.FromSeconds(151), "segurando: mostra o alvo e anda junto");
        l.Recebeu(i with { Atualizado = t0.AddSeconds(11) }, t0.AddSeconds(11)); // rajada com a posição velha
        Ok(l.Segurando, "evento com a posição velha não solta o alvo");
        l.Recebeu(i with { Posicao = TimeSpan.FromSeconds(151.5), Atualizado = t0.AddSeconds(12) }, t0.AddSeconds(12));
        Ok(!l.Segurando && l.Posicao(t0.AddSeconds(12)) == TimeSpan.FromSeconds(151.5), "evento perto do alvo solta e vale o do player");
        l.Pedir(TimeSpan.FromSeconds(20), t0.AddSeconds(20));
        Ok(!(l.Posicao(t0.AddSeconds(24)) == TimeSpan.FromSeconds(24)) && !l.Segurando, "player calado: solta depois de 3 s");
        l.Pedir(TimeSpan.FromSeconds(20), t0.AddSeconds(30));
        l.Recusado();
        Ok(!l.Segurando, "pedido recusado solta na hora");
        l.Pedir(TimeSpan.FromSeconds(999), t0.AddSeconds(40));
        Ok(l.Posicao(t0.AddSeconds(40)) == TimeSpan.FromSeconds(200), "alvo além do fim fica no fim");

        // pausar sem o player mandar linha nova: congela onde estava
        var l2 = new Linha(i);
        l2.MudouEstado(false, 1, t0.AddSeconds(8));
        Ok(l2.Posicao(t0.AddSeconds(60)) == TimeSpan.FromSeconds(38), "pausou aos 38 s: fica nos 38 s");
        l2.MudouEstado(true, 1, t0.AddSeconds(100));
        Ok(l2.Posicao(t0.AddSeconds(102)) == TimeSpan.FromSeconds(40), "voltou a tocar: anda a partir dos 38 s");
    }

    // ------------------------------------------------------------------ escolha da sessão

    private static void TestarEscolha()
    {
        var agora = new DateTime(2026, 11, 1, 12, 0, 0);
        var lim = TimeSpan.FromMinutes(10);
        Rastro R(string c, bool tocando, bool pausada, double desdeMin, double ultimaMin) => new()
        {
            Chave = c, Tocando = tocando, Pausada = pausada,
            TocandoDesde = agora.AddMinutes(-desdeMin), UltimaVezTocando = agora.AddMinutes(-ultimaMin),
        };
        var l1 = new List<Rastro> { R("spotify", false, true, 30, 1), R("brave", true, false, 2, 0) };
        Ok(Escolha.Sessao(l1, agora, lim, "spotify") == 1, "tocando ganha de pausada");
        var l2 = new List<Rastro> { R("spotify", true, false, 5, 0), R("brave", true, false, 1, 0) };
        Ok(Escolha.Sessao(l2, agora, lim, "spotify") == 1, "duas tocando: a que começou por último");
        var l3 = new List<Rastro> { R("spotify", false, true, 30, 3), R("brave", false, true, 30, 1) };
        Ok(Escolha.Sessao(l3, agora, lim, null) == 1, "só pausadas: a mais recente");
        var l4 = new List<Rastro> { R("spotify", false, true, 30, 11) };
        Ok(Escolha.Sessao(l4, agora, lim, "spotify") == -1, "pausada há 11 min (limite 10): some");
        var l5 = new List<Rastro> { R("spotify", false, false, 30, 1) };
        Ok(Escolha.Sessao(l5, agora, lim, null) == -1, "fechada (nem tocando nem pausada): não aparece");
        Ok(Escolha.Sessao(new List<Rastro>(), agora, lim, null) == -1, "sem sessão: nenhuma");
        var l6 = new List<Rastro> { R("a", true, false, 2, 0), R("b", true, false, 2, 0) };
        Ok(Escolha.Sessao(l6, agora, lim, "b") == 1, "empate fica com a atual");

        // rastro: transições
        var r = new Rastro { Chave = "x" };
        r.Atualizar(true, false, agora, null, true);
        Ok(r.Tocando && r.TocandoDesde == agora, "começou a tocar: marca o início");
        r.Atualizar(false, true, agora.AddMinutes(2), null, false);
        Ok(r.Pausada && r.UltimaVezTocando == agora.AddMinutes(2), "pausou: marca a última vez tocando");
        var nova = new Rastro { Chave = "y" };
        nova.Atualizar(false, true, agora, agora.AddMinutes(-30), true);
        Ok(Escolha.Sessao(new List<Rastro> { nova }, agora, lim, null) == -1, "já pausada há 30 min quando a ilha abriu: não aparece");
        var nova2 = new Rastro { Chave = "z" };
        nova2.Atualizar(false, true, agora, new DateTime(1601, 1, 1), true);
        Ok(nova2.UltimaVezTocando == agora, "sem LastUpdatedTime: conta a partir de agora");
    }

    // ------------------------------------------------------------------ espectro

    private static float[] Senoide(float hz, float amp, float taxa, int fase = 0)
    {
        var x = new float[Espectro.N];
        for (int i = 0; i < x.Length; i++) x[i] = amp * MathF.Sin(2 * MathF.PI * hz * (i + fase) / taxa);
        return x;
    }

    private static void TestarEspectro()
    {
        // FFT contra a DFT ingênua
        var rnd = new Random(7);
        int n = 64;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++) { re[i] = rnd.NextDouble() * 2 - 1; im[i] = rnd.NextDouble() * 2 - 1; }
        var r0 = (double[])re.Clone();
        var i0 = (double[])im.Clone();
        Espectro.Fft(re, im);
        double erro = 0;
        for (int k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (int t = 0; t < n; t++)
            {
                double a = -2 * Math.PI * k * t / n;
                sr += r0[t] * Math.Cos(a) - i0[t] * Math.Sin(a);
                si += r0[t] * Math.Sin(a) + i0[t] * Math.Cos(a);
            }
            erro = Math.Max(erro, Math.Abs(sr - re[k]) + Math.Abs(si - im[k]));
        }
        Ok(erro < 1e-9, $"FFT de 64 bate com a DFT (erro {erro:E1})");

        var h = Espectro.Hann(Espectro.N);
        Ok(h[0] < 1e-12 && h[^1] < 1e-12 && Math.Abs(h[Espectro.N / 2] - 1) < 1e-4, "janela de Hann: zero nas pontas, 1 no meio");

        const float taxa = 48000;
        var esp = new Espectro();
        var db = new float[4];
        float[] tons = { 100, 400, 1500, 6000 };
        for (int b = 0; b < 4; b++)
        {
            esp.NiveisDb(Senoide(tons[b], 0.5f, taxa), taxa, db);
            int maior = Array.IndexOf(db, db.Max());
            Ok(maior == b, $"senoide de {tons[b]} Hz cai na banda {b} ({string.Join(" ", db.Select(x => x.ToString("0")))} dB)");
        }
        esp.NiveisDb(Senoide(1000, 1f, taxa), taxa, db);
        // a energia da banda inclui a largura de banda equivalente da Hann (1,5 bin): +1,76 dB numa senoide de amplitude 1
        Ok(Math.Abs(db[2] - 1.76f) < 0.5, $"senoide de amplitude 1 dá ~+1,8 dB na banda dela ({db[2]:0.0} dB)");

        // normalizado: o tom sobe a banda dele e deixa as outras baixas; silêncio baixa tudo
        var saida = new float[4];
        for (int q = 0; q < 30; q++) esp.Analisar(Senoide(400, 0.3f, taxa, q * 1600), taxa, saida);
        Ok(saida[1] > 0.9f && saida[0] < 0.35f && saida[2] < 0.35f && saida[3] < 0.35f,
            $"400 Hz contínuo: barra 2 alta, resto baixo ({string.Join(" ", saida.Select(x => x.ToString("0.00")))})");
        for (int q = 0; q < 20; q++) esp.Analisar(new float[Espectro.N], taxa, saida);
        Ok(saida.All(x => x < 0.01f), "silêncio (pausa, DRM): todas descem a zero");
        var baixo = new Espectro();
        for (int q = 0; q < 30; q++) baixo.Analisar(Senoide(400, 0.0005f, taxa, q * 1600), taxa, saida);
        Ok(saida.All(x => x < 0.05f), "chiado de -66 dB não acende as barras (piso)");
        var alto = new Espectro();
        for (int q = 0; q < 30; q++) alto.Analisar(Senoide(400, 0.02f, taxa, q * 1600), taxa, saida);
        Ok(saida[1] > 0.9f, "música baixa (-34 dB) ainda enche a barra: acompanha o volume");
    }

    // ------------------------------------------------------------------ formato

    private static byte[] Extensivel(ushort sub, int canais, int bits, int taxa)
    {
        var b = new byte[40];
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)canais).CopyTo(b, 2);
        BitConverter.GetBytes(taxa).CopyTo(b, 4);
        BitConverter.GetBytes(taxa * canais * bits / 8).CopyTo(b, 8);
        BitConverter.GetBytes((ushort)(canais * bits / 8)).CopyTo(b, 12);
        BitConverter.GetBytes((ushort)bits).CopyTo(b, 14);
        BitConverter.GetBytes((ushort)22).CopyTo(b, 16);
        BitConverter.GetBytes(sub).CopyTo(b, 24);
        return b;
    }

    private static void TestarFormato()
    {
        var f = FormatoAudio.Ler(Extensivel(3, 2, 32, 48000));
        Ok(f is { Tipo: TipoAmostra.Float, Canais: 2, Bits: 32, Taxa: 48000, Bloco: 8, Suportado: true }, $"extensível float32 estéreo lido: {f}");
        var dados = new byte[16];
        BitConverter.GetBytes(0.5f).CopyTo(dados, 0);
        BitConverter.GetBytes(-0.5f).CopyTo(dados, 4);
        BitConverter.GetBytes(1f).CopyTo(dados, 8);
        BitConverter.GetBytes(0.5f).CopyTo(dados, 12);
        var mono = new float[2];
        int n = f!.Value.ParaMono(dados, 2, mono);
        Ok(n == 2 && mono[0] == 0f && mono[1] == 0.75f, "float estéreo vira mono pela média");

        var p16 = FormatoAudio.Ler(Extensivel(1, 2, 16, 44100))!.Value;
        var d16 = new byte[4];
        BitConverter.GetBytes((short)16384).CopyTo(d16, 0);
        BitConverter.GetBytes((short)-16384).CopyTo(d16, 2);
        p16.ParaMono(d16, 1, mono);
        Ok(p16.Tipo == TipoAmostra.Inteiro && mono[0] == 0f, "PCM 16 lido e misturado");

        var p24 = FormatoAudio.Ler(Extensivel(1, 1, 24, 48000))!.Value;
        p24.ParaMono(new byte[] { 0x00, 0x00, 0xC0 }, 1, mono);
        Ok(Math.Abs(mono[0] + 0.5f) < 1e-6, "PCM 24 negativo com sinal certo (-0,5)");

        var simples = new byte[18];
        BitConverter.GetBytes((ushort)1).CopyTo(simples, 0);
        BitConverter.GetBytes((ushort)1).CopyTo(simples, 2);
        BitConverter.GetBytes(22050).CopyTo(simples, 4);
        BitConverter.GetBytes((ushort)2).CopyTo(simples, 12);
        BitConverter.GetBytes((ushort)16).CopyTo(simples, 14);
        Ok(FormatoAudio.Ler(simples) is { Tipo: TipoAmostra.Inteiro, Canais: 1, Taxa: 22050 }, "WAVEFORMATEX simples (PCM mono)");
        var mp3 = (byte[])simples.Clone();
        BitConverter.GetBytes((ushort)0x55).CopyTo(mp3, 0);
        Ok(FormatoAudio.Ler(mp3) == null, "formato desconhecido é recusado");
    }

    // ------------------------------------------------------------------ cor

    private static byte[] Imagem(int w, int h, Func<int, int, (byte r, byte g, byte b, byte a)> pixel)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var (r, g, b, a) = pixel(x, y);
            int i = (y * w + x) * 4;
            px[i] = (byte)(b * a / 255);
            px[i + 1] = (byte)(g * a / 255);
            px[i + 2] = (byte)(r * a / 255);
            px[i + 3] = a;
        }
        return px;
    }

    private static string Hex(Color4 c) => $"#{(int)(c.R * 255):X2}{(int)(c.G * 255):X2}{(int)(c.B * 255):X2}";

    private static void TestarCor()
    {
        // 70% cinza + 30% vermelho: vence o vermelho
        var a = CorDaCapa.Escolher(Imagem(64, 64, (x, y) => y < 45 ? ((byte)128, (byte)128, (byte)128, (byte)255) : ((byte)220, (byte)30, (byte)30, (byte)255)), 64, 64);
        Ok(a is { } ca && ca.R > ca.G * 2 && ca.R > ca.B * 2, $"cinza com faixa vermelha: vermelho ({(a is { } x1 ? Hex(x1) : "nada")})");

        // preto e branco: sem cor
        var b = CorDaCapa.Escolher(Imagem(32, 32, (x, y) => (x + y) % 2 == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255)), 32, 32);
        Ok(b == null, "capa preto e branco: fica a cor padrão");

        // 50% marrom apagado + 30% azul vivo + 20% preto: o azul (saturado) pesa mais
        var c = CorDaCapa.Escolher(Imagem(40, 40, (x, y) => y < 20 ? ((byte)120, (byte)90, (byte)70, (byte)255) : y < 32 ? ((byte)30, (byte)60, (byte)220, (byte)255) : ((byte)5, (byte)5, (byte)5, (byte)255)), 40, 40);
        Ok(c is { } cc && cc.B > cc.R && cc.B > cc.G, $"marrom apagado x azul vivo: azul ({(c is { } x2 ? Hex(x2) : "nada")})");

        // azul-marinho escuro: clareado até contrastar com o preto
        var d = CorDaCapa.Escolher(Imagem(32, 32, (x, y) => ((byte)10, (byte)20, (byte)90, (byte)255)), 32, 32);
        Ok(d is { } cd && CorDaCapa.Luminancia(cd.R, cd.G, cd.B) >= 0.2 && cd.B > cd.R, $"azul-marinho clareado para ler no preto ({(d is { } x3 ? Hex(x3) + $", luminância {CorDaCapa.Luminancia(x3.R, x3.G, x3.B):0.00}" : "nada")})");

        // logo pequeno colorido numa capa cinza (menos de 3%): ruído
        var e = CorDaCapa.Escolher(Imagem(100, 100, (x, y) => x < 5 && y < 5 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)90, (byte)90, (byte)90, (byte)255)), 100, 100);
        Ok(e == null, "pontinho colorido numa capa cinza: fica a padrão");

        // imagem grande (reduz para 32x32) com gradiente verde: não quebra e dá verde
        var g = CorDaCapa.Escolher(Imagem(300, 300, (x, y) => ((byte)20, (byte)(100 + x * 155 / 300), (byte)40, (byte)255)), 300, 300);
        Ok(g is { } cg && cg.G > cg.R && cg.G > cg.B, $"300x300 com gradiente verde: verde ({(g is { } x4 ? Hex(x4) : "nada")})");

        // transparente não conta
        var t = CorDaCapa.Escolher(Imagem(32, 32, (x, y) => ((byte)255, (byte)0, (byte)0, (byte)0)), 32, 32);
        Ok(t == null, "pixels transparentes não contam");

        // amarelo claro já legível: continua amarelo e não estoura para branco
        var am = CorDaCapa.Ajustar(1.0, 0.85, 0.2);
        Ok(am.R > 0.9 && am.G > 0.7 && am.B < 0.6, $"amarelo continua amarelo ({Hex(am)})");

        // reduzir a capa para o tamanho desenhado
        var capa = new Capa(Imagem(64, 64, (x, y) => ((byte)200, (byte)50, (byte)50, (byte)255)), 64);
        var r24 = capa.NoTamanho(24);
        Ok(r24.Length == 24 * 24 * 4 && Math.Abs(r24[(12 * 24 + 12) * 4 + 2] - 200) <= 2 && r24[(0 * 24 + 0) * 4 + 3] == 255,
            "capa reduzida para 24 px mantém a cor e a borda opaca");
        Ok(ReferenceEquals(capa.NoTamanho(24), r24), "o tamanho reduzido fica guardado (não refaz por quadro)");

        // o caminho real da Thumbnail: PNG 300x200 (metade vermelha, metade azul) num stream do WinRT
        var dec = Task.Run(async () =>
        {
            using var bmp = new System.Drawing.Bitmap(300, 200);
            using (var gr = System.Drawing.Graphics.FromImage(bmp))
            {
                gr.Clear(System.Drawing.Color.FromArgb(220, 30, 30));
                gr.FillRectangle(new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(30, 60, 220)), 150, 0, 150, 200);
            }
            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            var fluxo = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var escritor = new Windows.Storage.Streams.DataWriter(fluxo))
            {
                escritor.WriteBytes(ms.ToArray());
                await escritor.StoreAsync();
                escritor.DetachStream();
            }
            fluxo.Seek(0);
            return await Capa.Decodificar(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(fluxo));
        });
        if (!dec.Wait(8000)) { Ok(false, "decodificar a capa: não respondeu"); return; }
        var cd2 = dec.Result;
        Ok(cd2 is { Lado: 200 }, $"PNG 300x200 decodificado e cortado no centro: {(cd2 == null ? "nada" : $"{cd2.Lado}x{cd2.Lado}")}");
        if (cd2 != null)
        {
            // corte do centro: x 0 da capa é o x 50 do original (vermelho), x 199 é o 249 (azul); a divisa fica no 100
            int Px(int x, int y, int canal) => cd2.Pixels[(y * cd2.Lado + x) * 4 + canal];
            Ok(Px(10, 100, 2) > 200 && Px(10, 100, 0) < 60 && Px(190, 100, 0) > 200 && Px(190, 100, 2) < 60, "corte no centro: vermelho à esquerda, azul à direita");
            var corReal = CorDaCapa.Escolher(cd2.Pixels, cd2.Lado, cd2.Lado);
            Ok(corReal != null, $"cor da capa decodificada: {(corReal is { } cr ? Hex(cr) : "nada")}");
        }
    }

    // ------------------------------------------------------------------ textos

    private static void TestarMeio()
    {
        var (de, ate) = AtividadeMidia.MeioLivre();
        Ok(de >= AtividadeMidia.CAPA_COMPACTA + AtividadeMidia.FOLGA_MEIO, $"meio livre começa depois da capa ({de})");
        Ok(ate <= AtividadeMidia.InicioDoVisualizador - AtividadeMidia.FOLGA_MEIO, $"meio livre acaba antes do visualizador ({ate})");
        Ok(MathF.Abs((de + ate) / 2 - AtividadeMidia.COMPACTA_L / 2) < 0.01f, "meio livre centrado na pílula");
        Ok(ate - de >= 60, $"meio livre cabe a hora ({ate - de} DIP)");
    }

    private static void TestarTextos()
    {
        Ok(Rotulo.Tempo(TimeSpan.FromSeconds(75)) == "1:15", "75 s = 1:15");
        Ok(Rotulo.Tempo(TimeSpan.FromSeconds(3725)) == "1:02:05", "3725 s = 1:02:05");
        Ok(Rotulo.Tempo(TimeSpan.FromSeconds(-4)) == "0:00", "negativo vira 0:00");
        Ok(Rotulo.App("Spotify.exe") == "Spotify", "AUMID Spotify.exe");
        Ok(Rotulo.App("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") == "Spotify", "AUMID de pacote da Store");
        Ok(Rotulo.App("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic") == "ZuneMusic", "AUMID com ponto depois do !");
        Ok(Rotulo.App("brave") == "Brave", "AUMID curto");
        Ok(Rotulo.App("Clipchamp.Clipchamp_yxz26nhyzhsrt!App") == "Clipchamp", "AUMID terminado em !App usa o nome do pacote");
        Ok(VolumeDoSistema.Novo(0.43f, 0.02f) == 0.45f, "volume 43% + 2% = 45%");
        Ok(VolumeDoSistema.Novo(0.995f, 0.02f) == 1f && VolumeDoSistema.Novo(0.01f, -0.02f) == 0f, "volume fica entre 0 e 100%");
        Ok(VolumeDoSistema.Novo(0.4299f, -0.04f) == 0.39f, "volume arredonda para o por cento inteiro");
    }

    // ------------------------------------------------------------------ sondas (só leem)

    private static void Sondas()
    {
        var smtc = Task.Run(async () =>
        {
            var g = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var lista = g.GetSessions();
            var linhas = new List<string> { $"ok      SMTC respondeu sem pacote: {lista.Count} sessões" };
            foreach (var s in lista)
            {
                var info = s.GetPlaybackInfo();
                var tl = s.GetTimelineProperties();
                linhas.Add($"ok        {Rotulo.App(s.SourceAppUserModelId)}: {info.PlaybackStatus}, seek {(info.Controls.IsPlaybackPositionEnabled ? "sim" : "não")}, " +
                           $"duração {Rotulo.Tempo(tl.EndTime - tl.StartTime)}, maxSeek {Rotulo.Tempo(tl.MaxSeekTime)}");
                try
                {
                    var props = await s.TryGetMediaPropertiesAsync();
                    if (props?.Thumbnail == null) { linhas.Add("ok          sem capa"); continue; }
                    var capa = await Capa.Decodificar(props.Thumbnail);
                    var cor = capa != null ? CorDaCapa.Escolher(capa.Pixels, capa.Lado, capa.Lado) : null;
                    linhas.Add(capa != null ? $"ok          capa decodificada {capa.Lado}x{capa.Lado}, cor {(cor is { } c ? Hex(c) : "padrão")}" : "FALHOU      capa não decodificou");
                }
                catch (Exception e) { linhas.Add("FALHOU      capa: " + e.Message); }
            }
            return linhas;
        });
        if (smtc.Wait(8000)) foreach (var l in smtc.Result) _s.Append(l).Append('\n');
        else Ok(false, "SMTC não respondeu em 8 s");

        var vol = Task.Run(VolumeDoSistema.Ler);
        if (!vol.Wait(5000)) Ok(false, "volume: não respondeu");
        else if (vol.Result is { } v) Ok(true, $"volume do dispositivo padrão lido: {v.Nivel * 100:0}%{(v.Mudo ? ", mudo" : "")} (não mexi)");
        else _s.Append("aviso   volume: nenhuma saída de áudio\n");

        // thread própria (MTA), não a do pool: outros autotestes deixam o pool ocupado (servidores de pipe) e a sondagem
        // ficava na fila mais de 5 s; a espera atende a fila desta thread caso o objeto de áudio tenha nascido aqui
        var fonte = new TaskCompletionSource<string>();
        var sonda = new System.Threading.Thread(() =>
        {
            try { fonte.SetResult(Loopback.Sondar()); } catch (Exception e) { fonte.SetResult("FALHOU  loopback: " + e.Message); }
        }) { IsBackground = true, Name = "teste do loopback" };
        sonda.SetApartmentState(System.Threading.ApartmentState.MTA);
        sonda.Start();
        var lb = fonte.Task;
        var espera = System.Diagnostics.Stopwatch.StartNew();
        while (!lb.IsCompleted && espera.ElapsedMilliseconds < 5000) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        if (lb.IsCompleted) _s.Append(lb.Result).Append('\n');
        else Ok(false, "loopback não respondeu");

        // a classe de verdade: thread própria, aviso de troca de dispositivo registrado, FFT e bandas publicadas
        var real = new Loopback();
        real.Ligar();
        var bandas = new float[Espectro.Bandas];
        float maior = 0;
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        while (relogio.ElapsedMilliseconds < 700)
        {
            System.Threading.Thread.Sleep(50);
            real.Ler(bandas);
            maior = Math.Max(maior, bandas.Max());
        }
        string formato = real.Formato;
        real.Desligar();
        Ok(formato.Length > 0, $"visualizador rodou na thread própria ({formato}); maior barra em 0,7 s: {maior:0.00}{(maior == 0 ? " (silêncio, ou som com DRM)" : "")}");
        real.Ler(bandas);
        Ok(bandas.All(x => x == 0), "desligado: as bandas zeram na hora");
    }
}
