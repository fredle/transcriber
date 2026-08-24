"""
Generates assets/transcriber.ico — the app icon.

Design: a dark rounded tile with a two-tone waveform, teal bars for the mic
(you) and orange for the speaker (others), matching the launcher's channel
colours. Bars rather than a mic glyph because they stay legible at 16px.

Re-run after changing any of the constants below:
    .venv/Scripts/python.exe make_icon.py
"""
import os

from PIL import Image, ImageDraw

BG_DARK = (30, 30, 30, 255)      # #1e1e1e — launcher background
COL_MIC = (78, 201, 176, 255)    # #4ec9b0 — mic / "you"
COL_SPK = (206, 145, 120, 255)   # #ce9178 — speaker / "others"

S = 1024                          # supersampled canvas, downscaled per icon size
ICON_SIZES = [256, 128, 64, 48, 32, 16]

# Bar heights as a fraction of canvas height, centred vertically.
BARS = [
    (0.30, COL_MIC),
    (0.58, COL_MIC),
    (0.88, COL_MIC),
    (0.46, COL_MIC),
    (0.72, COL_SPK),
    (0.40, COL_SPK),
    (0.62, COL_SPK),
]


def build_master():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    draw.rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.18), fill=BG_DARK)

    margin = S * 0.16
    span = S - 2 * margin
    slot = span / len(BARS)
    bar_w = slot * 0.52
    radius = bar_w / 2
    mid = S / 2

    for i, (height_frac, colour) in enumerate(BARS):
        cx = margin + slot * (i + 0.5)
        half = (span * height_frac) / 2
        draw.rounded_rectangle(
            [cx - bar_w / 2, mid - half, cx + bar_w / 2, mid + half],
            radius=radius, fill=colour,
        )

    return img


def main():
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "assets")
    os.makedirs(out_dir, exist_ok=True)
    ico_path = os.path.join(out_dir, "transcriber.ico")
    png_path = os.path.join(out_dir, "transcriber.png")

    master = build_master()
    master.resize((256, 256), Image.LANCZOS).save(png_path)
    master.save(ico_path, format="ICO",
                sizes=[(s, s) for s in ICON_SIZES])

    print(f"Wrote {ico_path}")
    print(f"Wrote {png_path}")


if __name__ == "__main__":
    main()
