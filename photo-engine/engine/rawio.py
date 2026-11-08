"""RAW-Dateien (Fuji .RAF, Sony .ARW und alles, was LibRaw kennt).

Die RAW-Daten werden einheitlich zu einem 8-Bit-sRGB-Bild entwickelt
(Kamera-Weißabgleich, lichterschonend, Ausrichtung angewendet). Auf dieses
Grundbild beziehen sich /analyze und /develop – genau wie bei einem JPG.
EXIF kommt aus dem eingebetteten Vorschau-JPEG der Kamera.
"""

from __future__ import annotations

import hashlib
import io
import threading
from collections import OrderedDict

import numpy as np
from PIL import Image, ImageOps

try:
    import rawpy
except ImportError:  # pragma: no cover
    rawpy = None

_TIFF = (b"II*\x00", b"MM\x00*")


def _params(half: bool) -> dict:
    return dict(
        half_size=half,
        use_camera_wb=True,
        output_color=rawpy.ColorSpace.sRGB,
        output_bps=16,                             # 16 Bit: Reserven für die Tonwertanpassung
        gamma=(2.4, 12.92),                        # sRGB-Kurve
        no_auto_bright=True,                       # Belichtung wie aufgenommen, Kurve kommt danach
        highlight_mode=rawpy.HighlightMode.Blend,  # ausgebrannte Kanäle mischen statt Magenta
    )


_LUMA = np.array([0.2126, 0.7152, 0.0722], np.float32)
_LUT_N = 4096
_ROLL_START = 0.94  # oberhalb: RAW-Abstufung behalten statt wie die Kamera abzuschneiden


