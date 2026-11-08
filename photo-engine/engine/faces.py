"""Gesichter finden und als Fingerabdruck beschreiben (für Personen-Gruppierung beim Aufrufer).

YuNet findet die Gesichter, SFace berechnet je Gesicht 128 Zahlen (L2-normiert).
Gleiche Person ↔ hohe Kosinus-Ähnlichkeit (Skalarprodukt der Vektoren);
SFace-Empfehlung: ≥ 0,363 gilt als dieselbe Person.
Die Engine speichert nichts – Gruppieren und Benennen macht der Aufrufer.
"""

from __future__ import annotations

import threading

import cv2
import numpy as np

from .metrics import MODEL_DIR, YUNET_FILE, load_image

SFACE_FILE = "face_recognition_sface_2021dec.onnx"
MODEL_NAME = "sface-2021dec"
WORK_EDGE = 2048      # größere Bilder werden verkleinert, kleine Gesichter bleiben erkennbar
MIN_SCORE = 0.8       # sichere Gesichter; der Aufrufer kann über "score" strenger filtern
MIN_SIDE = 40         # Pixel (kurze Seite des Gesichtsrahmens im Arbeitsbild)

_LOCK = threading.Lock()  # OpenCV-Netze nicht gleichzeitig aus mehreren Threads benutzen
_NETS: dict = {}


def available() -> bool:
    return ((MODEL_DIR / YUNET_FILE).is_file() and (MODEL_DIR / SFACE_FILE).is_file()
            and hasattr(cv2, "FaceDetectorYN") and hasattr(cv2, "FaceRecognizerSF"))


def _nets(w: int, h: int):
    if "det" not in _NETS:
        # eigener Detektor (nicht der aus metrics), damit Eingabegrößen sich nicht in die Quere kommen
        _NETS["det"] = cv2.FaceDetectorYN.create(str(MODEL_DIR / YUNET_FILE), "", (w, h), MIN_SCORE, 0.3, 200)
        _NETS["rec"] = cv2.FaceRecognizerSF.create(str(MODEL_DIR / SFACE_FILE), "")
    _NETS["det"].setInputSize((w, h))
    return _NETS["det"], _NETS["rec"]


def find_faces(data: bytes) -> dict:
    """Bild-Bytes (JPG/PNG/RAW) → Gesichter mit relativem Rahmen, Score und Fingerabdruck."""
    if not available():
        raise RuntimeError("Gesichtsmodelle fehlen")
    rgb, _ = load_image(data, max_edge=WORK_EDGE)
    h, w = rgb.shape[:2]
    bgr = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)
    faces = []
    with _LOCK:
        det, rec = _nets(w, h)
        found = det.detect(bgr)[1]
        for f in (found if found is not None else []):
            x, y, fw, fh, score = float(f[0]), float(f[1]), float(f[2]), float(f[3]), float(f[14])
            if score < MIN_SCORE or min(fw, fh) < MIN_SIDE:
                continue
            emb = rec.feature(rec.alignCrop(bgr, f)).reshape(-1).astype(np.float64)
            emb /= max(np.linalg.norm(emb), 1e-12)
            x0, y0 = max(0.0, x), max(0.0, y)
            x1, y1 = min(float(w), x + fw), min(float(h), y + fh)
            faces.append({
                "box": {"x": round(x0 / w, 4), "y": round(y0 / h, 4),
                        "w": round((x1 - x0) / w, 4), "h": round((y1 - y0) / h, 4)},
                "score": round(score, 3),
                "size_px": int(min(fw, fh)),
                "embedding": [round(float(v), 5) for v in emb],
            })
    faces.sort(key=lambda d: -d["size_px"])
    return {"model": MODEL_NAME, "width": w, "height": h, "faces": faces}
