using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ilha.Volume;

public sealed class ConfigVolume
{
    // quanto tempo a ilha fica com o volume na tela depois da última mudança
    [JsonPropertyName("segundos")] public double Segundos { get; set; } = 1.8;
    // porcentagem por dente da rodinha do mouse em cima da ilha
    [JsonPropertyName("passoRolar")] public int PassoRolar { get; set; } = 2;
    // mostrar "conectado"/"desconectado" quando um fone, headset ou caixa entra ou sai
    [JsonPropertyName("mostrarConexoes")] public bool MostrarConexoes { get; set; } = true;
    // EXPERIMENTAL: some com o pop-up de volume do Windows (some junto com o de brilho, modo avião e área de trabalho virtual)
    [JsonPropertyName("esconderOsdDoWindows")] public bool EsconderOsdDoWindows { get; set; }

    internal void Corrigir()
    {
        Segundos = double.IsFinite(Segundos) ? Math.Clamp(Segundos, 0.5, 30) : 1.8;
        PassoRolar = Math.Clamp(PassoRolar, 1, 25);
    }
}

[Modulo("volume", "Volume")]
internal sealed class ModuloVolume : Modulo
{
    private ConfigVolume _cfg = new();
    private Audio? _audio;
    private AtividadeVolume? _atv;
    private OsdDoWindows? _osd;
    private readonly Dictionary<string, DateTime> _avisados = new();

    public override Type? TipoConfig => typeof(ConfigVolume);

    protected override void Iniciar(JsonObject cfg)
    {
        _cfg = Ler<ConfigVolume>(cfg);
        _cfg.Corrigir();
        // se a ilha anterior morreu com o pop-up nativo escondido, ele volta antes de qualquer coisa
        OsdDoWindows.RestaurarDaUltimaVez();
        _audio = new Audio(S.NaInterface);
        _atv = new AtividadeVolume(_audio, () => _cfg, () => S.Ilha.Expandida == _atv);
        S.Ilha.Adicionar(_atv);
        _audio.VolumeMudou += AoMudarVolume;
        _audio.ListaMudou += () => _atv?.ListaMudou();
        _audio.Conexao += AoConectar;
        _audio.Iniciar();
        AplicarOsd();
    }

    public override void ConfigMudou(JsonObject cfg)
    {
        _cfg = Ler<ConfigVolume>(cfg);
        _cfg.Corrigir();
        AplicarOsd();
    }

    private void AplicarOsd()
    {
        if (_cfg.EsconderOsdDoWindows) (_osd ??= new OsdDoWindows()).Ligar();
        else _osd?.Desligar();
    }

    private void AoMudarVolume(bool deFora)
    {
        if (_atv == null) return;
        if (deFora && _osd != null)
        {
            // só esconde o nativo quando a ilha vai mesmo aparecer no lugar dele
            var ctx = S.ContextoAtual();
            _osd.MudouDeFora(!ctx.Competitivo && !ctx.D3DExclusivo && !S.Config().Pausada);
        }
        _atv.VolumeMudou(deFora);
    }

    private void AoConectar(string id, bool conectou)
    {
        if (!_cfg.MostrarConexoes || _atv == null || _audio == null) return;
        string nome = _audio.NomeDoAparelho(id);
        // os AirPods mudam de estado nos dois endpoints (estéreo e Hands-Free): um aviso só
        string chave = (conectou ? "+" : "-") + nome;
        var agora = DateTime.Now;
        if (_avisados.TryGetValue(chave, out var quando) && (agora - quando).TotalSeconds < 4) return;
        _avisados[chave] = agora;
        _atv.Aviso(conectou, nome);
    }

    public override void Parar()
    {
        _osd?.Dispose();
        _osd = null;
        _atv?.Esconder();
        _audio?.Dispose();
        _audio = null;
    }

    // ---------------------------------------------------------------- autoteste

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Conferir(bool ok, string oque) => r.AppendLine((ok ? "ok      " : "FALHOU  ") + oque);
        bool Perto(float a, float b, float tol = 0.01f) => MathF.Abs(a - b) <= tol;

