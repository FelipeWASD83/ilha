using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace Ilha.Configuracoes;

/// <summary>
/// Dono da tela de configurações (<see cref="JanelaDeConfiguracoes"/>). Registra o atalho "configuracoes", sem
/// combinação por padrão (a pessoa escolhe uma na própria tela), que abre a janela. O menu da bandeja abre pelo
/// <see cref="JanelaDeConfiguracoes.Mostrar"/> direto.
/// </summary>
[Modulo("configuracoes", "Tela de configurações", English = "Settings window")]
internal sealed class ModuloConfiguracoes : Modulo
{
    protected override void Iniciar(JsonObject cfg)
    {
        S.Atalhos.Registrar("configuracoes", new Atalho { Mods = 0, Tecla = 0 }, Abrir);
    }

    private void Abrir()
    {
        var c = S.Config();
        JanelaDeConfiguracoes.Mostrar(S.Config, nova => nova.Salvar(), JanelaDeConfiguracoes.ModulosDaConfig(c), AtalhosRegistrados(S.Atalhos));
    }

    /// <summary>
    /// Nome e combinação padrão de cada atalho que os módulos registraram. O núcleo ainda não expõe isso, então lê
    /// o dicionário interno do Atalhos por reflexão (só leitura); se o campo mudar de nome, volta null e a tela mostra
    /// só os atalhos já trocados na config.
    /// </summary>
    public static IReadOnlyDictionary<string, Atalho>? AtalhosRegistrados(Atalhos atalhos) => atalhos.Padroes;

    // ------------------------------------------------------------------ autoteste

    public enum Lado { Esquerda, Centro, Direita }

    /// <summary>Uma config de módulo com todos os tipos que o formulário sabe editar, e um que ele não sabe.</summary>
    public sealed class ConfigDeTeste
    {
        [JsonPropertyName("ligar")] public bool Ligar { get; set; } = true;
        [Range(1, 10)] [JsonPropertyName("vezes")] public int Vezes { get; set; } = 3;
        [JsonPropertyName("fator")] public double Fator { get; set; } = 0.25;
        [DisplayName("Nome bonito")] [JsonPropertyName("nome")] public string Nome { get; set; } = "x";
        [JsonPropertyName("lado")] public Lado Lado { get; set; } = Lado.Centro;
        [Description("Um por linha")] [JsonPropertyName("itens")] public List<string> Itens { get; set; } = new() { "a", "b" };
        [JsonPropertyName("mapa")] public Dictionary<string, int> Mapa { get; set; } = new() { ["k"] = 1 };
        [JsonIgnore] public int Escondido { get; set; }
    }

    /// <summary>[Label]/[Help] next to [DisplayName]/[Description]: the bilingual ones must win.</summary>
    public sealed class LanguageTestConfig
    {
        [Label("Rótulo em português", "English label")] [DisplayName("Perde")] [Help("Ajuda em português", "English help")] [Description("Perde")]
        [JsonPropertyName("bilingual")] public bool Bilingual { get; set; }
    }

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Conferir(string nome, bool ok, string detalhe = "") =>
            r.AppendLine((ok ? "ok      " : "FALHOU  ") + nome + (detalhe.Length > 0 ? $" ({detalhe})" : ""));

