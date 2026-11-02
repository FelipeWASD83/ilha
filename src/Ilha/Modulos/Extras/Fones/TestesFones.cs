using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;

namespace Ilha.Extras;

internal static class TestesFones
{
    // os nomes que estão em imagens/fones (FONTES.md): o teste não depende das fotos existirem no clone
    private static readonly string[] Arquivos =
    {
        "airpods-pro-3.png", "airpods-pro-2.png", "airpods-4.png", "airpods-5.png", "airpods-max-2.png", "airpods-max.png",
        "g733-preto.png", "g733-branco.png", "g733-lilas.png", "g733-azul.png", "sony-wh-1000xm6.png", "sony-wf-1000xm6.png",
        "samsung-galaxy-buds4-pro.png", "bose-qc-ultra-fones-2.png", "bose-qc-ultra-earbuds-2.png", "jbl-tune-770nc.png",
        "beats-studio-pro.png", "beats-powerbeats-pro-2.png", "hyperx-cloud-iii-wireless.png", "razer-blackshark-v2-pro.png",
        "steelseries-arctis-nova-pro-wireless.png", "logitech-g-pro-x-2-lightspeed.png", "corsair-hs80-rgb-wireless.png", "nothing-ear.png",
        "g502-x-plus-preto.png", "g502-x-plus-roxo.png", "g502-x-plus-branco.png", "g502-x-plus-menta.png",
    };

    private static byte[] Anuncio(ushort modelo, byte estado, byte pecas, byte estojo)
    {
        var d = new byte[27];
        d[0] = 0x07; d[1] = 25; d[2] = 0x01;
        d[3] = (byte)(modelo & 0xFF); d[4] = (byte)(modelo >> 8);
        d[5] = estado; d[6] = pecas; d[7] = estojo; d[8] = 0x04;
        return d;
    }

    public static string Rodar()
    {
        // the text assertions are about Portuguese; English has its own cases
        string idioma = Lang.Choice;
        Lang.Set("pt");
        try
        {
            var r = RodarPt();
            Lang.Set("en");
            r.Conferir("english: subtitles and battery labels", ModuloFones.Subtitulo(true, false, null, false) == "Connected · reading the battery"
                && ModuloFones.Partes(null, 63, true, true).Single() == ("estimate", 63, true));
            r.Conferir("english: low battery lines", ModuloFones.TextoBateriaBaixa(20, 20, "left", true) == "Left earbud · Battery at 20%"
                && ModuloFones.TextoBateriaBaixa(1, 1, null, false) == "Battery almost empty: 1%");
            return r.ToString();
        }
        finally { Lang.Set(idioma); }
    }

