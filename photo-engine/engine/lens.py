"""Objektivkorrektur: Verzeichnung, Vignettierung und Farbsäume (TCA) nach Profil.

Einheitliches Modell (alle Importe werden darauf umgerechnet):
  Koordinaten relativ zur Bildmitte, normiert auf die halbe lange Seite (lange Seite = ±1).
  Verzeichnung: Quellpunkt = u · (1 + k1 r² + k2 r⁴ + k3 r⁶)   (unverzeichnet → aufgenommen)
  Vignettierung: Helligkeit = 1 + a1 r² + a2 r⁴ + a3 r⁶          (Korrektur: teilen)
  TCA: Quellpunkt Rot/Blau = Quellpunkt Grün · s · (1 + c1 r² + c2 r⁴)   (r = Radius des Grün-Quellpunkts)

Profil: {"name", "lens", "camera", "source",
         "distortion": [{"focal", "aperture"?, "k": [k1, k2, k3]}],
         "vignetting": [{"focal", "aperture"?, "a": [a1, a2, a3]}],
         "tca": [{"focal", "aperture"?, "red": [s, c1, c2], "blue": [s, c1, c2]}]}
Zwischen Brennweiten wird linear gemittelt, zwischen Blenden in Blendenstufen; außerhalb gilt der
nächstliegende Wert.

Korrektur-Block in den Korrekturen ("lens"):
  {"profile": {…}|null, "focal": mm|null, "aperture": N|null,
   "distortion": 0…200 (% des Profils), "vignetting": 0…200, "tca": bool,
   "manual": {"distortion": -100…100, "vignetting": -100…100, "midpoint": 0…100}}
"""

from __future__ import annotations

import io
import math
import re
import xml.etree.ElementTree as ET

import cv2
import numpy as np

STRIP = 256
MAX_ENTRIES = 400
# Manuelle Verzeichnung: 100 entspricht k1 = 0,2 (deutliche Tonne bzw. Kissen)
MANUAL_K1 = 0.2


# ---------------------------------------------------------------- Bereinigen

def _f(v, lo=-1e6, hi=1e6, default=None):
    try:
        v = float(v)
    except (TypeError, ValueError):
        return default
    if v != v or v in (float("inf"), float("-inf")):
        return default
    return max(lo, min(hi, v))


def _vec(v, n, lo=-10.0, hi=10.0):
    v = list(v) if isinstance(v, (list, tuple)) else []
    out = [_f(x, lo, hi, 0.0) for x in v[:n]]
    return out + [0.0] * (n - len(out))


def _entries(lst, key, n, lo=-10.0, hi=10.0):
    out = []
    for e in (lst if isinstance(lst, list) else [])[:MAX_ENTRIES]:
        if not isinstance(e, dict):
            continue
        focal = _f(e.get("focal"), 0.5, 5000)
        if focal is None:
            continue
        x = {"focal": focal}
        ap = _f(e.get("aperture"), 0.5, 128)
        if ap:
            x["aperture"] = ap
        if isinstance(key, tuple):
            for k in key:
                x[k] = _vec(e.get(k), n, lo, hi)
        else:
            x[key] = _vec(e.get(key), n, lo, hi)
        out.append(x)
    return out


def sanitize_profile(p) -> dict | None:
    if not isinstance(p, dict):
        return None
    out = {
        "distortion": _entries(p.get("distortion"), "k", 3),
        "vignetting": _entries(p.get("vignetting"), "a", 3),
        "tca": _entries(p.get("tca"), ("red", "blue"), 3),
    }
    for k in ("name", "lens", "camera", "source"):
        if isinstance(p.get(k), str):
            out[k] = p[k][:200]
    return out


def sanitize(lens) -> dict | None:
    """Korrektur-Block prüfen; None = nichts zu tun."""
    if not isinstance(lens, dict):
        return None
    m = lens.get("manual") if isinstance(lens.get("manual"), dict) else {}
    out = {
        "profile": sanitize_profile(lens.get("profile")),
        "focal": _f(lens.get("focal"), 0.5, 5000),
        "aperture": _f(lens.get("aperture"), 0.5, 128),
        "distortion": _f(lens.get("distortion"), 0, 200, 100.0),
        "vignetting": _f(lens.get("vignetting"), 0, 200, 100.0),
        "tca": bool(lens.get("tca", True)),
        "manual": {
            "distortion": _f(m.get("distortion"), -100, 100, 0.0),
            "vignetting": _f(m.get("vignetting"), -100, 100, 0.0),
            "midpoint": _f(m.get("midpoint"), 0, 100, 50.0),
        },
    }
    return out if _active(out) else None