def _tone_lut(base16: np.ndarray, camera: Image.Image | None) -> np.ndarray:
    """Tonwertkurve (Luminanz 0…1 → 0…1), die das RAW an das Kamera-JPG angleicht.

    Quantil-Abgleich der Helligkeitsverteilungen; ab _ROLL_START wird die
    Kurve weich zu Weiß geführt, damit RAW-Lichter Zeichnung behalten.
    """
    step = max(1, max(base16.shape[:2]) // 800)
    Yb = (base16[::step, ::step].astype(np.float32) / 65535.0) @ _LUMA
    grid = np.linspace(0, 1, _LUT_N, dtype=np.float32)
    q = np.linspace(0, 1, 1001)
    xb = np.quantile(Yb, q)
    if camera is not None:
        cam = np.asarray(camera.convert("RGB").resize((Yb.shape[1], Yb.shape[0]), Image.BILINEAR),
                         dtype=np.float32) / 255.0
        yt = np.quantile(cam @ _LUMA, q)
    else:
        # ohne Kamera-Vorschau: Mitteltöne auf normale Helligkeit (Median → 0,42), sanft
        k = float(np.clip(0.42 / max(np.median(Yb), 1e-4), 1.0, 8.0))
        yt = np.minimum(xb * k, 1.0)
    xb, idx = np.unique(xb, return_index=True)
    lut = np.interp(grid, xb, yt[idx]).astype(np.float32)
    lut = np.maximum.accumulate(lut)
    # Lichter: ab dem Punkt, an dem die Kurve _ROLL_START erreicht, linear zu (1, 1)
    hit = np.searchsorted(lut, _ROLL_START)
    if hit < _LUT_N - 1:
        x0 = grid[hit]
        lut[hit:] = _ROLL_START + (grid[hit:] - x0) / max(1.0 - x0, 1e-4) * (1.0 - _ROLL_START)
    return np.clip(lut, 0.0, 1.0)


def _apply_lut(rgb16: np.ndarray, lut: np.ndarray, rows: int = 256) -> np.ndarray:
    """Kurve auf die Luminanz, Farbverhältnisse bleiben (streifenweise → wenig Speicher)."""
    h = rgb16.shape[0]
    out = np.empty(rgb16.shape, np.uint8)
    for y0 in range(0, h, rows):
        f = rgb16[y0:y0 + rows].astype(np.float32) * np.float32(1 / 65535.0)
        Y = f @ _LUMA
        Yn = lut[np.clip((Y * (_LUT_N - 1)).astype(np.int32), 0, _LUT_N - 1)]
        f *= (Yn / np.maximum(Y, 1e-5))[..., None]
        out[y0:y0 + rows] = (np.clip(f, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)
    return out


def is_raw(data: bytes) -> bool:
    """RAF erkennt man an der Signatur; ARW ist TIFF-basiert → LibRaw entscheidet."""
    if rawpy is None:
        return False
    if data[:15] == b"FUJIFILMCCD-RAW":
        return True
    if data[:4] in _TIFF:
        try:
            with rawpy.imread(io.BytesIO(data)):
                return True
        except Exception:
            return False  # gewöhnliches TIFF → Pillow
    return False


def size(data: bytes) -> tuple[int, int]:
    """Breite, Höhe des entwickelten (aufrechten) Bildes, ohne zu entwickeln."""
    try:
        with rawpy.imread(io.BytesIO(data)) as r:
            w, h = r.sizes.width, r.sizes.height
            return (h, w) if r.sizes.flip in (5, 6) else (w, h)
    except Exception as e:
        raise ValueError(f"RAW nicht lesbar: {e}") from e


_CACHE: "OrderedDict[str, np.ndarray]" = OrderedDict()
_CACHE_LOCK = threading.Lock()
_CACHE_MAX = 4  # Grundbilder in halber Auflösung (~20–30 MB je Bild)


def decode(data: bytes, *, half: bool = False) -> np.ndarray:
    """Wie _decode; halbe Auflösung wird gemerkt (Regler-Vorschauen derselben Datei)."""
    if not half:
        return _decode(data, half=False)
    key = hashlib.sha1(data).hexdigest()
    with _CACHE_LOCK:
        hit = _CACHE.get(key)
        if hit is not None:
            _CACHE.move_to_end(key)
            return hit.copy()  # Aufrufer dürfen in place arbeiten
    arr = _decode(data, half=True)
    with _CACHE_LOCK:
        _CACHE[key] = arr
        while len(_CACHE) > _CACHE_MAX:
            _CACHE.popitem(last=False)
    return arr.copy()


def _decode(data: bytes, *, half: bool = False) -> np.ndarray:
    """RAW → aufrechtes RGB uint8 im Look des Kamera-JPGs (half=True: halbe Kantenlänge, schnell).

    Die Tonwertkurve wird immer aus der halben Auflösung bestimmt, damit Vorschau,
    Analyse und Vollauflösung exakt dieselbe Grundentwicklung bekommen.
    """
    try:
        with rawpy.imread(io.BytesIO(data)) as r:
            half16 = r.postprocess(**_params(True))
        cam = embedded_jpeg(data)
        if cam is not None:
            cam = ImageOps.exif_transpose(cam)  # Vorschau-JPG hat eigene Ausrichtung
        lut = _tone_lut(half16, cam)
        if half:
            return _apply_lut(half16, lut)
        del half16
        with rawpy.imread(io.BytesIO(data)) as r:
            full16 = r.postprocess(**_params(False))
        return _apply_lut(full16, lut)
    except Exception as e:
        raise ValueError(f"RAW nicht lesbar: {e}") from e


def embedded_jpeg(data: bytes) -> Image.Image | None:
    """Das von der Kamera eingebettete Vorschau-JPEG (enthält die EXIF-Daten)."""
    try:
        with rawpy.imread(io.BytesIO(data)) as r:
            th = r.extract_thumb()
        if th.format == rawpy.ThumbFormat.JPEG:
            return Image.open(io.BytesIO(th.data))
    except Exception:
        pass
    return None


def exif_bytes_for_output(data: bytes, width: int, height: int) -> bytes | None:
    """EXIF der Kamera für das fertige JPG: Ausrichtung ist angewendet → Orientation 1, Maße angepasst."""
    im = embedded_jpeg(data)
    if im is None:
        return None
    try:
        ex = im.getexif()
        ex[0x0112] = 1
        sub = ex.get_ifd(0x8769)
        sub[0xA002], sub[0xA003] = width, height   # PixelXDimension / PixelYDimension
        return ex.tobytes()
    except Exception:
        return None