        // the assertions below compare Portuguese UI text: pin the language and put the previous choice back at the end
        var previousLanguage = Lang.Choice;
        Lang.Set("pt");
        try
        {
            // the main menu's grid
            var (spots, rows) = global::Ilha.Menu.Pack(new[] { (1, 1), (1, 1), (1, 1), (1, 1), (1, 1), (1, 2), (1, 1), (2, 1), (1, 1) });
            Conferir("menu grid: first free spot in order, a tall tile spans two rows, a later small one fills the hole",
                spots[4] == (0, 1) && spots[5] == (1, 1) && spots[6] == (2, 1) && spots[7] == (2, 2) && spots[8] == (3, 1) && rows == 3,
                string.Join(" ", spots));
            var (wide, wideRows) = global::Ilha.Menu.Pack(new[] { (1, 1), (1, 1), (1, 1), (2, 1), (9, 9) });
            Conferir("menu grid: a wide tile that doesn't fit the row's end goes to the next row; oversized is clamped to 4x4",
                wide[3] == (0, 1) && wide[4] == (0, 2) && wideRows == 6, string.Join(" ", wide));
            Conferir("menu grid: empty", global::Ilha.Menu.Pack(Array.Empty<(int, int)>()).Rows == 0);

            // formulário por reflexão
            var secao = new JsonObject { ["ligado"] = false, ["vezes"] = 7, ["nome"] = "lido", ["lado"] = 2, ["desconhecida"] = "fica" };
            var inst = (ConfigDeTeste)Formulario.Instanciar(typeof(ConfigDeTeste), secao);
            Conferir("instanciar lê a seção e completa com o padrão", inst.Vezes == 7 && inst.Nome == "lido" && inst.Lado == Lado.Direita && inst.Itens.Count == 2);
            var campos = Formulario.Gerar(typeof(ConfigDeTeste), inst);
            var tipos = string.Join(",", campos.Select(c => c.Editor.GetType().Name));
            Conferir("gera um controle por campo editável, na ordem", tipos == "CheckBox,NumericUpDown,NumericUpDown,TextBox,ComboBox,TextBox", tipos);
            Conferir("dicionário e [JsonIgnore] ficam de fora", Formulario.NaoEditaveis(typeof(ConfigDeTeste)).SequenceEqual(new[] { "mapa" }) && campos.All(c => c.Propriedade.Name != "Escondido"));
            Conferir("rótulos: [DisplayName] e nome separado", campos[3].Rotulo == "Nome bonito" && campos[0].Rotulo == "Ligar" && Formulario.Humanizar("procurarSozinha") == "Procurar sozinha");
            var vezes = (NumericUpDown)campos[1].Editor;
            Conferir("[Range] vira o limite do campo numérico", vezes.Minimum == 1 && vezes.Maximum == 10 && vezes.Value == 7);
            Conferir("descrição vem do [Description]", campos[5].Descricao == "Um por linha");

            ((CheckBox)campos[0].Editor).Checked = false;
            vezes.Value = 9;
            ((NumericUpDown)campos[2].Editor).Value = 1.5m;
            ((TextBox)campos[3].Editor).Text = "novo";
            ((ComboBox)campos[4].Editor).SelectedItem = "Esquerda";
            ((TextBox)campos[5].Editor).Text = "um\r\n\r\n  dois  \r\ntrês";
            Formulario.Aplicar(campos, inst);
            Conferir("os controles voltam para a instância", !inst.Ligar && inst.Vezes == 9 && Math.Abs(inst.Fator - 1.5) < 1e-9 && inst.Nome == "novo"
                && inst.Lado == Lado.Esquerda && inst.Itens.SequenceEqual(new[] { "um", "dois", "três" }), string.Join("|", inst.Itens));
            var mesclada = Formulario.Mesclar(secao, typeof(ConfigDeTeste), inst, true);
            Conferir("mesclar mantém chave desconhecida, troca o ligado e grava os campos",
                mesclada["desconhecida"]?.GetValue<string>() == "fica" && mesclada["ligado"]?.GetValue<bool>() == true
                && mesclada["vezes"]?.GetValue<int>() == 9 && mesclada["mapa"]?["k"]?.GetValue<int>() == 1, mesclada.ToJsonString());
            Conferir("mesclar não mexe na seção original", secao["vezes"]?.GetValue<int>() == 7);
            var volta = (ConfigDeTeste)Formulario.Instanciar(typeof(ConfigDeTeste), mesclada);
            Conferir("a seção mesclada lê de volta igual", volta.Vezes == 9 && volta.Lado == Lado.Esquerda && volta.Itens.Count == 3);
            foreach (var c in campos) c.Editor.Dispose();

            // [Label]/[Help] win over [DisplayName]/[Description] and follow the language
            var labelPt = Formulario.Gerar(typeof(LanguageTestConfig), new LanguageTestConfig());
            Lang.Set("en");
            var labelEn = Formulario.Gerar(typeof(LanguageTestConfig), new LanguageTestConfig());
            Lang.Set("pt");
            Conferir("[Label] e [Help] ganham do [DisplayName] e do [Description] e seguem o idioma",
                labelPt[0].Rotulo == "Rótulo em português" && labelPt[0].Descricao == "Ajuda em português" && labelPt[0].Editor.Text == "Rótulo em português"
                && labelEn[0].Rotulo == "English label" && labelEn[0].Descricao == "English help" && labelEn[0].Editor.Text == "English label",
                $"{labelPt[0].Rotulo} | {labelPt[0].Descricao} | {labelEn[0].Rotulo} | {labelEn[0].Descricao}");
            foreach (var c in labelPt.Concat(labelEn)) c.Editor.Dispose();

            // seção que não lê não vira o padrão (salvar o padrão apagaria o que estava escrito à mão)
            var ruim = new JsonObject { ["ligado"] = false, ["vezes"] = "7", ["itens"] = new JsonArray("manual") };
            bool recusou = false;
            try { Formulario.Instanciar(typeof(ConfigDeTeste), ruim); } catch (InvalidOperationException) { recusou = true; }
            Conferir("seção ilegível dá erro em vez do padrão", recusou);
            var ruimMesclada = Formulario.Mesclar(ruim, null, null, true);
            ruim["ligado"] = true;
            Conferir("mesclar sem tipo mantém a seção e só troca o ligado", JsonNode.DeepEquals(ruimMesclada, ruim), ruimMesclada.ToJsonString());
            ruim["ligado"] = false;

            // só o que a pessoa mexeu vai por cima da instância
            var base0 = new ConfigDeTeste();
            var cs = Formulario.Gerar(typeof(ConfigDeTeste), base0);
            var iniciais = Formulario.Valores(cs);
            ((NumericUpDown)cs[1].Editor).Value = 8;
            var maisNova = new ConfigDeTeste { Nome = "do módulo", Vezes = 5 };
            Formulario.AplicarMudados(cs, iniciais, maisNova);
            Conferir("aplicar só os mudados: o mexido vai, o resto fica o da instância", maisNova.Vezes == 8 && maisNova.Nome == "do módulo", $"{maisNova.Vezes} {maisNova.Nome}");
            foreach (var c in cs) c.Editor.Dispose();

            // captura de atalho
            var (r1, a1) = CaixaDeAtalho.DeTecla(Keys.Space, true, true, false, false);
            Conferir("Ctrl + Alt + Espaço", r1 == CaixaDeAtalho.Resultado.Atalho && a1.Mods == (Nativo.MOD_CONTROL | Nativo.MOD_ALT) && a1.Tecla == 0x20, Teclas.Nome(a1));
            var (r2, a2) = CaixaDeAtalho.DeTecla(Keys.K, false, false, true, true);
            Conferir("Shift + Win + K", r2 == CaixaDeAtalho.Resultado.Atalho && a2.Mods == (Nativo.MOD_SHIFT | Nativo.MOD_WIN) && a2.Tecla == (int)Keys.K, Teclas.Nome(a2));
            Conferir("só o modificador espera a tecla", CaixaDeAtalho.DeTecla(Keys.ControlKey, true, false, false, false).Resultado == CaixaDeAtalho.Resultado.Ignorar);
            Conferir("letra sozinha é recusada", CaixaDeAtalho.DeTecla(Keys.A, false, false, false, false).Resultado == CaixaDeAtalho.Resultado.SemModificador);
            Conferir("F9 sozinho vale", CaixaDeAtalho.DeTecla(Keys.F9, false, false, false, false) is { Resultado: CaixaDeAtalho.Resultado.Atalho, Atalho.Tecla: (int)Keys.F9 });
            var (r3, a3) = CaixaDeAtalho.DeTecla(Keys.Back, false, false, false, false);
            Conferir("Backspace deixa sem atalho", r3 == CaixaDeAtalho.Resultado.Atalho && a3.Tecla == 0 && Teclas.Nome(a3) == "nenhum");
            Conferir("Esc desfaz", CaixaDeAtalho.DeTecla(Keys.Escape, false, false, false, false).Resultado == CaixaDeAtalho.Resultado.Cancelar);

            // Esc fecha a janela, menos na caixa de atalho (lá ele desfaz)
            using (var caixa = new CaixaDeAtalho())
            using (var campo = new TextBox())
                Conferir("Esc fecha a janela fora da caixa de atalho",
                    JanelaDeConfiguracoes.EscFecha(null) && JanelaDeConfiguracoes.EscFecha(campo) && !JanelaDeConfiguracoes.EscFecha(caixa));

            // prioridades
            var l = new List<string> { "a", "b", "c" };
            Conferir("subir e descer", JanelaDeConfiguracoes.Mover(l, 2, -1) && l.SequenceEqual(new[] { "a", "c", "b" }) && !JanelaDeConfiguracoes.Mover(l, 0, -1) && !JanelaDeConfiguracoes.Mover(l, 2, 1));

            // módulos de verdade, sem rodar o construtor deles
            var cfg = new Config();
            cfg.Modulos["atualizador"] = new JsonObject { ["ligado"] = false };
            var mods = JanelaDeConfiguracoes.ModulosDaConfig(cfg);
            var atu = mods.FirstOrDefault(m => m.id == "atualizador");
            Conferir("lista de módulos acha o atualizador com a config dele e o ligado da config",
                atu.tipoConfig?.Name == "ConfigAtualizador" && !atu.ligado && mods.Any(m => m.id == "configuracoes" && m.ligado), string.Join(",", mods.Select(m => m.id)));

            // a janela inteira, sem mostrar
            cfg.AtalhosModulos["timer"] = new Atalho { Mods = Nativo.MOD_CONTROL, Tecla = (int)Keys.T };
            cfg.Modulos["teste"] = new JsonObject { ["ligado"] = true, ["vezes"] = 4, ["outra"] = 1 };
            cfg.Modulos["ilegivel"] = new JsonObject { ["ligado"] = true, ["vezes"] = "7", ["itens"] = new JsonArray("manual") };
            var antes = JsonSerializer.Serialize(cfg, Config.Json);
            var lista = mods.Select(m => (m.id, m.nome, m.tipoConfig, m.ligado)).ToList();
            lista.Add(("teste", "Teste", typeof(ConfigDeTeste), true));
            lista.Add(("ilegivel", "Ilegível", typeof(ConfigDeTeste), true));
            var padroes = new Dictionary<string, Atalho> { ["calculadora"] = new() { Mods = Nativo.MOD_CONTROL | Nativo.MOD_ALT, Tecla = (int)Keys.C }, ["timer"] = new() { Mods = Nativo.MOD_ALT, Tecla = (int)Keys.T } };
            using (var j = JanelaDeConfiguracoes.ParaTeste(() => cfg, lista, padroes))
            {
                Conferir("janela monta sem mostrar", !j.Visible && j.ModulosNaTela.Count() == lista.Count);
                Conferir("aba de módulos tem os campos do de teste", j.ModulosNaTela.First(m => m.Id == "teste").Campos.Count == 6);
                Conferir("aba de atalhos junta os registrados e os da config", j.CaixasDeAtalho.Select(a => a.Nome).SequenceEqual(new[] { "calculadora", "timer" }, StringComparer.OrdinalIgnoreCase));
                var sem = j.Coletar();
                // o que a tela representa sem mexer em nada: a mesma config, mais as seções dos módulos que ainda não tinham
                var semNode = JsonSerializer.SerializeToNode(sem, Config.Json)!.AsObject();
                var antesNode = JsonNode.Parse(antes)!.AsObject();
                semNode.Remove("modulos");
                antesNode.Remove("modulos");
                Conferir("sem mexer, coletar devolve a mesma config", semNode.ToJsonString() == antesNode.ToJsonString());
                Conferir("sem mexer, o atalho trocado continua trocado e o padrão não é gravado",
                    sem.AtalhosModulos.ContainsKey("timer") && !sem.AtalhosModulos.ContainsKey("calculadora"));
                Conferir("sem mexer, a seção do teste mantém chave desconhecida", sem.Modulos["teste"]?["outra"]?.GetValue<int>() == 1 && sem.Modulos["teste"]?["vezes"]?.GetValue<int>() == 4);
                Conferir("sem mexer, o atualizador continua desligado", sem.Modulos["atualizador"]?["ligado"]?.GetValue<bool>() == false);
                Conferir("seção ilegível fica sem campos e sai do salvar intacta",
                    j.ModulosNaTela.First(m => m.Id == "ilegivel").Campos.Count == 0 && JsonNode.DeepEquals(sem.Modulos["ilegivel"], cfg.Modulos["ilegivel"]),
                    sem.Modulos["ilegivel"]?.ToJsonString() ?? "null");

                j.CampoTamanho.Value = 1.25m;
                j.CampoColada.Checked = true;
                Mover(j.ListaDePrioridades, 1);
                j.ListaDeJogos.Items.Add("jogo-novo.exe");
                j.ModulosNaTela.First(m => m.Id == "atualizador").Ligado.Checked = true;
                j.CaixasDeAtalho.First(a => a.Nome == "timer").Caixa.Valor = padroes["timer"];
                Conferir("opções novas abrem com o valor da config",
                    j.CampoAtrasoRecolher.Value == cfg.AtrasoRecolherMs && j.CampoBorda.Checked == cfg.BordaAdaptativa && j.CampoRelogio.Checked == cfg.Relogio
                    && j.CampoAtrasoRecolher.Minimum == 0 && j.CampoAtrasoRecolher.Maximum == 5000);
                j.CampoAtrasoRecolher.Value = 1200;
                j.CampoBorda.Checked = !cfg.BordaAdaptativa;
                j.CampoRelogio.Checked = !cfg.Relogio;
                Conferir("idioma abre no da config", j.CampoIdioma.SelectedIndex == JanelaDeConfiguracoes.LanguageIndex(cfg.Language) && cfg.Language == "auto");
                j.CampoIdioma.SelectedIndex = 2;
                var com = j.Coletar();
                Conferir("mexendo, coletar leva o idioma", com.Language == "en" && cfg.Language == "auto", com.Language);
                Conferir("mexendo, coletar leva o atraso de recolher, a borda e a hora",
                    com.AtrasoRecolherMs == 1200 && com.BordaAdaptativa != cfg.BordaAdaptativa && com.Relogio != cfg.Relogio,
                    $"{com.AtrasoRecolherMs} {com.BordaAdaptativa} {com.Relogio}");
                Conferir("mexendo, coletar leva tamanho, colada, prioridade, jogo e módulo",
                    Math.Abs(com.Tamanho - 1.25f) < 1e-5 && com.Colada && com.Prioridades[0] == cfg.Prioridades[1]
                    && com.JogosCompetitivos.Last() == "jogo-novo.exe" && com.Modulos["atualizador"]?["ligado"]?.GetValue<bool>() == true);
                Conferir("atalho de volta ao padrão sai da config", !com.AtalhosModulos.ContainsKey("timer"));
                Conferir("coletar não mexe na config que vale", cfg.Tamanho == 1f && !cfg.Colada && cfg.AtalhosModulos.ContainsKey("timer"));

                // o módulo grava a seção dele com a tela aberta (estado do timer, por exemplo): salvar não volta ela
                cfg.Modulos["teste"] = new JsonObject { ["ligado"] = true, ["vezes"] = 4, ["outra"] = 1, ["nome"] = "do módulo", ["mapa"] = new JsonObject { ["k"] = 5 } };
                ((NumericUpDown)j.ModulosNaTela.First(m => m.Id == "teste").Campos[2].Editor).Value = 2m;
                var depois = j.Coletar().Modulos["teste"];
                Conferir("salvar parte da seção de agora e só põe por cima o que a tela mudou",
                    depois?["mapa"]?["k"]?.GetValue<int>() == 5 && depois?["nome"]?.GetValue<string>() == "do módulo"
                    && depois?["fator"]?.GetValue<double>() == 2.0 && depois?["vezes"]?.GetValue<int>() == 4 && depois?["outra"]?.GetValue<int>() == 1,
                    depois?.ToJsonString() ?? "null");
            }

            // a janela fica aberta depois do salvar: mudar, salvar, desfazer e salvar de novo também grava a volta
            var cfgSalva = new Config();
            cfgSalva.Modulos["teste"] = new JsonObject { ["ligado"] = true, ["vezes"] = 3 };
            cfgSalva.Modulos["lista"] = new JsonArray("manual");
            var listaSalvar = new List<(string id, string nome, Type? tipoConfig, bool ligado)>
            {
                ("teste", "Teste", typeof(ConfigDeTeste), true),
                ("lista", "Lista", typeof(ConfigDeTeste), true),
            };
            using (var j = JanelaDeConfiguracoes.ParaTeste(() => cfgSalva, listaSalvar, null, c => cfgSalva = c))
            {
                var vz = (NumericUpDown)j.ModulosNaTela.First(m => m.Id == "teste").Campos[1].Editor;
                vz.Value = 5;
                j.Salvar();
                var primeiro = cfgSalva.Modulos["teste"]?["vezes"]?.GetValue<int>();
                vz.Value = 3;
                j.Salvar();
                var segundo = cfgSalva.Modulos["teste"]?["vezes"]?.GetValue<int>();
                Conferir("salvar, desfazer e salvar de novo grava a volta", primeiro == 5 && segundo == 3, $"{primeiro} {segundo}");

                var lst = j.ModulosNaTela.First(m => m.Id == "lista");
                Conferir("seção que não é objeto fica sem campos, com o ligado travado, e sai do salvar intacta",
                    lst.Campos.Count == 0 && !lst.Ligado.Enabled && JsonNode.DeepEquals(cfgSalva.Modulos["lista"], new JsonArray("manual")),
                    cfgSalva.Modulos["lista"]?.ToJsonString() ?? "null");
            }

            // in English: window title, module names (ModuloAttribute.English) and shortcut names follow the language
            Lang.Set("en");
            var modsEn = JanelaDeConfiguracoes.ModulosDaConfig(new Config());
            // the Menu section: a box per entry; an entry hidden before but not listed now (module off) stays hidden
            var cfgMenu = new Config { MenuEscondido = new List<string> { "mascote", "velho" } };
            using (var j = JanelaDeConfiguracoes.ParaTeste(() => cfgMenu, lista, null, null, new[] { ("conversas", "Conversas"), ("mascote", "Mascote") }))
            {
                var caixas = j.MenuNaTela.ToList();
                bool marcadas = caixas.Count == 2 && caixas[0].Mostrar.Checked && !caixas[1].Mostrar.Checked;
                caixas[0].Mostrar.Checked = false;
                caixas[1].Mostrar.Checked = true;
                var escondidos = j.Coletar().MenuEscondido;
                Conferir("menu section: boxes from the config, hiding and showing, unlisted hidden ids kept",
                    marcadas && escondidos.OrderBy(x => x).SequenceEqual(new[] { "conversas", "velho" }), string.Join(",", escondidos));
            }
            var menuTeste = new MenuDaIlha();
            menuTeste.Adicionar(new ItemDoMenu { Id = "a", Nome = "A", Abrir = () => { } });
            menuTeste.Adicionar(new ItemDoMenu { Id = "b", Nome = "B", Abrir = () => { } });
            menuTeste.Esconder(new[] { "B" });
            Conferir("hidden menu entries leave the menu only (case does not matter)", menuTeste.Visiveis.Select(i => i.Id).SequenceEqual(new[] { "a" }) && menuTeste.Itens.Count == 2);

            using (var j = JanelaDeConfiguracoes.ParaTeste(() => new Config(), modsEn, null))
                Conferir("em inglês, a janela e os nomes dos módulos e dos atalhos saem em inglês",
                    j.Text == "Ilha settings" && modsEn.Any(m => m.id == "configuracoes" && m.nome == "Settings window")
                    && JanelaDeConfiguracoes.NomeDoAtalho("calculadora") == "Calculator" && Teclas.Nome(new Atalho { Mods = 0, Tecla = 0 }) == "none",
                    j.Text);
            Lang.Set("pt");
            Conferir("em português, os mesmos nomes voltam",
                JanelaDeConfiguracoes.ModulosDaConfig(new Config()).Any(m => m.id == "configuracoes" && m.nome == "Tela de configurações")
                && JanelaDeConfiguracoes.NomeDoAtalho("calculadora") == "Calculadora" && JanelaDeConfiguracoes.LanguageIndex("EN") == 2 && JanelaDeConfiguracoes.LanguageIndex("xx") == 0);

            // opcional, para o desenvolvimento: ILHA_FOTO_CONFIG=<pasta> guarda uma foto de cada aba (sem mostrar a janela)
            var pastaFotos = Environment.GetEnvironmentVariable("ILHA_FOTO_CONFIG");
            if (!string.IsNullOrEmpty(pastaFotos))
            {
                using var j = JanelaDeConfiguracoes.ParaTeste(() => cfg, lista, padroes);
                var fotos = j.Fotografar(pastaFotos);
                r.AppendLine($"info    {fotos.Count} fotos das abas em {pastaFotos}");
            }
        }
        catch (Exception e) { Conferir("testes da tela", false, e.ToString()); }
        finally { Lang.Set(previousLanguage); }
        return r.ToString().TrimEnd();
    }

    private static void Mover(List<string> l, int i) => JanelaDeConfiguracoes.Mover(l, i, -1);
}
