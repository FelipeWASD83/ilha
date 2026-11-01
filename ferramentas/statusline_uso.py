#!/usr/bin/env python3
"""Status line do Claude Code que guarda o uso do plano para a ilha.

O Claude Code manda o JSON da sessão pela entrada padrão do comando da status line. Para assinantes do claude.ai
(Pro, Max, Ultra) esse JSON traz `rate_limits.five_hour` e `rate_limits.seven_day`, cada um com
`used_percentage` (0 a 100) e `resets_at` (segundos desde 1970), conforme a documentação oficial da status line.

Este script:
  1. lê o JSON da entrada;
  2. grava só as duas janelas (porcentagem e reset) e a hora da coleta em `~/.ilha/uso-claude.json`, de forma
     atômica, e só quando o JSON traz alguma janela (nunca troca dado bom por nada, nem dado novo por um mais velho
     de uma sessão parada);
  3. roda a status line que você já tinha, com a mesma entrada, e repassa a saída e o código dela. Se qualquer coisa
     aqui der errado, a status line anterior continua aparecendo.

A status line anterior vem de `--anterior "<comando>"` ou de `~/.ilha/config.json` -> modulos -> limite ->
statuslineAnterior. Sem nenhuma das duas, não imprime nada.

Uso no settings.json do Claude Code (barras normais no caminho, por causa do Git Bash):
    "statusLine": {"type": "command", "command": "python C:/caminho/ilha/ferramentas/statusline_uso.py"}

Só biblioteca padrão. `--testar` roda o autoteste numa pasta temporária, sem tocar em nada seu.
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

JANELAS = ("five_hour", "seven_day")
TEMPO_PADRAO = 10.0
# a mesma janela pode vir com o reset alguns segundos diferente entre respostas
TOLERANCIA_RESET = 600
VAR_ANINHADA = "ILHA_STATUSLINE_ANINHADA"


def pasta_ilha():
    return os.path.join(os.path.expanduser("~"), ".ilha")


# ---------------------------------------------------------------------------------------------- dados


def numero(v):
    return float(v) if isinstance(v, (int, float)) and not isinstance(v, bool) else None


def extrair(entrada):
    """As janelas válidas do JSON da status line: {"five_hour": {"used_percentage": .., "resets_at": ..}, ...}."""
    if not isinstance(entrada, dict):
        return {}
    rl = entrada.get("rate_limits")
    if not isinstance(rl, dict):
        return {}
    saida = {}
    for nome in JANELAS:
        j = rl.get(nome)
        if not isinstance(j, dict):
            continue
        pct = numero(j.get("used_percentage"))
        if pct is None or pct != pct:
            continue
        item = {"used_percentage": pct}
        reset = numero(j.get("resets_at"))
        if reset is not None and reset > 0:
            item["resets_at"] = reset
        saida[nome] = item
    return saida


def vencida(janela, agora):
    r = janela.get("resets_at")
    return r is not None and agora >= r


def mais_nova(nova, velha):
    """A leitura nova vale mais que a guardada? (uma sessão parada pode mandar a janela de horas atrás)"""
    rn, rv = nova.get("resets_at"), velha.get("resets_at")
    if rn is None or rv is None:
        return True
    if rn > rv + TOLERANCIA_RESET:
        return True
    if rn < rv - TOLERANCIA_RESET:
        return False
    # mesma janela: o uso só sobe dentro dela
    return nova["used_percentage"] >= velha["used_percentage"]


def mesclar(guardadas, novas, agora):
    """Devolve (janelas para gravar, se alguma janela nova foi aceita)."""
    resultado = {}
    aceitou = False
    for nome in JANELAS:
        v = guardadas.get(nome)
        if v is not None and vencida(v, agora):
            v = None
        n = novas.get(nome)
        if n is None:
            if v is not None:
                resultado[nome] = v
            continue
        if v is None or mais_nova(n, v):
            resultado[nome] = n
            aceitou = True
        else:
            resultado[nome] = v
    return resultado, aceitou


def ler_guardado(caminho):
    try:
        with open(caminho, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        rl = d.get("rate_limits") if isinstance(d, dict) else None
        return extrair({"rate_limits": rl})
    except (OSError, ValueError):
        return {}


def gravar_atomico(caminho, conteudo):
    pasta = os.path.dirname(caminho) or "."
    os.makedirs(pasta, exist_ok=True)
    fd, temp = tempfile.mkstemp(prefix=".uso-claude-", suffix=".tmp", dir=pasta)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(conteudo)
        # a ilha pode estar lendo bem nessa hora: tenta de novo umas vezes
        for tentativa in range(5):
            try:
                os.replace(temp, caminho)
                return
            except PermissionError:
                if tentativa == 4:
                    raise
                time.sleep(0.05)
    finally:
        if os.path.exists(temp):
            try:
                os.remove(temp)
            except OSError:
                pass


def guardar(entrada_bytes, caminho, agora=None):
    """Grava o uso, se a entrada trouxer. Devolve True se gravou."""
    agora = time.time() if agora is None else agora
    try:
        entrada = json.loads(entrada_bytes.decode("utf-8-sig"))
    except (UnicodeDecodeError, ValueError):
        return False
    novas = extrair(entrada)
    if not novas:
        return False
    janelas, aceitou = mesclar(ler_guardado(caminho), novas, agora)
    if not aceitou:
        return False
    gravar_atomico(caminho, json.dumps({"coletado_em": round(agora, 3), "rate_limits": janelas}, indent=2))
    return True


# ---------------------------------------------------------------------------------------------- config


def ler_config(caminho):
    """A seção modulos.limite do config.json da ilha ({} se não existir ou não abrir)."""
    try:
        with open(caminho, "r", encoding="utf-8-sig") as f:
            texto = f.read()
    except OSError:
        return {}
    try:
        d = json.loads(texto)
    except ValueError:
        # a ilha aceita comentário de linha e vírgula sobrando; tenta sem eles
        import re
        limpo = re.sub(r'^\s*//.*$', "", texto, flags=re.M)
        limpo = re.sub(r",(\s*[}\]])", r"\1", limpo)
        try:
            d = json.loads(limpo)
        except ValueError:
            return {}
    sec = d.get("modulos", {}).get("limite", {}) if isinstance(d, dict) else {}
    return sec if isinstance(sec, dict) else {}


# ---------------------------------------------------------------------------------------------- status line anterior


def git_bash():
    """O bash do Git for Windows (o Claude Code usa ele quando existe); nunca o bash.exe do WSL em System32."""
    candidatos = []
    var = os.environ.get("CLAUDE_CODE_GIT_BASH_PATH")
    if var:
        candidatos.append(var)
    git = shutil.which("git")
    if git:
        raiz = os.path.dirname(os.path.dirname(os.path.realpath(git)))
        candidatos.append(os.path.join(raiz, "bin", "bash.exe"))
    for base in (os.environ.get("ProgramFiles"), os.environ.get("ProgramFiles(x86)"),
                 os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs")):
        if base:
            candidatos.append(os.path.join(base, "Git", "bin", "bash.exe"))
    sistema = os.path.normcase(os.environ.get("SystemRoot", r"C:\Windows"))
    for c in candidatos:
        if c and os.path.isfile(c) and not os.path.normcase(os.path.abspath(c)).startswith(sistema):
            return c
    return None


def argv_do_shell(comando):
    """Roda o comando no mesmo shell em que o Claude Code roda a status line."""
    if os.name == "nt":
        bash = git_bash()
        if bash:
            return [bash, "-c", comando]
        return ["powershell", "-NoProfile", "-NonInteractive", "-Command", comando]
    return [os.environ.get("SHELL") or "/bin/sh", "-c", comando]


def matar_arvore(p):
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(p.pid)], stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, timeout=5, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        else:
            import signal
            os.killpg(p.pid, signal.SIGKILL)
    except Exception:
        pass
    try:
        p.kill()
    except Exception:
        pass


def rodar_anterior(comando, entrada_bytes, tempo):
    """Devolve (saída, código). Tempo esgotado: o que saiu até ali e o processo inteiro é encerrado."""
    env = dict(os.environ)
    env[VAR_ANINHADA] = "1"
    extra = {}
    if os.name == "nt":
        extra["creationflags"] = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    else:
        extra["start_new_session"] = True
    try:
        p = subprocess.Popen(argv_do_shell(comando), stdin=subprocess.PIPE, stdout=subprocess.PIPE, env=env, **extra)
    except OSError as e:
        sys.stderr.write(f"statusline_uso: não deu para rodar a status line anterior: {e}\n")
        return b"", 1
    try:
        saida, _ = p.communicate(entrada_bytes, timeout=tempo)
        return saida or b"", p.returncode
    except subprocess.TimeoutExpired as e:
        parcial = e.output or b""
        matar_arvore(p)
        try:
            saida, _ = p.communicate(timeout=2)
            parcial = saida or parcial
        except Exception:
            pass
        sys.stderr.write(f"statusline_uso: a status line anterior passou de {tempo:g} s\n")
        return parcial, 0 if parcial.strip() else 1


# ---------------------------------------------------------------------------------------------- principal


def argumentos(argv):
    a = argparse.ArgumentParser(add_help=False)
    a.add_argument("--anterior")
    a.add_argument("--saida")
    a.add_argument("--config")
    a.add_argument("--tempo", type=float)
    a.add_argument("--testar", action="store_true")
    try:
        ns, _ = a.parse_known_args(argv)
    except SystemExit:
        ns = a.parse_args([])
    return ns


def principal(argv, entrada_bytes, saida_buffer):
    ns = argumentos(argv)
    pasta = pasta_ilha()
    cfg = ler_config(ns.config or os.path.join(pasta, "config.json"))
    # chamado de dentro de si mesmo (a "anterior" aponta para este script): não grava nem roda de novo
    if os.environ.get(VAR_ANINHADA):
        return 0
    if cfg.get("ligado", True) is not False:
        try:
            guardar(entrada_bytes, ns.saida or os.path.join(pasta, "uso-claude.json"))
        except Exception as e:
            sys.stderr.write(f"statusline_uso: não gravou o uso: {e}\n")
    comando = ns.anterior if ns.anterior is not None else cfg.get("statuslineAnterior", "")
    if not isinstance(comando, str) or not comando.strip():
        return 0
    tempo = ns.tempo if ns.tempo and ns.tempo > 0 else TEMPO_PADRAO
    saida, codigo = rodar_anterior(comando, entrada_bytes, tempo)
    try:
        saida_buffer.write(saida)
        saida_buffer.flush()
    except Exception:
        pass
    return codigo if isinstance(codigo, int) else 1


def main():
    argv = sys.argv[1:]
    if "--testar" in argv:
        sys.exit(testar())
    try:
        entrada = sys.stdin.buffer.read()
    except Exception:
        entrada = b""
    try:
        codigo = principal(argv, entrada, sys.stdout.buffer)
    except Exception as e:
        # último recurso: nem o erro daqui derruba a status line anterior
        sys.stderr.write(f"statusline_uso: {e}\n")
        codigo = 1
        try:
            ns = argumentos(argv)
            cfg = ler_config(ns.config or os.path.join(pasta_ilha(), "config.json"))
            comando = ns.anterior if ns.anterior is not None else cfg.get("statuslineAnterior", "")
            if isinstance(comando, str) and comando.strip() and not os.environ.get(VAR_ANINHADA):
                saida, codigo = rodar_anterior(comando, entrada, TEMPO_PADRAO)
                sys.stdout.buffer.write(saida)
                sys.stdout.buffer.flush()
        except Exception:
            pass
    sys.exit(codigo)


# ---------------------------------------------------------------------------------------------- autoteste


def testar():
    import io
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    falhas = []

    def ok(cond, texto):
        print(("ok      " if cond else "FALHOU  ") + texto)
        if not cond:
            falhas.append(texto)

    agora = 1_800_000_000.0
    js = {"model": {"display_name": "X"}, "cwd": "C:/projeto",
          "rate_limits": {"five_hour": {"used_percentage": 23.5, "resets_at": agora + 3600},
                          "seven_day": {"used_percentage": 41.2, "resets_at": agora + 86400},
                          "spend_limit": {"used_percentage": 5, "resets_at": agora + 999}}}
    ex = extrair(js)
    ok(ex == {"five_hour": {"used_percentage": 23.5, "resets_at": agora + 3600},
              "seven_day": {"used_percentage": 41.2, "resets_at": agora + 86400}}, "extrai só as duas janelas")
    ok(extrair({"model": {}}) == {}, "sem rate_limits: nada")
    ok(extrair({"rate_limits": {"five_hour": {"used_percentage": True}, "seven_day": {"used_percentage": "40"}}}) == {},
       "porcentagem que não é número: ignorada")
    ok(extrair({"rate_limits": {"five_hour": {"used_percentage": 10}}}) == {"five_hour": {"used_percentage": 10.0}},
       "janela sem resets_at continua valendo")

    g = {"five_hour": {"used_percentage": 50.0, "resets_at": agora + 3600}}
    r, a = mesclar(g, {"five_hour": {"used_percentage": 20.0, "resets_at": agora + 3600 - 7200}}, agora)
    ok(not a and r == g, "janela mais velha (de uma sessão parada) não troca a guardada")
    r, a = mesclar(g, {"five_hour": {"used_percentage": 40.0, "resets_at": agora + 3610}}, agora)
    ok(not a and r == g, "mesma janela com uso menor: fica a guardada")
    r, a = mesclar(g, {"five_hour": {"used_percentage": 55.0, "resets_at": agora + 3600}}, agora)
    ok(a and r["five_hour"]["used_percentage"] == 55.0, "mesma janela com uso maior: troca")
    r, a = mesclar(g, {"five_hour": {"used_percentage": 3.0, "resets_at": agora + 3600 + 18000}}, agora)
    ok(a and r["five_hour"]["used_percentage"] == 3.0, "janela nova: troca mesmo com uso menor")
    r, a = mesclar(g, {"seven_day": {"used_percentage": 9.0, "resets_at": agora + 86400}}, agora)
    ok(a and set(r) == {"five_hour", "seven_day"}, "janela que não veio fica a guardada")
    r, a = mesclar({"five_hour": {"used_percentage": 90.0, "resets_at": agora - 1}},
                   {"seven_day": {"used_percentage": 9.0, "resets_at": agora + 86400}}, agora)
    ok(a and set(r) == {"seven_day"}, "janela guardada que já venceu sai")

    with tempfile.TemporaryDirectory() as d:
        destino = os.path.join(d, "uso-claude.json")
        cfg = os.path.join(d, "config.json")
        falso = os.path.join(d, "anterior_falso.py")
        with open(falso, "w", encoding="utf-8") as f:
            f.write("import json, sys, time\n"
                    "if len(sys.argv) > 1 and sys.argv[1] == 'dorme':\n"
                    "    sys.stdout.write('antes ')\n    sys.stdout.flush()\n    time.sleep(8)\n"
                    "d = json.loads(sys.stdin.buffer.read().decode('utf-8'))\n"
                    "sys.stdout.buffer.write(('anterior: ' + d['model']['display_name'] + ' ção').encode('utf-8'))\n"
                    "sys.exit(3)\n")
        py = sys.executable.replace("\\", "/")
        sc = falso.replace("\\", "/")
        shell = argv_do_shell("x")[0]
        prefixo = "& " if os.path.basename(shell).lower().startswith("powershell") else ""
        comando = f'{prefixo}"{py}" "{sc}"'
        entrada = json.dumps(js).encode("utf-8")

        out = io.BytesIO()
        cod = principal(["--saida", destino, "--config", cfg, "--anterior", comando], entrada, out)
        texto = out.getvalue().decode("utf-8", "replace")
        ok(cod == 3 and texto == "anterior: X ção", f"repassa saída e código da anterior ({cod}, {texto!r}, shell {os.path.basename(shell)})")
        with open(destino, encoding="utf-8") as f:
            gravado = json.load(f)
        ok(set(gravado) == {"coletado_em", "rate_limits"} and set(gravado["rate_limits"]) == set(JANELAS)
           and gravado["rate_limits"]["five_hour"]["used_percentage"] == 23.5, "grava só o uso e a hora da coleta")
        ok(not [n for n in os.listdir(d) if n.endswith(".tmp")], "nenhum arquivo temporário sobrando")
        antes = open(destino, encoding="utf-8").read()

        out = io.BytesIO()
        sem = json.dumps({"model": {"display_name": "Y"}}).encode("utf-8")
        cod = principal(["--saida", destino, "--config", cfg, "--anterior", comando], sem, out)
        ok(cod == 3 and out.getvalue().decode("utf-8") == "anterior: Y ção" and open(destino, encoding="utf-8").read() == antes,
           "sem rate_limits: não mexe no arquivo e a anterior aparece")

        out = io.BytesIO()
        cod = principal(["--saida", destino, "--config", cfg, "--anterior", comando], b"isto nao e json", out)
        ok(cod != 0 and open(destino, encoding="utf-8").read() == antes, "entrada quebrada: arquivo intacto, a anterior roda mesmo assim")

        with open(cfg, "w", encoding="utf-8") as f:
            f.write('{\n  // comentario\n  "modulos": {"limite": {"ligado": true, "statuslineAnterior": ' + json.dumps(comando) + '},},\n}\n')
        out = io.BytesIO()
        cod = principal(["--saida", destino, "--config", cfg], entrada, out)
        ok(cod == 3 and out.getvalue().decode("utf-8") == "anterior: X ção", "a anterior vem do config.json da ilha (com comentário)")

        with open(cfg, "w", encoding="utf-8") as f:
            json.dump({"modulos": {"limite": {"ligado": False}}}, f)
        os.remove(destino)
        out = io.BytesIO()
        cod = principal(["--saida", destino, "--config", cfg], entrada, out)
        ok(cod == 0 and out.getvalue() == b"" and not os.path.exists(destino), "módulo desligado e sem anterior: não grava nem imprime")

        out = io.BytesIO()
        t0 = time.time()
        cod = principal(["--saida", destino, "--config", cfg, "--anterior", f'{prefixo}"{py}" "{sc}" dorme', "--tempo", "1.5"], entrada, out)
        dur = time.time() - t0
        ok(out.getvalue().startswith(b"antes") and dur < 6, f"anterior lenta: corta no tempo e repassa o que saiu ({out.getvalue()!r}, {dur:.1f} s)")

        out = io.BytesIO()
        cod = principal(["--saida", destino, "--config", cfg, "--anterior", "comando-que-nao-existe-ilha"], entrada, out)
        ok(cod != 0 and out.getvalue() == b"", f"anterior que não existe: sem exceção (código {cod})")

        os.environ[VAR_ANINHADA] = "1"
        try:
            out = io.BytesIO()
            cod = principal(["--saida", destino, "--config", cfg, "--anterior", comando], entrada, out)
            ok(cod == 0 and out.getvalue() == b"", "chamado de dentro de si mesmo: não roda de novo")
        finally:
            del os.environ[VAR_ANINHADA]

    print(f"{'FALHOU' if falhas else 'ok'}: {len(falhas)} falha(s)")
    return 1 if falhas else 0


if __name__ == "__main__":
    main()
