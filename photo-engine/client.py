"""Beispiel-Aufrufer (so würde z. B. dmOffice die Engine nutzen).

    python client.py foto.jpg [--out foto_edit.jpg] [--refine] [--server http://127.0.0.1:8765]

1. Original laden, auf 1600 px lange Kante verkleinern
2. POST /analyze → Korrektur-JSON
3. JSON auf das Original in voller Auflösung anwenden (engine.develop)
4. optional: bearbeitete Vorschau an /refine, neues JSON erneut aufs Original
"""

from __future__ import annotations

import argparse
import io
import json
import sys
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image, ImageOps

from engine.develop import apply_corrections


def post(url: str, body: bytes, ctype: str) -> dict:
    req = urllib.request.Request(url, data=body, headers={"Content-Type": ctype}, method="POST")
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read().decode("utf-8"))


def preview_jpeg(im: Image.Image, edge: int = 1600) -> bytes:
    p = im.copy()
    p.thumbnail((edge, edge), Image.LANCZOS)
    buf = io.BytesIO()
    p.save(buf, "JPEG", quality=90)
    return buf.getvalue()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("image")
    ap.add_argument("--out")
    ap.add_argument("--server", default="http://127.0.0.1:8765")
    ap.add_argument("--refine", action="store_true")
    a = ap.parse_args()

    src = Path(a.image)
    out = Path(a.out) if a.out else src.with_name(src.stem + "_edit.jpg")
    original = Image.open(src)
    exif_bytes = original.info.get("exif")
    icc = original.info.get("icc_profile")
    upright = ImageOps.exif_transpose(original).convert("RGB")

    # Vorschau inkl. EXIF schicken, damit die Engine ISO/Zeit/Uhrzeit kennt
    p = upright.copy()
    p.thumbnail((1600, 1600), Image.LANCZOS)
    buf = io.BytesIO()
    p.save(buf, "JPEG", quality=90, exif=exif_bytes or b"")
    corr = post(a.server + "/analyze", buf.getvalue(), "image/jpeg")
    print("Runde 1:", corr["analysis"])

    full = np.asarray(upright)
    result = apply_corrections(full, corr)

    if a.refine:
        import base64
        prev_jpg = preview_jpeg(Image.fromarray(result))
        body = json.dumps({"image": base64.b64encode(prev_jpg).decode(), "previous": corr,
                           "exif": corr.get("metrics", {}).get("exif")}).encode()
        corr = post(a.server + "/refine", body, "application/json")
        print(corr["analysis"].split(" | ")[-1])
        result = apply_corrections(full, corr)

    # Orientierung ist bereits angewendet → EXIF-Orientation auf 1 setzen
    save_kw = {"quality": 95, "subsampling": 0}
    if exif_bytes:
        ex = original.getexif()
        ex[0x0112] = 1
        save_kw["exif"] = ex.tobytes()
    if icc:
        save_kw["icc_profile"] = icc
    Image.fromarray(result).save(out, "JPEG", **save_kw)
    out.with_suffix(".json").write_text(json.dumps(corr, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"gespeichert: {out}  (+ {out.with_suffix('.json').name})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