        // escala e porcentagem
        Conferir(Logica.Porcentagem(0f) == 0 && Logica.Porcentagem(0.5f) == 50 && Logica.Porcentagem(1f) == 100, "porcentagem de 0, 0,5 e 1");
        Conferir(Logica.Porcentagem(1.3f) == 100 && Logica.Porcentagem(-0.2f) == 0 && Logica.Porcentagem(float.NaN) == 0, "porcentagem presa entre 0 e 100 (e NaN vira 0)");
        Conferir(Logica.Porcentagem(0.333f) == 33 && Logica.Porcentagem(0.996f) == 100, "porcentagem arredonda como o Windows");
        Conferir(Perto(Logica.Escala(64), 0.64f) && Logica.Escala(150) == 1f && Logica.Escala(-3) == 0f, "porcentagem para escala");

        // decibéis
        Conferir(Perto(Logica.Amplitude(0f), 1f) && Perto(Logica.Amplitude(-6.0206f), 0.5f, 0.001f) && Perto(Logica.Amplitude(-20f), 0.1f, 0.0001f), "dB para amplitude");
        Conferir(Perto(Logica.Decibeis(0.5f), -6.0206f) && Perto(Logica.Decibeis(1f), 0f) && float.IsNegativeInfinity(Logica.Decibeis(0f)), "amplitude para dB");
        Conferir(Perto(Logica.Decibeis(Logica.Amplitude(-37.5f)), -37.5f), "dB ida e volta");
        Conferir(Logica.TextoDb(-12.54f) == "-12,5 dB" && Logica.TextoDb(-0.04f) == "0,0 dB" && Logica.TextoDb(float.NegativeInfinity) == "-∞ dB",
            $"texto dos dB ({Logica.TextoDb(-12.54f)}, {Logica.TextoDb(-0.04f)}, {Logica.TextoDb(float.NegativeInfinity)})");

        // rodinha e barra
        Conferir(Logica.Porcentagem(Logica.Rolar(0.63f, 120, 2)) == 64 && Logica.Porcentagem(Logica.Rolar(0.64f, 120, 2)) == 66, "rodinha para cima anda na grade de 2%");
        Conferir(Logica.Porcentagem(Logica.Rolar(0.63f, -120, 2)) == 62 && Logica.Porcentagem(Logica.Rolar(0.64f, -240, 2)) == 60, "rodinha para baixo (um e dois dentes)");
        Conferir(Logica.Rolar(1f, 120, 2) == 1f && Logica.Rolar(0f, -120, 2) == 0f && Logica.Porcentagem(Logica.Rolar(0.01f, -120, 2)) == 0, "rodinha presa nos extremos");
        Conferir(Logica.Porcentagem(Logica.Rolar(0.5f, 360, 5)) == 65, "passo configurável (3 dentes de 5%)");
        Conferir(Logica.DaBarra(48, 48, 242) == 0f && Perto(Logica.DaBarra(169, 48, 242), 0.5f) && Logica.DaBarra(400, 48, 242) == 1f && Logica.DaBarra(-5, 48, 242) == 0f, "posição na barra para escala");
        Conferir(Logica.Ondas(0.2f, false) == 1 && Logica.Ondas(0.5f, false) == 2 && Logica.Ondas(0.9f, false) == 3 && Logica.Ondas(0.9f, true) == 0 && Logica.Ondas(0f, false) == 0, "ondas do alto-falante");

