"""Das „Auge“: Bild laden, EXIF lesen, Kennzahlen messen, Motiv und Horizont finden."""

from __future__ import annotations

import io
import math
import os
import threading
from fractions import Fraction
from pathlib import Path

import cv2
import numpy as np
from PIL import ExifTags, Image, ImageOps

from . import rawio

MAX_EDGE = 1600
# Pillows „Dekompressionsbomben“-Schutz greift sonst schon bei Handy-Fotos mit 200 MP;
# die Größengrenze für die Vollauflösung prüft jpegio.develop_jpeg selbst.
Image.MAX_IMAGE_PIXELS = None

_CASCADES: dict[str, cv2.CascadeClassifier] = {}


# ---------------------------------------------------------------- Laden

def load_image(data: bytes, max_edge: int = MAX_EDGE) -> tuple[np.ndarray, dict]:
    """Dekodiert JPG/PNG/RAW, wendet EXIF-Orientierung an, verkleinert auf max_edge.

    Rückgabe: RGB uint8, EXIF-Dict. Koordinaten der Antwort beziehen sich auf
    das *aufrecht gedrehte* Bild (EXIF-Orientierung angewendet).
    """
    if rawio.is_raw(data):
        thumb = rawio.embedded_jpeg(data)
        exif = read_exif(thumb) if thumb is not None else {}
        im = Image.fromarray(rawio.decode(data, half=True))  # bereits aufrecht
        if max(im.size) > max_edge:
            im.thumbnail((max_edge, max_edge), Image.LANCZOS)
        exif["raw"] = True
        return np.asarray(im, dtype=np.uint8).copy(), exif

    im = Image.open(io.BytesIO(data))
    exif = read_exif(im)
    im.draft("RGB", (max_edge, max_edge))  # JPG verkleinert dekodieren: schnell, auch bei 200 MP
    im = ImageOps.exif_transpose(im).convert("RGB")
    if max(im.size) > max_edge:
        im.thumbnail((max_edge, max_edge), Image.LANCZOS)
    return np.asarray(im, dtype=np.uint8).copy(), exif


def _f(v):
    try:
        if isinstance(v, tuple) and len(v) == 2:
            return v[0] / v[1] if v[1] else None
        return float(Fraction(v)) if isinstance(v, str) else float(v)
    except Exception:
        return None


def read_exif(im: Image.Image) -> dict:
    out: dict = {}
    try:
        raw = im.getexif()
    except Exception:
        return out
    if not raw:
        return out
    tags = {ExifTags.TAGS.get(k, k): v for k, v in raw.items()}
    try:
        tags.update({ExifTags.TAGS.get(k, k): v for k, v in raw.get_ifd(0x8769).items()})
    except Exception:
        pass
    if tags.get("Make"):
        out["make"] = str(tags["Make"]).strip("\x00 ")
    if tags.get("Model"):
        out["model"] = str(tags["Model"]).strip("\x00 ")
    for key, name in (("ExposureTime", "exposure_time"), ("FNumber", "f_number"), ("FocalLength", "focal_length")):
        v = _f(tags.get(key))
        if v:
            out[name] = round(v, 6)
    iso = tags.get("ISOSpeedRatings") or tags.get("PhotographicSensitivity")
    if isinstance(iso, (tuple, list)):
        iso = iso[0] if iso else None
    if iso:
        try:
            out["iso"] = int(iso)
        except Exception:
            pass
    dt = tags.get("DateTimeOriginal") or tags.get("DateTime")
    if dt:
        out["datetime"] = str(dt)
        try:
            out["hour"] = int(str(dt)[11:13])
        except Exception:
            pass
    if "Flash" in tags:
        try:
            out["flash_fired"] = bool(int(tags["Flash"]) & 1)
        except Exception:
            pass
    return out


def merge_exif(measured: dict, supplied: dict | None) -> dict:
    """Vom Aufrufer mitgeschickte EXIF-Werte haben Vorrang (z. B. wenn die Vorschau keine EXIF trägt)."""
    out = dict(measured)
    for k, v in (supplied or {}).items():
        if v is not None:
            out[k] = v
    if "datetime" in out and "hour" not in out:
        try:
            out["hour"] = int(str(out["datetime"])[11:13])
        except Exception:
            pass
    return out


