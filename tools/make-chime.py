"""Generates sounds/chime.wav, the bundled "prompt finished" chime.

A soft two-note bell (E6 then A6) in the spirit of Cursor's "done" sound, made from
scratch so it can be shipped with this repo. Only needed to regenerate the file:

    python tools/make-chime.py
"""
import math
import pathlib
import struct
import wave

RATE = 44100
LENGTH = 0.9  # seconds
PEAK = 0.19   # of full scale, about -14.5 dBFS, as loud as Cursor's done1.mp3

# (start s, frequency Hz, loudness, decay time constant s)
NOTES = [
    (0.00, 1318.51, 0.8, 0.16),
    (0.11, 1760.00, 1.0, 0.22),
]
# (frequency multiple, loudness): a little shimmer on top of each note
PARTIALS = [(1.0, 1.0), (2.0, 0.22), (3.0, 0.06)]
ATTACK = 0.004


def sample(t):
    total = 0.0
    for start, freq, loud, tau in NOTES:
        dt = t - start
        if dt < 0:
            continue
        env = min(1.0, dt / ATTACK) * math.exp(-dt / tau)
        for mult, part in PARTIALS:
            # higher partials fade faster, like a real bell
            total += loud * part * env ** mult * math.sin(2 * math.pi * freq * mult * dt)
    return total


def main():
    n = int(RATE * LENGTH)
    raw = [sample(i / RATE) for i in range(n)]
    scale = PEAK / max(abs(x) for x in raw)
    fade = int(RATE * 0.02)  # last 20 ms to exact silence, no click
    frames = bytearray()
    for i, x in enumerate(raw):
        if i >= n - fade:
            x *= (n - i) / fade
        frames += struct.pack("<h", int(round(x * scale * 32767)))

    out = pathlib.Path(__file__).resolve().parent.parent / "sounds" / "chime.wav"
    out.parent.mkdir(exist_ok=True)
    with wave.open(str(out), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(bytes(frames))
    print("wrote", out, len(frames), "bytes of audio")


if __name__ == "__main__":
    main()
