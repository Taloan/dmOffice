"""Antwortformat der Engine: Wertebereiche, JSON-Schema, Clamping."""

from __future__ import annotations

import copy

SCHEMA_VERSION = "1.0"

# (min, max, neutral) pro Regler
GLOBAL_RANGES: dict[str, tuple[float, float, float]] = {
    "exposure_ev": (-2.0, 2.0, 0.0),
    "contrast": (-100, 100, 0),
    "highlights": (-100, 100, 0),
    "shadows": (-100, 100, 0),
    "whites": (-100, 100, 0),
    "blacks": (-100, 100, 0),
    "temperature_shift": (-50, 50, 0),
    "tint_shift": (-50, 50, 0),
    "vibrance": (-100, 100, 0),
    "saturation": (-100, 100, 0),
    "clarity": (-100, 100, 0),
    "dehaze": (-100, 100, 0),
    "sharpen": (0, 100, 0),
}

SUBJECT_RANGES: dict[str, tuple[float, float, float]] = {
    "exposure_ev": (-1.0, 1.0, 0.0),
    "shadows": (-100, 100, 0),
}

STRAIGHTEN_RANGE = (-10.0, 10.0)

DESCRIPTIONS = {
    "exposure_ev": "Belichtung in Blendenstufen (2^ev; beim Abdunkeln Tiefen stärker, Farbe gehalten), -2.0 … +2.0",
    "contrast": "Kontrast (S-Kurve um Mittelgrau), -100 … +100",
    "highlights": "Lichter, -100 … +100 (negativ = Lichter zurückholen)",
    "shadows": "Tiefen, -100 … +100 (positiv = Schatten aufhellen)",
    "whites": "Weißpunkt, -100 … +100",
    "blacks": "Schwarzpunkt, -100 … +100 (positiv = Schwarz anheben)",
    "temperature_shift": "Relative Farbtemperatur, -50 (kühler) … +50 (wärmer)",
    "tint_shift": "Relativer Farbton, -50 (grüner) … +50 (magenta)",
    "vibrance": "Dynamik (schont gesättigte Farben und Hauttöne), -100 … +100",
    "saturation": "Sättigung global, -100 … +100",
    "clarity": "Klarheit / Mitteltonkontrast, -100 … +100",
    "dehaze": "Dunst entfernen, -100 … +100",
    "sharpen": "Schärfen, 0 … 100",
}


def _num(desc: str) -> dict:
    return {"type": "number", "description": desc}


RESPONSE_SCHEMA: dict = {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "title": "PhotoCorrections",
    "type": "object",
    "properties": {
        "schema_version": {"type": "string"},
        "analysis": {"type": "string", "description": "Lesbare Begründung der Korrekturen"},
        "global": {
            "type": "object",
            "properties": {k: _num(v) for k, v in DESCRIPTIONS.items()},
            "required": list(DESCRIPTIONS),
            "additionalProperties": False,
        },
        "subject_adjustments": {
            "type": "object",
            "properties": {
                "apply": {"type": "boolean"},
                "exposure_ev": _num("Zusätzliche Belichtung auf dem Motiv, -1.0 … +1.0"),
                "shadows": _num("Zusätzliche Tiefenaufhellung auf dem Motiv, -100 … +100"),
                "region": {
                    "type": "object",
                    "description": "Motivrahmen normiert 0…1 (Ursprung oben links); Maske = weiche Ellipse darin",
                    "properties": {k: _num(k) for k in ("x", "y", "w", "h")},
                    "required": ["x", "y", "w", "h"],
                },
                "source": {"type": "string", "enum": ["face", "saliency", "center", "none"]},
            },
            "required": ["apply", "exposure_ev", "shadows", "region"],
        },
        "crop": {
            "type": "object",
            "properties": {"straighten_deg": _num("Drehung in Grad, positiv = gegen den Uhrzeigersinn, -10 … +10")},
            "required": ["straighten_deg"],
        },
        "confidence": {"type": "string", "enum": ["low", "medium", "high"]},
        "metrics": {"type": "object", "description": "Gemessene Kennzahlen (nur Info)"},
    },
    "required": ["schema_version", "analysis", "global", "subject_adjustments", "crop", "confidence"],
}


def _clamp(v, lo, hi):
    try:
        v = float(v)
    except (TypeError, ValueError):
        return None
    if v != v:  # NaN
        return None
    return max(lo, min(hi, v))


def neutral_corrections() -> dict:
    return {
        "schema_version": SCHEMA_VERSION,
        "analysis": "",
        "global": {k: n for k, (_, _, n) in GLOBAL_RANGES.items()},
        "subject_adjustments": {
            "apply": False,
            "exposure_ev": 0.0,
            "shadows": 0,
            "region": {"x": 0.25, "y": 0.25, "w": 0.5, "h": 0.5},
            "source": "none",
        },
        "crop": {"straighten_deg": 0.0},
        "confidence": "low",
    }


def sanitize(data: dict) -> dict:
    """Klemmt alle Werte in ihre Bereiche, rundet und füllt Fehlendes neutral auf."""
    out = neutral_corrections()
    data = copy.deepcopy(data or {})
    out["analysis"] = str(data.get("analysis", ""))[:4000]
    if data.get("confidence") in ("low", "medium", "high"):
        out["confidence"] = data["confidence"]
    if "metrics" in data:
        out["metrics"] = data["metrics"]

    g = data.get("global") or {}
    for k, (lo, hi, n) in GLOBAL_RANGES.items():
        v = _clamp(g.get(k, n), lo, hi)
        v = n if v is None else v
        out["global"][k] = round(v, 2) if k == "exposure_ev" else int(round(v))

    s = data.get("subject_adjustments") or {}
    sa = out["subject_adjustments"]
    sa["apply"] = bool(s.get("apply", False))
    for k, (lo, hi, n) in SUBJECT_RANGES.items():
        v = _clamp(s.get(k, n), lo, hi)
        v = n if v is None else v
        sa[k] = round(v, 2) if k == "exposure_ev" else int(round(v))
    r = s.get("region") or {}
    x = _clamp(r.get("x", 0.25), 0.0, 0.99) or 0.0
    y = _clamp(r.get("y", 0.25), 0.0, 0.99) or 0.0
    w = _clamp(r.get("w", 0.5), 0.01, 1.0 - x) or 0.01
    h = _clamp(r.get("h", 0.5), 0.01, 1.0 - y) or 0.01
    sa["region"] = {"x": round(x, 4), "y": round(y, 4), "w": round(w, 4), "h": round(h, 4)}
    if s.get("source") in ("face", "saliency", "center", "none"):
        sa["source"] = s["source"]
    if sa["exposure_ev"] == 0 and sa["shadows"] == 0:
        sa["apply"] = False

    c = data.get("crop") or {}
    v = _clamp(c.get("straighten_deg", 0.0), *STRAIGHTEN_RANGE)
    out["crop"]["straighten_deg"] = round(v or 0.0, 2)
    return out
