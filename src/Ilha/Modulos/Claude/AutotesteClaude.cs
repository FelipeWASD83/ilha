using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ilha.Claude;

/// <summary>`Ilha.exe --testar claude`: a lógica pura do módulo, o protocolo do pipe de ponta a ponta e sondas só de leitura.</summary>
internal static class AutotesteClaude
{
    private static readonly StringBuilder R = new();

    private static void Ok(bool cond, string o)
    {
        R.AppendLine((cond ? "ok      " : "FALHOU  ") + o);
    }

    private static void Info(string o) => R.AppendLine("ok      " + o);

    public static string Rodar()
    {
        R.Clear();
        Bloco("parser de eventos", Parser);
        Bloco("leitor da fila", Leitor);
        Bloco("vigia da fila (arquivo de verdade)", VigiaDeVerdade);
        Bloco("texto neutro", TextoNeutro);
        Bloco("máquina de estados", Maquina);
        Bloco("permissão na máquina", PermissaoNaMaquina);
        Bloco("arquivo de sessão", ArquivoSessao);
        Bloco("botão do modelo", Botao);
        Bloco("protocolo do pipe", Protocolo);
        Bloco("pipe de ponta a ponta", PipeDePontaAPonta);
        Bloco("hook em python", HookPython);
        Bloco("sonda: arquivos de sessão", SondaSessoes);
        Bloco("sonda: app do Claude", SondaApp);
        return R.ToString().TrimEnd();
    }

    private static void Bloco(string nome, Action a)
    {
        try { a(); }
        catch (Exception e) { R.AppendLine($"FALHOU  {nome}: exceção {e.GetType().Name}: {e.Message}"); }
    }

    // ------------------------------------------------------------------ lógica pura

    private const string L1 = "{\"t\": 1793550000.125, \"tipo\": \"ouvindo\", \"sessao\": \"s-1\", \"projeto\": \"ilha\", \"ligada\": false, \"limpa\": true, \"facetas\": [], \"volume\": null, \"emoji\": null, \"teto\": false}";
    private const string L2 = "{\"t\": 1793550003.5, \"tipo\": \"digitando\", \"sessao\": \"s-1\", \"projeto\": \"ilha\", \"ligada\": false, \"limpa\": true, \"detalhe\": \"Editando Ilha.cs\"}";
    private const string L3 = "{\"t\": 1793550010.0, \"tipo\": \"rodando\", \"sessao\": \"s-1\", \"projeto\": \"ilha\", \"ligada\": true, \"limpa\": false, \"detalhe\": \"texto de outra voz que nunca aparece\"}";
    private const string L4 = "{\"t\": 1793550020.0, \"tipo\": \"terminou\", \"sessao\": \"s-1\", \"projeto\": \"ilha\", \"ligada\": false, \"limpa\": true, \"pergunta\": false, \"fala\": \"resposta inteira que nunca aparece\", \"duracao\": 20}";
    private const string L5 = "{\"t\": 1793550030.0, \"tipo\": \"fala\", \"sessao\": \"s-2\", \"projeto\": \"açaí\", \"ligada\": true, \"texto\": \"segredo\"}";

    private static void Parser()
    {
        var e = Evento.Ler(L2)!;
        Ok(e != null && e.T == 1793550003.5 && e.Tipo == "digitando" && e.Sessao == "s-1" && e.Projeto == "ilha" && e.Detalhe == "Editando Ilha.cs", "linha de ferramenta lida com hora, tipo, sessão, projeto e detalhe");
        var t = Evento.Ler(L4)!;
        Ok(t.Duracao == 20 && !t.Pergunta && !t.Ligada, "terminou com duração e sem pergunta");
        var f = Evento.Ler(L5)!;
        Ok(f.Projeto == "açaí" && f.Ligada && f.Detalhe == "", "acento no projeto e o texto da fala nunca é guardado");
        Ok(Evento.Ler("") == null && Evento.Ler("{quebrado") == null && Evento.Ler("[1,2]") == null, "linha vazia, JSON quebrado e não objeto viram null");
        Ok(Evento.Ler("{\"tipo\":\"ouvindo\"}") == null && Evento.Ler("{\"t\":5}") == null, "sem hora ou sem tipo não é evento");
        Ok(Evento.Ler("{\"t\":7,\"tipo\":\"notificacao\",\"sessao\":\"x\",\"aviso\":\"permission_prompt\",\"titulo\":\"t\",\"texto\":\"u\"}")!.Aviso == "permission_prompt", "aviso da notificação");
    }

    private static MemoryStream Fluxo(string texto) { var m = new MemoryStream(); var b = Encoding.UTF8.GetBytes(texto); m.Write(b); return m; }