# ---------------------------------------------------------------- Kennzahlen

def tone_metrics(rgb: np.ndarray) -> dict:
    Y = (0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]).astype(np.float32)
    h, w = Y.shape
    p = np.percentile(Y, [1, 5, 25, 50, 75, 95, 99])
    center = Y[h // 3: 2 * h // 3, w // 3: 2 * w // 3]
    top = Y[: h // 4]
    ch_clip = [(rgb[..., c] >= 250).mean() * 100 for c in range(3)]
    # wahrgenommene Helligkeit: gesättigtes Blau/Gelb hat wenig Luminanz, wirkt aber hell
    V = 0.5 * Y + 0.5 * rgb.max(axis=2).astype(np.float32)
    return {
        "mean_v": float(V.mean()),
        "median_v": float(np.median(V)),
        "mean": float(Y.mean()),
        "median": float(p[3]),
        "p1": float(p[0]), "p5": float(p[1]), "p25": float(p[2]),
        "p75": float(p[4]), "p95": float(p[5]), "p99": float(p[6]),
        "clip_hi_pct": float((Y >= 250).mean() * 100),
        "near_hi_pct": float(((Y >= 230) & (Y < 250)).mean() * 100),
        "clip_lo_pct": float((Y <= 5).mean() * 100),
        "channel_clip_pct": [float(v) for v in ch_clip],
        "center_mean": float(center.mean()),
        "top_mean": float(top.mean()),
        "std": float(Y.std()),
    }


def color_metrics(rgb: np.ndarray) -> dict:
    f = rgb.astype(np.float32) / 255.0
    lab = cv2.cvtColor(f, cv2.COLOR_RGB2Lab)
    L, A, B = lab[..., 0], lab[..., 1], lab[..., 2]
    C = np.sqrt(A * A + B * B)
    hue = np.degrees(np.arctan2(B, A))

    # Kandidaten für „eigentlich neutrale“ Flächen: Mitteltöne mit wenig Farbe
    # Kein Gray-World-Fallback: Wiese/Himmel würden sonst als Farbstich gelten.
    # Ein echter Farbstich wirkt in allen Helligkeiten gleich → nur werten, wenn
    # die neutralen Kandidaten in mindestens zwei Helligkeitsbändern übereinstimmen.
    neutral = (L > 25) & (L < 92) & (C < 28)
    # Himmel ausschließen: bläuliche Kandidaten in den oberen 45 % des Bildes
    rows = np.arange(L.shape[0])[:, None] < int(L.shape[0] * 0.45)
    neutral &= ~(rows & (B < -8) & (hue > -140) & (hue < -70))
    # Vegetation ausschließen: gelbgrün bis grün (Laub, Gras) ist Bildinhalt, kein Stich.
    # Kunstlicht-Stiche liegen auf der orangen Seite (a ≥ 0) und bleiben erfasst.
    neutral &= ~((C > 6) & (hue > 90) & (hue < 170))
    frac_neutral = float(neutral.mean())
    a_n = b_n = 0.0
    bands = []
    for lo, hi in ((25, 45), (45, 70), (70, 92)):
        sel = neutral & (L >= lo) & (L < hi)
        if sel.mean() >= 0.01:
            bands.append((float(np.median(A[sel])), float(np.median(B[sel])), float(sel.mean())))
    if frac_neutral >= 0.03 and len(bands) >= 2:
        best = None
        def close(u, v):  # Stich wächst mit der Helligkeit → relative Toleranz
            return abs(u - v) < max(5.0, 0.35 * max(abs(u), abs(v)))

        for a0, b0, _ in bands:
            agree = [bd for bd in bands if close(bd[0], a0) and close(bd[1], b0)]
            if len(agree) >= 2 and (best is None or sum(x[2] for x in agree) > sum(x[2] for x in best)):
                best = agree
        if best:
            wsum = sum(x[2] for x in best)
            a_n = sum(x[0] * x[2] for x in best) / wsum
            b_n = sum(x[1] * x[2] for x in best) / wsum
        else:
            frac_neutral = 0.0  # widersprüchlich → nicht verlässlich

    skin = (hue > 20) & (hue < 65) & (C > 10) & (C < 45) & (L > 25) & (L < 90)
    warm = (hue > 30) & (hue < 90) & (C > 25)
    blue = (hue > -130) & (hue < -80) & (C > 12)
    green = (hue > 100) & (hue < 160) & (C > 12)
    foliage = (hue > 75) & (hue < 165) & (C > 12)  # gelbes Herbstlaub bis sattes Grün
    return {
        "foliage_frac": float(foliage.mean()),
        "chroma_mean": float(C.mean()),
        "chroma_p90": float(np.percentile(C, 90)),
        "neutral_a": a_n,
        "neutral_b": b_n,
        "neutral_frac": frac_neutral,
        "skin_frac": float(skin.mean()),
        "warm_frac": float(warm.mean()),
        "blue_frac": float(blue.mean()),
        "green_frac": float(green.mean()),
        "blue_top_frac": float(blue[: blue.shape[0] // 3].mean()),
    }


def texture_metrics(rgb: np.ndarray) -> dict:
    gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY).astype(np.float32) / 255.0
    h, w = gray.shape
    scale = max(h, w) / 1600.0
    lap = cv2.Laplacian(gray, cv2.CV_32F, ksize=3)
    sharp = float(lap.var() * 1e4)

    # Rauschen: MAD des Hochpasses in flachen Bereichen
    hp = gray - cv2.GaussianBlur(gray, (0, 0), 1.0)
    grad = cv2.GaussianBlur(np.abs(cv2.Sobel(gray, cv2.CV_32F, 1, 0)) + np.abs(cv2.Sobel(gray, cv2.CV_32F, 0, 1)), (0, 0), 2)
    flat = grad < np.percentile(grad, 30)
    noise = float(np.median(np.abs(hp[flat])) * 1.4826 * 255) if flat.any() else 0.0

    # Lokaler Kontrast (Mitteltöne)
    blur = cv2.GaussianBlur(gray, (0, 0), max(2.0, 16 * scale))
    local = float(np.abs(gray - blur).mean() * 255)

    # Dunst: Dark Channel
    dark = cv2.erode(rgb.min(axis=2), np.ones((15, 15), np.uint8))
    haze = float(np.percentile(dark, 50))
    return {"sharpness": sharp, "noise": noise, "local_contrast": local, "dark_channel_median": haze}


# ---------------------------------------------------------------- Motiv

MODEL_DIR = Path(os.environ.get("PHOTO_ENGINE_MODELS", Path(__file__).resolve().parent.parent / "models"))
YUNET_FILE = "face_detection_yunet_2023mar.onnx"
_YUNET: dict = {}
_FACE_LOCK = threading.Lock()


def _cascade(name: str) -> cv2.CascadeClassifier | None:
    if name not in _CASCADES:
        c = None
        for d in (MODEL_DIR, Path(getattr(getattr(cv2, "data", None), "haarcascades", "") or ".")):
            p = Path(d) / name
            if p.is_file():
                c = cv2.CascadeClassifier(str(p))
                if c.empty():
                    c = None
                break
        _CASCADES[name] = c
    return _CASCADES[name]


def _yunet(w: int, h: int):
    p = MODEL_DIR / YUNET_FILE
    if not p.is_file() or not hasattr(cv2, "FaceDetectorYN"):
        return None
    det = _YUNET.get("det")
    if det is None:
        det = cv2.FaceDetectorYN.create(str(p), "", (w, h), 0.75, 0.3, 50)
        _YUNET["det"] = det
    det.setInputSize((w, h))
    return det


def face_detector_status() -> str:
    if (MODEL_DIR / YUNET_FILE).is_file():
        return "yunet"
    if _cascade("haarcascade_frontalface_default.xml") is not None:
        return "haar"
    return "none (Salienz-Fallback)"


def _find_faces(rgb: np.ndarray) -> list[tuple]:
    h, w = rgb.shape[:2]
    with _FACE_LOCK:  # ein Detektor für alle Server-Threads → serialisieren
        det = _yunet(w, h)
        found = det.detect(cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR))[1] if det is not None else None
    if det is not None:
        if found is None:
            return []
        return [tuple(int(v) for v in f[:4]) for f in found]

    gray_eq = cv2.equalizeHist(cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY))  # hilft bei Gegenlicht-Gesichtern
    min_side = max(24, int(min(h, w) * 0.04))
    faces = []
    with _FACE_LOCK:
        for name in ("haarcascade_frontalface_default.xml", "haarcascade_profileface.xml"):
            c = _cascade(name)
            if c is None:
                continue
            found = c.detectMultiScale(gray_eq, scaleFactor=1.1, minNeighbors=6, minSize=(min_side, min_side))
            faces.extend([tuple(int(v) for v in f) for f in found])
    return faces


def detect_subject(rgb: np.ndarray) -> dict:
    """Findet das Hauptmotiv: Gesichter → Salienz → Bildmitte."""
    h, w = rgb.shape[:2]
    gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    faces = [(max(0, x), max(0, y), min(fw, w - max(0, x)), min(fh, h - max(0, y)))
             for x, y, fw, fh in _find_faces(rgb) if fw > 0 and fh > 0]

    if faces:
        faces = _nms(faces)
        # Gesichter zu einem Rahmen vereinen, inkl. Oberkörper (Rahmen nach unten erweitert)
        x0 = min(f[0] for f in faces); y0 = min(f[1] for f in faces)
        x1 = max(f[0] + f[2] for f in faces); y1 = max(f[1] + f[3] for f in faces)
        fw, fh = x1 - x0, y1 - y0
        bx0 = max(0, x0 - 0.6 * fw); bx1 = min(w, x1 + 0.6 * fw)
        by0 = max(0, y0 - 0.4 * fh); by1 = min(h, y1 + 1.6 * fh)
        face_area = sum(f[2] * f[3] for f in faces) / (w * h)
        Y = gray.astype(np.float32)
        face_lum = float(np.mean([Y[f[1]:f[1] + f[3], f[0]:f[0] + f[2]].mean() for f in faces]))
        return {
            "source": "face",
            "count": len(faces),
            "face_area_frac": float(face_area),
            "region": _norm(bx0, by0, bx1 - bx0, by1 - by0, w, h),
            "subject_mean": face_lum,
            "faces": [_norm(*f, w, h) for f in faces],
        }

    region = _saliency_region(rgb)
    if region is not None:
        x, y, rw, rh = region
        sub = gray[y:y + rh, x:x + rw]
        return {"source": "saliency", "count": 0, "face_area_frac": 0.0,
                "region": _norm(x, y, rw, rh, w, h), "subject_mean": float(sub.mean())}

    x, y, rw, rh = w // 4, h // 4, w // 2, h // 2
    return {"source": "center", "count": 0, "face_area_frac": 0.0,
            "region": _norm(x, y, rw, rh, w, h), "subject_mean": float(gray[y:y + rh, x:x + rw].mean())}


def _norm(x, y, rw, rh, w, h) -> dict:
    return {"x": round(x / w, 4), "y": round(y / h, 4), "w": round(rw / w, 4), "h": round(rh / h, 4)}


def _nms(boxes: list[tuple], thr: float = 0.3) -> list[tuple]:
    boxes = sorted(boxes, key=lambda b: b[2] * b[3], reverse=True)
    keep: list[tuple] = []
    for b in boxes:
        if all(_iou(b, k) < thr for k in keep):
            keep.append(b)
    return keep


def _iou(a, b) -> float:
    ax1, ay1, bx1, by1 = a[0] + a[2], a[1] + a[3], b[0] + b[2], b[1] + b[3]
    iw = max(0, min(ax1, bx1) - max(a[0], b[0])); ih = max(0, min(ay1, by1) - max(a[1], b[1]))
    inter = iw * ih
    return inter / float(a[2] * a[3] + b[2] * b[3] - inter + 1e-9)


def _saliency_region(rgb: np.ndarray):
    try:
        sal = cv2.saliency.StaticSaliencySpectralResidual_create()
    except Exception:
        return None
    small = cv2.resize(rgb, (320, int(320 * rgb.shape[0] / rgb.shape[1])), interpolation=cv2.INTER_AREA)
    ok, smap = sal.computeSaliency(cv2.cvtColor(small, cv2.COLOR_RGB2BGR))
    if not ok:
        return None
    smap = cv2.GaussianBlur(smap.astype(np.float32), (0, 0), 6)
    thr = np.percentile(smap, 85)
    mask = (smap >= thr).astype(np.uint8)
    n, labels, stats, _ = cv2.connectedComponentsWithStats(mask)
    if n <= 1:
        return None
    # größte Komponente, leicht zur Mitte gewichtet
    sh, sw = smap.shape
    best, score = None, -1.0
    for i in range(1, n):
        x, y, bw, bh, area = stats[i]
        if bw > 0.85 * sw or bh > 0.85 * sh:  # Horizont-/Kantenband, kein Motiv
            continue
        cx, cy = (x + bw / 2) / sw - 0.5, (y + bh / 2) / sh - 0.5
        s = area * (1.2 - math.hypot(cx, cy))
        if s > score:
            best, score = (x, y, bw, bh), s
    if best is None:
        return None
    x, y, bw, bh = best
    if bw * bh < 0.02 * sw * sh:
        return None
    f = rgb.shape[1] / sw
    return int(x * f), int(y * f), int(bw * f), int(bh * f)


# ---------------------------------------------------------------- Horizont

def detect_tilt(rgb: np.ndarray) -> dict:
    """Schätzt die Schieflage aus langen, fast waagerechten/senkrechten Linien."""
    gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    h, w = gray.shape
    edges = cv2.Canny(cv2.GaussianBlur(gray, (5, 5), 0), 50, 150)
    min_len = int(max(h, w) * 0.15)
    lines = cv2.HoughLinesP(edges, 1, np.pi / 720, threshold=80, minLineLength=min_len, maxLineGap=8)
    if lines is None:
        return {"angle": 0.0, "support": 0.0, "lines": 0}
    angles, weights = [], []
    for x1, y1, x2, y2 in np.asarray(lines).reshape(-1, 4):
        dx, dy = x2 - x1, y2 - y1
        length = math.hypot(dx, dy)
        a = math.degrees(math.atan2(-dy, dx))  # Bildkoordinaten: y nach unten
        # auf -90…90 normieren
        if a > 90: a -= 180
        if a < -90: a += 180
        if abs(a) <= 8:  # fast waagerecht → Drehung, die es gerade macht
            angles.append(-a); weights.append(length)
        elif abs(abs(a) - 90) <= 5:  # fast senkrecht (schwächer gewichtet, Stürzende Linien)
            dev = a - 90 if a > 0 else a + 90
            angles.append(-dev); weights.append(length * 0.5)
    if not angles:
        return {"angle": 0.0, "support": 0.0, "lines": 0}
    angles, weights = np.array(angles), np.array(weights)
    med = _weighted_median(angles, weights)
    agree = np.abs(angles - med) < 0.75
    support = float(weights[agree].sum() / (max(h, w) * 2.0))  # ~Anzahl Bildbreiten Linie
    return {"angle": float(med), "support": support, "lines": int(agree.sum())}


def _weighted_median(v: np.ndarray, w: np.ndarray) -> float:
    i = np.argsort(v)
    cw = np.cumsum(w[i])
    return float(v[i][np.searchsorted(cw, cw[-1] / 2)])


# ---------------------------------------------------------------- Gesamt

def measure(rgb: np.ndarray, *, with_subject: bool = True, with_tilt: bool = True) -> dict:
    m = {"size": {"w": int(rgb.shape[1]), "h": int(rgb.shape[0])}}
    m["tone"] = tone_metrics(rgb)
    m["color"] = color_metrics(rgb)
    m["texture"] = texture_metrics(rgb)
    if with_subject:
        m["subject"] = detect_subject(rgb)
    if with_tilt:
        m["tilt"] = detect_tilt(rgb)
    return m
