"""Synthesizes the island's sounds (sons/<type>.wav and sons/timer-loop.wav).

Soft UI sounds in one family: tones that swell in (40 to 120 ms) instead of clicking, warm (most energy around
1 to 2 kHz), voiced in fifths and octaves around F major, a pair of slightly detuned layers spread left and right, and a
short room reverb. The timer loop is a small marimba-like melody over a soft pad, rendered as a seamless loop (the
reverb tail of the end wraps into the start).

To use your own sounds, put a .wav with the same name in %USERPROFILE%\\.ilha\\sons\\ (it wins over these).
Usage: python ferramentas/gerar_sons.py [--out FOLDER]
"""
import argparse
import math
import wave
from pathlib import Path

import numpy as np

SR = 48000
OUT = Path(__file__).resolve().parent.parent / "sons"
RNG = np.random.default_rng(7)  # fixed seed: the same files on every run


def hz(note: str) -> float:
    """'F5' -> frequency (A4 = 440)."""
    names = {"C": 0, "C#": 1, "D": 2, "D#": 3, "E": 4, "F": 5, "F#": 6, "G": 7, "G#": 8, "A": 9, "A#": 10, "B": 11}
    n, octave = note[:-1], int(note[-1])
    midi = 12 * (octave + 1) + names[n]
    return 440.0 * 2 ** ((midi - 69) / 12)


def envelope(n: int, attack: float, decay: float, release: float = 0.04) -> np.ndarray:
    """Raised-cosine attack, exponential decay (time constant `decay`), short fade at the very end."""
    t = np.arange(n) / SR
    a = np.clip(t / max(attack, 1e-4), 0, 1)
    env = (0.5 - 0.5 * np.cos(np.pi * a)) * np.exp(-np.maximum(t - attack, 0) / decay)
    r = int(release * SR)
    if r > 0 and r < n:
        env[-r:] *= np.linspace(1, 0, r) ** 2
    return env


def soft_tone(freq: float, dur: float, attack=0.07, decay=0.35, bright=1.0, detune_cents=5.0, glide_cents=0.0):
    """The swelling UI tone: a harmonic stack whose upper partials fade faster, as two detuned layers (L and R)."""
    n = int(dur * SR)
    t = np.arange(n) / SR
    env = envelope(n, attack, decay)
    glide = 2 ** ((glide_cents / 1200) * (1 - np.exp(-t / 0.12)))  # a little rise that settles
    partials = [(1, 1.0), (2, 0.32 * bright), (3, 0.11 * bright), (4, 0.05 * bright), (6, 0.015 * bright)]
    out = np.zeros((n, 2))
    for side, cents in ((0, -detune_cents), (1, detune_cents)):
        f0 = freq * 2 ** (cents / 1200)
        layer = np.zeros(n)
        for k, amp in partials:
            # upper partials decay faster: the tone starts airy and settles into a soft core
            pe = np.exp(-t / (decay / (1 + 0.6 * (k - 1))))
            phase = 2 * np.pi * np.cumsum(f0 * k * glide) / SR + RNG.uniform(0, 2 * np.pi)
            layer += amp * pe * np.sin(phase)
        out[:, side] = layer * env
    # each side keeps a quarter of the other: wide, but still solid in headphones and when summed to mono
    return np.stack([out[:, 0] * 0.75 + out[:, 1] * 0.25, out[:, 1] * 0.75 + out[:, 0] * 0.25], axis=1)


def mallet(freq: float, dur: float, decay=0.45, hardness=0.6):
    """Marimba-like note for the melody: quick but not clicky attack, inharmonic overtones that die fast."""
    n = int(dur * SR)
    t = np.arange(n) / SR
    env = envelope(n, 0.004, decay, release=0.03)
    tone = (np.sin(2 * np.pi * freq * t)
            + 0.22 * hardness * np.sin(2 * np.pi * freq * 3.93 * t) * np.exp(-t / 0.05)
            + 0.08 * hardness * np.sin(2 * np.pi * freq * 9.2 * t) * np.exp(-t / 0.018)
            + 0.18 * np.sin(2 * np.pi * freq * 2 * t) * np.exp(-t / (decay * 0.5)))
    # a breath of soft noise in the first milliseconds gives the felt-mallet contact
    noise = RNG.normal(0, 1, n) * np.exp(-t / 0.006) * 0.05 * hardness
    noise = np.convolve(noise, np.ones(8) / 8, mode="same")
    mono = (tone + noise) * env
    pan = 0.5 + 0.12 * math.sin(freq)  # each pitch sits at a slightly different place
    return np.stack([mono * (1 - pan) * 2 ** 0.5, mono * pan * 2 ** 0.5], axis=1) * 0.7


