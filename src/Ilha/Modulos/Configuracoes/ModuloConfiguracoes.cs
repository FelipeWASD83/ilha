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
[Modulo("configuracoes", "Tela de configurações")]
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
    public static IReadOnlyDictionary<string, Atalho>? AtalhosRegistrados(Atalhos atalhos)
    {
        try
        {
            var campo = typeof(Atalhos).GetField("_pedidos", BindingFlags.NonPublic | BindingFlags.Instance);
            if (campo?.GetValue(atalhos) is not IDictionary pedidos) return null;
            var r = new Dictionary<string, Atalho>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry kv in pedidos)
            {
                if (kv.Key is not string nome || kv.Value == null) continue;
                var item1 = kv.Value.GetType().GetField("Item1")?.GetValue(kv.Value);
                if (item1 is Atalho a) r[nome] = a;
            }
            return r;
        }
        catch { return null; }
    }

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

    public override string Autoteste()
    {
        var r = new StringBuilder();
        void Conferir(string nome, bool ok, string detalhe = "") =>
            r.AppendLine((ok ? "ok      " : "FALHOU  ") + nome + (detalhe.Length > 0 ? $" ({detalhe})" : ""));

        try
        {
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
            var antes = JsonSerializer.Serialize(cfg, Config.Json);
            var lista = mods.Select(m => (m.id, m.nome, m.tipoConfig, m.ligado)).ToList();
            lista.Add(("teste", "Teste", typeof(ConfigDeTeste), true));
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

                j.CampoTamanho.Value = 1.25m;
                j.CampoColada.Checked = true;
                Mover(j.ListaDePrioridades, 1);
                j.ListaDeJogos.Items.Add("jogo-novo.exe");
                j.ModulosNaTela.First(m => m.Id == "atualizador").Ligado.Checked = true;
                j.CaixasDeAtalho.First(a => a.Nome == "timer").Caixa.Valor = padroes["timer"];
                var com = j.Coletar();
                Conferir("mexendo, coletar leva tamanho, colada, prioridade, jogo e módulo",
                    Math.Abs(com.Tamanho - 1.25f) < 1e-5 && com.Colada && com.Prioridades[0] == cfg.Prioridades[1]
                    && com.JogosCompetitivos.Last() == "jogo-novo.exe" && com.Modulos["atualizador"]?["ligado"]?.GetValue<bool>() == true);
                Conferir("atalho de volta ao padrão sai da config", !com.AtalhosModulos.ContainsKey("timer"));
                Conferir("coletar não mexe na config que vale", cfg.Tamanho == 1f && !cfg.Colada && cfg.AtalhosModulos.ContainsKey("timer"));
            }

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
        return r.ToString().TrimEnd();
    }

    private static void Mover(List<string> l, int i) => JanelaDeConfiguracoes.Mover(l, i, -1);
}
