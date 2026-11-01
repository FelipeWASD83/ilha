"""Prepara as animações das logos e das bolinhas para a ilha.

Lê os vídeos com alfa que o Felipe fez (videos/ChatGPT-logo.mov e videos/Claude-logo.mov, ProRes 4444 a
23,976 fps), recorta cada trecho em uma faixa horizontal de quadros (128 px cada) e desenha as animações de
bolinhas no mesmo ritmo (23,976 fps), partindo da bolinha do Claude. Escreve:

  src/Ilha/imagens/logos/<nome>.png   faixas de quadros (fora do git: logos são marcas de terceiros)
  src/Ilha/imagens/logos/claude.png, chatgpt.png   as logos paradas (fora do git)
  src/Ilha/animacoes.json             o que existe, quantos quadros, se repete (vai no git)

Uso: python ferramentas/preparar_logos.py   (precisa de ffmpeg no PATH e de numpy + Pillow)
"""
import json
import math
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

RAIZ = Path(__file__).resolve().parent.parent
VIDEOS = RAIZ / "videos"
PARADAS = RAIZ / "imagens"
SAIDA = RAIZ / "src" / "Ilha" / "imagens" / "logos"
INDICE = RAIZ / "src" / "Ilha" / "animacoes.json"

LADO = 128
FPS = 24000 / 1001
COR_CLAUDE = (217, 119, 87)   # #D97757, medida na bola e na logo parada do vídeo
BOLA_DELE = 0.0586            # diâmetro da bola do vídeo, em fração do quadro
# a logo do Claude ocupa 50% do quadro dela e a do GPT 90%: a ilha mostra a família Claude com zoom de 1,8x
# para as duas ficarem do mesmo tamanho. As bolinhas são desenhadas a 72% para caber nesse zoom.
# chave terminada em "-" vale para os nomes que começam com ela (a mais longa ganha): as bolinhas do GPT têm zoom
# próprio, porque desenhadas 1,8x maiores no quadro elas passavam da borda dele e saíam cortadas
ZOOM = {"claude": 1.8, "chatgpt": 1.0, "chatgpt-bola-": 1.8}
# bolinhas por família: a do GPT é branca e desenhada do mesmo tamanho da do Claude (a ilha amplia as duas 1,8x)
# dele para aparecer do mesmo tamanho que a do Claude
FAMILIAS = {"claude": {"cor": COR_CLAUDE, "esc": 0.72}, "chatgpt": {"cor": (255, 255, 255), "esc": 0.72}}
# o "virando bola" do GPT ("chatgpt-bola", sem o "-" do fim) começa com a logo dele, que não tem zoom: a bola que nasce
# nele é desenhada já no tamanho final (0,72 x 1,8) para emendar no "bola-cresce" ampliado
ESC_GPT_VIRANDO_BOLA = 0.72 * 1.8
COR, ESC = COR_CLAUDE, 0.72     # trocados por família em gerar_bolinhas
BOLA0 = BOLA_DELE / 0.72        # a bola do vídeo do Claude, em unidades das bolinhas (vale para as duas famílias)

# trechos (primeiro e último quadro, inclusive), achados pela diferença entre quadros seguidos:
# loop 0-119 (o 120 é igual ao 0), voltas começando e terminando paradas, pulso inteiro e suas metades
TRECHOS = {
    "chatgpt": {
        "loop": (0, 119, True), "volta": (143, 217, False), "saida": (240, 289, False),
        "entrada": (312, 361, False), "pulso": (384, 431, True), "some": (384, 404, False), "surge": (413, 431, False),
    },
    "claude": {
        "loop": (0, 119, True), "volta": (144, 217, False), "saida": (240, 289, False),
        "entrada": (312, 361, False), "pulso": (385, 431, True), "some": (385, 403, False), "surge": (414, 431, False),
        "bola": (455, 480, False),
    },
}
VIDEO = {"chatgpt": "ChatGPT-logo.mov", "claude": "Claude-logo.mov"}
PARADA = {"chatgpt": "ChatGPT-logo_00000.png", "claude": "Claude-logo_00000.png"}