def pad(freqs, dur, attack=0.5, level=0.12):
    """A quiet, slow pad under the melody (sines with a slow shimmer)."""
    n = int(dur * SR)
    t = np.arange(n) / SR
    env = np.clip(t / attack, 0, 1) * np.clip((dur - t) / attack, 0, 1)
    out = np.zeros((n, 2))
    for i, f in enumerate(freqs):
        for side, cents in ((0, -7), (1, 7)):
            ff = f * 2 ** (cents / 1200)
            trem = 1 + 0.15 * np.sin(2 * np.pi * (0.35 + 0.07 * i) * t + side)
            out[:, side] += np.sin(2 * np.pi * ff * t) * trem
    return out * env[:, None] * level / max(1, len(freqs))


def place(buf: np.ndarray, sound: np.ndarray, at: float, gain=1.0):
    i = int(at * SR)
    end = min(len(buf), i + len(sound))
    buf[i:end] += sound[: end - i] * gain


def lowpass_fast(x: np.ndarray, cutoff: float) -> np.ndarray:
    """Gentle low-pass (one-pole response, 6 dB/octave), done in the frequency domain."""
    n = len(x)
    f = np.fft.rfftfreq(n * 2, 1 / SR)
    h = 1 / np.sqrt(1 + (f / cutoff) ** 2)
    X = np.fft.rfft(np.vstack([x, np.zeros_like(x)]), axis=0)
    return np.fft.irfft(X * h[:, None], axis=0)[:n]


def room_ir(seconds=1.1, rt60=0.9, predelay=0.012):
    """Stereo impulse response: a few early reflections and a decaying, darkening noise tail."""
    n = int(seconds * SR)
    t = np.arange(n) / SR
    ir = np.zeros((n, 2))
    for side in (0, 1):
        tail = RNG.normal(0, 1, n) * np.exp(-6.9 * t / rt60)
        tail[: int(predelay * SR)] = 0
        ir[:, side] = tail
        for d, g in ((0.007, 0.5), (0.013, 0.35), (0.021, 0.25), (0.029, 0.18)):
            ir[int((d + 0.002 * side) * SR), side] += g
    ir = lowpass_fast(ir, 3500)
    return ir / np.sqrt((ir ** 2).sum(axis=0, keepdims=True))


IR = room_ir()


def reverb(x: np.ndarray, wet=0.16, circular=False) -> np.ndarray:
    """Convolution with the room. `circular`: the tail wraps around (for a seamless loop)."""
    n = len(x)
    size = 1 << int(math.ceil(math.log2(n + len(IR))))
    y = np.fft.irfft(np.fft.rfft(x, size, axis=0) * np.fft.rfft(IR, size, axis=0), size, axis=0)[: n + len(IR)]
    if circular:
        wet_sig = fold(y, n)
    else:
        wet_sig = y
        x = np.vstack([x, np.zeros((len(IR), 2))])
    return x * (1 - wet) + wet_sig * wet * 3.0


def fold(x: np.ndarray, n: int) -> np.ndarray:
    """Whatever rings past `n` samples is added back at the start: a buffer that loops with no cut."""
    out = x[:n].copy()
    rest = x[n:]
    while len(rest):
        k = min(len(rest), n)
        out[:k] += rest[:k]
        rest = rest[k:]
    return out


