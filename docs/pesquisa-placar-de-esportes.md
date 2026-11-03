# Pesquisa: fonte de placar ao vivo de esportes
Data: 2026-11-03 · Projeto: ilha

## Pergunta
De onde a ilha tira, de graça e sem chave, o placar ao vivo de futebol (Brasileirão, Copa do Brasil, Libertadores,
Sul-Americana, Champions, Premier, La Liga), F1 e outros esportes, com escudo dos times e os próximos jogos.

## Recomendação
Os endpoints JSON abertos da ESPN (`site.api.espn.com/apis/site/v2/sports/<esporte>/<liga>/scoreboard`). Um formato
só para todos os esportes, sem chave, com escudo, horário, status e placar, respondendo em 100 a 200 ms e com cache
de 1 a 10 s (dá para ler a cada 30 s durante um jogo). Não é uma API documentada: a ESPN pode mudar o formato sem
aviso, então a ilha lê de forma defensiva e mostra "sem dados" em vez de quebrar. A F1 sai da ESPN também; a OpenF1
fica como opção futura (ao vivo de verdade, mas pede login).

## Candidatos avaliados
- **ESPN (site.api.espn.com)**: sem chave, todos os esportes num formato, escudo (`team.logo`), status
  (`STATUS_SCHEDULED`, `STATUS_IN_PROGRESS`, `STATUS_FULL_TIME`, `STATUS_FINAL_PEN`...), placar, data. Não oficial e
  sem termos de uso publicados para ele: serve para uso pessoal; se mudar, quebra. Escolhida.
- **OpenF1**: grátis e de código aberto; o histórico é aberto, mas o ao vivo pede login (usuário e senha, token
  OAuth2) e o jeito recomendado é MQTT ou WebSocket. Respondeu em 0,7 a 1 s. Fica para depois, se a F1 da ESPN não
  bastar (e a senha seria digitada por ele, guardada no Gerenciador de Credenciais).
- **Jolpica (sucessora da Ergast)**: resultados e calendário da F1, sem ao vivo (cache de 10 min). Serve para o
  calendário e a classificação do campeonato.
- **TheSportsDB**: a chave grátis antiga ("3") foi desligada; o ao vivo é pago. Descartada.
- **football-data.org e API-Football**: pedem chave e têm limite baixo no plano grátis. Descartadas por isso.

## O que eu testei
`scratchpad\esportes\sonda.py` (urllib, sem chave) em 03/11/2026:
- ESPN: 200 em todas as ligas pedidas (bra.1, bra.2, bra.copa_do_brazil, conmebol.libertadores,
  conmebol.sudamericana, uefa.champions, eng.1, esp.1, racing/f1, nba, nfl, mma/ufc, tennis/atp), 100 a 800 ms,
  `Cache-Control: max-age` de 1 a 10. Futebol e as ligas americanas vêm com escudo; F1, UFC e tênis vêm com atletas
  (sem escudo). `soccer/bra.1/teams` lista os times com escudo.
- OpenF1: `sessions?session_key=latest` e `position?session_key=latest` responderam (dados da última corrida).
- Jolpica: `current/last/results.json` respondeu.
- TheSportsDB: 403, chave "3" desligada.

## Riscos e o que pode dar errado
- A ESPN muda o JSON: a leitura é defensiva (campo faltando = não mostra) e o autoteste guarda um exemplo real.
- Durante uma corrida, quanto da F1 a ESPN mostra ao vivo (posições, volta) não foi visto: não havia corrida.
- Chamar a cada 30 s só enquanto há jogo seguido por ele; fora disso, a cada 15 min para saber os próximos jogos.
- Trocar de fonte depois custa só o leitor de cada esporte.

## Confirmado / inferência / incerto
Confirmado: as respostas acima (testadas aqui); a OpenF1 pede login para o ao vivo (documentação dela).
Inferência: o cache curto da ESPN indica que o placar acompanha o jogo com segundos de atraso.
Incerto: o detalhe da F1 ao vivo na ESPN; se a ESPN limita quem chama com frequência (nada visto).

## Fontes
- ESPN, endpoints testados diretamente (sem documentação oficial).
- [Using OpenF1 with authentication](https://openf1.org/auth.html): o ao vivo pede login.
- [Jolpica F1](https://api.jolpi.ca/ergast/f1/current/last/results.json): testado.
