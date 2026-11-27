"""Vorschau als JPEG für Formate, die dmOffice selbst nicht dekodiert (TIFF, auch 16 Bit und mehrseitig).

Aufrecht gedreht (EXIF/TIFF-Orientierung), verkleinert auf max_edge, ICC-Profil bleibt erhalten. Mehrseitige
Dateien: erste Seite.
"""

from __future__ import annotations

import io

import numpy as np
from PIL import Image, ImageOps

_SIXTEEN = ("I;16", "I;16B", "I;16L", "I;16N")


def _to_rgb8(im: Image.Image) -> Image.Image:
    """Auf 8 Bit RGB bringen. 16-Bit-Graustufen und Fließkomma würde convert('RGB') abschneiden (weiß bzw. schwarz)."""
    if im.mode in _SIXTEEN or im.mode == "I":
        a = np.asarray(im, dtype=np.float64)
        top = 65535.0 if im.mode in _SIXTEEN or a.max(initial=0) > 255 else 255.0
        return Image.fromarray(np.clip(a / top * 255.0, 0, 255).astype(np.uint8)).convert("RGB")
    if im.mode == "F":
        a = np.asarray(im, dtype=np.float64)
        lo, hi = float(np.nanmin(a)), float(np.nanmax(a))
        a = (a - lo) / (hi - lo) if hi > lo else np.zeros_like(a)
        return Image.fromarray(np.clip(np.nan_to_num(a) * 255.0, 0, 255).astype(np.uint8)).convert("RGB")
    return im.convert("RGB")


def preview_jpeg(data: bytes, max_edge: int = 2048, quality: int = 90) -> tuple[bytes, dict]:
    im = Image.open(io.BytesIO(data))
    im.seek(0)
    width, height = im.size
    icc = im.info.get("icc_profile")
    im.draft("RGB", (max_edge, max_edge))
    im = _to_rgb8(ImageOps.exif_transpose(im))
    if max(im.size) > max_edge:
        im.thumbnail((max_edge, max_edge), Image.LANCZOS)
    out = io.BytesIO()
    im.save(out, "JPEG", quality=quality, **({"icc_profile": icc} if icc else {}))
    return out.getvalue(), {"width": width, "height": height}