        // junção dos AirPods (nomes de exemplo, sem aparelho de ninguém)
        var bt = Guid.NewGuid();
        var eps = new List<Endpoint>
        {
            new("{0.0.0.00000000}.{monitor}", "VG34 (NVIDIA High Definition Audio)", "NVIDIA High Definition Audio", "VG34", "HDAUDIO", Logica.ConteinerDoPc, 9),
            new("{0.0.0.00000000}.{hf}", "Headset (AirPods Pro 3 Hands-Free)", "AirPods Pro 3 Hands-Free", "Headset", "BTHHFENUM", bt, 5),
            new("{0.0.0.00000000}.{g733}", "Alto-falantes (Logitech G733 Gaming Headset)", "Logitech G733 Gaming Headset", "Alto-falantes", "USB", Guid.NewGuid(), 1),
            new("{0.0.0.00000000}.{estereo}", "Fones de ouvido (AirPods Pro 3)", "AirPods Pro 3", "Fones de ouvido", "BTHENUM", bt, 3),
            new("{0.0.0.00000000}.{caixas}", "Alto-falantes (Realtek(R) Audio)", "Realtek(R) Audio", "Alto-falantes", "HDAUDIO", Logica.ConteinerDoPc, 1),
        };
        var j = Logica.Juntar(eps);
        var air = j.Find(s => s.Contem("{0.0.0.00000000}.{hf}"));
        Conferir(j.Count == 4, $"AirPods estéreo e Hands-Free numa linha só ({j.Count} linhas para 5 endpoints)");
        Conferir(air != null && air.Id == "{0.0.0.00000000}.{estereo}" && air.Ids.Count == 2 && air.Contem("{0.0.0.00000000}.{ESTEREO}"), "a linha dos AirPods troca para o estéreo e marca pelos dois ids");
        Conferir(j[0].Nome.StartsWith("VG34") && j[1] == air && j[2].Aparelho.StartsWith("Logitech") && j[3].Aparelho.StartsWith("Realtek"), "a ordem do Windows é mantida (a linha fica onde o primeiro endpoint estava)");
        Conferir(j.Find(s => s.Aparelho.StartsWith("Realtek"))!.Ids.Count == 1 && j[0].Ids.Count == 1, "saídas do próprio PC (mesmo contêiner) não se juntam");
        var semConteiner = Logica.Juntar(new[]
        {
            new Endpoint("a", "Fones de ouvido (Fone X)", "Fone X", "Fones de ouvido", "BTHENUM", Guid.Empty, 3),
            new Endpoint("b", "Headset (Fone X Hands-Free)", "Fone X Hands-Free", "Headset", "BTHHFENUM", Guid.Empty, 5),
        });
        Conferir(semConteiner.Count == 1 && semConteiner[0].Id == "a", "sem contêiner, junta pelo nome sem \"Hands-Free\"");
        var soHf = Logica.Juntar(new[] { new Endpoint("b", "Headset (Fone X Hands-Free)", "Fone X Hands-Free", "Headset", "BTHHFENUM", bt, 5) });
        Conferir(soHf.Count == 1 && soHf[0].Id == "b", "Hands-Free sozinho continua na lista");
        var iguais = Logica.Juntar(new[]
        {
            new Endpoint("m1", "Monitor (NVIDIA)", "NVIDIA High Definition Audio", "Monitor", "HDAUDIO", Guid.Empty, 9),
            new Endpoint("m2", "Monitor 2 (NVIDIA)", "NVIDIA High Definition Audio", "Monitor 2", "HDAUDIO", Guid.Empty, 9),
        });
        Conferir(iguais.Count == 2, "dois monitores com o mesmo nome de aparelho não se juntam");
        Conferir(Logica.SemHandsFree("AirPods Pro 3 Hands-Free") == "AirPods Pro 3" && Logica.SemHandsFree("G733") == "G733", "nome sem o \"Hands-Free\"");

        // conectou e desconectou
        Conferir(Logica.Classificar(EstadoAparelho.Desconectado, EstadoAparelho.Ativo) == Transicao.Conectou, "8 para 1 = conectou");
        Conferir(Logica.Classificar(EstadoAparelho.Ativo, EstadoAparelho.Desconectado) == Transicao.Desconectou && Logica.Classificar(EstadoAparelho.Ativo, EstadoAparelho.Ausente) == Transicao.Desconectou, "1 para 8 ou 4 = desconectou");
        Conferir(Logica.Classificar(EstadoAparelho.Desativado, EstadoAparelho.Ativo) == Transicao.Nada && Logica.Classificar(EstadoAparelho.Ativo, EstadoAparelho.Desativado) == Transicao.Nada, "ativar e desativar à mão não avisam");
        Conferir(Logica.Classificar(0x10000008, 0x10000001) == Transicao.Conectou, "bits altos do estado são ignorados");

