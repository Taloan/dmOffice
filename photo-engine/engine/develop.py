"""Referenz-Implementierung: wendet das Korrektur-JSON auf ein Bild an.

Wird von der Engine intern genutzt, um ihre eigenen Vorschläge an der
Vorschau zu überprüfen (Simulation), und von /develop für Vorschau und
Vollauflösung. Eingabe/Ausgabe: RGB uint8.

Speicher: Großflächige Ebenen (kantenerhaltende Basis, Dunst) werden auf
einer Fassung mit höchstens GUIDE_EDGE Pixeln langer Kante berechnet und
bilinear hochgerechnet („Fast Guided Filter“). Alles Punktweise läuft in
Streifen. Bis GUIDE_EDGE ist das Ergebnis exakt, darüber praktisch gleich.
"""

from __future__ import annotations

import math

import cv2
import numpy as np

GUIDE_EDGE = 1600   # Auflösung der großflächigen Ebenen
STRIP = 256         # Zeilen pro Streifen
GUIDE_EPS = 0.01


# ---------------------------------------------------------------- Hilfen

def _srgb_to_lin_exact(x):
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def _lin_to_srgb_exact(x):
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1 / 2.4) - 0.055)


# Lookup-Tabellen statt pow() auf Millionen Pixeln
_LUT_U8_TO_LIN = _srgb_to_lin_exact(np.arange(256) / 255.0).astype(np.float32)
_LUT_N = 65536
_LUT_LIN_TO_SRGB = _lin_to_srgb_exact(np.linspace(0, 1, _LUT_N)).astype(np.float32)
_LUMA = np.array([[0.2126, 0.7152, 0.0722]], np.float32)


def srgb_u8_to_lin(img: np.ndarray) -> np.ndarray:
    return _LUT_U8_TO_LIN[img]


def lin_to_srgb(x: np.ndarray) -> np.ndarray:
    i = np.clip(x, 0.0, 1.0)
    i = (i * (_LUT_N - 1) + 0.5).astype(np.uint16)
    return _LUT_LIN_TO_SRGB[i]


def luma(rgb: np.ndarray) -> np.ndarray:
    return cv2.transform(rgb, _LUMA)


def smoothstep(e0: float, e1: float, x: np.ndarray) -> np.ndarray:
    t = np.clip((x - np.float32(e0)) * np.float32(1.0 / (e1 - e0)), 0.0, 1.0)
    return t * t * (np.float32(3) - np.float32(2) * t)


