"""Generates the island's older synthesized sounds (sons/<tipo>.wav): short, subtle (sine with harmonics and decay).
Since 1.2.1 the island ships Kenney's CC0 sounds (sons/CREDITS.md); running this replaces them with the synthesized set.

To use your own sounds, put a .wav with the same name in %USERPROFILE%\\.ilha\\sons\\ (it wins over these).
Usage: python ferramentas/gerar_sons.py
"""
import math
import struct
import wave
from pathlib import Path

TAXA = 44100
SAIDA = Path(__file__).resolve().parent.parent / "sons"


def nota(freq, dur, ataque=0.006, decai=6.0, harm=(1.0, 0.35, 0.12), atraso=0.0):
    n = int(TAXA * (dur + atraso))
    out = [0.0] * n
    ini = int(TAXA * atraso)
    for i in range(ini, n):
        t = (i - ini) / TAXA
        env = min(1.0, t / ataque) * math.exp(-decai * t)
        out[i] = env * sum(a * math.sin(2 * math.pi * freq * (k + 1) * t) for k, a in enumerate(harm))
    return out


def mistura(*partes):
    n = max(len(p) for p in partes)
    out = [0.0] * n
    for p in partes:
        for i, v in enumerate(p):
            out[i] += v
    return out


def salvar(nome, amostras, pico=0.22):
    m = max(1e-9, max(abs(v) for v in amostras))
    SAIDA.mkdir(exist_ok=True)
    with wave.open(str(SAIDA / f"{nome}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(TAXA)
        w.writeframes(b"".join(struct.pack("<h", int(32767 * pico * v / m)) for v in amostras))
    print("sound:", nome)


def main():
    # chegada: duas notas subindo, suave (algo novo apareceu)
    salvar("chegada", mistura(nota(880, 0.35, decai=9), nota(1318.5, 0.45, decai=8, atraso=0.07)))
    # alerta: um sino só
    salvar("alerta", nota(1046.5, 0.6, decai=6, harm=(1.0, 0.5, 0.25, 0.1)))
    # timer acabou: três toques claros (importante: toca até no modo competitivo)
    salvar("timer", mistura(*[nota(1567.98, 0.3, decai=10, harm=(1.0, 0.3), atraso=k * 0.28) for k in range(3)]), pico=0.3)
    # claude: duas notas quentes descendo, tipo marimba
    salvar("claude", mistura(nota(659.25, 0.4, decai=10, harm=(1.0, 0.2, 0.05)), nota(523.25, 0.5, decai=9, harm=(1.0, 0.2, 0.05), atraso=0.11)))
    # fone conectou: blip subindo
    salvar("conectou", mistura(nota(740, 0.18, decai=14), nota(1108.7, 0.3, decai=10, atraso=0.08)))
    # erro: duas notas graves
    salvar("erro", mistura(nota(311.1, 0.25, decai=12, harm=(1.0, 0.4)), nota(277.2, 0.35, decai=10, harm=(1.0, 0.4), atraso=0.14)))


if __name__ == "__main__":
    main()