        // quanto tempo fica na tela
        var ex = new Exibicao();
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0);
        ex.Volume(t0, 1.8);
        Conferir(ex.Conferir(t0.AddSeconds(1), false) == t0.AddSeconds(1.8), "antes do prazo continua (devolve o prazo)");
        ex.Volume(t0.AddSeconds(1.5), 1.8);
        Conferir(ex.Conferir(t0.AddSeconds(2), false) != null && ex.Ligada, "nova mudança renova os 1,8 s");
        Conferir(ex.Conferir(t0.AddSeconds(3.4), true) is DateTime volta && Math.Abs((volta - t0.AddSeconds(4.4)).TotalMilliseconds) < 1 && ex.Ligada, "com o cartão aberto não some (confere de novo 1 s depois)");
        Conferir(ex.Conferir(t0.AddSeconds(4.4), false) == null && !ex.Ligada, "cartão fechado e prazo vencido: some");
        bool trocouParaAviso = ex.Aviso(t0, true, "Fone X", 3);
        bool modoAviso = ex.Atual == Exibicao.Modo.Conectou && ex.Aparelho == "Fone X";
        bool trocouParaVolume = ex.Volume(t0, 1.8);
        Conferir(trocouParaAviso && modoAviso && trocouParaVolume && ex.Atual == Exibicao.Modo.Volume && !ex.Volume(t0, 1.8), "aviso de aparelho e volume trocam de modo (e o layout só muda quando troca)");

        // notificação do Windows e o eco das mudanças da própria ilha
        var buf = Marshal.AllocHGlobal(32);
        try
        {
            var g = Guid.NewGuid();
            Marshal.StructureToPtr(g, buf, false);
            Marshal.WriteInt32(buf, 16, 1);
            Marshal.WriteInt32(buf, 20, BitConverter.SingleToInt32Bits(0.42f));
            Marshal.WriteInt32(buf, 24, 2);
            var (ctx, mudo, esc) = Audio.LerNotificacao(buf);
            Conferir(ctx == g && mudo && esc == 0.42f, "AUDIO_VOLUME_NOTIFICATION_DATA lido no lugar certo");
        }
        finally { Marshal.FreeHGlobal(buf); }
        var fila = new List<Action>();
        var audioTeste = new Audio(fila.Add);
        var vistos = new List<bool>();
        audioTeste.VolumeMudou += vistos.Add;
        audioTeste.ChegouVolume(0.3f, false, Audio.ContextoDaIlha);
        audioTeste.ChegouVolume(0.32f, false, Audio.ContextoDaIlha);
        foreach (var a in fila) a();
        fila.Clear();
        audioTeste.ChegouVolume(0.5f, true, Guid.Empty);
        foreach (var a in fila) a();
        Conferir(vistos.Count == 2 && !vistos[0] && vistos[1] && audioTeste.Escala == 0.5f && audioTeste.Mudo, "eco da ilha não abre; mudança de fora abre; rajada vira um despacho só");

        // janela do pop-up nativo
        uint pid = 100;
        var altTab = new RetratoJanela(new IntPtr(1), pid, true, false, false, 0, 0, 3440, 1440);
        var osd = new RetratoJanela(new IntPtr(2), pid, true, false, false, 1614, 1304, 211, 76);
        Conferir(Logica.CandidataAoOsd(null, osd, 3440, 1440, pid), "pop-up pequeno que acabou de aparecer é aceito");
        Conferir(!Logica.CandidataAoOsd(null, altTab, 3440, 1440, pid), "ilha XAML da tela inteira (Alt+Tab) é recusada");
        Conferir(!Logica.CandidataAoOsd(osd, osd, 3440, 1440, pid), "o que já estava à vista antes da mudança é recusado");
        Conferir(Logica.CandidataAoOsd(osd with { Visivel = false }, osd, 3440, 1440, pid), "escondida antes e à vista agora é aceita");
        Conferir(!Logica.CandidataAoOsd(null, osd with { Pid = 7 }, 3440, 1440, pid) && !Logica.CandidataAoOsd(null, osd, 3440, 1440, 0), "janela de outro processo (ou sem shell) é recusada");
        Conferir(!Logica.CandidataAoOsd(null, osd with { Encoberta = true }, 3440, 1440, pid) && !Logica.CandidataAoOsd(null, osd with { X = -32000, Y = -32000 }, 3440, 1440, pid), "encoberta ou já fora da tela não conta");

        Conferir(Marshal.SizeOf<ValorDePropriedade>() == 24 && Marshal.SizeOf<ChaveDePropriedade>() == 20, "PROPVARIANT com 24 bytes e PROPERTYKEY com 20");

        Sonda(r, Conferir);
        return r.ToString().TrimEnd();
    }

    /// <summary>Sonda real, só leitura: lista as saídas e lê o volume sem mudar nada no sistema.</summary>
    private static void Sonda(StringBuilder r, Action<bool, string> conferir)
    {
        IMMDeviceEnumerator en;
        try { en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }
        catch (Exception e) { conferir(false, "sonda: MMDeviceEnumerator não abriu: " + e.Message); return; }
        try
        {
            var eps = Audio.ListarAtivos(en);
            conferir(true, $"sonda: {eps.Count} endpoints de saída ativos");
            foreach (var e in eps) r.AppendLine($"          · {e.Nome}  [{e.Enumerador}, formato {e.Formato}{(Logica.EhHandsFree(e) ? ", Hands-Free" : "")}]");
            var linhas = Logica.Juntar(eps);
            conferir(linhas.Count <= eps.Count, $"sonda: {linhas.Count} linhas na lista da ilha");
            foreach (var s in linhas) r.AppendLine($"          · {s.Nome}{(s.Ids.Count > 1 ? $"  (junta {s.Ids.Count} endpoints)" : "")}");

            if (en.GetDefaultAudioEndpoint(FluxoAudio.Render, PapelAudio.Multimidia, out var d) != 0)
                r.AppendLine("aviso   sonda: nenhuma saída padrão agora");
            else
            {
                try
                {
                    d.GetId(out var id);
                    var iid = Com.IID_IAudioEndpointVolume;
                    int hr = d.Activate(ref iid, Com.CLSCTX_ALL, IntPtr.Zero, out var obj);
                    conferir(hr == 0, $"sonda: IAudioEndpointVolume do padrão (0x{hr:X8})");
                    if (hr == 0)
                    {
                        var vol = (IAudioEndpointVolume)obj;
                        try
                        {
                            vol.GetMasterVolumeLevelScalar(out float esc);
                            vol.GetMasterVolumeLevel(out float db);
                            vol.GetMute(out bool mudo);
                            vol.GetVolumeRange(out float min, out float max, out float passo);
                            var padrao = linhas.Find(s => s.Contem(id));
                            conferir(esc >= 0 && esc <= 1, $"sonda: padrão \"{padrao?.Nome ?? id}\" em {Logica.Porcentagem(esc)}% ({Logica.TextoDb(db)}, faixa {Logica.TextoDb(min)} a {Logica.TextoDb(max)}){(mudo ? ", mudo" : "")}");
                            conferir(padrao != null, "sonda: a saída padrão está na lista e seria marcada");
                            // registrar e soltar o callback não muda nada no sistema e prova que a interface gerenciada é aceita
                            var cb = new CallbackDeSonda();
                            int r1 = vol.RegisterControlChangeNotify(cb), r2 = r1 == 0 ? vol.UnregisterControlChangeNotify(cb) : -1;
                            conferir(r1 == 0 && r2 == 0, $"sonda: callback de volume registra e solta (0x{r1:X8}, 0x{r2:X8})");
                        }
                        finally { Com.Soltar(vol); }
                    }
                }
                finally { Com.Soltar(d); }
            }

            var nc = new NotificacaoDeSonda();
            int n1 = en.RegisterEndpointNotificationCallback(nc), n2 = n1 == 0 ? en.UnregisterEndpointNotificationCallback(nc) : -1;
            conferir(n1 == 0 && n2 == 0, $"sonda: aviso de aparelho registra e solta (0x{n1:X8}, 0x{n2:X8})");
        }
        catch (Exception e) { conferir(false, "sonda: " + e.Message); }
        finally { Com.Soltar(en); }

        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClientCom();
            Com.Soltar(pc);
            conferir(true, "sonda: IPolicyConfig responde ao QueryInterface (a troca de saída fica disponível)");
        }
        catch (Exception e) { r.AppendLine("aviso   sonda: IPolicyConfig não respondeu (a ilha só lista as saídas): " + e.Message); }

        try
        {
            uint shell = OsdDoWindows.PidDoShell();
            var ilhas = OsdDoWindows.Retratar();
            int doShell = 0, aVista = 0;
            foreach (var j in ilhas.Values) { if (j.Pid == shell) doShell++; if (j.AVista) aVista++; }
            conferir(true, $"sonda: {doShell} ilhas XAML do explorer agora ({aVista} à vista); nenhuma foi tocada");
        }
        catch (Exception e) { r.AppendLine("aviso   sonda: janelas do explorer: " + e.Message); }
    }

    private sealed class CallbackDeSonda : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr dados) => 0;
    }

    private sealed class NotificacaoDeSonda : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, uint novoEstado) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnDefaultDeviceChanged(FluxoAudio fluxo, PapelAudio papel, string? id) => 0;
        public int OnPropertyValueChanged(string id, ChaveDePropriedade chave) => 0;
    }
}