def _active(lens: dict) -> bool:
    p = lens["profile"]
    has = p and (p["distortion"] or p["vignetting"] or p["tca"])
    m = lens["manual"]
    return bool(has or m["distortion"] or m["vignetting"])


# ---------------------------------------------------------------- Interpolation

def _stops(n: float) -> float:
    return 2.0 * math.log2(max(n, 0.1))


def _at_focal(group: list[dict], aperture: float | None, get) -> np.ndarray:
    """Werte einer Brennweite für die gewünschte Blende (in Blendenstufen gemittelt, sonst nächstliegende)."""
    with_ap = sorted((e for e in group if e.get("aperture")), key=lambda e: e["aperture"])
    without = [e for e in group if not e.get("aperture")]
    if not with_ap or (aperture is None and without):
        src = without or with_ap
        return np.mean([get(e) for e in src], axis=0)
    if aperture is None:
        return get(with_ap[0])  # offenblendig
    s = _stops(aperture)
    if s <= _stops(with_ap[0]["aperture"]):
        return get(with_ap[0])
    if s >= _stops(with_ap[-1]["aperture"]):
        return get(with_ap[-1])
    for a, b in zip(with_ap, with_ap[1:]):
        sa, sb = _stops(a["aperture"]), _stops(b["aperture"])
        if sa <= s <= sb:
            t = 0.0 if sb == sa else (s - sa) / (sb - sa)
            return (1 - t) * get(a) + t * get(b)
    return get(with_ap[-1])


def interpolate(entries: list[dict], focal: float | None, aperture: float | None, get) -> np.ndarray | None:
    """Werte für Brennweite/Blende; linear zwischen Brennweiten, außerhalb der nächstliegende Rand."""
    if not entries:
        return None
    groups: dict[float, list] = {}
    for e in entries:
        groups.setdefault(float(e["focal"]), []).append(e)
    fs = sorted(groups)
    if focal is None or focal <= fs[0]:
        return _at_focal(groups[fs[0]], aperture, get)
    if focal >= fs[-1]:
        return _at_focal(groups[fs[-1]], aperture, get)
    for a, b in zip(fs, fs[1:]):
        if a <= focal <= b:
            t = (focal - a) / (b - a)
            return (1 - t) * _at_focal(groups[a], aperture, get) + t * _at_focal(groups[b], aperture, get)
    return _at_focal(groups[fs[-1]], aperture, get)


def params_for(profile: dict | None, focal: float | None, aperture: float | None) -> dict:
    """Profilwerte für ein Foto: {"k": [3], "a": [3]|None, "red": [3]|None, "blue": [3]|None}."""
    out = {"k": [0.0, 0.0, 0.0], "a": None, "red": None, "blue": None}
    if not profile:
        return out
    k = interpolate(profile.get("distortion") or [], focal, aperture, lambda e: np.array(e["k"], float))
    if k is not None:
        out["k"] = [float(x) for x in k]
    a = interpolate(profile.get("vignetting") or [], focal, aperture, lambda e: np.array(e["a"], float))
    if a is not None:
        out["a"] = [float(x) for x in a]
    t = interpolate(profile.get("tca") or [], focal, aperture, lambda e: np.array(e["red"] + e["blue"], float))
    if t is not None:
        out["red"], out["blue"] = [float(x) for x in t[:3]], [float(x) for x in t[3:]]
    return out


# ---------------------------------------------------------------- Anwenden

def _poly(k, r2):
    return 1.0 + r2 * (k[0] + r2 * (k[1] + r2 * k[2]))