    private static void Leitor()
    {
        var l = new LeitorDeFila();
        var m = Fluxo(L1 + "\n" + L2 + "\n" + L3.Substring(0, 30));
        var a = l.Ler(m);
        Ok(a.Count == 2 && a[0].Tipo == "ouvindo" && a[1].Tipo == "digitando", "lê as linhas inteiras e deixa a linha pela metade");
        long pos = l.Posicao;
        m.Write(Encoding.UTF8.GetBytes(L3.Substring(30) + "\n"));
        var b = l.Ler(m);
        Ok(b.Count == 1 && b[0].Tipo == "rodando" && l.Posicao > pos, "completa a linha que estava pela metade");
        Ok(l.Ler(m).Count == 0, "nada novo, nada lido");

        // o hook corta o começo: o arquivo encolhe para as últimas linhas e ganha uma nova
        var m2 = Fluxo(L3 + "\n" + L4 + "\n");
        var c = l.Ler(m2);
        Ok(c.Count == 1 && c[0].Tipo == "terminou", "arquivo encolheu: relê do começo e pula o que já tinha saído (pela hora)");

        // trocado pelo os.replace com tamanho maior que a posição: o vigia pede para recomeçar
        var l2 = new LeitorDeFila();
        l2.Ler(Fluxo(L1 + "\n"));
        l2.Recomecar();
        var d = l2.Ler(Fluxo(L1 + "\n" + L2 + "\n" + L4 + "\n" + L5 + "\n"));
        Ok(d.Count == 3 && d[0].Tipo == "digitando", "arquivo trocado: relê e só entrega o que é novo");

        var l3 = new LeitorDeFila();
        var e = l3.Ler(Fluxo(L1 + "\n" + L1 + "\n" + "lixo\n" + L2 + "\r\n"));
        Ok(e.Count == 2, "linha repetida (mesma hora) sai uma vez só; lixo e \\r ignorados");
    }