def subject_mask(h: int, w: int, region: dict, y0: int = 0, y1: int | None = None) -> np.ndarray:
    """Weiche Ellipse im Motivrahmen (Werte 0…1), optional nur Zeilen y0…y1."""
    y1 = h if y1 is None else y1
    cx = (region["x"] + region["w"] / 2) * w
    cy = (region["y"] + region["h"] / 2) * h
    rx = max(2.0, region["w"] * w / 2 * 1.15)
    ry = max(2.0, region["h"] * h / 2 * 1.15)
    yy = np.arange(y0, y1, dtype=np.float32)[:, None]
    xx = np.arange(w, dtype=np.float32)[None, :]
    d = np.sqrt(((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2)
    return (1.0 - smoothstep(0.6, 1.25, d)).astype(np.float32)


def shoulder(x: np.ndarray, knee: float = 0.8) -> np.ndarray:
    """Weiches Abrollen der Lichter oberhalb des Knies statt hartem Clipping."""
    over = x > knee
    y = x.copy()
    r = 1.0 - knee
    y[over] = knee + r * (1.0 - np.exp(-(x[over] - knee) / r))
    return y


def contrast_curve(x: np.ndarray, amount: float, pivot: float = 0.45) -> np.ndarray:
    """Monotone S-Kurve, amount -1…1."""
    a = amount * 0.8
    lo = x < pivot
    t = np.where(lo, x / pivot, (x - pivot) / (1 - pivot))
    # untere Hälfte: Kurve unter der Diagonale, obere darüber (bei a>0)
    s_lo = t - a * t * (1 - t) * 0.5
    s_hi = t + a * t * (1 - t) * 0.5
    return np.where(lo, s_lo * pivot, pivot + s_hi * (1 - pivot)).astype(np.float32)


def straighten(img: np.ndarray, deg: float) -> np.ndarray:
    """Dreht um deg (positiv = gegen den Uhrzeigersinn) und schneidet randlos zu."""
    if abs(deg) < 0.05:
        return img
    h, w = img.shape[:2]
    th = math.radians(abs(deg))
    c, s = math.cos(th), math.sin(th)
    scale = min(w / (w * c + h * s), h / (w * s + h * c))
    M = cv2.getRotationMatrix2D((w / 2, h / 2), deg, 1.0 / scale)
    return cv2.warpAffine(img, M, (w, h), flags=cv2.INTER_LANCZOS4, borderMode=cv2.BORDER_REFLECT)


def guided_coeffs(L: np.ndarray, radius: float, eps: float = GUIDE_EPS) -> tuple[np.ndarray, np.ndarray]:
    """Selbstgeführter Guided Filter (He et al.): Basis = A·L + B."""
    r = max(2, int(round(radius)))
    k = (2 * r + 1, 2 * r + 1)
    mean = cv2.boxFilter(L, -1, k, borderType=cv2.BORDER_REFLECT)
    var = cv2.boxFilter(L * L, -1, k, borderType=cv2.BORDER_REFLECT) - mean * mean
    a = var / (var + np.float32(eps))
    b = mean - a * mean
    A = cv2.boxFilter(a, -1, k, borderType=cv2.BORDER_REFLECT)
    B = cv2.boxFilter(b, -1, k, borderType=cv2.BORDER_REFLECT)
    return A, B


def _upsample_rows(small: np.ndarray, w: int, h: int, y0: int, y1: int) -> np.ndarray:
    """Bilinear hochgerechnete Zeilen y0…y1 einer kleinen Ebene (wie cv2.resize, ohne Vollbild)."""
    hs, ws = small.shape[:2]
    if hs == h and ws == w:
        return small[y0:y1]
    mx = ((np.arange(w, dtype=np.float32) + 0.5) * (ws / w) - 0.5)[None, :]
    my = ((np.arange(y0, y1, dtype=np.float32) + 0.5) * (hs / h) - 0.5)[:, None]
    map_x = np.broadcast_to(mx, (y1 - y0, w)).astype(np.float32)
    map_y = np.broadcast_to(my, (y1 - y0, w)).astype(np.float32)
    return cv2.remap(small, map_x, map_y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)


# ---------------------------------------------------------------- Bausteine

class _Plan:
    """Vorberechnete Parameter einer Korrektur."""

    def __init__(self, corr: dict, img: np.ndarray):
        g = self.g = corr["global"]
        sa = corr.get("subject_adjustments") or {}
        self.h, self.w = img.shape[:2]
        self.long_edge = max(self.h, self.w)
        self.region = sa["region"] if sa.get("apply") else None
        self.sub_ev = float(sa.get("exposure_ev") or 0) if self.region else 0.0
        self.sub_sh = float(sa.get("shadows") or 0) / 100.0 if self.region else 0.0

        t, m = g["temperature_shift"] / 50.0, g["tint_shift"] / 50.0
        self.gains = None
        if t or m:
            # ±50 entspricht etwa ±13 b* bzw. ±11 a* auf Mittelgrau; Grau behält seine Helligkeit
            gains = np.array([2 ** (0.5 * t), 2 ** (-0.45 * m), 2 ** (-0.5 * t)], np.float32)
            self.gains = gains / float(_LUMA[0] @ gains)
        self.linear = bool(self.gains is not None or self.sub_ev)
        self.ev_lut = _exposure_lut(g["exposure_ev"]) if g["exposure_ev"] else None

        # Schulter nur, wenn irgendein Pixel über Weiß geschoben wird (global entschieden)
        self.shoulder = False
        if self.linear:
            peak = _LUT_U8_TO_LIN[img.reshape(-1, 3).max(axis=0)]
            if self.gains is not None:
                peak = peak * self.gains
            self.shoulder = float(peak.max()) * 2.0 ** max(self.sub_ev, 0.0) > 1.0

        self.bp = -0.06 * g["blacks"] / 100.0 + 0.035 * max(g["dehaze"], 0) / 100.0
        self.wp = 1.0 - 0.06 * g["whites"] / 100.0
        self.dehaze = g["dehaze"] / 100.0
        self.sat = g["saturation"] / 100.0 + 0.15 * max(self.dehaze, 0)
        self.vib = g["vibrance"] / 100.0


# Belichtung wie Lightroom (gemessen 31.10.2026 an einem JPG, nur Belichtung −2 in LR Classic): LR fährt eine Kurve, die auf
# R, G und B gleich wirkt - nicht ×2^EV im Linearlicht. Nachgebildet als Verschiebung vor einer Filmkurve
# F(s) = sRGB((tanh(k·s)/tanh(k))^g): das Bild gilt als F(Szene), neu ist F(Szene·2^EV). Abdunkeln drückt so die Mitten
# stärker als die Lichter, die Farben bleiben kräftig; Aufhellen läuft weich in die Schulter. Abweichung zu LR < 2/255.
_EV_K, _EV_G = 3.776, 1.33
_EV_GRID = np.linspace(0.0, 1.0, 4097, dtype=np.float64)


def _film(s: np.ndarray) -> np.ndarray:
    t = np.clip(np.tanh(_EV_K * s) / np.tanh(_EV_K), 0.0, 1.0) ** _EV_G
    return np.where(t <= 0.0031308, t * 12.92, 1.055 * np.power(t, 1 / 2.4) - 0.055)


_EV_FILM = _film(_EV_GRID)


def _exposure_lut(ev: float) -> np.ndarray:
    """Kurve 0…1 → 0…1 (4097 Stützstellen) für eine Belichtung in Blendenstufen."""
    scene = np.interp(_EV_GRID, _EV_FILM, _EV_GRID)
    return _film(scene * 2.0 ** ev).astype(np.float32)


def _light(p: _Plan, img: np.ndarray, mask: np.ndarray | None) -> np.ndarray:
    """Weißabgleich (linear) + Belichtung (Kurve wie Lightroom) → sRGB float 0…1."""
    if not p.linear:
        out = img.astype(np.float32) * np.float32(1 / 255.0)
    else:
        lin = srgb_u8_to_lin(img)
        if p.gains is not None:
            lin = lin * p.gains
        if mask is not None and p.sub_ev:
            # nur Dunkles im Motivbereich anheben – heller Hintergrund (Himmel) bekommt keinen Lichthof
            dark = 1.0 - smoothstep(0.35, 0.7, luma(img.astype(np.float32) * np.float32(1 / 255.0)))
            lin = lin * np.exp2(mask * dark * np.float32(p.sub_ev))[..., None]
        if p.shoulder:
            mx = lin.max(axis=2)
            lin *= (shoulder(mx) / np.maximum(mx, 1e-6))[..., None]
        out = lin_to_srgb(lin)
    if p.ev_lut is not None:
        idx = np.clip(out * np.float32(4096.0), 0.0, 4096.0)
        out = p.ev_lut[(idx + 0.5).astype(np.int32)]
    return out


def _tone(p: _Plan, L: np.ndarray, base: np.ndarray, mask: np.ndarray | None) -> np.ndarray:
    """Tonwerte auf der Basisebene + Klarheit → Luminanz vor Dunst."""
    g = p.g
    detail = L - base
    b = (base - np.float32(p.bp)) * np.float32(1.0 / max(p.wp - p.bp, 1e-3))
    shadows = np.float32(g["shadows"] / 100.0)
    if mask is not None and p.sub_sh:
        shadows = shadows + mask * np.float32(p.sub_sh)
    ws = 1.0 - smoothstep(0.05, 0.6, b)
    wh = smoothstep(0.45, 0.9, b)
    if g["highlights"] < 0:
        # ausgebranntes Weiß enthält keine Zeichnung → weiß lassen statt grau ziehen
        wh *= 1.0 - smoothstep(0.94, 0.99, base)
    b = b + np.float32(0.25) * shadows * ws * (1.0 - b) + np.float32(0.25 * g["highlights"] / 100.0) * wh * b
    b = np.clip(b, 0.0, 1.0)
    if g["contrast"]:
        b = contrast_curve(b, g["contrast"] / 100.0)
    k = g["clarity"] / 100.0
    return b + detail * (1.0 + np.float32(1.2 * k) * (4.0 * b * (1.0 - b)))


def _color(p: _Plan, rgb: np.ndarray) -> np.ndarray:
    """Sättigung / Dynamik in Lab, Hauttöne geschützt."""
    if not (p.sat or p.vib):
        return rgb
    lab = cv2.cvtColor(rgb, cv2.COLOR_RGB2Lab)
    A, B = lab[..., 1], lab[..., 2]
    C = np.sqrt(A * A + B * B)
    hue = np.degrees(np.arctan2(B, A))
    skin = smoothstep(10, 25, hue) * (1 - smoothstep(60, 75, hue)) * (1 - smoothstep(35, 55, C))
    f = 1.0 + p.sat + p.vib * (1.0 - np.clip(C / 60.0, 0, 1)) * (1.0 - 0.6 * skin)
    lab[..., 1] = A * f
    lab[..., 2] = B * f
    return np.clip(cv2.cvtColor(lab, cv2.COLOR_Lab2RGB), 0.0, 1.0)


def _sharpen_inplace(out: np.ndarray, amount: float, long_edge: int) -> None:
    """Unscharfmaske nur auf Luminanz, streifenweise mit Überlappung."""
    h = out.shape[0]
    radius = max(0.6, long_edge / 5000.0)
    halo = int(math.ceil(radius * 4)) + 2
    prev = None  # ungeschärfte Zeilen direkt über dem aktuellen Streifen (statt Vollbildkopie)
    for y0 in range(0, h, STRIP):
        y1 = min(h, y0 + STRIP)
        a1 = min(h, y1 + halo)
        block = out[y0:a1] if prev is None else np.concatenate([prev, out[y0:a1]])
        top = 0 if prev is None else prev.shape[0]
        rgb = block.astype(np.float32) * np.float32(1 / 255.0)
        L = luma(rgb)
        hp = L - cv2.GaussianBlur(L, (0, 0), radius, borderType=cv2.BORDER_REFLECT)
        hp *= smoothstep(0.004, 0.02, np.abs(hp))
        res = np.clip(rgb + (np.float32(amount) * hp)[..., None], 0.0, 1.0)
        prev = out[max(y0, y1 - halo):y1].copy()
        out[y0:y1] = (res[top:top + (y1 - y0)] * 255.0 + 0.5).astype(np.uint8)


# ---------------------------------------------------------------- Pipeline

def apply_corrections(img_rgb: np.ndarray, corr: dict, *, simulate: bool = False,
                      inplace: bool = False) -> np.ndarray:
    """Wendet die Korrekturen an. simulate=True überspringt Schärfen/Drehen (nur Tonwerte).
    inplace=True schreibt das Ergebnis in img_rgb (spart bei großen Bildern ein Vollbild)."""
    p = _Plan(corr, img_rgb)
    h, w, long_edge = p.h, p.w, p.long_edge

    # 1) Großflächige Ebenen auf kleiner Fassung (bis GUIDE_EDGE exakt = Vollbild)
    f = min(1.0, GUIDE_EDGE / long_edge)
    small = img_rgb if f == 1.0 else cv2.resize(img_rgb, (round(w * f), round(h * f)), interpolation=cv2.INTER_AREA)
    hs, ws_ = small.shape[:2]
    mask_s = subject_mask(hs, ws_, p.region) if p.region else None
    L_s = luma(_light(p, small, mask_s))
    A_s, B_s = guided_coeffs(L_s, long_edge / 100.0 * f)
    big_s = None
    if p.dehaze:
        Ln_s = _tone(p, L_s, A_s * L_s + B_s, mask_s)
        big_s = cv2.GaussianBlur(Ln_s, (0, 0), long_edge / 25.0 * f)
    del small, L_s

    # 2) Vollauflösung streifenweise
    # Jeder Streifen liest nur seine eigenen Eingabezeilen → Überschreiben in place ist sicher
    out = img_rgb if inplace and img_rgb.flags.writeable else np.empty((h, w, 3), np.uint8)
    for y0 in range(0, h, STRIP):
        y1 = min(h, y0 + STRIP)
        mask = subject_mask(h, w, p.region, y0, y1) if p.region else None
        rgb = _light(p, img_rgb[y0:y1], mask)
        L = luma(rgb)
        base = _upsample_rows(A_s, w, h, y0, y1) * L + _upsample_rows(B_s, w, h, y0, y1)
        Ln = _tone(p, L, base, mask)
        if big_s is not None:
            Ln = Ln + np.float32(0.5 * p.dehaze) * (Ln - _upsample_rows(big_s, w, h, y0, y1))
        Ln = np.clip(Ln, 0.0, 1.0)
        ratio = np.clip(Ln / np.maximum(L, 1e-4), 0.0, 4.0)
        rgb = np.clip(rgb * ratio[..., None], 0.0, 1.0)
        rgb = _color(p, rgb)
        out[y0:y1] = (rgb * 255.0 + 0.5).astype(np.uint8)

    if not simulate:
        amt = p.g["sharpen"] / 100.0 * 1.2
        if amt > 0:
            _sharpen_inplace(out, amt, long_edge)
        out = straighten(out, corr.get("crop", {}).get("straighten_deg", 0.0))
    return out