def ler_quadros(video: Path) -> np.ndarray:
    """O vídeo inteiro, reduzido a 128 px por área (média), em RGBA de 8 bits."""
    cmd = ["ffmpeg", "-v", "error", "-i", str(video), "-vf", f"scale={LADO}:{LADO}:flags=area,format=rgba",
           "-f", "rawvideo", "-"]
    bruto = subprocess.run(cmd, capture_output=True, check=True).stdout
    return np.frombuffer(bruto, np.uint8).reshape(-1, LADO, LADO, 4)


def salvar_faixa(nome: str, quadros: list) -> None:
    faixa = Image.new("RGBA", (LADO * len(quadros), LADO))
    for i, q in enumerate(quadros):
        faixa.paste(q if isinstance(q, Image.Image) else Image.fromarray(q, "RGBA"), (i * LADO, 0))
    faixa.save(SAIDA / f"{nome}.png", optimize=True)


# ------------------------------------------------------------------ bolinhas

D1 = 0.26        # bola única, depois de crescer
D = 0.16         # bolinhas das animações com várias
INCLINACAO = math.radians(26)   # a órbita é vista um pouco de cima
FOCO = 2.0                      # distância da câmera (perspectiva: o que está atrás fica menor)
ESCURO = 0.4                    # brilho da bolinha mais ao fundo (a da frente fica com a cor cheia)
AMOSTRAS = 8                    # borrão de movimento: média de 8 instantes por quadro
OBTURADOR = 0.5                 # fração do quadro com o "obturador aberto" (180 graus, como câmera de cinema)
S = 4                           # desenha 4x maior e reduz (borda lisa)


def projetar(x, y, z, plana=False):
    """Mundo (centro em 0, unidade = lado do quadro) para a tela: inclina em X e aplica a perspectiva."""
    if plana:
        return 0.5 + x, 0.5 + y, 1.0, 0.0
    # vista de cima: o que está na frente (z > 0) desce na tela, o que está atrás sobe
    y2 = y * math.cos(INCLINACAO) + z * math.sin(INCLINACAO)
    z2 = -y * math.sin(INCLINACAO) + z * math.cos(INCLINACAO)
    s = FOCO / (FOCO - z2)
    return 0.5 + x * s, 0.5 + y2 * s, s, z2


def tom(z2, alcance):
    """Cor pela profundidade: do centro para a frente a cor fica cheia; atrás do centro escurece até ESCURO."""
    f = 1.0 if alcance <= 0 or z2 >= 0 else 1 - (1 - ESCURO) * min(1, -z2 / alcance)
    return tuple(int(c * f) for c in COR)


def desenhar_cena(cena) -> np.ndarray:
    """Uma cena = {"bolas": [(x, y, z, diâmetro, opacidade)], "anel": (raio, espessura, opacidade) ou None,
    "plana": bool, "alcance": float}. Devolve RGBA pré-multiplicado em float, S vezes maior."""
    L = LADO * S
    im = Image.new("RGBA", (L, L))
    plana, alcance = cena.get("plana", False), cena.get("alcance", 0.3) * ESC
    itens = []
    for x, y, z, diam, a in cena["bolas"]:
        if a <= 0.004 or diam <= 0.001:
            continue
        x, y, z, diam = x * ESC, y * ESC, z * ESC, diam * ESC
        sx, sy, esc, z2 = projetar(x, y, z, plana)
        itens.append((z2, "bola", (sx, sy, diam * esc, a, tom(z2, alcance))))
    an = cena.get("anel")
    if an and an[2] > 0.004:
        r, esp, a = an[0] * ESC, an[1] * ESC, an[2]
        for k in range(96):   # o anel em pedaços, cada um na sua profundidade (passa por trás e pela frente)
            t0, t1 = 2 * math.pi * k / 96, 2 * math.pi * (k + 1) / 96
            p0, p1 = projetar(r * math.cos(t0), 0, r * math.sin(t0)), projetar(r * math.cos(t1), 0, r * math.sin(t1))
            itens.append(((p0[3] + p1[3]) / 2, "anel", (p0, p1, esp, a, tom((p0[3] + p1[3]) / 2, r))))
    for z2, tipo, dados in sorted(itens, key=lambda i: i[0]):
        camada = Image.new("RGBA", (L, L))
        dr = ImageDraw.Draw(camada)
        if tipo == "bola":
            sx, sy, dd, a, cor = dados
            rr = dd * L / 2
            dr.ellipse((sx * L - rr, sy * L - rr, sx * L + rr, sy * L + rr), fill=cor + (int(255 * a),))
        else:
            p0, p1, esp, a, cor = dados
            dr.line((p0[0] * L, p0[1] * L, p1[0] * L, p1[1] * L), fill=cor + (int(255 * a),), width=max(1, int(esp * L)))
        im = Image.alpha_composite(im, camada)
    arr = np.asarray(im).astype(np.float32) / 255
    arr[..., :3] *= arr[..., 3:4]
    return arr