def finish(x: np.ndarray, target_rms_db=-21.0, peak_db=-1.5, tail_fade=0.08, circular=False) -> np.ndarray:
    """Warmth (gentle saturation), trims silence at the end, same loudness for every sound, peak limit."""
    x = np.tanh(x * 1.2) / 1.2
    if not circular:
        level = np.abs(x).max(axis=1)
        keep = np.nonzero(level > 10 ** (-60 / 20) * level.max())[0]
        x = x[: keep[-1] + 1] if len(keep) else x
        f = min(int(tail_fade * SR), len(x) // 4)
        x[-f:] *= np.linspace(1, 0, f)[:, None] ** 2
    rms = np.sqrt((x ** 2).mean())
    x = x * (10 ** (target_rms_db / 20) / max(rms, 1e-9))
    peak = np.abs(x).max()
    limit = 10 ** (peak_db / 20)
    if peak > limit:
        x *= limit / peak
    return x


def save(path: Path, x: np.ndarray):
    data = (np.clip(x, -1, 1) * 32767).astype("<i2")
    with wave.open(str(path), "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(data.tobytes())


# ------------------------------------------------------------------ the sounds

def chegada():
    """A message or notice arriving: two soft notes rising a fifth."""
    b = np.zeros((int(1.3 * SR), 2))
    place(b, soft_tone(hz("F5"), 1.0, attack=0.04, decay=0.30), 0.0, 0.9)
    place(b, soft_tone(hz("F4"), 1.0, attack=0.05, decay=0.32, bright=0.4), 0.0, 0.35)
    place(b, soft_tone(hz("C6"), 1.1, attack=0.05, decay=0.40), 0.11, 0.8)
    return reverb(lowpass_fast(b, 5000), wet=0.18)


def alerta():
    """General alert: an open chord that blooms (F, C, F) with an A on top a moment later."""
    b = np.zeros((int(1.5 * SR), 2))
    for note, at, g in (("F4", 0.0, 0.55), ("C5", 0.0, 0.6), ("F5", 0.02, 0.6), ("A5", 0.09, 0.45)):
        place(b, soft_tone(hz(note), 1.2, attack=0.11, decay=0.42, bright=0.8), at, g)
    return reverb(lowpass_fast(b, 4200), wet=0.2)


def claude():
    """Claude Code finished or wants you: a warm C and G that rise a touch, with a soft octave shimmer."""
    b = np.zeros((int(1.5 * SR), 2))
    place(b, soft_tone(hz("C5"), 1.2, attack=0.08, decay=0.42, glide_cents=18), 0.0, 0.75)
    place(b, soft_tone(hz("G5"), 1.2, attack=0.10, decay=0.40, glide_cents=18), 0.03, 0.6)
    place(b, soft_tone(hz("C6"), 1.0, attack=0.25, decay=0.30, bright=0.3), 0.08, 0.25)
    return reverb(lowpass_fast(b, 4500), wet=0.2)


def conectou():
    """A device connecting: a quick rising arpeggio that lands bright."""
    b = np.zeros((int(1.4 * SR), 2))
    for note, at, g in (("F4", 0.0, 0.5), ("C5", 0.065, 0.55), ("F5", 0.13, 0.65), ("C6", 0.2, 0.45)):
        place(b, soft_tone(hz(note), 1.0, attack=0.035, decay=0.30, bright=1.1), at, g)
    return reverb(lowpass_fast(b, 6000), wet=0.17)


def erro():
    """Error: two muted notes going down a minor third, low and short, never harsh."""
    b = np.zeros((int(0.9 * SR), 2))
    place(b, soft_tone(hz("D4"), 0.5, attack=0.02, decay=0.16, bright=0.7), 0.0, 0.8)
    place(b, soft_tone(hz("B3"), 0.6, attack=0.02, decay=0.20, bright=0.7), 0.13, 0.85)
    place(b, soft_tone(hz("D3"), 0.6, attack=0.03, decay=0.22, bright=0.3), 0.0, 0.35)
    return reverb(lowpass_fast(b, 2200), wet=0.12)


def timer():
    """A single chime (pomodoro phases and timers that ended with the island closed): C, E, G and a high C."""
    b = np.zeros((int(1.8 * SR), 2))
    for note, at, g in (("C5", 0.0, 0.6), ("E5", 0.05, 0.55), ("G5", 0.10, 0.55), ("C6", 0.16, 0.5)):
        place(b, mallet(hz(note), 1.4, decay=0.5, hardness=0.5), at, g)
    place(b, pad([hz("C4"), hz("G4")], 1.5, attack=0.3, level=0.25), 0.0)
    return reverb(b, wet=0.2)


def timer_loop():
    """The timer finishing, played in a loop until it is dismissed: a small marimba melody in F over a soft pad.
    Two bars at 104 bpm and a short rest; the reverb tail of the end wraps into the start, so the seam is silent."""
    beat = 60 / 104
    melody = [  # (note, beat, length in beats)
        ("F5", 0, 0.5), ("A5", 0.5, 0.5), ("C6", 1, 0.5), ("A5", 1.5, 0.5),
        ("G5", 2, 0.5), ("C6", 2.5, 0.5), ("E6", 3, 1.0),
        ("D6", 4, 0.5), ("C6", 4.5, 0.5), ("A5", 5, 0.5), ("G5", 5.5, 0.5),
        ("F5", 6, 1.5),
    ]
    total_beats = 8  # the last 1.5 beats rest
    n = int(total_beats * beat * SR)
    b = np.zeros((n + 3 * SR, 2))  # room for the notes that ring past the end; folded back below
    for note, at, length in melody:
        place(b, mallet(hz(note), min(1.6, length * beat + 0.9), decay=0.32 + 0.12 * length, hardness=0.55), at * beat, 0.62)
    # the same melody an octave down, quiet, for body
    for note, at, length in melody[::2]:
        f = hz(note) / 2
        place(b, mallet(f, 1.2, decay=0.35, hardness=0.3), at * beat, 0.22)
    # a soft pad: F for the first bar, then C, back home
    place(b, pad([hz("F3"), hz("C4"), hz("A4")], 4 * beat + 0.4, attack=0.6, level=0.5), 0.0)
    place(b, pad([hz("C3"), hz("G3"), hz("E4")], 4 * beat, attack=0.6, level=0.5), 4 * beat - 0.2)
    # what rings past the end goes back to the start, then the circular room
    return reverb(fold(b, n), wet=0.2, circular=True)


def gol():
    """Our goal in a game: a quick bright fanfare climbing F, A, C, F with a held top chord."""
    b = np.zeros((int(1.6 * SR), 2))
    for note, at, g in (("F5", 0.0, 0.55), ("A5", 0.07, 0.55), ("C6", 0.14, 0.6), ("F6", 0.21, 0.5)):
        place(b, soft_tone(hz(note), 0.9, attack=0.02, decay=0.22, bright=1.2), at, g)
    for note in ("F5", "A5", "C6"):
        place(b, soft_tone(hz(note), 1.2, attack=0.05, decay=0.45, bright=0.9), 0.3, 0.38)
    return reverb(lowpass_fast(b, 6500), wet=0.2)


def vitoria():
    """A match won: a warm major chord that blooms under a small mallet run up the octave."""
    b = np.zeros((int(2.2 * SR), 2))
    for note, at in (("C5", 0.0), ("E5", 0.08), ("G5", 0.16), ("C6", 0.24), ("E6", 0.32)):
        place(b, mallet(hz(note), 1.4, decay=0.5, hardness=0.6), at, 0.55)
    place(b, pad([hz("C4"), hz("G4"), hz("E5")], 1.9, attack=0.25, level=0.35), 0.1)
    return reverb(b, wet=0.22)


SOUNDS = {
    "chegada": chegada,
    "alerta": alerta,
    "claude": claude,
    "conectou": conectou,
    "erro": erro,
    "timer": timer,
    "timer-loop": timer_loop,
    # new sounds go last: they share the seeded generator, and the ones above must stay the same files
    "gol": gol,
    "vitoria": vitoria,
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(OUT))
    out = Path(ap.parse_args().out)
    out.mkdir(parents=True, exist_ok=True)
    for name, make in SOUNDS.items():
        x = finish(make(), circular=(name == "timer-loop"), target_rms_db=-23 if name == "timer-loop" else -21)
        save(out / f"{name}.wav", x)
        print(f"{name:11s} {len(x) / SR:.2f} s")


if __name__ == "__main__":
    main()
