"""Stapeltest mit echten Fotos: Engine über test/real/*.jpg, Vorher/Nachher-Bögen + Tabelle.

    python test/batch_real.py                 → alle Fotos, test/real/out/
    python test/batch_real.py Schenna Hopfen  → nur Dateien, deren Name einen der Begriffe enthält
"""

from __future__ import annotations

import csv
import re
import json
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from engine.analyzer import analyze  # noqa: E402
from engine.develop import apply_corrections  # noqa: E402
from engine.metrics import load_image  # noqa: E402

SRC = Path(__file__).resolve().parent / "real"
OUT = SRC / "out"
THUMB = 520          # Breite je Bild im Bogen
PER_SHEET = 6        # Paare pro Bogen
KEYS = ["exposure_ev", "contrast", "highlights", "shadows", "whites", "blacks",
        "temperature_shift", "tint_shift", "vibrance", "saturation", "clarity", "dehaze", "sharpen"]


def label(img: Image.Image, text: str) -> Image.Image:
    d = ImageDraw.Draw(img)
    d.rectangle((0, 0, img.width, 16), fill=(0, 0, 0))
    d.text((4, 2), text, fill=(255, 255, 255))
    return img


def short_name(stem: str) -> str:
    parts = stem.split("_")
    if len(parts) >= 3 and parts[0].isdigit():
        return parts[2]  # 20261027_132529_DSCF2226_…
    return re.sub(r"[^\w-]+", "", stem)[:40]  # 20250628-172454-Schenna[ ]


def pick_files(argv: list[str]) -> tuple[list[Path], list[str]]:
    """Ohne --src: test/real (optional nach Namensteilen gefiltert).
    Mit --src DIR --sample N [--seed S]: Zufallsstichprobe aus DIR (rekursiv)."""
    if "--src" in argv:
        import random
        opt = dict(zip(argv[::2], argv[1::2]))
        src = Path(opt["--src"])
        exts = (".raf", ".arw") if opt.get("--typ") == "raw" else (".jpg", ".jpeg")
        allf = sorted(p for p in src.rglob("*") if p.suffix.lower() in exts
                      and ".stversions" not in p.parts)
        n = int(opt.get("--sample", 100))
        random.Random(int(opt.get("--seed", 1))).shuffle(allf)
        return sorted(allf[:n]), ["stichprobe" + ("_raw" if opt.get("--typ") == "raw" else "")]
    terms = argv
    files = sorted(p for p in SRC.glob("*.jpg") if "_bearbeitet" not in p.stem
                   and (not terms or any(t.lower() in p.name.lower() for t in terms)))
    return files, terms


def main() -> None:
    OUT.mkdir(exist_ok=True)
    files, terms = pick_files(sys.argv[1:])
    prefix = "bogen_" + ("_".join(terms) + "_" if terms else "")
    rows, pairs = [], []
    for i, f in enumerate(files, 1):
        t0 = time.perf_counter()
        rgb, exif = load_image(f.read_bytes())
        c = analyze(rgb, exif)
        out = apply_corrections(rgb, c)
        dt = time.perf_counter() - t0
        g = c["global"]
        sa = c["subject_adjustments"]
        short = short_name(f.stem)
        rows.append({"datei": short, "szene": "+".join(c["metrics"]["scene"]), "conf": c["confidence"],
                     **{k: g[k] for k in KEYS}, "motiv_ev": sa["exposure_ev"] if sa["apply"] else 0,
                     "motiv": sa["source"], "drehen": c["crop"]["straighten_deg"], "ms": round(dt * 1000),
                     "analyse": c["analysis"]})
        (OUT / f"{short}.json").write_text(json.dumps(c, ensure_ascii=False, indent=1), encoding="utf-8")
        a = Image.fromarray(rgb); b = Image.fromarray(out)
        for im in (a, b):
            im.thumbnail((THUMB, THUMB))
        tag = f"{short} {'+'.join(c['metrics']['scene'])[:40]}"
        vals = " ".join(f"{k[:4]}{g[k]:+g}" for k in KEYS if g[k])
        pairs.append((label(a, tag), label(b, vals[:90])))
        print(f"[{i}/{len(files)}] {short} {dt:.1f}s {c['metrics']['scene']} {vals}", flush=True)

    with open(OUT / (prefix.replace("bogen_", "ergebnisse_") + "liste.csv" if terms else "ergebnisse.csv"), "w", newline="", encoding="utf-8-sig") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0]), delimiter=";")
        w.writeheader(); w.writerows(rows)

    for s in range(0, len(pairs), PER_SHEET):
        chunk = pairs[s:s + PER_SHEET]
        hmax = max(max(a.height, b.height) for a, b in chunk)
        sheet = Image.new("RGB", (THUMB * 2 + 10, (hmax + 6) * len(chunk)), (40, 40, 40))
        for j, (a, b) in enumerate(chunk):
            y = j * (hmax + 6)
            sheet.paste(a, (0, y)); sheet.paste(b, (THUMB + 10, y))
        sheet.save(OUT / f"{prefix}{s // PER_SHEET + 1:02d}.jpg", quality=88)
    print("fertig:", OUT)


if __name__ == "__main__":
    main()