def quadro(cena_em, u) -> Image.Image:
    """Quadro u com borrão de movimento: média (pré-multiplicada) de AMOSTRAS instantes em volta de u."""
    soma = None
    for j in range(AMOSTRAS):
        uu = u + ((j + 0.5) / AMOSTRAS - 0.5) * OBTURADOR
        arr = desenhar_cena(cena_em(uu))
        soma = arr if soma is None else soma + arr
    m = soma / AMOSTRAS
    alfa = m[..., 3:4]
    rgb = np.where(alfa > 1e-6, m[..., :3] / np.maximum(alfa, 1e-6), 0)
    img = Image.fromarray((np.concatenate([rgb, alfa], axis=-1) * 255).clip(0, 255).astype(np.uint8), "RGBA")
    return img.resize((LADO, LADO), Image.LANCZOS)


def suave(t):          # ease in-out
    t = min(1.0, max(0.0, t))
    return t * t * (3 - 2 * t)


def mola(t):           # sai rápido e passa um pouco do ponto, como as molas da ilha
    t = max(0.0, t)
    return 1 - math.exp(-6 * t) * math.cos(9 * t) if t < 1 else 1.0


def bola_unica(d):
    return {"bolas": [(0, 0, 0, d, 1.0)], "plana": True}


# loops: função de t em [0, 1) para a cena, com bolinhas em 3D
def orbita(t):
    R = 0.3
    return {"bolas": [(R * math.cos(2 * math.pi * (t + k / 3)), 0, R * math.sin(2 * math.pi * (t + k / 3)), D, 1.0)
                      for k in range(3)], "alcance": R}


def onda(t):
    # três bolinhas numa roda inclinada que gira e pulam uma de cada vez
    R = 0.27
    bolas = []
    for k in range(3):
        ang = 2 * math.pi * (t + k / 3)
        pulo = 0.12 * max(0.0, math.sin(2 * math.pi * (t * 2 - k / 3))) ** 2
        bolas.append((R * math.cos(ang), -pulo, R * math.sin(ang), D, 1.0))
    return {"bolas": bolas, "alcance": R}


def cometa(t):
    R = 0.3
    bolas = []
    for k in range(7):
        ang = 2 * math.pi * t - k * 0.38
        bolas.append((R * math.cos(ang), 0, R * math.sin(ang), D * (1 - k * 0.1), 1 - k * 0.13))
    return {"bolas": bolas, "alcance": R}


def anel(t):
    return {"bolas": [(0, 0, 0, 0.22 * (1 + 0.06 * math.sin(2 * math.pi * t)), 1.0)],
            "anel": (0.13 + 0.3 * t, 0.03, 0.75 * (1 - t) ** 1.4), "alcance": 0.3}


def divide(t):
    sep = 0.22 * math.sin(math.pi * t) ** 2
    ang = 2 * math.pi * t * 2
    return {"bolas": [(sep * math.cos(ang + k * math.pi), 0, sep * math.sin(ang + k * math.pi), 0.18, 1.0) for k in range(2)],
            "alcance": 0.22}


# nome: (função, quadros por volta)
LOOPS_DE_BOLA = {"orbita": (orbita, 36), "onda": (onda, 48), "cometa": (cometa, 30), "anel": (anel, 36), "divide": (divide, 48)}


def transicao(f, n=9):
    """Da bola única (D1, no centro) até o primeiro instante do loop: cada bolinha sai do centro, em 3D."""
    c0 = f(0.0)

    def em(u):
        e = mola(u / (n - 1))
        bolas = [(x * e, y * e, z * e, D1 + (dd - D1) * min(1, e), a if k == 0 else a * min(1, max(0, e)))
                 for k, (x, y, z, dd, a) in enumerate(c0["bolas"])]
        cena = dict(c0, bolas=bolas)
        if c0.get("anel"):
            r, esp, a = c0["anel"]
            cena["anel"] = (r, esp, a * min(1, max(0, e)))
        return cena
    return [quadro(em, i) for i in range(n)]


