# Módulo "ia": chat com IA e tradutor

Id `ia`, atividades `ia` (chat) e `tradutor`. Namespace `Ilha.IA`, não `Ilha.Modulos.IA`: o núcleo tem a classe
estática `Ilha.Modulos` e o namespace com o mesmo nome dá CS0101.

## Como usar

- **Chat:** `Ctrl + Alt + J` (atalho `ia`). Enter manda, Shift+Enter quebra linha, Ctrl+V cola print, Esc fecha.
  Clicar no nome do modelo alterna entre os de `modelos` na config (padrão GPT-6 Luna, Claude Sonnet 5.5,
  GPT-6.1 Sol); "Nova" limpa a conversa (durante a resposta vira "Parar"); "Chaves" abre a tela das chaves.
  Rodinha do mouse rola a conversa. Guarda as últimas 6 trocas, só em memória.
- **Chaves:** sem nenhuma, a tela de chaves abre no lugar do chat. "Colar" abre um campo de senha (bolinhas) e
  Enter grava direto no Gerenciador de Credenciais (`Ilha/openai`, `Ilha/anthropic`) e testa. "Testar" faz
  `GET /models` (de graça nos dois lados). "Apagar" pede o segundo clique. Basta uma chave: a outra serve de reserva.
- **Tradutor:** selecione um texto em qualquer app e `Ctrl + Alt + T` (atalho `tradutor`). Para o português, ou
  para o inglês se já era português. Enter copia a tradução e fecha (o foco volta ao app, pronto para colar);
  digitar outro texto e Enter traduz ele. Nada selecionado: o campo fica aberto para digitar ou colar.

## Decisões

- **Provedor portado do `contato-ia/contato/provedor.py`** (`Provedor.cs`): pares, reserva, preços, falta de
  crédito (402 sempre; 400/401/403/429 só com os sinais), disjuntor de 30 min, raciocínio do Claude e
  `converter_entrada`. A conversão de esquema e de ferramentas ficou de fora (nada aqui usa). É um terceiro mestre:
  mudou a tabela lá, muda aqui. Os casos do `test_provedor.py` que cabem estão no `TestesIA.cs`.
- **HttpClient puro + `System.Net.ServerSentEvents`** (vem no .NET 10), sem SDK. OpenAI pela Responses
  (`store: false`, `reasoning` só em modelo de raciocínio), Claude pela Messages com `x-api-key`.
- **Troca de lado só por falta de crédito e só antes do primeiro texto.** Erro de servidor (5xx, 529,
  `overloaded_error` no meio do stream) continua erro. Sem a chave do lado pedido, a reserva assume sem abrir o
  disjuntor.
- **Cancelamento:** recolher a ilha, outra ferramenta tomar a frente, Esc no campo, jogo competitivo ou "Parar"
  cancelam o token, e o stream para na hora (testado com um stream falso que fica parado). Stream mudo por 90 s vira
  erro.
- **A chave** é lida do Gerenciador de Credenciais na hora de cada pedido e só vai no cabeçalho. Toda mensagem de
  erro passa pelo `Segredo.Limpar` (a OpenAI devolve parte da chave no 401) e o 401 nunca repete o texto do servidor.
- **Imagem colada:** lado maior até 2576 px (`ladoMaiorDaImagem`), PNG; acima de ~3,5 MB vira JPEG. Uma por
  mensagem. Fica no histórico e volta nas perguntas seguintes da mesma conversa.
- **Tradutor:** até `limiteGoogle` (1.200) caracteres pelo endpoint não oficial do Google (`client=gtx`, POST),
  acima pela IA no par barato sem raciocínio (`gpt-6-luna` esforço `none`, reserva `claude-haiku-5-5` com
  `thinking: disabled`), com instrução anti-injeção. Sem chave de IA, o grande vai ao Google em pedaços de 4.500
  que remontam o texto exato. Texto acima de `limiteTradutorIA` (12.000) é cortado. Um motor falhou, o outro assume.
- **Seleção:** UI Automation primeiro (IUIAutomation2 com timeouts de ~450 ms, thread MTA próprio, prazo total de
  600 ms; vtable conferida no `UIAutomationClient.h` do SDK 26100 e pela sonda do autoteste). Reserva: Ctrl+C por
  SendInput depois de soltar as teclas do atalho, espera o número de sequência mudar (300 ms) e devolve o que
  estava lá (texto, imagem ou arquivos). **Nunca com jogo competitivo na frente; nunca Ctrl+C em terminal**
  (Ctrl+C sem seleção mataria o programa rodando).

## Não testado (só dá para conferir no PC, com as chaves de verdade)

- Chamada de verdade às duas APIs (só servidores falsos). O texto exato do erro de saldo esgotado vem da
  documentação: se o chat disser erro em vez de trocar de lado, é o primeiro suspeito.
- A tela na ilha de verdade (fotos, clique, rodinha), o atalho global e o foco da Entrada.
- A leitura da seleção em apps reais (Brave, Word, VS Code, Discord, Bloco de Notas). No VS Code, Ctrl+C sem seleção
  copia a linha inteira; no terminal integrado dele (que a ilha não reconhece como terminal) interrompe o programa.
- O Google Tradutor de verdade.
- O Ctrl+C entra no histórico da área de transferência do Windows (Win+V); formatos que não são texto, imagem ou
  arquivo (HTML, RTF) não são devolvidos.