def effective(lens: dict) -> dict:
    """Profil + Stärke + manuelle Regler → wirksame Koeffizienten."""
    p = params_for(lens["profile"], lens["focal"], lens["aperture"])
    sd, sv = lens["distortion"] / 100.0, lens["vignetting"] / 100.0
    k = [x * sd for x in p["k"]]
    k[0] -= MANUAL_K1 * lens["manual"]["distortion"] / 100.0
    a = [x * sv for x in p["a"]] if p["a"] else None
    red = blue = None
    if lens["tca"] and p["red"]:
        red, blue = p["red"], p["blue"]
    return {"k": k, "a": a, "red": red, "blue": blue,
            "mv": lens["manual"]["vignetting"] / 100.0, "mid": lens["manual"]["midpoint"]}


def _frame(w: int, h: int):
    half = max(w, h) / 2.0
    return half, (w - 1) / 2.0, (h - 1) / 2.0, w / 2.0 / half, h / 2.0 / half


def zoom_for(k, w: int, h: int) -> float:
    """Größter Maßstab s (Ausgabe u → u·s), bei dem der ganze Ausgaberand innerhalb des Originals liegt
    und die Abbildung umkehrbar bleibt: keine leeren Ecken, Seitenverhältnis unverändert."""
    if not any(k):
        return 1.0
    _, _, _, ex, ey = _frame(w, h)
    t = np.linspace(0.0, 1.0, 97)
    px = np.concatenate([t * ex, np.full_like(t, ex)])
    py = np.concatenate([np.full_like(t, ey), t * ey])
    rc = math.hypot(ex, ey)

    def ok(s):
        qx, qy = px * s, py * s
        f = _poly(k, qx * qx + qy * qy)
        if np.any(np.abs(qx * f) > ex * 1.0005) or np.any(np.abs(qy * f) > ey * 1.0005):
            return False
        r = np.linspace(0.0, rc * s, 200)
        r2 = r * r
        d = _poly(k, r2) + 2 * r2 * (k[0] + 2 * k[1] * r2 + 3 * k[2] * r2 * r2)
        return bool(np.all(d > 0.05))

    lo, hi = 0.25, 3.0
    if not ok(lo):
        return lo
    if ok(hi):
        return hi
    for _ in range(40):
        mid = (lo + hi) / 2
        if ok(mid):
            lo = mid
        else:
            hi = mid
    return lo


def _maps(e: dict, w: int, h: int, y0: int, y1: int, zoom: float):
    half, cx, cy, _, _ = _frame(w, h)
    xs = ((np.arange(w, dtype=np.float32) - cx) / half) * zoom
    ys = ((np.arange(y0, y1, dtype=np.float32) - cy) / half) * zoom
    X, Y = np.meshgrid(xs, ys)
    f = _poly(e["k"], X * X + Y * Y).astype(np.float32)
    gx, gy = X * f, Y * f
    out = {"g": (gx * half + cx, gy * half + cy)}
    if e["red"]:
        r2 = gx * gx + gy * gy
        for ch in ("red", "blue"):
            s, c1, c2 = e[ch]
            fc = (s * (1.0 + r2 * (c1 + r2 * c2))).astype(np.float32)
            out[ch] = (gx * fc * half + cx, gy * fc * half + cy)
    return out


def _lin():
    from .develop import lin_to_srgb, srgb_u8_to_lin
    return srgb_u8_to_lin, lin_to_srgb


def _radius(w, h, y0, y1, zoom=1.0):
    half, cx, cy, _, _ = _frame(w, h)
    xs = ((np.arange(w, dtype=np.float32) - cx) / half) * zoom
    ys = ((np.arange(y0, y1, dtype=np.float32) - cy) / half) * zoom
    X, Y = np.meshgrid(xs, ys)
    return X * X + Y * Y