    private static void VigiaDeVerdade()
    {
        var pasta = Path.Combine(Path.GetTempPath(), "ilha-teste-fila-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(pasta);
        var arq = Path.Combine(pasta, "eventos.jsonl");
        try
        {
            File.WriteAllText(arq, L1 + "\n");
            var recebidos = new List<(string Tipo, bool Primeira)>();
            using var sinal = new SemaphoreSlim(0);
            using (var v = new VigiaDaFila(arq, (evs, primeira) => { lock (recebidos) foreach (var e in evs) recebidos.Add((e.Tipo, primeira)); sinal.Release(); }))
            {
                Ok(sinal.Wait(3000), "leitura inicial chega");
                lock (recebidos) Ok(recebidos.Count == 1 && recebidos[0].Primeira, "a leitura inicial é marcada como histórico");
                // como o hook: abre em append, escreve e fecha
                File.AppendAllText(arq, L2 + "\n");
                Ok(sinal.Wait(3000), "append do hook chega pelo FileSystemWatcher");
                File.AppendAllText(arq, L3 + "\n");
                sinal.Wait(3000);
                lock (recebidos) Ok(recebidos.Count == 3 && !recebidos[1].Primeira && recebidos[2].Tipo == "rodando", "cada append chega uma vez, em ordem");
                // como o os.replace do corte: grava num temporário e troca por cima (falha se a ilha segurar o arquivo)
                var tmp = arq + ".tmp";
                File.WriteAllText(tmp, L3 + "\n" + L4 + "\n");
                bool trocou = false;
                for (int i = 0; i < 10 && !trocou; i++)
                {
                    try { File.Move(tmp, arq, overwrite: true); trocou = true; }
                    catch (IOException) { Thread.Sleep(40); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(40); }
                }
                Ok(trocou, "o arquivo pode ser trocado por cima (a ilha não segura o arquivo aberto)");
                var fim = Environment.TickCount64 + 3000;
                while (Environment.TickCount64 < fim) { lock (recebidos) if (recebidos.Count >= 4) break; Thread.Sleep(30); }
                Thread.Sleep(200);
                lock (recebidos) Ok(recebidos.Count == 4 && recebidos[3].Tipo == "terminou", "depois da troca: só o evento novo, sem repetir o que já tinha saído");
            }
        }
        finally
        {
            try { Directory.Delete(pasta, true); } catch (IOException) { }
        }
    }

    private static Evento Ev(double t, string tipo, string sessao = "s", string detalhe = "", bool ligada = false, string aviso = "", double? dur = null, bool pergunta = false) =>
        new() { T = t, Tipo = tipo, Sessao = sessao, Projeto = "proj-" + sessao, Detalhe = detalhe, Ligada = ligada, Aviso = aviso, Duracao = dur, Pergunta = pergunta };

    private static void TextoNeutro()
    {
        Ok(Neutro.DoEvento(Ev(1, "ouvindo")) == "Pensando", "ouvindo vira Pensando");
        Ok(Neutro.DoEvento(Ev(1, "lendo", detalhe: "Lendo Ilha.cs", ligada: true)) == "Lendo Ilha.cs", "ligada: ler com nome de arquivo continua");
        Ok(Neutro.DoEvento(Ev(1, "digitando", detalhe: "Editando Demo.cs", ligada: true)) == "Editando Demo.cs", "ligada: editar com nome de arquivo continua");
        Ok(Neutro.DoEvento(Ev(1, "digitando", detalhe: "Editando um texto qualquer dela", ligada: true)) == "Editando arquivos", "ligada: detalhe que não é só um arquivo vira genérico");
        Ok(Neutro.DoEvento(Ev(1, "rodando", detalhe: "descrição na voz dela", ligada: true)) == "Rodando um comando", "ligada: comando sempre vira Rodando um comando");
        Ok(Neutro.DoEvento(Ev(1, "rodando", detalhe: "Compila o projeto", ligada: false)) == "Compila o projeto", "sem persona: a descrição do comando aparece");
        Ok(Neutro.DoEvento(Ev(1, "rodando", ligada: false)) == "Rodando um comando", "sem detalhe: Rodando um comando");
        Ok(Neutro.DoEvento(Ev(1, "lendo", detalhe: "Procurando segredo.*", ligada: true)) == "Procurando nos arquivos", "ligada: o padrão da busca não aparece");
        Ok(Neutro.DoEvento(Ev(1, "fala", detalhe: "x", ligada: false)) == "" && Neutro.DoEvento(Ev(1, "balao", detalhe: "x")) == "", "fala e balão nunca viram texto");
        Ok(Neutro.Duracao(45) == "45 s" && Neutro.Duracao(200) == "3 min 20 s" && Neutro.Duracao(120) == "2 min" && Neutro.Duracao(3900) == "1 h 05 min", "duração escrita");
    }

    private static void Maquina()
    {
        var m = new Sessoes();
        Ok(m.Aplicar(Ev(100, "ouvindo"), false).Terminaram.Count == 0, "ouvindo não termina nada");
        var s = m.Pegar("s")!;
        Ok(s.Fase == Fase.Pensando && s.Inicio == 100, "ouvindo: pensando, com a hora do pedido");
        m.Aplicar(Ev(103, "digitando", detalhe: "Editando a.cs"), false);
        Ok(s.Fase == Fase.Trabalhando && s.Texto == "Editando a.cs", "digitando: trabalhando, com o arquivo");
        m.Tique(109.5);
        Ok(s.Fase is Fase.Pensando, "6 s sem evento depois de editar: voltou a pensar");
        m.Aplicar(Ev(110, "rodando", detalhe: "Build"), false);
        m.Tique(200);
        Ok(s.Fase == Fase.Trabalhando, "comando longo continua rodando sem evento");
        var ef = m.Aplicar(Ev(220, "terminou"), false);
        Ok(ef.Terminaram.Count == 1 && Math.Abs(ef.Terminaram[0].Duracao - 120) < 0.01 && s.Fase == Fase.Terminou, "terminou: alerta com a duração calculada pelo pedido (120 s)");
        Ok(m.Principal() == null && !Sessoes.Visivel(s), "terminou: sai da ilha na hora (o fim fica só no alerta)");
        Ok(m.ProximoPrazo() is double pz && Math.Abs(pz - (220 + Sessoes.GUARDA_DO_FIM)) < 0.01, "o próximo prazo é o fim da guarda da fase terminou");
        m.Tique(220 + Sessoes.GUARDA_DO_FIM + 1);
        Ok(m.Pegar("s") == null, "depois da guarda, sem arquivo de sessão: esquecida");

        // a duração que vem no evento manda
        var m2 = new Sessoes();
        m2.Aplicar(Ev(10, "ouvindo"), false);
        Ok(m2.Aplicar(Ev(50, "terminou", dur: 33), false).Terminaram[0].Duracao == 33, "duração do evento tem prioridade");
        // histórico (silencioso): sem alerta
        var m3 = new Sessoes();
        m3.Aplicar(Ev(10, "ouvindo"), true);
        Ok(m3.Aplicar(Ev(20, "terminou"), true).Terminaram.Count == 0, "leitura inicial não alerta");
        // terminou sem pedido em andamento (sessão vista no meio): sem alerta
        var m4 = new Sessoes();
        Ok(m4.Aplicar(Ev(20, "terminou"), false).Terminaram.Count == 0, "terminou sem começo conhecido não alerta");

        // Esc: o status vira idle e o Stop não roda
        var m5 = new Sessoes();
        m5.Aplicar(Ev(100, "ouvindo"), false);
        m5.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "busy", StatusEm = 100.5, Nome = "Minha sessão" }, 101);
        m5.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "idle", StatusEm = 130 }, 130);
        m5.Tique(131);
        Ok(m5.Pegar("s")!.Fase == Fase.Pensando, "idle há 1 s: ainda espera o terminou");
        m5.Tique(133.1);
        Ok(m5.Pegar("s")!.Fase == Fase.Parada && m5.Pegar("s")!.Nome == "Minha sessão", "idle há 3 s sem terminou: parou (Esc ou erro), nome da sessão guardado");
        // terminou chegando logo depois do idle: termina normal
        var m6 = new Sessoes();
        m6.Aplicar(Ev(100, "ouvindo"), false);
        m6.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "idle", StatusEm = 140 }, 140);
        Ok(m6.Aplicar(Ev(140.5, "terminou"), false).Terminaram.Count == 1 && m6.Pegar("s")!.Fase == Fase.Terminou, "idle seguido de terminou: terminou");
        // status busy velho (de antes do terminou) não reabre
        m6.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "busy", StatusEm = 120 }, 141);
        Ok(m6.Pegar("s")!.Fase == Fase.Terminou, "busy anterior ao terminou não reabre a sessão");
        m6.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "busy", StatusEm = 150 }, 150);
        Ok(m6.Pegar("s")!.Fase == Fase.Pensando && m6.Pegar("s")!.Inicio == 150, "busy novo sem evento: começou a trabalhar");
        // waiting
        m6.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "waiting", StatusEm = 160, EsperandoPor = "permission prompt" }, 160);
        Ok(m6.Pegar("s")!.Fase == Fase.Esperando && m6.Pegar("s")!.Texto == "Pedindo permissão", "waiting de permissão: pedindo permissão");

        // várias sessões: a que pede permissão primeiro, depois a mais recente em andamento
        var m7 = new Sessoes();
        m7.Aplicar(Ev(100, "ouvindo", "a"), false);
        m7.Aplicar(Ev(105, "ouvindo", "b"), false);
        Ok(m7.Principal()!.Id == "b", "a mais recente em andamento aparece");
        m7.Aplicar(Ev(107, "terminou", "b"), false);
        Ok(m7.Principal()!.Id == "a" && m7.Ordenadas().Count == 1, "a que terminou some; a em andamento fica");
        m7.Aplicar(Ev(109, "notificacao", "b", aviso: "permission_prompt"), false);
        m7.PedidoChegou(new PedidoPermissao { Sessao = "b", Ferramenta = "Bash", Chegou = 110 });
        Ok(m7.Principal()!.Id == "b" && m7.Ordenadas().Count == 2, "a que pede permissão vem primeiro");
        Ok(m7.Aplicar(Ev(120, "fim", "a"), false).Mudou && m7.Pegar("a") == null, "fim: a sessão sai");
        // sessão esquecida (morreu sem fim e sem arquivo)
        var m8 = new Sessoes();
        m8.Aplicar(Ev(100, "rodando"), false);
        m8.Tique(100 + Sessoes.ESQUECIDO + 1);
        Ok(m8.Principal() == null, "trabalhando 15 min sem sinal nenhum: esquecida");
        // privacidade dentro da máquina
        var m9 = new Sessoes();
        m9.Aplicar(Evento.Ler(L3)!, false);
        m9.Aplicar(Evento.Ler(L5)!, false);
        Ok(m9.Pegar("s-1")!.Texto == "Rodando um comando" && m9.Pegar("s-2")!.Texto == "", "fala e detalhe de sessão ligada não viram texto");
        // notificação de idle depois de terminar não muda nada
        var m10 = new Sessoes();
        m10.Aplicar(Ev(1, "ouvindo"), false);
        m10.Aplicar(Ev(2, "terminou", pergunta: true), false);
        m10.Aplicar(Ev(62, "notificacao", aviso: "idle_prompt"), false);
        Ok(m10.Pegar("s")!.Fase == Fase.Terminou && m10.Pegar("s")!.Texto == "Fez uma pergunta", "terminou com pergunta; o aviso de idle não muda");
        m10.Tique(2 + Sessoes.GUARDA_DO_FIM + 1);
        m10.Aplicar(Ev(200, "notificacao", aviso: "idle_prompt"), false);
        Ok(m10.Principal() == null, "aviso de idle depois da guarda do fim: continua fora da ilha");

        // só trabalhando ou pedindo atenção aparece
        var m11 = new Sessoes();
        m11.Aplicar(Ev(1, "ouvindo"), false);
        Ok(m11.Principal() != null, "pensando: aparece");
        m11.Aplicar(Ev(2, "notificacao", aviso: "elicitation_dialog"), false);
        Ok(m11.Principal()?.Fase == Fase.Esperando, "precisa de você: aparece");
        m11.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "waiting", StatusEm = 3 }, 3);
        Ok(m11.Principal()?.Texto == "Esperando você", "waiting no arquivo (pergunta): aparece esperando");
        m11.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "idle", StatusEm = 10 }, 10);
        m11.Tique(14);
        Ok(m11.Pegar("s")!.Fase == Fase.Parada && m11.Principal() == null, "parou (Esc ou erro): sai da ilha na hora");
        var m12 = new Sessoes();
        m12.Aplicar(Ev(1, "ouvindo"), false);
        m12.Aplicar(Ev(5, "notificacao", aviso: "idle_prompt"), false);
        Ok(m12.Principal()?.Texto == "Esperando você", "aviso de idle com o Stop perdido (ainda trabalhando): esperando você");
        var semConstrutor = (AtividadeClaude)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AtividadeClaude));
        Ok(!semConstrutor.AnunciaChegada, "a atividade não abre o cartão sozinha ao ligar");

        // o relógio nunca fica preso: depois de cada Tique no prazo, o próximo prazo é futuro (ou não existe)
        var mx = new Sessoes();
        mx.Aplicar(Ev(10, "ouvindo", "a"), false);
        mx.Aplicar(Ev(11, "digitando", "a", "Editando a.cs"), false);
        mx.AplicarArquivo(new ArquivoDeSessao { SessaoId = "a", Status = "idle", StatusEm = 12 }, 12);
        mx.Aplicar(Ev(10, "ouvindo", "b"), false);
        mx.PedidoChegou(new PedidoPermissao { Sessao = "b", Ferramenta = "Bash", Chegou = 13 });
        mx.AplicarArquivo(new ArquivoDeSessao { SessaoId = "b", Status = "idle", StatusEm = 14 }, 14);
        mx.Aplicar(Ev(10, "rodando", "c"), false);
        mx.Aplicar(Ev(15, "terminou", "d"), false);
        bool preso = false;
        int passos = 0;
        for (double? px = mx.ProximoPrazo(); px != null && passos < 50; px = mx.ProximoPrazo(), passos++)
        {
            mx.Tique(px.Value);
            if (mx.ProximoPrazo() is double prox && prox <= px.Value) { preso = true; break; }
        }
        Ok(!preso && passos < 50, $"relógio sem laço: {passos} prazos até assentar");
    }

    private static void PermissaoNaMaquina()
    {
        var m = new Sessoes();
        m.Aplicar(Ev(100, "ouvindo"), false);
        // o PreToolUse escreve "rodando" antes do hook de permissão; ele pode ser lido depois que o pedido chega
        var p = new PedidoPermissao { Sessao = "s", Ferramenta = "Bash", Resumo = "git push", Chegou = 105 };
        m.PedidoChegou(p);
        var ef = m.Aplicar(Ev(104.9, "rodando", detalhe: "Push"), false);
        Ok(ef.Encerrados.Count == 0 && m.Pegar("s")!.Pedidos.Count == 1 && m.Pegar("s")!.Fase == Fase.Esperando, "evento anterior ao pedido não fecha o cartão nem tira a sessão da espera");
        ef = m.Aplicar(Ev(111, "notificacao", aviso: "permission_prompt"), false);
        Ok(ef.Encerrados.Count == 0 && ef.PediuPermissaoSemPonte.Count == 0, "a notificação de permissão não fecha e não duplica o alerta");
        m.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "waiting", StatusEm = 105.2 }, 106);
        ef = m.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "busy", StatusEm = 120 }, 120);
        Ok(ef.Encerrados.Count == 1 && ef.Encerrados[0] == p && m.Pegar("s")!.Pedidos.Count == 0, "saiu de waiting: o pedido foi respondido no app e some");

        var m2 = new Sessoes();
        var p2 = new PedidoPermissao { Sessao = "s", Ferramenta = "Write", Chegou = 200 };
        m2.PedidoChegou(p2);
        ef = m2.Aplicar(Ev(205, "lendo", detalhe: "Lendo x.cs"), false);
        Ok(ef.Encerrados.Count == 1, "evento novo da sessão depois do pedido: some");
        var p3 = new PedidoPermissao { Sessao = "s", Ferramenta = "Write", Chegou = 300 };
        m2.PedidoChegou(p3);
        ef = m2.AplicarArquivo(new ArquivoDeSessao { SessaoId = "s", Status = "busy", StatusEm = 301 }, 301);
        Ok(ef.Encerrados.Count == 0, "busy sem ter passado por waiting não fecha o pedido");
        Ok(m2.PedidoSaiu(p3) && !m2.PedidoSaiu(p3), "pedido sai uma vez só");
        var m3 = new Sessoes();
        m3.Aplicar(Ev(1, "ouvindo"), false);
        Ok(m3.Aplicar(Ev(7, "notificacao", aviso: "permission_prompt"), false).PediuPermissaoSemPonte.Count == 1, "sem o hook da ilha: a notificação vira alerta sem botões");

        // dois pedidos da mesma sessão: quando o primeiro sai, o cartão vê outro número e refaz os botões
        var m4 = new Sessoes();
        var pa = new PedidoPermissao { Sessao = "s", Ferramenta = "Bash", Chegou = 400 };
        var pb = new PedidoPermissao { Sessao = "s", Ferramenta = "Bash", Chegou = 401 };
        m4.PedidoChegou(pa);
        m4.PedidoChegou(pb);
        long antes = m4.Pegar("s")!.Pedidos.FirstOrDefault()?.Numero ?? 0;
        m4.PedidoSaiu(pa);
        long depois = m4.Pegar("s")!.Pedidos.FirstOrDefault()?.Numero ?? 0;
        Ok(antes == pa.Numero && depois == pb.Numero && antes != depois && depois != 0, "o primeiro pedido saiu: a identidade do pedido mostrado muda");

        Ok(!PedidoPermissao.PassouCarencia(1000, 1000) && !PedidoPermissao.PassouCarencia(1000, 1000 + PedidoPermissao.CarenciaMs - 1)
            && PedidoPermissao.PassouCarencia(1000, 1000 + PedidoPermissao.CarenciaMs) && PedidoPermissao.PassouCarencia(1000, 60_000),
            $"Permitir surdo nos primeiros {PedidoPermissao.CarenciaMs} ms depois de aparecer, livre depois");
    }

    private static void ArquivoSessao()
    {
        var a = ArquivoDeSessao.Ler("{\"pid\":15384,\"sessionId\":\"e3\",\"cwd\":\"C:\\\\x\\\\ilha\",\"name\":\"Nome da sessão\",\"status\":\"waiting\",\"waitingFor\":\"permission prompt\",\"statusUpdatedAt\":1793545740461}");
        Ok(a != null && a.Pid == 15384 && a.SessaoId == "e3" && a.Status == "waiting" && a.Nome == "Nome da sessão" && Math.Abs(a.StatusEm - 1793545740.461) < 0.001, "lê pid, sessão, status, nome e a hora em segundos");
        Ok(ArquivoDeSessao.Ler("{\"pid\":1}") == null && ArquivoDeSessao.Ler("nada") == null, "sem sessionId ou quebrado: ignorado");
        var m = new Sessoes();
        m.AplicarArquivo(a!, 1793545741);
        Ok(m.Pegar("e3")!.Projeto == "ilha", "projeto tirado do cwd quando a fila não disse");
    }

    private static void Botao()
    {
        var (mo, es, nv, ul) = BotaoDoModelo.Ler("Modelo: Opus 5.5 Alto · Ultracode");
        Ok(mo == "Opus 5.5" && es == "Ultracode" && nv == 5 && ul, "Opus 5.5 Alto · Ultracode: nível 5");
        (mo, es, nv, ul) = BotaoDoModelo.Ler("Modelo: Sonnet 5.5 Médio");
        Ok(mo == "Sonnet 5.5" && es == "Médio" && nv == 1 && !ul, "Sonnet 5.5 Médio: nível 1");
        (mo, es, nv, ul) = BotaoDoModelo.Ler("Modelo: Haiku 5.5");
        Ok(mo == "Haiku 5.5" && es == null && nv == null, "sem esforço no botão");
        (mo, _, nv, _) = BotaoDoModelo.Ler("Modelo: Fable 5.1 Max · 1M");
        Ok(mo == "Fable 5.1" && nv == 4, "Max sem acento e extra que não é Ultracode");
        Ok(BotaoDoModelo.EhBotao("Modelo: x") && !BotaoDoModelo.EhBotao("Modelos"), "reconhece o botão");
    }

    private static void Protocolo()
    {
        var p = ProtocoloPermissao.LerPedido("{\"v\":1,\"sessao\":\"s\",\"ferramenta\":\"Bash\",\"resumo\":\"git push\",\"cwd\":\"C:\\\\a\\\\b\"}");
        Ok(p != null && p.Sessao == "s" && p.Ferramenta == "Bash" && p.Resumo == "git push" && p.Cwd == "C:\\a\\b", "lê o pedido do hook");
        Ok(ProtocoloPermissao.LerPedido("{\"sessao\":\"s\"}") == null && ProtocoloPermissao.LerPedido("x") == null, "pedido sem ferramenta ou quebrado: recusado");
        Ok(ProtocoloPermissao.LerResposta(ProtocoloPermissao.Recebido(110)).Espera == 110, "recebido diz quanto esperar");
        Ok(ProtocoloPermissao.LerResposta(ProtocoloPermissao.Decisao("allow")).Decisao == "allow", "allow");
        var d = ProtocoloPermissao.LerResposta(ProtocoloPermissao.Decisao("deny"));
        Ok(d.Decisao == "deny" && d.Mensagem == "Negado pela ilha.", "deny com mensagem");
        Ok(ProtocoloPermissao.LerResposta(ProtocoloPermissao.Decisao(null)).Decisao == null && ProtocoloPermissao.LerResposta("{\"decisao\":\"talvez\"}").Decisao == null, "sem decisão e decisão estranha viram nada");
    }

    private static string? Cliente(string nome, string pedido, Action<NamedPipeClientStream>? depoisDoRecebido = null)
    {
        using var c = new NamedPipeClientStream(".", nome, PipeDirection.InOut, PipeOptions.Asynchronous);
        c.Connect(1000);
        var b = Encoding.UTF8.GetBytes(pedido + "\n");
        c.Write(b);
        c.Flush();
        var ack = ProtocoloPermissao.LerLinha(c, CancellationToken.None).Result;
        if (ack == null || ProtocoloPermissao.LerResposta(ack).Espera == null) return "sem recebido";
        if (depoisDoRecebido != null) { depoisDoRecebido(c); return null; }
        using var cts = new CancellationTokenSource(5000);
        return ProtocoloPermissao.LerLinha(c, cts.Token).Result;
    }

    private static void PipeDePontaAPonta()
    {
        string nome = "ilha-permissao-teste-" + Guid.NewGuid().ToString("N")[..8];
        string? proxima = "allow";
        var tirados = 0;
        var chegou = new List<ProtocoloPermissao.Pedido>();
        using var serv = new ServidorPermissao(nome, () => 30, (p, responder) =>
        {
            lock (chegou) chegou.Add(p);
            var d = proxima;
            if (d != "esperar") Task.Run(() => { Thread.Sleep(50); responder(d); });
            return () => Interlocked.Increment(ref tirados);
        });
        Thread.Sleep(100);
        string pedido = "{\"v\":1,\"sessao\":\"s\",\"ferramenta\":\"Bash\",\"resumo\":\"git push\",\"cwd\":\"C:\\\\x\"}";
        var r1 = Cliente(nome, pedido);
        Ok(r1 != null && ProtocoloPermissao.LerResposta(r1).Decisao == "allow", "cliente pede, servidor responde allow");
        proxima = "deny";
        var r2 = Cliente(nome, pedido);
        Ok(r2 != null && ProtocoloPermissao.LerResposta(r2).Decisao == "deny", "deny");
        proxima = null;
        var r3 = Cliente(nome, pedido);
        Ok(r3 != null && ProtocoloPermissao.LerResposta(r3).Decisao == null, "sem decisão: o hook cai no diálogo normal");
        proxima = "esperar";
        int antes = Volatile.Read(ref tirados);
        Cliente(nome, pedido, c => c.Dispose());
        var fim = Environment.TickCount64 + 3000;
        while (Volatile.Read(ref tirados) == antes && Environment.TickCount64 < fim) Thread.Sleep(20);
        Ok(Volatile.Read(ref tirados) > antes, "o hook desistiu (conexão fechou): o pedido sai da ilha");
        // pedido quebrado: o servidor fecha sem chamar a ilha
        int n;
        lock (chegou) n = chegou.Count;
        using (var c = new NamedPipeClientStream(".", nome, PipeDirection.InOut))
        {
            c.Connect(1000);
            c.Write(Encoding.UTF8.GetBytes("isto não é json\n"));
            c.Flush();
            var lido = new byte[16];
            int k = 0;
            try { k = c.Read(lido, 0, lido.Length); } catch (IOException) { }
            Ok(k == 0, "pedido quebrado: fecha sem resposta");
        }
        lock (chegou) Ok(chegou.Count == n && chegou.All(p => p.Ferramenta == "Bash"), "a ilha só vê pedido válido");
        // segunda ilha com o mesmo nome não abre (primeira instância)
        bool segunda = true;
        try { using var s2 = NamedPipeServerStreamAcl.Create(nome, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.FirstPipeInstance, 0, 0, null); }
        catch (UnauthorizedAccessException) { segunda = false; }
        catch (IOException) { segunda = false; }
        Ok(!segunda, "outro processo não consegue criar o mesmo pipe antes da ilha (FirstPipeInstance)");
    }

    // ------------------------------------------------------------------ hook em python

    private static string? AcharHook()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "ferramentas", "hook_permissao.py");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    private static readonly string LogDoHook = Path.Combine(Path.GetTempPath(), "ilha-hook-teste.log");

    private static string UltimaDoLog()
    {
        try { return File.ReadAllLines(LogDoHook).LastOrDefault() ?? ""; } catch (IOException) { return ""; }
    }

    private static (int Codigo, string Saida, long Ms) RodarHook(string hook, string pipe, string entrada, int espera = 20)
    {
        var psi = new ProcessStartInfo("python", $"-I \"{hook}\" --pipe {pipe} --espera {espera} --log \"{LogDoHook}\"")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(entrada);
        p.StandardInput.Close();
        var saida = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return (-1, "", sw.ElapsedMilliseconds); }
        return (p.ExitCode, saida.Result.Trim(), sw.ElapsedMilliseconds);
    }

    private static void HookPython()
    {
        var hook = AcharHook();
        if (hook == null) { Info("hook_permissao.py não encontrado perto do exe (pulado)"); return; }
        string nome = "ilha-permissao-teste-" + Guid.NewGuid().ToString("N")[..8];
        string entrada = "{\"session_id\":\"s-9\",\"cwd\":\"C:\\\\proj\\\\ilha\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git push origin main\",\"description\":\"texto que não vai\"}}";
        (int, string, long) r;
        try { r = RodarHook(hook, nome, entrada); }
        catch (System.ComponentModel.Win32Exception) { Info("python não está no PATH (pulado)"); return; }
        Ok(r.Item1 == 0 && r.Item2 == "" && r.Item3 < 5000, $"sem ilha aberta: sai 0 sem saída ({r.Item3} ms)");

        ProtocoloPermissao.Pedido? visto = null;
        string? decisao = "allow";
        using var serv = new ServidorPermissao(nome, () => 20, (p, responder) =>
        {
            visto = p;
            var d = decisao;
            Task.Run(() => { Thread.Sleep(80); responder(d); });
            return () => { };
        });
        Thread.Sleep(100);
        r = RodarHook(hook, nome, entrada);
        Ok(r.Item1 == 0 && r.Item2.Contains("\"hookEventName\": \"PermissionRequest\"") && r.Item2.Contains("\"behavior\": \"allow\""), "permitir: devolve decision allow" + (r.Item2.Length == 0 ? " (log do hook: " + UltimaDoLog() + ")" : ""));
        Ok(visto != null && visto.Ferramenta == "Bash" && visto.Resumo == "git push origin main" && visto.Sessao == "s-9" && !visto.Resumo.Contains("texto"), "o pedido leva o comando (não a descrição), a sessão e o cwd");
        decisao = "deny";
        r = RodarHook(hook, nome, entrada);
        Ok(r.Item1 == 0 && r.Item2.Contains("\"behavior\": \"deny\"") && r.Item2.Contains("Negado pela ilha"), "negar: devolve deny com mensagem");
        decisao = null;
        r = RodarHook(hook, nome, entrada);
        Ok(r.Item1 == 0 && r.Item2 == "", "sem decisão: sai sem saída (diálogo normal)");
        visto = null;
        r = RodarHook(hook, nome, entrada.Replace("\"Bash\"", "\"AskUserQuestion\""));
        Ok(r.Item1 == 0 && r.Item2 == "" && visto == null, "AskUserQuestion: sai na hora sem perguntar à ilha");
        r = RodarHook(hook, nome, entrada.Replace("\"Bash\"", "\"ExitPlanMode\""));
        Ok(r.Item1 == 0 && r.Item2 == "" && visto == null, "ExitPlanMode: idem");
        r = RodarHook(hook, nome, entrada.Replace("\"cwd\"", "\"agent_id\":\"sub-1\",\"cwd\""));
        Ok(r.Item1 == 0 && r.Item2 == "" && visto == null, "subagente: sai na hora (sem diálogo em paralelo, esperar travaria)");
        r = RodarHook(hook, nome, "isto não é json");
        Ok(r.Item1 == 0 && r.Item2 == "", "entrada quebrada: sai 0 sem saída");
    }

    // ------------------------------------------------------------------ sondas (só leitura)

    private static void SondaSessoes()
    {
        var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
        if (!Directory.Exists(pasta)) { Info("sem ~/.claude/sessions (pulado)"); return; }
        var lidos = Directory.GetFiles(pasta, "*.json").Select(f => { try { return ArquivoDeSessao.Ler(File.ReadAllText(f)); } catch (IOException) { return null; } }).Where(a => a != null).ToList();
        var status = string.Join(", ", lidos.GroupBy(a => a!.Status.Length > 0 ? a!.Status : "sem status").Select(g => $"{g.Count()} {g.Key}"));
        Info($"sonda: {lidos.Count} arquivos de sessão legíveis ({status}); {lidos.Count(a => a!.Nome.Length > 0)} com nome");
    }

    private static void SondaApp()
    {
        using var ponte = new PonteClaudeApp();
        RespostaPonte? r = null;
        using var pronto = new ManualResetEventSlim();
        ponte.Estado(x => { r = x; pronto.Set(); });
        pronto.Wait(15000);
        if (r == null) { Info("sonda: a ponte não respondeu em 15 s"); return; }
        if (!r.Ok) { Info("sonda: " + (r.Erro ?? "erro") + " (pulado)"); return; }
        int comModelo = r.Paineis.Count(p => p.Modelo != null);
        int comEsforco = r.Paineis.Count(p => p.Nivel != null);
        int comNome = r.Paineis.Count(p => p.Nome != null);
        Ok(comNome == 0 || comModelo > 0, $"sonda: app do Claude lido, {r.Paineis.Count} painéis, {comNome} com nome, {comModelo} com modelo, {comEsforco} com esforço");
        // o alvo da ponte é o nome do arquivo de sessão: confere que bate com o cabeçalho dos painéis
        var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
        if (comNome > 0 && Directory.Exists(pasta))
        {
            var nomes = Directory.GetFiles(pasta, "*.json").Select(f => { try { return ArquivoDeSessao.Ler(File.ReadAllText(f))?.Nome; } catch (IOException) { return null; } })
                .Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int batem = r.Paineis.Count(p => p.Nome != null && nomes.Contains(p.Nome));
            Ok(batem > 0, $"sonda: {batem} de {comNome} painéis com o mesmo nome de um arquivo de sessão (é assim que a ponte acha a sessão)");
        }
    }
}
