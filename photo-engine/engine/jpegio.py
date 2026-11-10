"""Original-JPG entwickeln: Orientierung beachten, Metadaten byteweise durchreichen."""

from __future__ import annotations

import io
import os
import struct

import numpy as np
from PIL import Image, ImageOps

from . import rawio
from .develop import apply_corrections

Image.MAX_IMAGE_PIXELS = None  # eigene Grenze statt Pillows Bomben-Schutz
# Vollauflösung: ~3 Byte/Pixel Eingabe + JPEG-Puffer; 210 MP ≈ 1,3 GB Spitze
MAX_PIXELS = int(os.environ.get("PHOTO_ENGINE_MAX_PIXELS", "210000000"))


class ImageTooLarge(ValueError):
    pass

# Transpose, die das gespeicherte Bild aufrecht dreht, und ihre Umkehrung
_UPRIGHT = {2: Image.FLIP_LEFT_RIGHT, 3: Image.ROTATE_180, 4: Image.FLIP_TOP_BOTTOM,
            5: Image.TRANSPOSE, 6: Image.ROTATE_270, 7: Image.TRANSVERSE, 8: Image.ROTATE_90}
_INVERSE = {2: Image.FLIP_LEFT_RIGHT, 3: Image.ROTATE_180, 4: Image.FLIP_TOP_BOTTOM,
            5: Image.TRANSPOSE, 6: Image.ROTATE_90, 7: Image.TRANSVERSE, 8: Image.ROTATE_270}


def _app_segments(data: bytes) -> list[bytes]:
    """APPn- und COM-Segmente des Originals (EXIF, XMP, ICC, IPTC …).

    Ausgelassen: APP14 „Adobe“ (Farbtransformation passt nicht zum neuen
    YCbCr-Strom) und APP2 „MPF“ (Offsets auf eingebettete Bilder wären falsch).
    """
    if data[:2] != b"\xff\xd8":
        return []
    out, i, n = [], 2, len(data)
    while i + 4 <= n and data[i] == 0xFF:
        marker = data[i + 1]
        if marker == 0xFF:  # Füllbyte
            i += 1
            continue
        if not (0xE0 <= marker <= 0xEF or marker == 0xFE):
            break  # DQT/SOF/… → Ende des Metadatenblocks
        length = struct.unpack(">H", data[i + 2:i + 4])[0]
        seg = data[i:i + 2 + length]
        payload = seg[4:]
        skip = (marker == 0xEE and payload.startswith(b"Adobe")) or (marker == 0xE2 and payload.startswith(b"MPF\x00"))
        if not skip:
            out.append(seg)
        i += 2 + length
    return out


def _strip_app(data: bytes) -> bytes:
    """Neu kodiertes JPG ohne eigene APP-Segmente (nur SOI + Bilddaten)."""
    i = 2
    while i + 4 <= len(data) and data[i] == 0xFF and (0xE0 <= data[i + 1] <= 0xEF or data[i + 1] == 0xFE):
        i += 2 + struct.unpack(">H", data[i + 2:i + 4])[0]
    return data[i:]


def develop_jpeg(data: bytes, corrections: dict, *, max_edge: int | None = None, quality: int = 93) -> tuple[bytes, dict]:
    """Wendet die Korrekturen auf ein Original an.

    Vollauflösung (max_edge None): Pixel bleiben in der gespeicherten Ausrichtung,
    alle Metadaten-Segmente werden unverändert übernommen (Orientation gilt weiter).
    Vorschau (max_edge gesetzt): aufrecht gedreht, verkleinert, nur ICC-Profil, keine EXIF.
    """
    if rawio.is_raw(data):
        return _develop_raw(data, corrections, max_edge=max_edge, quality=quality)

    im = Image.open(io.BytesIO(data))
    orientation = 1
    try:
        orientation = int(im.getexif().get(0x0112, 1) or 1)
    except Exception:
        pass
    icc = im.info.get("icc_profile")

    if max_edge:
        im.draft("RGB", (max_edge, max_edge))  # DCT-Skalierung: schnelles Dekodieren
        im = ImageOps.exif_transpose(im).convert("RGB")
        if max(im.size) > max_edge:
            im.thumbnail((max_edge, max_edge), Image.LANCZOS)
    else:
        if im.size[0] * im.size[1] > MAX_PIXELS:
            raise ImageTooLarge(
                f"Bild hat {im.size[0] * im.size[1] / 1e6:.0f} MP, Grenze für Vollauflösung "
                f"{MAX_PIXELS / 1e6:.0f} MP (PHOTO_ENGINE_MAX_PIXELS)")
        im = im.convert("RGB")
        if orientation in _UPRIGHT:
            im = im.transpose(_UPRIGHT[orientation])

    arr = np.array(im)  # beschreibbare Kopie; die Entwicklung schreibt hinein
    del im
    out = apply_corrections(arr, corrections, inplace=True)
    del arr
    res = Image.fromarray(out)

    if not max_edge and orientation in _INVERSE:
        res = res.transpose(_INVERSE[orientation])

    buf = io.BytesIO()
    kw = {"quality": int(quality), "subsampling": 0 if quality >= 90 else 2, "optimize": False}
    if max_edge and icc:
        kw["icc_profile"] = icc
    res.save(buf, "JPEG", **kw)
    encoded = buf.getvalue()

    if not max_edge:
        segs = _app_segments(data)
        if segs:
            encoded = b"\xff\xd8" + b"".join(segs) + _strip_app(encoded)
    info = {"width": res.size[0], "height": res.size[1], "orientation_kept": None if max_edge else orientation}
    return encoded, info


_SRGB_ICC: bytes | None = None


def _srgb_icc() -> bytes | None:
    global _SRGB_ICC
    if _SRGB_ICC is None:
        try:
            from PIL import ImageCms
            _SRGB_ICC = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()
        except Exception:
            _SRGB_ICC = b""
    return _SRGB_ICC or None


def _develop_raw(data: bytes, corrections: dict, *, max_edge: int | None, quality: int) -> tuple[bytes, dict]:
    """RAW → JPG. Immer aufrecht (Orientation 1), sRGB; Vollauflösung mit Kamera-EXIF."""
    if not max_edge:
        w, h = rawio.size(data)
        if w * h > MAX_PIXELS:
            raise ImageTooLarge(
                f"Bild hat {w * h / 1e6:.0f} MP, Grenze für Vollauflösung "
                f"{MAX_PIXELS / 1e6:.0f} MP (PHOTO_ENGINE_MAX_PIXELS)")
        arr = rawio.decode(data)
    else:
        # halbe Auflösung reicht für jede Vorschau bis ~2500 px und ist um ein Vielfaches schneller
        arr = rawio.decode(data, half=True)
        if max(arr.shape[:2]) > max_edge:
            im = Image.fromarray(arr)
            im.thumbnail((max_edge, max_edge), Image.LANCZOS)
            arr = np.array(im)
    if not arr.flags.writeable:
        arr = np.array(arr)
    out = apply_corrections(arr, corrections, inplace=True)
    res = Image.fromarray(out)

    buf = io.BytesIO()
    kw = {"quality": int(quality), "subsampling": 0 if quality >= 90 else 2, "optimize": False}
    icc = _srgb_icc()
    if icc:
        kw["icc_profile"] = icc
    if not max_edge:
        ex = rawio.exif_bytes_for_output(data, res.size[0], res.size[1])
        if ex:
            kw["exif"] = ex
    res.save(buf, "JPEG", **kw)
    return buf.getvalue(), {"width": res.size[0], "height": res.size[1], "orientation_kept": None}