def apply(img: np.ndarray, lens: dict) -> np.ndarray:
    """Wendet den (bereinigten) Korrektur-Block auf ein RGB-uint8-Bild an; Ergebnis gleich groß."""
    e = effective(lens)
    h, w = img.shape[:2]
    to_lin, to_srgb = _lin()

    # 1) Vignettierung des Profils im Original (dort sitzt sie), linear
    if e["a"] and any(e["a"]):
        src = np.empty_like(img)
        for y0 in range(0, h, STRIP):
            y1 = min(h, y0 + STRIP)
            v = _poly(e["a"], _radius(w, h, y0, y1))
            g = np.clip(1.0 / np.maximum(v, 0.05), 0.2, 8.0).astype(np.float32)
            lin = to_lin(img[y0:y1]) * g[..., None]
            src[y0:y1] = (to_srgb(lin) * 255.0 + 0.5).astype(np.uint8)
        img = src

    # 2) Geometrie (Verzeichnung, Farbsäume) mit automatischem Zuschnitt
    if any(e["k"]) or e["red"]:
        zoom = zoom_for(e["k"], w, h)
        out = np.empty_like(img)
        chans = [np.ascontiguousarray(img[..., c]) for c in range(3)] if e["red"] else None
        for y0 in range(0, h, STRIP):
            y1 = min(h, y0 + STRIP)
            m = _maps(e, w, h, y0, y1, zoom)
            if chans is None:
                out[y0:y1] = cv2.remap(img, m["g"][0], m["g"][1], cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
            else:
                for c, key in ((0, "red"), (1, "g"), (2, "blue")):
                    out[y0:y1, :, c] = cv2.remap(chans[c], m[key][0], m[key][1], cv2.INTER_LINEAR,
                                                 borderMode=cv2.BORDER_REPLICATE)
        img = out

    # 3) Manuelle Vignettierung in der Ausgabe (positiv hellt die Ecken auf)
    if e["mv"]:
        _, _, _, ex, ey = _frame(w, h)
        rc2 = ex * ex + ey * ey
        p = 2.0 * 4.0 ** ((e["mid"] - 50.0) / 50.0)  # Mitte 0 → weit ins Bild, 100 → nur die Ecken
        out = img if img.flags.writeable else np.array(img)
        for y0 in range(0, h, STRIP):
            y1 = min(h, y0 + STRIP)
            rel = (_radius(w, h, y0, y1) / rc2) ** (p / 2.0)
            g = np.clip(1.0 + e["mv"] * rel, 0.05, 8.0).astype(np.float32)
            lin = to_lin(out[y0:y1]) * g[..., None]
            out[y0:y1] = (to_srgb(lin) * 255.0 + 0.5).astype(np.uint8)
        img = out
    return img


# ---------------------------------------------------------------- Import

_SAFE = re.compile(rb"<!(DOCTYPE|ENTITY)", re.I)


def _xml(data: bytes) -> ET.Element:
    if _SAFE.search(data[:200000]):
        raise ValueError("XML mit DOCTYPE/ENTITY wird nicht gelesen")
    return ET.fromstring(data)


_CAM = "http://ns.adobe.com/photoshop/1.0/camera-profile"
_RDF = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"


def _lcp_get(node: ET.Element, name: str):
    """Wert als Kindelement oder Attribut (beide Schreibweisen kommen in .lcp vor)."""
    v = node.get(f"{{{_CAM}}}{name}")
    if v is None:
        c = node.find(f"{{{_CAM}}}{name}")
        if c is not None:
            v = c.text
    return v


def _lcp_child(node: ET.Element, name: str) -> ET.Element | None:
    c = node.find(f"{{{_CAM}}}{name}")
    if c is None:
        return None
    d = c.find(f"{{{_RDF}}}Description")
    return d if d is not None else c


def lcp_items(root: ET.Element) -> list[ET.Element]:
    """Profileinträge einer .lcp: Beschreibungen mit PerspectiveModel oder VignetteModel, die selbst Objektivangaben tragen
    (nicht die Beschreibung im PerspectiveModel)."""
    return [n for n in root.iter()
            if (_lcp_child(n, "PerspectiveModel") is not None or _lcp_child(n, "VignetteModel") is not None)
            and any(_lcp_get(n, k) is not None for k in ("FocalLength", "LensPrettyName", "Lens", "Make"))]


def lcp_focal(it: ET.Element, sff: float) -> float | None:
    """Brennweite eines Eintrags; Festbrennweiten ohne FocalLength (etwa manuelle Objektive): aus FocalLengthX des Modells
    (Brennweite / lange Sensorseite), sonst aus LensInfo."""
    focal = _f(_lcp_get(it, "FocalLength"), 0.5, 5000)
    if focal:
        return focal
    for m in (_lcp_child(it, "PerspectiveModel"), _lcp_child(it, "VignetteModel")):
        fx = _f(_lcp_get(m, "FocalLengthX"), 1e-4, 100) if m is not None else None
        if fx:
            return round(fx * 36.0 / sff, 1)
    info = (_lcp_get(it, "LensInfo") or "").split()
    if info:
        try:
            a, _, b = info[0].partition("/")
            return _f(float(a) / float(b or 1), 0.5, 5000)
        except (ValueError, ZeroDivisionError):
            return None
    return None


def parse_lcp(data: bytes) -> list[dict]:
    """Adobe-Objektivprofil (.lcp, PerspectiveModel) → ein Profil im eigenen Format."""
    root = _xml(data)
    items = lcp_items(root)
    if not items and any(n.tag == f"{{{_CAM}}}FisheyeModel" for n in root.iter()):
        raise ValueError("Fischaugen-Profil (FisheyeModel) wird nicht unterstützt")
    if not items:
        raise ValueError("Keine Profileinträge (PerspectiveModel) gefunden")
    name = lens_name = camera = None
    dist, vig, tca = {}, {}, {}
    for it in items:
        sff = _f(_lcp_get(it, "SensorFormatFactor"), 0.1, 20, 1.0)
        focal = lcp_focal(it, sff)
        if not focal:
            continue
        av = _f(_lcp_get(it, "ApertureValue"), -2, 30)
        aperture = round(2 ** (av / 2.0), 2) if av is not None else None
        name = name or _lcp_get(it, "LensPrettyName") or _lcp_get(it, "Lens") or _lcp_get(it, "ProfileName")
        lens_name = lens_name or _lcp_get(it, "Lens") or _lcp_get(it, "LensPrettyName")
        camera = camera or _lcp_get(it, "CameraPrettyName") or _lcp_get(it, "Model")
        key = (focal, aperture)

        def fx_of(m):
            fx = _f(_lcp_get(m, "FocalLengthX"), 1e-4, 100)
            # Version 1 ohne FocalLengthX: Brennweite / lange Sensorseite (Kleinbild 36 mm)
            return fx if fx else focal / (36.0 / sff)

        pm = _lcp_child(it, "PerspectiveModel")
        if pm is not None:
            fx = fx_of(pm)
            s = 1.0 / (2.0 * fx)  # eigene Einheit (halbe lange Seite) → Adobe-Einheit (Brennweite)
            p = [_f(_lcp_get(pm, f"RadialDistortParam{i}"), -100, 100, 0.0) for i in (1, 2, 3)]
            dist.setdefault(key, []).append([p[0] * s ** 2, p[1] * s ** 4, p[2] * s ** 6])
            red, blue = _lcp_child(pm, "ChromaticRedGreenModel"), _lcp_child(pm, "ChromaticBlueGreenModel")
            if red is not None and blue is not None:
                def tc(m):
                    sc = 1.0 / (2.0 * fx_of(m))
                    q = [_f(_lcp_get(m, f"RadialDistortParam{i}"), -100, 100, 0.0) for i in (1, 2)]
                    return [_f(_lcp_get(m, "ScaleFactor"), 0.5, 2, 1.0), q[0] * sc ** 2, q[1] * sc ** 4]
                tca.setdefault(key, []).append(tc(red) + tc(blue))
        vm = _lcp_child(it, "VignetteModel")
        if vm is None and pm is not None:
            vm = _lcp_child(pm, "VignetteModel")  # meist im PerspectiveModel
        if vm is not None:
            fx = fx_of(vm)
            s = 1.0 / (2.0 * fx)
            a = [_f(_lcp_get(vm, f"VignetteModelParam{i}"), -100, 100, 0.0) for i in (1, 2, 3)]
            vig.setdefault(key, []).append([a[0] * s ** 2, a[1] * s ** 4, a[2] * s ** 6])

    def entries(d, make):
        out = []
        for (focal, ap), vals in sorted(d.items(), key=lambda x: (x[0][0], x[0][1] or 0)):
            v = np.mean(vals, axis=0).tolist()  # mehrere Entfernungen: gemittelt
            e = {"focal": focal}
            if ap:
                e["aperture"] = ap
            e.update(make(v))
            out.append(e)
        return out

    prof = {
        "name": name or "Objektiv", "lens": lens_name or name or "", "camera": camera or "", "source": "lcp",
        "distortion": entries(dist, lambda v: {"k": v}),
        "vignetting": entries(vig, lambda v: {"a": v}),
        "tca": entries(tca, lambda v: {"red": v[:3], "blue": v[3:]}),
    }
    return [sanitize_profile(prof)]


def _fit_even(r: np.ndarray, ratio: np.ndarray, n: int = 3) -> list[float]:
    """ratio(r) ≈ 1 + c1 r² + c2 r⁴ (+ c3 r⁶) per Ausgleichsrechnung."""
    A = np.stack([r ** (2 * i) for i in range(1, n + 1)], axis=1)
    c, *_ = np.linalg.lstsq(A, ratio - 1.0, rcond=None)
    return [float(x) for x in c] + [0.0] * (3 - n)


def parse_lensfun(data: bytes) -> list[dict]:
    """lensfun-XML → je Objektiv ein Profil (calibration: distortion, vignetting, tca)."""
    root = _xml(data)
    lenses = root.findall(".//lens") if root.tag != "lens" else [root]
    out = []
    r = np.linspace(0.0, 1.25, 126)
    for ln in lenses:
        cal = ln.find("calibration")
        if cal is None:
            continue
        model = (ln.findtext("model") or "").strip()
        maker = (ln.findtext("maker") or "").strip()
        aspect = _f(ln.findtext("aspect-ratio"), 1, 4, 1.5)
        if ln.findtext("aspect-ratio") and ":" in ln.findtext("aspect-ratio"):
            a, b = ln.findtext("aspect-ratio").split(":", 1)
            aspect = _f(float(a) / max(float(b), 1e-6), 1, 4, 1.5)
        s = aspect  # lensfun: 1 = halbe kurze Seite; eigene Einheit: halbe lange Seite
        diag = math.sqrt(1 + 1 / aspect ** 2)  # halbe Diagonale in eigener Einheit
        dist, vig, tca = [], [], []
        for d in cal.findall("distortion"):
            focal = _f(d.get("focal"), 0.5, 5000)
            if not focal:
                continue
            m = d.get("model")
            g = lambda k, dv=0.0: _f(d.get(k), -100, 100, dv)
            rl = r * s
            if m == "poly3":
                ratio = 1 - g("k1") + g("k1") * rl ** 2
            elif m == "poly5":
                dist.append({"focal": focal, "k": [g("k1") * s ** 2, g("k2") * s ** 4, 0.0]})
                continue
            elif m == "ptlens":
                a, b, c = g("a"), g("b"), g("c")
                ratio = a * rl ** 3 + b * rl ** 2 + c * rl + (1 - a - b - c)
            else:
                continue
            # Maßstab in der Mitte (≠ 1 bei ptlens/poly3) spielt keine Rolle: der Zuschnitt gleicht ihn aus
            dist.append({"focal": focal, "k": _fit_even(r, ratio / ratio[0])})
        vgroups: dict = {}
        for v in cal.findall("vignetting"):
            focal, ap = _f(v.get("focal"), 0.5, 5000), _f(v.get("aperture"), 0.5, 128)
            if not focal or v.get("model") != "pa":
                continue
            dist_m = _f(v.get("distance"), 0, 1e6, 1000.0)
            k = [_f(v.get(f"k{i}"), -100, 100, 0.0) for i in (1, 2, 3)]
            # lensfun: r = 1 an der halben Diagonale → eigene Einheit
            a = [k[0] / diag ** 2, k[1] / diag ** 4, k[2] / diag ** 6]
            cur = vgroups.get((focal, ap))
            if cur is None or dist_m > cur[0]:  # größte Entfernung (Unendlich) bevorzugt
                vgroups[(focal, ap)] = (dist_m, a)
        for (focal, ap), (_, a) in sorted(vgroups.items(), key=lambda x: (x[0][0], x[0][1] or 0)):
            e = {"focal": focal, "a": a}
            if ap:
                e["aperture"] = ap
            vig.append(e)
        for t in cal.findall("tca"):
            focal = _f(t.get("focal"), 0.5, 5000)
            if not focal:
                continue
            m = t.get("model")
            g = lambda k, dv=0.0: _f(t.get(k), -100, 100, dv)
            rl = r * s
            if m == "linear":
                tca.append({"focal": focal, "red": [g("kr", 1.0), 0.0, 0.0], "blue": [g("kb", 1.0), 0.0, 0.0]})
            elif m == "poly3":
                ch = {}
                for c in ("r", "b"):
                    ratio = g("v" + c, 1.0) + g("c" + c) * rl + g("b" + c) * rl ** 2
                    s0 = float(np.mean(ratio[:3]))
                    k = _fit_even(r, ratio / s0, 2)
                    ch[c] = [s0, k[0], k[1]]
                tca.append({"focal": focal, "red": ch["r"], "blue": ch["b"]})
        name = model if not maker or model.lower().startswith(maker.lower()) else f"{maker} {model}"
        out.append(sanitize_profile({"name": name, "lens": model, "camera": "", "source": "lensfun",
                                     "distortion": dist, "vignetting": vig, "tca": tca}))
    if not out:
        raise ValueError("Keine Objektive mit Kalibrierdaten gefunden")
    return out


def parse_any(data: bytes) -> list[dict]:
    """Erkennt eigenes JSON, Adobe .lcp und lensfun-XML."""
    head = data[:4096].lstrip()
    if head[:1] in (b"{", b"["):
        import json
        v = json.loads(data.decode("utf-8"))
        items = v.get("profiles") if isinstance(v, dict) and "profiles" in v else v
        items = items if isinstance(items, list) else [items]
        out = [p for p in (sanitize_profile(x) for x in items) if p and (p["distortion"] or p["vignetting"] or p["tca"])]
        if not out:
            raise ValueError("Keine Profile in der Datei")
        for p, x in zip(out, items):
            p.setdefault("name", "Objektiv")
            p.setdefault("source", (x.get("source") if isinstance(x, dict) else None) or "json")
        return out
    if b"camera-profile" in data[:20000] or b"stCamera" in data[:20000]:
        return parse_lcp(data)
    if b"<lensdatabase" in data[:20000] or b"<lens" in data[:20000]:
        return parse_lensfun(data)
    raise ValueError("Unbekanntes Format (erwartet: .json, .lcp oder lensfun-XML)")


# ---------------------------------------------------------------- Einmessen

def _decode_gray(data: bytes) -> np.ndarray:
    from . import rawio
    if rawio.is_raw(data):
        rgb = rawio.decode(data, half=True)
        return cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    arr = np.frombuffer(data, np.uint8)
    g = cv2.imdecode(arr, cv2.IMREAD_GRAYSCALE | cv2.IMREAD_IGNORE_ORIENTATION)
    if g is None:
        from PIL import Image
        g = np.array(Image.open(io.BytesIO(data)).convert("L"))
    return g


def find_corners(data: bytes, cols: int = 15, rows: int = 8) -> dict:
    """Schachbrett-Ecken (innere Ecken cols×rows) im Foto finden, subpixelgenau."""
    gray = _decode_gray(data)
    h, w = gray.shape[:2]
    f = min(1.0, 2000.0 / max(w, h))
    small = gray if f == 1.0 else cv2.resize(gray, (round(w * f), round(h * f)), interpolation=cv2.INTER_AREA)
    found, pc, pr = False, cols, rows
    # erst schnell, dann gründlich (EXHAUSTIVE dauert bei schwierigen Fotos deutlich länger)
    for flags in (cv2.CALIB_CB_NORMALIZE_IMAGE | cv2.CALIB_CB_ACCURACY,
                  cv2.CALIB_CB_NORMALIZE_IMAGE | cv2.CALIB_CB_EXHAUSTIVE | cv2.CALIB_CB_ACCURACY):
        for c, r in ((cols, rows), (rows, cols)):
            ok, corners = cv2.findChessboardCornersSB(small, (c, r), flags=flags)
            if ok:
                found, pc, pr = True, c, r
                break
        if found:
            break
    if not found:
        return {"found": False, "width": w, "height": h}
    pts = corners.reshape(-1, 2).astype(np.float32) / f
    g = pts.reshape(pr, pc, 2)
    sq = float(np.median(np.linalg.norm(np.diff(g, axis=1), axis=2)))
    win = int(max(3, min(25, round(sq * 0.3))))
    crit = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 50, 0.01)
    pts = cv2.cornerSubPix(gray, pts.reshape(-1, 1, 2), (win, win), (-1, -1), crit).reshape(-1, 2)
    return {"found": True, "width": w, "height": h, "cols": pc, "rows": pr,
            "corners": [[round(float(x), 3), round(float(y), 3)] for x, y in pts]}


