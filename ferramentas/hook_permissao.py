"""Hook PermissionRequest do Claude Code que deixa aprovar ou negar o pedido pela ilha.

Lê o pedido do Claude Code no stdin, pergunta à ilha pelo pipe nomeado \\\\.\\pipe\\ilha-permissao e devolve a decisão
que ele clicar (Permitir ou Negar). Sem a ilha aberta, sem resposta a tempo, ou se a ilha não decidir, sai com
código 0 sem escrever nada: o fluxo normal do Claude Code segue (o diálogo do app continua valendo). Nunca permite
por tempo esgotado.

Fica de fora, saindo na hora sem saída:
  - AskUserQuestion e ExitPlanMode, que não se aprovam com um "allow" seco (pedem a resposta junto);
  - pedido de subagente (agent_id), que não tem diálogo em paralelo: esperar a ilha travaria o subagente.

Só biblioteca padrão. Uso no settings.json (hooks -> PermissionRequest):
  python "<pasta da ilha>\\ferramentas\\hook_permissao.py"        com "timeout" maior que a espera (padrão 110 s)
Opções: --pipe <nome> (padrão ilha-permissao), --espera <segundos> (se a ilha não disser) e --log <arquivo>
(acrescenta uma linha por passo, para conferir depois de instalar; nunca grava o comando nem o tool_input).
"""

import ctypes
import ctypes.wintypes as wt
import json
import msvcrt
import os
import sys
import threading
import time

PIPE_PADRAO = "ilha-permissao"
ESPERA_PADRAO = 110
CONECTAR_MS = 300
FORA = {"AskUserQuestion", "ExitPlanMode"}

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.WaitNamedPipeW.argtypes = [wt.LPCWSTR, wt.DWORD]
kernel32.WaitNamedPipeW.restype = wt.BOOL
kernel32.GetNamedPipeServerProcessId.argtypes = [wt.HANDLE, ctypes.POINTER(wt.ULONG)]
kernel32.GetNamedPipeServerProcessId.restype = wt.BOOL
kernel32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
kernel32.OpenProcess.restype = wt.HANDLE
kernel32.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]
kernel32.QueryFullProcessImageNameW.restype = wt.BOOL
kernel32.CloseHandle.argtypes = [wt.HANDLE]
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


LOG = None


def anotar(texto):
    if not LOG:
        return
    try:
        with open(LOG, "a", encoding="utf-8") as saida:
            saida.write(time.strftime("%Y-%m-%d %H:%M:%S") + " " + texto + "\n")
    except OSError:
        pass


def sair(motivo="sem decisão"):
    """Sem decisão: o diálogo normal segue. os._exit mata a thread de leitura que ainda espera."""
    anotar("saiu: " + motivo)
    sys.stdout.flush()
    os._exit(0)


def opcoes(argv):
    global LOG
    pipe, espera = PIPE_PADRAO, None
    i = 0
    while i < len(argv):
        if argv[i] == "--pipe" and i + 1 < len(argv):
            pipe = argv[i + 1]
            i += 1
        elif argv[i] == "--espera" and i + 1 < len(argv):
            try:
                espera = float(argv[i + 1])
            except ValueError:
                pass
            i += 1
        elif argv[i] == "--log" and i + 1 < len(argv):
            LOG = argv[i + 1]
            i += 1
        i += 1
    return pipe, espera


def uma_linha(texto, limite=300):
    texto = " ".join(str(texto or "").split())
    return texto if len(texto) <= limite else texto[: limite - 1] + "…"


def resumo(ferramenta, entrada):
    """O que está sendo pedido, numa linha, só com campos mecânicos (nunca a descrição do comando nem prompt)."""
    if not isinstance(entrada, dict):
        return ""
    if ferramenta in ("Bash", "PowerShell"):
        return uma_linha(entrada.get("command"))
    for chave in ("file_path", "notebook_path", "url", "query", "pattern", "path"):
        valor = entrada.get(chave)
        if isinstance(valor, str) and valor.strip():
            return uma_linha(valor)
    return ""


