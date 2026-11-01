# Changelog

## 0.3.0 (01/11/2026)

**Novo**
- Mídia: o que toca no Windows (Spotify, navegador, players) com capa, visualizador na cor da capa, tocar e pausar,
  pular, arrastar a barra para avançar e a rodinha do mouse mudando o volume.
- Volume: o nível aparece na ilha quando muda, e o cartão troca a saída de áudio (fone, caixa, monitor).
- Câmera e microfone: bolinha verde (câmera) e laranja (microfone) na pílula, com o nome de quem está usando.
- Timer, cronômetro e pomodoro, com criação rápida digitando "25m", "1h30" ou "10s" (Ctrl + Alt + R).
- Uso do PC: aparece sozinho quando processador, placa de vídeo ou memória ficam no talo (Ctrl + Alt + U).
- Claude Code de verdade: o que cada sessão está fazendo, aviso ao terminar ou perguntar, e aprovar ou negar pedido
  de permissão direto da ilha.
- Mascote morando na ilha, que reage ao Claude Code.
- Calculadora com unidades e câmbio ao vivo (Ctrl + Alt + K).
- Chat com GPT e Claude (Ctrl + Alt + J) e tradutor do texto selecionado (Ctrl + Alt + T); as chaves ficam no
  Gerenciador de Credenciais do Windows.
- Ar-condicionado do quarto pela ilha (Ctrl + Alt + A).
- Fones: foto do fone e bateria ao conectar (AirPods, G733 e outros), print com miniatura que dá para arrastar
  para outro programa, aviso de render terminado do After Effects e do Media Encoder, e notificações do Windows
  (experimental, desligado por padrão).
- Sons para alertas, timer e Claude Code (mudos no modo competitivo e no Não Incomodar).
- Tela de configurações (menu do ícone, "Configurações..."), com cada módulo, os atalhos e as opções da ilha.
- Atualização pela própria ilha: procura versão nova nas releases do GitHub, confere o arquivo e volta para a
  anterior sozinha se a nova não abrir.

**Mudou**
- O menu do ícone ganhou "Configurações...", "Abrir o config.json" e "Procurar atualização".

## 0.2.0 (01/11/2026)

**Novo**
- Animações das logos do Claude e do GPT (as que o Felipe fez): loop girando, volta suavizada, volta de saída,
  volta de entrada, diminuindo e aumentando, sumindo e surgindo, e o Claude virando bola.
- Bolinhas girando em 3D a partir da bola do Claude, com borrão de movimento e as de trás mais escuras: órbita,
  cometa, onda, divisão e anel. As mesmas em branco para o GPT, que também ganhou o "virando bola".
- O Claude de mentira usa as animações de verdade: entra girando, trabalha em loop, vira bola e solta as bolinhas
  quando pensa, volta a ser logo para trabalhar, sai girando ao terminar e pulsa esperando você.
- GPT de mentira e galeria com todas as animações (menu Demonstração).
- Instalador (`ferramentas\instalar.ps1`) com atalho no Menu Iniciar e ícone próprio.
- Iniciar com o Windows (ligado por padrão, opção no menu do ícone).

**Mudou**
- As duas logos aparecem do mesmo tamanho (a do Claude ganha zoom, porque ocupa metade do quadro dela).
- O ícone da bandeja é o mesmo do Menu Iniciar.

**Corrigido**
- Janela maximizada não conta mais como tela cheia.

## 0.1.0 (01/11/2026)

**Novo**
- A ilha: pílula preta flutuando no topo do monitor principal, com sombra suave, que muda de forma com mola e um
  leve passar do ponto, a 250 quadros por segundo no ultrawide.
- Estados: escondida, vazia na área de trabalho, compacta, compacta dividida com bolha ao lado, expandida e alerta.
- O conteúdo entra e sai com fade, escala e desfoque, sempre recortado no formato da ilha.
- Prévia ao passar o mouse, expandir com clique, voltar ao tirar o mouse ou clicar fora.
- Modo competitivo pela lista de jogos: só o timer aparece.
- Respeita o Não Incomodar e a sessão de Foco do Windows.
- Atalho global (Ctrl + Alt + Espaço, com reserva se estiver ocupado).
- Ícone na bandeja: pausar, flutuante ou colada (estilo notch), demonstração, configuração, logs e sair.
- Configuração em JSON que vale assim que é salva.
- Demonstração com timer, música, Claude Code (com a logo original) e uso do PC de mentira, mais um passeio
  automático por todos os estados.
- Medição de quadros por segundo, processador e memória (`--medir`) e autoteste das janelas (`--autoteste`).