def _hom(hp, G):
    H = np.append(hp, 1.0).reshape(3, 3)
    P = G @ H.T
    return P[:, :2] / P[:, 2:3]


def fit_distortion(images: list[dict], terms: int = 2) -> dict:
    """Gemeinsame k1, k2 (, k3) für Fotos einer Brennweite; je Foto eine Homographie (Ebene → Bild).

    Gauss-Newton/Levenberg-Marquardt mit numerischer Ableitung (ohne SciPy).
    Rückgabe: {"k": [3], "residual_px", "before_px", "images"}."""
    data = []
    for im in images:
        c = np.array(im["corners"], float)
        cols, rows = int(im["cols"]), int(im["rows"])
        if c.shape != (cols * rows, 2):
            raise ValueError("Ecken passen nicht zum Muster")
        half, cx, cy, _, _ = _frame(int(im["width"]), int(im["height"]))
        obs = (c - [cx, cy]) / half
        jj, ii = np.mgrid[0:rows, 0:cols]
        G = np.stack([ii.ravel(), jj.ravel(), np.ones(cols * rows)], axis=1).astype(float)
        H, _ = cv2.findHomography(G[:, :2], obs, 0)
        H = H / H[2, 2]
        data.append((G, obs, half, H.ravel()[:8]))
    if not data:
        raise ValueError("Keine Fotos")
    terms = max(1, min(3, terms))
    n = len(data)

    def resid(x, nk):
        k = list(x[:nk]) + [0.0] * (3 - nk)
        out = []
        for i, (G, obs, _, _) in enumerate(data):
            q = _hom(x[nk + 8 * i: nk + 8 * i + 8], G)
            f = _poly(k, (q * q).sum(1))
            out.append((q * f[:, None] - obs).ravel())
        return np.concatenate(out)

    def solve(nk, x):
        lam = 1e-3
        r = resid(x, nk)
        cost = r @ r
        for _ in range(100):
            J = np.empty((r.size, x.size))
            for j in range(x.size):
                d = 1e-7 * max(1.0, abs(x[j]))
                xp = x.copy()
                xp[j] += d
                J[:, j] = (resid(xp, nk) - r) / d
            A, g = J.T @ J, J.T @ r
            improved = False
            for _ in range(10):
                step = np.linalg.solve(A + lam * np.diag(np.diag(A) + 1e-12), -g)
                xn = x + step
                rn = resid(xn, nk)
                cn = rn @ rn
                if cn < cost:
                    x, r, lam, improved = xn, rn, max(lam / 5, 1e-9), True
                    break
                lam *= 10
            if not improved or abs(cost - cn) < 1e-16 + 1e-12 * cost:
                cost = r @ r
                break
            cost = cn
        return x, r

    x0 = np.concatenate([np.zeros(terms)] + [d[3] for d in data])
    # ohne Verzeichnung (nur Homographien) als Vergleich
    xb, rb = solve(0, np.concatenate([d[3] for d in data]))
    x, r = solve(terms, x0)

    def rms(res):
        out, off = [], 0
        for G, _, half, _ in data:
            m = G.shape[0] * 2
            out.append(np.sqrt(np.mean(res[off:off + m] ** 2) * 2) * half)
            off += m
        return out

    per, before = rms(r), rms(rb)
    k = [float(v) for v in x[:terms]] + [0.0] * (3 - terms)
    return {"k": k, "residual_px": float(np.sqrt(np.mean(np.square(per)))),
            "before_px": float(np.sqrt(np.mean(np.square(before)))),
            "images": [{"residual_px": float(a), "before_px": float(b)} for a, b in zip(per, before)]}


def checkerboard(cols: int = 16, rows: int = 9, square: int = 60, margin: int = 1) -> np.ndarray:
    """Schachbrett als Graustufenbild (Felder cols×rows, weißer Rand von margin Feldern) – für Tests."""
    W, H = (cols + 2 * margin) * square, (rows + 2 * margin) * square
    img = np.full((H, W), 255, np.uint8)
    for j in range(rows):
        for i in range(cols):
            if (i + j) % 2 == 0:
                y, x = (j + margin) * square, (i + margin) * square
                img[y:y + square, x:x + square] = 0
    return img