def servidor_e_a_ilha(arquivo):
    """Confere que quem serve o pipe é o Ilha.exe (outro programa podia ter criado o nome)."""
    try:
        h = msvcrt.get_osfhandle(arquivo.fileno())
        pid = wt.ULONG(0)
        if not kernel32.GetNamedPipeServerProcessId(h, ctypes.byref(pid)):
            return False
        proc = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
        if not proc:
            return False
        try:
            buf = ctypes.create_unicode_buffer(1024)
            tam = wt.DWORD(len(buf))
            if not kernel32.QueryFullProcessImageNameW(proc, 0, buf, ctypes.byref(tam)):
                return False
            return os.path.basename(buf.value).lower() == "ilha.exe"
        finally:
            kernel32.CloseHandle(proc)
    except OSError:
        return False


def ler_linha(arquivo, prazo):
    """Lê uma linha do pipe com prazo (em segundos); None se fechou ou o prazo acabou."""
    resultado = []
    pronto = threading.Event()

    def ler():
        try:
            resultado.append(arquivo.readline())
        except OSError:
            resultado.append(b"")
        pronto.set()

    threading.Thread(target=ler, daemon=True).start()
    if not pronto.wait(prazo) or not resultado or not resultado[0]:
        return None
    try:
        return json.loads(resultado[0].decode("utf-8"))
    except ValueError:
        return None


def main():
    pipe, espera_arg = opcoes(sys.argv[1:])
    try:
        pedido = json.loads(sys.stdin.buffer.read().decode("utf-8-sig", "replace"))
    except (ValueError, OSError):
        sair("entrada ilegível")
    if not isinstance(pedido, dict):
        sair("entrada não é objeto")
    ferramenta = str(pedido.get("tool_name") or "")
    if not ferramenta or ferramenta in FORA or pedido.get("agent_id"):
        sair(f"fora do hook ({ferramenta or 'sem ferramenta'}{', subagente' if pedido.get('agent_id') else ''})")

    caminho = "\\\\.\\pipe\\" + pipe
    if not kernel32.WaitNamedPipeW(caminho, CONECTAR_MS):
        sair(f"ilha fechada (WaitNamedPipe, erro {ctypes.get_last_error()})")
    try:
        arquivo = open(caminho, "r+b", buffering=0)
    except OSError as erro:
        sair(f"não abriu o pipe ({erro})")
    if not servidor_e_a_ilha(arquivo):
        sair("quem serve o pipe não é o Ilha.exe")
    anotar(f"pedido de {ferramenta} enviado à ilha")

    mensagem = {
        "v": 1,
        "sessao": str(pedido.get("session_id") or ""),
        "ferramenta": ferramenta,
        "resumo": resumo(ferramenta, pedido.get("tool_input")),
        "cwd": str(pedido.get("cwd") or ""),
    }
    try:
        arquivo.write((json.dumps(mensagem, ensure_ascii=False) + "\n").encode("utf-8"))
    except OSError as erro:
        sair(f"falhou ao escrever ({erro})")

    recebido = ler_linha(arquivo, 3)
    if not isinstance(recebido, dict) or not recebido.get("ok"):
        sair("a ilha não confirmou o pedido")
    espera = recebido.get("espera")
    if not isinstance(espera, (int, float)):
        espera = espera_arg if espera_arg else ESPERA_PADRAO
    espera = max(1.0, min(float(espera), 590.0))

    resposta = ler_linha(arquivo, espera)
    if not isinstance(resposta, dict):
        sair(f"sem resposta em {espera:.0f} s ou a ilha fechou o pedido")
    decisao = resposta.get("decisao")
    if decisao == "allow":
        saida = {"behavior": "allow"}
    elif decisao == "deny":
        saida = {"behavior": "deny", "message": str(resposta.get("mensagem") or "Negado pela ilha.")}
    else:
        sair("a ilha não decidiu")
    anotar("decisão: " + saida["behavior"])
    sys.stdout.write(json.dumps({"hookSpecificOutput": {"hookEventName": "PermissionRequest", "decision": saida}}) + "\n")
    sys.stdout.flush()
    os._exit(0)


if __name__ == "__main__":
    main()
