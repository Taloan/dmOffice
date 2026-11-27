"""Profilbibliothek (Kennung lenslib): eine Datei (.lcp, lensfun-XML, eigenes JSON) → Profile samt Angaben für den Index.

Antwort je Profil: {"profile": {…wie lens.parse_any…}, "meta": {
    "make", "model", "camera", "lens", "lensName", "lensId", "raw" (bool|None), "crop",
    "focalMin", "focalMax", "apertureMin", "apertureMax"}}
Die Zuordnung zu Fotos macht dmOffice (Gallery/LensLibrary.cs); hier wird nur gelesen.
"""

from __future__ import annotations

from . import lens


def _s(v, n=200):
    return v.strip()[:n] if isinstance(v, str) and v.strip() else None


def _ranges(profile: dict) -> dict:
    focals, apertures = [], []
    for k in ("distortion", "vignetting", "tca"):
        for e in profile.get(k) or []:
            focals.append(e["focal"])
            if e.get("aperture"):
                apertures.append(e["aperture"])
    return {
        "focalMin": min(focals) if focals else None, "focalMax": max(focals) if focals else None,
        "apertureMin": min(apertures) if apertures else None, "apertureMax": max(apertures) if apertures else None,
    }


def _lcp_meta(data: bytes) -> dict:
    root = lens._xml(data)
    items = lens.lcp_items(root)
    meta: dict = {}

    def first(*names):
        for it in items:
            for n in names:
                v = _s(lens._lcp_get(it, n))
                if v:
                    return v
        return None

    raw = first("CameraRawProfile")
    meta["make"] = first("Make")
    meta["model"] = first("Model", "UniqueCameraModel")
    meta["camera"] = first("CameraPrettyName")
    meta["lens"] = first("Lens")
    meta["lensName"] = first("LensPrettyName")
    meta["lensId"] = first("LensID")
    meta["raw"] = None if raw is None else raw.lower() == "true"
    meta["crop"] = lens._f(first("SensorFormatFactor"), 0.1, 20)
    return meta


def read(data: bytes) -> list[dict]:
    """Datei lesen; ValueError, wenn sie kein brauchbares Profil enthält."""
    profiles = [p for p in lens.parse_any(data) if p["distortion"] or p["vignetting"] or p["tca"]]
    if not profiles:
        raise ValueError("Keine Korrekturwerte in der Datei")
    is_lcp = b"camera-profile" in data[:20000] or b"stCamera" in data[:20000]
    lcp = _lcp_meta(data) if is_lcp else None
    out = []
    for p in profiles:
        meta = dict(lcp) if lcp else {
            "make": None, "model": None, "camera": _s(p.get("camera")), "lens": _s(p.get("lens")),
            "lensName": _s(p.get("name")), "lensId": None, "raw": None, "crop": None,
        }
        meta.update(_ranges(p))
        out.append({"profile": p, "meta": meta})
    return out