    private static Relatorio RodarPt()
    {
        var r = new Relatorio();

        // ---- anúncio dos AirPods
        var a = AnuncioApple.Decodificar(Anuncio(0x2027, 0x2B, 0x98, 0x56), -50);
        r.Conferir("AirPods Pro 3 pelo modelo 0x2027", a?.Modelo == 0x2027 && ModelosApple.Nome(0x2027) == "AirPods Pro 3");
        r.Conferir("anunciado pelo esquerdo: nibble baixo é o esquerdo", a is { Esquerdo: 80, Direito: 90, Estojo: 60 }, $"{a?.Esquerdo}/{a?.Direito}/{a?.Estojo}");
        r.Conferir("carregando: esquerdo e estojo", a is { CarregandoEsquerdo: true, CarregandoDireito: false, CarregandoEstojo: true });
        var b = AnuncioApple.Decodificar(Anuncio(0x2027, 0x0B, 0x98, 0x16));
        r.Conferir("anunciado pelo direito: troca os lados", b is { Esquerdo: 90, Direito: 80, CarregandoDireito: true, CarregandoEsquerdo: false }, $"{b?.Esquerdo}/{b?.Direito}");
        var real = AnuncioApple.Decodificar(Convert.FromHexString("0719010E202B998F110005".PadRight(54, '0')));
        r.Conferir("anúncio de exemplo (AirPods Pro, 90/90, estojo sem leitura)", real is { Modelo: 0x200E, Esquerdo: 90, Direito: 90, Estojo: null, CarregandoEstojo: false });
        var semUm = AnuncioApple.Decodificar(Anuncio(0x2024, 0x2B, 0xF7, 0x0F));
        r.Conferir("nibble 0xF vira sem leitura", semUm is { Esquerdo: 70, Direito: null, Estojo: null } && semUm.MenorFone == 70);
        r.Conferir("tamanho errado é recusado", AnuncioApple.Decodificar(new byte[26]) == null && AnuncioApple.Decodificar(new byte[27]) == null);
        var outroTipo = Anuncio(0x2027, 0x2B, 0x98, 0x56); outroTipo[0] = 0x10;
        r.Conferir("outra mensagem da Apple (0x10) é recusada", AnuncioApple.Decodificar(outroTipo) == null);
        r.Conferir("aceita só o modelo esperado", VarreduraApple.Aceita(a!, 0x2027, -75) && !VarreduraApple.Aceita(a!, 0x2014, -75));
        r.Conferir("sinal fraco é de outra pessoa", !VarreduraApple.Aceita(a! with { Rssi = -90 }, 0x2027, -75));
        r.Conferir("sem modelo esperado: qualquer modelo conhecido", VarreduraApple.Aceita(a!, null, -75) && !VarreduraApple.Aceita(a! with { Modelo = 0x1234 }, null, -75));
        r.Conferir("anúncio sem bateria nenhuma não serve", !VarreduraApple.Aceita(AnuncioApple.Decodificar(Anuncio(0x2027, 0x2B, 0xFF, 0x0F))!, 0x2027, -75));

        // ---- G733
        r.Conferir("curva: 3520 mV dá ~11%", CurvaG733.Porcentagem(3520) == 11, CurvaG733.Porcentagem(3520)?.ToString() ?? "null");
        r.Conferir("curva: pontos exatos", CurvaG733.Porcentagem(4100) == 100 && CurvaG733.Porcentagem(3850) == 60 && CurvaG733.Porcentagem(3150) == 0);
        r.Conferir("curva: meio do caminho", CurvaG733.Porcentagem(3900) == 70 && CurvaG733.Porcentagem(3700) == 30, $"{CurvaG733.Porcentagem(3900)}/{CurvaG733.Porcentagem(3700)}");
        r.Conferir("curva: acima do topo e abaixo do fim", CurvaG733.Porcentagem(4250) == 100 && CurvaG733.Porcentagem(3000) == null);
        var c = HidPP.Consulta();
        r.Conferir("consulta HID++ 11 FF 08 0A com 20 bytes", c.Length == 20 && c[0] == 0x11 && c[1] == 0xFF && c[2] == 0x08 && c[3] == 0x0A && c.Skip(4).All(x => x == 0));
        var resp = HidPP.Interpretar(new byte[] { 0x11, 0xFF, 0x08, 0x0A, 0x0D, 0xC0, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        r.Conferir("resposta: 0x0DC0 = 3520 mV, descarregando", resp is { Tipo: TipoRespostaG733.Leitura, Milivolts: 3520, Carregando: false } && resp.Value.Porcentagem == 11);
        var carregando = HidPP.Interpretar(new byte[] { 0x11, 0xFF, 0x08, 0x03, 0x0F, 0xA0, 0x03, 0 });
        r.Conferir("resposta dada ao G HUB (outro software id) também vale, e 3 = carregando", carregando is { Milivolts: 4000, Carregando: true });
        r.Conferir("erro HID++ 2.0 da consulta = desligado", HidPP.Interpretar(new byte[] { 0x11, 0xFF, 0xFF, 0x08, 0x0A, 0x09, 0, 0 })?.Tipo == TipoRespostaG733.Desligado);
        r.Conferir("erro curto HID++ 1.0 = desligado", HidPP.Interpretar(new byte[] { 0x10, 0xFF, 0x8F, 0x08, 0x0A, 0x09, 0 })?.Tipo == TipoRespostaG733.Desligado);
        r.Conferir("relatórios que não são a resposta são descartados",
            HidPP.Interpretar(new byte[] { 0x11, 0xFF, 0x04, 0x0A, 1, 2, 3, 0 }) == null
            && HidPP.Interpretar(new byte[] { 0x11, 0xFF, 0x08, 0x1A, 1, 2, 3, 0 }) == null
            && HidPP.Interpretar(new byte[] { 0x11, 0x01, 0x08, 0x0A, 1, 2, 3, 0 }) == null
            && HidPP.Interpretar(new byte[] { 0x20, 0xFF }) == null
            && HidPP.Interpretar(new byte[] { 0x11, 0xFF, 0x08, 0x0A, 0, 0, 1, 0 }) == null);
        var est = new EstadoG733(silenciarPrimeira: true);
        var leitura = new RespostaG733(TipoRespostaG733.Leitura, 3900, false);
        var desligado = new RespostaG733(TipoRespostaG733.Desligado, 0, false);
        var seq = new[] { est.Receber(leitura), est.Receber(leitura), est.SemResposta(), est.Receber(desligado), est.Receber(leitura) };
        r.Conferir("estado: já ligado ao abrir não avisa; desliga e liga avisa",
            seq.SequenceEqual(new[] { MudancaG733.Atualizou, MudancaG733.Atualizou, MudancaG733.Desligou, MudancaG733.Nada, MudancaG733.Ligou }), string.Join(",", seq));
        var est2 = new EstadoG733(silenciarPrimeira: false);
        r.Conferir("estado: receptor ligado depois, headset respondendo = conectou", est2.Receber(leitura) == MudancaG733.Ligou && est2.Ultima?.Milivolts == 3900);
        var est3 = new EstadoG733(silenciarPrimeira: true);
        r.Conferir("estado: abriu desligado e depois ligou avisa", est3.SemResposta() == MudancaG733.Nada && est3.Receber(leitura) == MudancaG733.Ligou);

        // ---- foto pelo nome
        void Foto(string nome, string? esperado, IReadOnlyDictionary<string, string>? trocas = null, string? variante = "preto")
        {
            var achado = Fotos.Escolher(nome, Arquivos, trocas, variante);
            r.Conferir($"foto de \"{nome}\"", achado == esperado, achado ?? "nenhuma");
        }
        Foto("AirPods Pro 3 de Fulano - Find My", "airpods-pro-3.png");
        Foto("AirPods Pro de Fulano", "airpods-pro-3.png");
        Foto("AirPods de Fulano", "airpods-5.png");
        Foto("AirPods Max de Fulano", "airpods-max.png");
        Foto("AirPods Max 2", "airpods-max-2.png");
        Foto("WH-1000XM6", "sony-wh-1000xm6.png");
        Foto("WF-1000XM6", "sony-wf-1000xm6.png");
        Foto("Galaxy Buds4 Pro (A1B2)", "samsung-galaxy-buds4-pro.png");
        Foto("Bose QC Ultra Headphones", "bose-qc-ultra-fones-2.png");
        Foto("Bose QC Ultra Earbuds", "bose-qc-ultra-earbuds-2.png");
        Foto("JBL TUNE 770NC", "jbl-tune-770nc.png");
        Foto("Beats Studio Pro", "beats-studio-pro.png");
        Foto("Powerbeats Pro 2", "beats-powerbeats-pro-2.png");
        Foto("HyperX Cloud III Wireless", "hyperx-cloud-iii-wireless.png");
        Foto("BlackShark V2 Pro", "razer-blackshark-v2-pro.png");
        Foto("Arctis Nova Pro Wireless", "steelseries-arctis-nova-pro-wireless.png");
        Foto("PRO X 2 LIGHTSPEED", "logitech-g-pro-x-2-lightspeed.png");
        Foto("HS80 RGB WIRELESS", "corsair-hs80-rgb-wireless.png");
        Foto("Nothing Ear (a)", "nothing-ear.png");
        Foto("G733", "g733-preto.png");
        Foto("G733", "g733-lilas.png", variante: "lilas");
        Foto("G502 X PLUS", "g502-x-plus-preto.png");
        Foto("G502 X PLUS", "g502-x-plus-roxo.png", variante: "roxo");
        Foto("G502 X PLUS", "g502-x-plus-branco.png", variante: "branco");
        Foto("G502 X PLUS", "g502-x-plus-menta.png", variante: "menta");
        Foto("DualSense Edge Wireless Controller", null);
        Foto("Teclado K380", null);
        Foto("Fone Qualquer", "airpods-pro-3.png", new Dictionary<string, string> { ["qualquer"] = "airpods-pro-3.png" });
        Foto("Fone Qualquer", null, new Dictionary<string, string> { ["qualquer"] = "nao-existe.png" });
        r.Conferir("acento e caixa não atrapalham", Fotos.Palavras("Fônes ÁIRPODS-Pro_3").SequenceEqual(new[] { "fones", "airpods", "pro", "3" }));
        r.Conferir("nome sem o \" - Find My\"", Fotos.NomeLimpo("AirPods Pro 3 de Fulano - Find My") == "AirPods Pro 3 de Fulano" && Fotos.NomeLimpo("G733") == "G733");
        r.Conferir("foto pelo modelo da Apple", ModelosApple.Foto(0x2027) == "airpods-pro-3.png" && ModelosApple.Foto(0x2024) == "airpods-pro-2.png" && ModelosApple.Foto(0x9999) == null);
        r.Conferir("Apple pelo fabricante ou pelo nome", ModelosApple.EhApple(0x004C, "x") && ModelosApple.EhApple(null, "Powerbeats Pro 2") && !ModelosApple.EhApple(0x054C, "DualSense"));

        // ---- PnP
        const string idAirPods = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\8&2BEF24E&0&A1B2C3D4E5F6_C00000000";
        r.Conferir("fabricante e produto do id do PnP", NosPnp.LerVidPid(idAirPods) == (0x004C, 0x2027) && NosPnp.LerVidPid(@"BTHENUM\DEV_A1B2C3D4E5F6\8&X") == null);
        var ids = new[] { idAirPods, @"BTHENUM\DEV_A1B2C3D4E5F6\8&2BEF24E&0&BLUETOOTHDEVICE_A1B2C3D4E5F6", @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0DF2\8&2BEF24E&0&112233445566_C00000000" };
        r.Conferir("nós do aparelho pelo endereço", NosPnp.IdsDoEndereco(ids, 0xA1B2C3D4E5F6).Count == 2 && NosPnp.EnderecoHex(0x0000112233445566) == "112233445566");

        // ---- alerta
        var partes = ModuloFones.Partes(a, null, false, false);
        r.Conferir("partes do alerta dos AirPods", partes.Select(x => x.Rotulo + x.Pct + (x.Carregando ? "+" : "")).SequenceEqual(new[] { "E80+", "D90", "estojo60+" }));
        r.Conferir("parte única do G733", ModuloFones.Partes(null, 63, true, true).Single() == ("estimada", 63, true) && ModuloFones.Partes(null, null, false, false).Count == 0);
        r.Conferir("subtítulos", ModuloFones.Subtitulo(true, false, null, false) == "Conectado · lendo a bateria" && ModuloFones.Subtitulo(false, true, null, false) == "Ligado · bateria estimada pela tensão");

        // ---- alertas de bateria baixa
        var al = new BatteryAlerts();
        int? F(int pct, bool carregando = false) => al.Feed("x", pct, carregando);
        r.Conferir("alerta: primeira leitura alta não avisa", F(80) == null);
        r.Conferir("alerta: 52 nada, 49 cruza o 50, 48 não repete", F(52) == null && F(49) == 50 && F(48) == null);
        r.Conferir("alerta: 25", F(30) == null && F(25) == 25);
        r.Conferir("alerta: queda grande avisa só o nível mais baixo cruzado", F(8) == 10);
        r.Conferir("alerta: 5, depois 4 sem repetir, depois 1", F(5) == 5 && F(4) == null && F(1) == 1);
        r.Conferir("alerta: carregando zera, e a descarga seguinte avisa de novo", F(1, true) == null && F(40, true) == null && F(40) == null && F(24) == 25);
        var al2 = new BatteryAlerts();
        int? G(int pct) => al2.Feed("y", pct, false);
        r.Conferir("alerta: oscilar em volta do nível não repete até subir a margem", G(30) == null && G(24) == 25 && G(26) == null && G(24) == null && G(31) == null && G(24) == 25);
        r.Conferir("alerta: primeira leitura já baixa avisa uma vez, pelo nível mais baixo acima dela",
            new BatteryAlerts().Feed("a", 8, false) == 10 && new BatteryAlerts().Feed("a", 3, false) == 5 && new BatteryAlerts().Feed("a", 40, false) == null);
        r.Conferir("alerta: cada bateria tem a sua conta", new BatteryAlerts().Feed("a", 60, false) == null && al2.Feed("outra", 8, false) == 10);
        r.Conferir("alerta: níveis dos AirPods (de 10 em 10)", BatteryAlerts.Coarse(BatteryAlerts.DefaultLevels).SequenceEqual(new[] { 50, 20, 10, 0 }));
        var al3 = new BatteryAlerts();
        int? A(int pct) => al3.Feed("ap", pct, false, coarse: true);
        var sequencia = new[] { A(60), A(50), A(40), A(30), A(20), A(10), A(0) };
        r.Conferir("alerta: AirPods avisam em 50, 20, 10 e 0 (abaixo de 10)", sequencia.SequenceEqual(new int?[] { null, 50, null, null, 20, 10, 0 }), string.Join(",", sequencia.Select(x => x?.ToString() ?? "-")));
        r.Conferir("alerta: níveis do texto da configuração", BatteryAlerts.ParseLevels("50, 25 10; 5%,1").SequenceEqual(new[] { 50, 25, 10, 5, 1 })
            && BatteryAlerts.ParseLevels("abc").SequenceEqual(BatteryAlerts.DefaultLevels) && BatteryAlerts.ParseLevels("").SequenceEqual(BatteryAlerts.DefaultLevels)
            && BatteryAlerts.ParseLevels("30,200,-4").SequenceEqual(new[] { 30 }));
        r.Conferir("alerta: nível configurado de outro jeito", new BatteryAlerts(new[] { 30 }).Feed("z", 40, false) == null);
        var al4 = new BatteryAlerts(new[] { 30 });
        al4.Feed("z", 40, false);
        r.Conferir("alerta: só o nível configurado", al4.Feed("z", 29, false) == 30);
        r.Conferir("textos do alerta", ModuloFones.TextoBateriaBaixa(25, 25, null, false) == "Bateria em 25%"
            && ModuloFones.TextoBateriaBaixa(10, 10, null, false) == "Bateria baixa: 10%"
            && ModuloFones.TextoBateriaBaixa(1, 1, null, false) == "Bateria quase no fim: 1%"
            && ModuloFones.TextoBateriaBaixa(0, 0, "esquerdo", true) == "Fone esquerdo · Bateria quase no fim: <10%");

        // ---- G HUB (formato que ele respondeu no PC em 01/11/2026)
        using var lista = System.Text.Json.JsonDocument.Parse("""
            {"path":"/devices/list","payload":{"deviceInfos":[
              {"id":"dev00000000","deviceModel":"g733","displayName":"G733","pid":2814,"state":"ACTIVE"},
              {"id":"dev00000001","deviceModel":"g502x_plus","displayName":"G502 X PLUS","state":"ACTIVE"},
              {"id":"dev00000002","deviceModel":"receiver","displayName":"Receiver"}]}}
            """);
        var dispositivos = GHubClient.ParseDeviceList(lista.RootElement);
        r.Conferir("G HUB: lista de aparelhos", dispositivos.Count == 3 && dispositivos[1] == ("dev00000001", "g502x_plus", "G502 X PLUS"));
        using var bateria = System.Text.Json.JsonDocument.Parse("""
            {"path":"/battery/dev00000001/state","result":{"code":"SUCCESS"},"payload":{"deviceId":"dev00000001","percentage":98,"charging":true,"mileage":38.0}}
            """);
        var rato = GHubClient.ParseBattery(bateria.RootElement, dispositivos[1]);
        r.Conferir("G HUB: bateria do mouse", rato is { IsMouse: true, IsG733: false, Percent: 98, Charging: true, HoursLeft: 38.0 });
        using var semBateria = System.Text.Json.JsonDocument.Parse("""{"path":"/battery/dev00000002/state","result":{"code":"NO_SUCH_PATH"}}""");
        r.Conferir("G HUB: aparelho sem bateria (o receptor) fica de fora", GHubClient.ParseBattery(semBateria.RootElement, dispositivos[2]) == null);
        using var fone = System.Text.Json.JsonDocument.Parse("""{"path":"/battery/dev00000000/state","result":{"code":"SUCCESS"},"payload":{"percentage":49,"charging":false,"mileage":9.27}}""");
        r.Conferir("G HUB: bateria do G733", GHubClient.ParseBattery(fone.RootElement, dispositivos[0]) is { IsG733: true, Percent: 49, Charging: false });
        using var lixo = System.Text.Json.JsonDocument.Parse("""{"path":"/battery/x/state","result":{"code":"SUCCESS"},"payload":{"percentage":250}}""");
        r.Conferir("G HUB: porcentagem sem sentido é recusada", GHubClient.ParseBattery(lixo.RootElement, dispositivos[0]) == null);

        // ---- sondas de verdade, só leitura (a consulta do G733 é uma pergunta de leitura de tensão)
        r.Tentar("Bluetooth", () =>
        {
            var pareados = DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), null, DeviceInformationKind.AssociationEndpoint).AsTask().GetAwaiter().GetResult();
            int conectados = 0, apple = 0;
            foreach (var d in pareados)
            {
                using var dev = BluetoothDevice.FromIdAsync(d.Id).AsTask().GetAwaiter().GetResult();
                if (dev == null) continue;
                if (dev.ConnectionStatus == BluetoothConnectionStatus.Connected) conectados++;
                var vp = NosPnp.VidPidDoAparelho(dev.BluetoothAddress);
                if (vp?.Fabricante == AnuncioApple.Fabricante)
                {
                    apple++;
                    r.Sonda($"Apple pareado: produto 0x{vp.Value.Produto:X4} ({ModelosApple.Nome(vp.Value.Produto) ?? "desconhecido"}), foto {ModelosApple.Foto(vp.Value.Produto) ?? "pelo nome"}, bateria do Windows {NosPnp.BateriaDoAparelho(dev.BluetoothAddress)?.ToString() ?? "nenhuma"}");
                }
            }
            r.Sonda($"Bluetooth: {pareados.Count} pareados, {conectados} conectados, {apple} da Apple");
        });
        r.Tentar("varredura BLE", () =>
        {
            var v = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
            v.AdvertisementFilter.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData { CompanyId = AnuncioApple.Fabricante });
            var antes = ParametrosBle.Ler(v);
            bool ok = ParametrosBle.Coexistencia(v, out string det);
            var depois = ParametrosBle.Ler(v);
            r.Sonda($"varredura BLE montada (não iniciada): coexistência {det}; janela/intervalo antes {antes?.Janela}/{antes?.Intervalo}, depois {depois?.Janela}/{depois?.Intervalo}");
            r.Conferir("parâmetros de coexistência aplicados no vigia", ok && depois != null, det);
        });
        r.Tentar("G733", () =>
        {
            int achados = 0;
            foreach (var pid in HidPP.ProdutosG733)
            {
                var lista = DeviceInformation.FindAllAsync(HidDevice.GetDeviceSelector(HidPP.PaginaHidPP, HidPP.UsoHidPP, HidPP.Logitech, pid)).AsTask().GetAwaiter().GetResult();
                foreach (var d in lista)
                {
                    achados++;
                    var resp = LeitorG733.ConsultarUmaVez(d.Id, 1500, out string det);
                    r.Sonda(resp is { Tipo: TipoRespostaG733.Leitura } x
                        ? $"G733 (PID {pid:X4}) respondeu: {x.Milivolts} mV, ~{x.Porcentagem}%{(x.Carregando ? ", carregando" : "")}"
                        : $"G733 (PID {pid:X4}) presente, headset {(resp is { Tipo: TipoRespostaG733.Desligado } ? "desligado" : det)}");
                }
            }
            if (achados == 0) r.Sonda("G733: receptor não está ligado no PC");
        });
        r.Tentar("G HUB", () =>
        {
            var todos = GHubClient.ReadAll().GetAwaiter().GetResult();
            if (todos == null) { r.Sonda("G HUB: não respondeu (fechado?)"); return; }
            foreach (var d in todos) r.Sonda($"G HUB: {d.Name} ({d.Model}) {d.Percent}%{(d.Charging ? ", carregando" : "")}{(d.HoursLeft is double h ? $", ~{h:0.#} h" : "")}");
        });
        r.Tentar("fotos", () =>
        {
            var arqs = Fotos.Arquivos();
            r.Sonda($"fotos de fones na pasta do app: {arqs.Count}");
            foreach (var arq in arqs)
            {
                var t0 = DateTime.UtcNow;
                var ret = Retrato.DeArquivo(System.IO.Path.Combine(Fotos.Pasta, arq), 65, 65, recortar: true);
                if (ret == null) { r.Conferir($"foto {arq} carrega", false); continue; }
                // canto de cima à esquerda: depois do recorte da margem e do fundo branco, quase sempre transparente
                r.Sonda($"{arq}: {ret.Largura}x{ret.Altura} px em {(DateTime.UtcNow - t0).TotalMilliseconds:0} ms, alfa do canto {ret.Dados[3]}");
            }
        });
        return r;
    }
}