def gpt_virando_bola() -> list:
    """O GPT não tem "virando bola" no vídeo: a logo encolhe até sumir (o trecho "some" dele) e no lugar nasce a
    bolinha branca, que cresce até o tamanho da bola do Claude."""
    faixa = Image.open(SAIDA / "chatgpt-some.png").convert("RGBA")
    some = [faixa.crop((i * LADO, 0, (i + 1) * LADO, LADO)) for i in range(faixa.width // LADO)]
    nasce = [quadro(lambda u: bola_unica(BOLA0 * suave(u / 11)), i) for i in range(12)]
    return some + nasce


def gerar_bolinhas(indice: dict) -> None:
    global COR, ESC
    for familia, cfg in FAMILIAS.items():
        COR, ESC = cfg["cor"], cfg["esc"]
        if familia == "chatgpt":
            ESC = ESC_GPT_VIRANDO_BOLA
            q = gpt_virando_bola()
            salvar_faixa("chatgpt-bola", q)
            indice["chatgpt-bola"] = {"quadros": len(q), "loop": False}
            ESC = cfg["esc"]
        # a bola do vídeo cresce até a bola única, e o caminho de volta (encolhe até sumir, para a logo surgir)
        cresce = [quadro(lambda u: bola_unica(BOLA0 + (D1 - BOLA0) * mola(u / 9)), i) for i in range(10)]
        encolhe = [quadro(lambda u: bola_unica(D1 * (1 - suave(u / 7))), i) for i in range(8)]
        for nome, q in (("bola-cresce", cresce), ("bola-encolhe", encolhe)):
            salvar_faixa(f"{familia}-{nome}", q)
            indice[f"{familia}-{nome}"] = {"quadros": len(q), "loop": False}
        for nome, (f, n) in LOOPS_DE_BOLA.items():
            loop = [quadro(lambda u, f=f, n=n: f((u / n) % 1.0), i) for i in range(n)]
            ida = transicao(f)
            salvar_faixa(f"{familia}-bola-{nome}", loop)
            salvar_faixa(f"{familia}-bola-{nome}-entrada", ida)
            salvar_faixa(f"{familia}-bola-{nome}-saida", list(reversed(ida)))
            indice[f"{familia}-bola-{nome}"] = {"quadros": n, "loop": True}
            indice[f"{familia}-bola-{nome}-entrada"] = {"quadros": len(ida), "loop": False}
            indice[f"{familia}-bola-{nome}-saida"] = {"quadros": len(ida), "loop": False}
            print(f"{familia} dots: {nome} done")


def main() -> None:
    import sys
    SAIDA.mkdir(parents=True, exist_ok=True)
    so_bolinhas = "--so-bolinhas" in sys.argv   # refaz só as bolinhas, sem reler os vídeos (que pesam 3 GB)
    indice = {}
    if so_bolinhas and INDICE.exists():
        indice = {k: v for k, v in json.loads(INDICE.read_text(encoding="utf-8"))["animacoes"].items() if "-bola-" not in k and "bola-cresce" not in k and "bola-encolhe" not in k}
    for logo, trechos in ({} if so_bolinhas else TRECHOS).items():
        quadros = ler_quadros(VIDEOS / VIDEO[logo])
        print(f"{logo}: {len(quadros)} frames read")
        for nome, (a, b, rep) in trechos.items():
            salvar_faixa(f"{logo}-{nome}", list(quadros[a:b + 1]))
            indice[f"{logo}-{nome}"] = {"quadros": b - a + 1, "loop": rep}
        Image.open(PARADAS / PARADA[logo]).convert("RGBA").resize((512, 512), Image.LANCZOS).save(SAIDA / f"{logo}.png", optimize=True)
    gerar_bolinhas(indice)
    INDICE.write_text(json.dumps({"fps": FPS, "lado": LADO, "zoom": ZOOM, "animacoes": indice}, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(indice)} animations in {SAIDA}")


if __name__ == "__main__":
    main()
