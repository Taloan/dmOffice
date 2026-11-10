"""Das „Urteil“: Szene einordnen, Korrekturen ableiten, an der Vorschau gegenprüfen.

Grundsätze (fest eingebaut):
- Natürlichkeit vor Effekt; Stimmung (Tageszeit, Licht) bleibt erhalten.
- Hauptmotiv gut erkennbar, Hauttöne natürlich.
- Lichter nicht ausbrennen, Schatten nicht absaufen – nach Messwerten.
- JPG: Schatten nur moderat aufhellen (Rauschen/Artefakte).
- Lieber zu wenig als zu viel; gute Fotos bekommen (fast) nichts.
"""

from __future__ import annotations

import math

import cv2
import numpy as np

from . import metrics as M
from .develop import apply_corrections
from .schema import neutral_corrections, sanitize

DEFAULT_TARGET = 110.0          # „normale“ wahrgenommene Bildhelligkeit (0–255)
TONAL_KEYS = ("exposure_ev", "contrast", "highlights", "shadows", "whites", "blacks",
              "temperature_shift", "tint_shift")


# ---------------------------------------------------------------- Helfer

def _lin(v255: float) -> float:
    x = max(v255, 0.5) / 255.0
    return x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4


def _ev_between(cur: float, tgt: float) -> float:
    return math.log2(_lin(tgt) / _lin(cur))


def _key(t: dict) -> float:
    """Helligkeits-„Schwerpunkt“ (wahrgenommen): Median robust, Mittelwert für Lichtanteile."""
    return 0.6 * t.get("median_v", t["median"]) + 0.4 * t.get("mean_v", t["mean"])


def _clip(v, lo, hi):
    return max(lo, min(hi, v))


def _downscale(rgb: np.ndarray, edge: int) -> np.ndarray:
    h, w = rgb.shape[:2]
    f = edge / max(h, w)
    if f >= 1:
        return rgb
    return cv2.resize(rgb, (round(w * f), round(h * f)), interpolation=cv2.INTER_AREA)


def _region_mean(rgb: np.ndarray, region: dict) -> float:
    h, w = rgb.shape[:2]
    x0, y0 = int(region["x"] * w), int(region["y"] * h)
    x1, y1 = max(x0 + 1, int((region["x"] + region["w"]) * w)), max(y0 + 1, int((region["y"] + region["h"]) * h))
    sub = rgb[y0:y1, x0:x1].astype(np.float32)
    return float((0.2126 * sub[..., 0] + 0.7152 * sub[..., 1] + 0.0722 * sub[..., 2]).mean())


# ---------------------------------------------------------------- Szene

def classify(m: dict, exif: dict) -> dict:
    t, c, x, s = m["tone"], m["color"], m["texture"], m["subject"]
    tags: list[str] = []
    why: list[str] = []
    iso = exif.get("iso") or 0
    et = exif.get("exposure_time") or 0
    hour = exif.get("hour")
    spread = t["p95"] - t["p5"]

    long_or_high = et >= 1 / 15 or iso >= 3200
    late = hour is not None and (hour >= 20 or hour <= 5)
    # Tageslicht belegt: kurze Zeit + niedrige ISO → dunkles Bild ist Schatten/Wald, keine Nacht
    daylight_proof = bool(et) and et <= 1 / 60 and 0 < iso <= 800 and not late
    fn = exif.get("f_number") or 0
    # Lichtwert der Szene (EV bei ISO 100): ≥ 12,5 heißt Sonne, darunter Schatten/bedeckt
    ev100 = math.log2(fn * fn / et) - math.log2(iso / 100) if fn and et and iso else None
    no_exif = not et and not iso
    dark_with_lights = t["median"] < 45 and t["p99"] > 200
    if not daylight_proof and t["median"] < 70 and (long_or_high or late or (no_exif and dark_with_lights)):
        tags.append("night"); why.append("Nacht-/Available-Light-Aufnahme")
    elif late:
        # Abend/Blaue Stunde: Silhouetten und Dämmerlicht sind gewollt
        tags.append("dusk"); why.append("Dämmerung/Blaue Stunde, Silhouetten bleiben dunkel")

    golden_time = hour is not None and (6 <= hour <= 8 or 17 <= hour <= 21)
    if "night" not in tags and (c["neutral_b"] > 8 and c["warm_frac"] > 0.12 or golden_time and c["neutral_b"] > 5):
        tags.append("golden"); why.append("warmes Abend-/Morgenlicht")

    indoor_light = iso >= 800 or (et and et >= 1 / 30)
    if ({"night", "golden"}.isdisjoint(tags) and abs(c["neutral_b"]) >= 6 and indoor_light
            and not exif.get("flash_fired")):
        tags.append("artificial"); why.append("Kunstlicht")

    if t["median"] > 150 and c["chroma_mean"] < 16:
        tags.append("high_key")
        why.append("Schnee/Strand/High-Key" if c["neutral_b"] < 2 else "sehr helle Szene")

    if x["dark_channel_median"] > 60 and t["p1"] > 25 and spread < 175 and c["chroma_mean"] < 20:
        tags.append("haze"); why.append("Dunst/flaues Licht")

    bright_bg = t["top_mean"] > 160 or t["clip_hi_pct"] > 1.0
    sub_dark = s["subject_mean"] < (0.7 if s["source"] in ("face", "saliency") else 0.6) * t["mean"]
    # Histogramm-Signatur: großer ausgebrannter Bereich + dunkle Mehrheit
    silhouette = t["clip_hi_pct"] > 8 and t["median"] < 100 and t["p75"] - t["p25"] > 120
    # Person im Schatten vor hellerem Hintergrund (z. B. sonniger Wald) ist Gegenlicht, keine Low-Key-Absicht
    face_in_shadow = (s["source"] == "face" and s["subject_mean"] < 100 and "night" not in tags
                      and (t["p95"] > 200 or s["subject_mean"] < 0.85 * t["mean"]))
    if (sub_dark and bright_bg) or silhouette or face_in_shadow:
        tags.append("backlight"); why.append("Gegenlicht, Motiv zu dunkel")

    if "night" not in tags and c.get("foliage_frac", 0) > 0.2:
        tags.append("foliage"); why.append("viel Laub/Vegetation (Farben sind Bildinhalt)")

    if s["source"] == "face" and s.get("face_area_frac", 0) > 0.006:
        tags.append("portrait"); why.append(f"{s['count']} Gesicht(er) erkannt")

    daytime = hour is None or 8 <= hour <= 17
    # Harte Sonne: tiefe Schlagschatten neben sonnigen Flächen. Die Kamera belichtet dann knapp
    # (Lichter geschützt) – keine Low-Key-Absicht und kein dunkler Wald, sondern zu viel Kontrast
    # fürs JPG: Lichter zurück, Schatten auf, notfalls etwas heller.
    if ev100 is not None:
        sunny_contrast = ev100 >= 12.5 and spread > 160 and t["p5"] < 25
    else:
        sunny_contrast = spread > 180 and t["p5"] < 22 and t["p95"] > 195
    if (daylight_proof and daytime and sunny_contrast
            and not {"night", "dusk", "golden", "artificial", "backlight", "high_key"} & set(tags)):
        tags.append("harsh_sun"); why.append("harte Sonne: tiefe Schatten neben hellen Flächen")

    # Sonne laut Lichtwert, Bild aber dunkel → die Kamera hat unterbelichtet (Lichter geschützt).
    # Die Szene war hell, also darf kräftig angehoben werden.
    if (daylight_proof and daytime and ev100 is not None and ev100 >= 12.5 and _key(t) < 70
            and not {"backlight", "golden", "artificial", "dusk", "high_key"} & set(tags)):
        tags.append("underexposed"); why.append(f"Sonne (LW {ev100:.0f}), aber unterbelichtet")

    if (daylight_proof and daytime and t["median"] < 60
            and not {"backlight", "golden", "artificial", "dusk", "harsh_sun", "underexposed"} & set(tags)):
        tags.append("shade"); why.append("Schatten/Wald bei Tageslicht, zu dunkel")

    if ("night" not in tags and "backlight" not in tags and "shade" not in tags and "harsh_sun" not in tags
            and t["median"] < 75 and t["p99"] > 215
            and spread > 190 and t["clip_hi_pct"] < 5):
        tags.append("low_key"); why.append("bewusst dunkle, kontrastreiche Stimmung")

    # unterbelichtet: der Umfang ist nur wegen der Dunkelheit klein, nicht flau
    if spread < 150 and not {"haze", "high_key", "night", "dusk", "underexposed"} & set(tags):
        tags.append("flat"); why.append("geringer Tonwertumfang")

    if "golden" not in tags and "night" not in tags and c["blue_top_frac"] > 0.25 and c["green_frac"] > 0.08:
        tags.append("landscape"); why.append("Landschaft mit Himmel")

    return {"tags": tags, "reasons": why}


def _ev_cap(tags: list[str], t: dict) -> float:
    """Höchste globale Belichtungsanhebung je Szene."""
    if "night" in tags or "dusk" in tags:
        return 0.3  # Nacht bleibt Nacht; Anheben bringt vor allem Rauschen
    if "underexposed" in tags:
        return 2.0  # Lichter-Regel und Schulter schützen die hellen Stellen
    if "backlight" in tags:
        return 0.0 if t["clip_hi_pct"] + t.get("near_hi_pct", 0.0) > 6 else 0.3
    if "shade" in tags:
        return 0.15
    if "harsh_sun" in tags:
        return 0.7  # knapp belichtete Sonnenbilder; Lichter fängt die Lichter-Regel ab
    if "portrait" in tags:
        return 0.4  # Gesicht holt die lokale Aufhellung, global zurückhaltend
    return 1.0


def _accept_range(tags: list[str]) -> tuple[float, float, float]:
    """(untere Grenze, Ziel, obere Grenze) für den Helligkeits-Schwerpunkt."""
    if "night" in tags:
        return 35, 60, 85
    if "low_key" in tags:
        return 60, 80, 105
    if "high_key" in tags:
        return 140, 165, 200
    if "golden" in tags:
        return 60, 90, 135  # Abend-/Lampenlicht darf dunkler bleiben
    if "shade" in tags:
        return 35, 45, 140  # Schatten: kaum heller, den Hauptteil machen die Tiefen
    if "underexposed" in tags:
        return 80, 100, 160
    if "harsh_sun" in tags:
        return 75, 92, 160  # Schlagschatten drücken den Schwerpunkt etwas, nur deutlich zu dunkel wird angehoben
    if "backlight" in tags:
        # Gegenlicht: Gesamthelligkeit nicht auf Normalniveau ziehen (Himmel brennt sonst aus);
        # das Motiv holen Tiefen und lokale Aufhellung
        return 50, 70, 175
    return 85, DEFAULT_TARGET, 160


# ---------------------------------------------------------------- Regeln

def initial_rules(m: dict, exif: dict, scene: dict) -> tuple[dict, list[str]]:
    t, c, x, s = m["tone"], m["color"], m["texture"], m["subject"]
    tags = scene["tags"]
    notes: list[str] = []
    corr = neutral_corrections()
    g = corr["global"]
    iso = exif.get("iso") or 0
    spread = t["p95"] - t["p5"]

    noise_factor = 1.0
    if iso >= 3200 or x["noise"] > 5:
        noise_factor = 0.6
    elif iso >= 800 or x["noise"] > 3:
        noise_factor = 0.8

    # --- Belichtung
    lo, tgt, hi = _accept_range(tags)
    key = _key(t)
    # Helle Sonnenbilder ohne nennenswertes Ausbrennen bleiben hell – Abdunkeln macht sie grau
    too_bright = key > hi and t["clip_hi_pct"] > 5 and "backlight" not in tags
    if key < lo or too_bright:
        bound = lo if key < lo else hi
        aim = bound + 0.4 * (tgt - bound)
        ev = _ev_between(key, aim)
        if ev > 0:
            ev *= 0.85 if noise_factor < 1 else 1.0
        g["exposure_ev"] = _clip(ev, -0.3, _ev_cap(tags, t))  # Abdunkeln wirkt schnell grau → vorsichtig
        notes.append(f"Helligkeit {key:.0f} außerhalb {lo:.0f}–{hi:.0f} → {g['exposure_ev']:+.2f} EV")

    if "backlight" in tags:
        bright_share = t["clip_hi_pct"] + t.get("near_hi_pct", 0.0)
        if bright_share > 6:
            # viel (fast) weiße Fläche, z. B. Himmel: global nicht anheben
            g["exposure_ev"] = min(g["exposure_ev"], 0.0)
        elif g["exposure_ev"] < 0.2:
            g["exposure_ev"] = 0.2  # leicht anheben, die Schulter schützt die Lichter
        g["exposure_ev"] = min(g["exposure_ev"], 0.3)
    if "shade" in tags:
        g["exposure_ev"] = min(g["exposure_ev"], 0.15)
    ev = g["exposure_ev"]

    # --- Lichter
    # Rettbar ist nur, was knapp unter Weiß liegt (230–249); echtes Clipping bleibt weiß.
    near = t.get("near_hi_pct", 0.0)
    if near > 1.0 or t["p95"] >= 235 or ev > 0.2:
        hl = 6 + 1.5 * near + max(0, t["p95"] - 225) * 0.6 + 15 * max(ev, 0)
        if "high_key" in tags:
            hl *= 0.5
        if "backlight" in tags and ev <= 0:
            hl *= 0.5  # nichts angehoben → heller Hintergrund darf hell bleiben
        g["highlights"] = -_clip(hl, 0, 30)
        if g["highlights"] <= -10:
            notes.append(f"helle Bereiche zurücknehmen ({near:.1f} % knapp unter Weiß)")
    if "harsh_sun" in tags:
        hl = 10 + 1.5 * near + max(0, t["p95"] - 195) * 0.6
        g["highlights"] = min(g["highlights"], -_clip(hl, 0, 30))
        notes.append("harte Sonne: helle Flächen zurücknehmen")
    if t["clip_hi_pct"] > 5:
        notes.append(f"{t['clip_hi_pct']:.0f} % ausgebrannt – nicht rettbar, bleibt weiß")

    # --- Tiefen
    if t["clip_lo_pct"] > 0.8 or t["p5"] < 12 or ev < -0.2:
        sh = 6 + 6 * t["clip_lo_pct"] + max(0, 12 - t["p5"]) * 1.5 + 12 * max(-ev, 0)
        sh *= noise_factor
        if {"night", "low_key", "dusk"} & set(tags):
            sh = min(sh, 12)
        g["shadows"] = _clip(sh, 0, 40)
        if g["shadows"] >= 10:
            notes.append(f"Tiefen moderat aufhellen ({t['clip_lo_pct']:.1f} % ≤5)")
    if "backlight" in tags and g["shadows"] < 30 * noise_factor:
        g["shadows"] = 30 * noise_factor
        notes.append("Gegenlicht: Tiefen/Vordergrund aufhellen")
    if "shade" in tags and g["shadows"] < 35 * noise_factor:
        g["shadows"] = 35 * noise_factor
        notes.append("Schatten: Tiefen kräftig aufhellen")
    if "harsh_sun" in tags:
        # je tiefer die Schlagschatten, desto mehr; 30 … 45
        sun_sh = _clip(30 + max(0, 20 - t["p5"]) * 1.5, 30, 45) * noise_factor
        g["shadows"] = max(g["shadows"], sun_sh)
        notes.append("harte Sonne: Schlagschatten aufhellen")
    if "portrait" in tags and "backlight" not in tags:
        # Gesicht natürlich, dunkle Kleidung darf dunkel bleiben; Schlagschatten brauchen etwas mehr
        g["shadows"] = min(g["shadows"], 38 if "harsh_sun" in tags else 25)

    # --- Schwarz/Weiß
    if t["p1"] > 22 and "high_key" not in tags:
        g["blacks"] = -_clip((t["p1"] - 16) * 0.8, 0, 25)
        notes.append("kein echtes Schwarz → Schwarzpunkt setzen")
    if {"harsh_sun", "underexposed"} & set(tags) and g["shadows"] >= 35:
        # stark aufgehellte Tiefen wirken sonst milchig – Schwarz hält die Tiefe
        g["blacks"] = min(g["blacks"], -_clip((g["shadows"] - 25) * 0.6, 0, 15))
    if t["p99"] < 220 and t["clip_hi_pct"] < 0.1 and c["blue_top_frac"] < 0.2 and not {"night", "low_key"} & set(tags):
        g["whites"] = _clip((232 - t["p99"]) * 0.5, 0, 12)
        notes.append("kein echtes Weiß → Weißpunkt anheben")

    # --- Kontrast
    if spread < 150 and not {"night", "dusk"} & set(tags):
        g["contrast"] = _clip((170 - spread) * 0.25, 0, 20)
    elif spread > 235 and t["clip_hi_pct"] > 0.5 and t["clip_lo_pct"] > 0.5:
        g["contrast"] = -8
    if g["whites"] and g["blacks"]:
        g["contrast"] *= 0.5  # Tonwertspreizung wirkt schon wie Kontrast
    if "portrait" in tags:
        g["contrast"] = min(g["contrast"], 10)
    if "haze" in tags:
        g["contrast"] = min(g["contrast"], 8)  # Rest übernimmt Dehaze

    # --- Weißabgleich: nur ein kleiner Teil des Stichs, Stimmung hat Vorrang
    a_n, b_n = c["neutral_a"], c["neutral_b"]
    # Laub schützt nur vor „Entgelben“; ein kühler Stich darf auch dort moderat weg
    mood = (bool({"night", "dusk", "golden", "artificial"} & set(tags))
            or ({"foliage", "harsh_sun"} & set(tags) and b_n > 0)  # Laub/Sonnenlicht ist warm
            # viel Blau im Bild (Meer, Himmel, Dämmerung): kühler „Stich“ ist meist Bildinhalt
            or (b_n < 0 and c["blue_frac"] > 0.2))
    f = 0.3 if c["neutral_frac"] > 0.08 else 0.2  # wenige neutrale Flächen → vorsichtiger
    if b_n > 0:
        f *= 0.7  # warmer Stich wirkt meist angenehm → noch zurückhaltender
    if mood:
        f = 0.1   # Available Light, Abendlicht, Kunstlicht: Lichtstimmung erhalten
    if "high_key" in tags and b_n < 0:
        f = 0.5   # Schnee soll weiß sein, nicht blau
    if "harsh_sun" in tags and b_n > 0:
        f = 0.0   # Sonnenlicht ist warm – nicht abkühlen
    f_t = f * 0.6  # Tönung (grün/magenta) noch vorsichtiger als Temperatur
    t_cap, m_cap = (5, 3) if mood else (10, 6)
    # Kalibrierung develop.py: 1 Einheit Temperatur ≈ 0,28 b*, 1 Einheit Tint ≈ 0,23 a*
    temp = -3.6 * b_n * f if abs(b_n) >= 5 else 0.0
    # nur Grünstiche (a < 0, z. B. Leuchtstoff); „Magenta“ ist praktisch immer Bildinhalt (Abendhimmel)
    tint = -4.3 * a_n * f_t if a_n <= -4 else 0.0
    g["temperature_shift"] = _clip(temp, -t_cap, t_cap)
    g["tint_shift"] = _clip(tint, -m_cap, m_cap)
    if abs(g["temperature_shift"]) >= 4 or abs(g["tint_shift"]) >= 4:
        notes.append(f"Farbstich (a={a_n:+.1f}, b={b_n:+.1f}) leicht abmildern")
    elif mood and (abs(b_n) >= 5 or abs(a_n) >= 4):
        notes.append("Lichtstimmung bleibt erhalten (Farbstich bewusst nicht ausgeglichen)")
    corr["_wb_target"] = (a_n * (1 - f_t) if abs(a_n) >= 4 else a_n, b_n * (1 - f) if abs(b_n) >= 5 else b_n)

    # --- Farbe
    cm = c["chroma_mean"]
    if cm < 12:
        vib = (16 - cm) * 2
    elif cm < 22:
        vib = 8
    elif cm < 30:
        vib = 4
    else:
        vib = 0
    if "haze" in tags:
        vib += 5
    if "shade" in tags:
        vib += 3  # aufgehellte Schatten wirken sonst etwas blass
    if {"harsh_sun", "underexposed"} & set(tags):
        vib += 6  # stark aufgehellte Schatten verlieren sichtbar Farbe
    if "portrait" in tags or c["skin_frac"] > 0.05:
        vib = min(vib, 8 if "harsh_sun" in tags else 6)  # Haut wird sonst schnell rot
    if "night" in tags:
        vib = min(vib, 5)
    g["vibrance"] = _clip(vib, 0, 15)
    if c["chroma_p90"] > 70:
        g["saturation"] = -_clip((c["chroma_p90"] - 70) * 0.5, 0, 15)
        notes.append("Farben bereits sehr kräftig → Sättigung leicht zurück")

    # --- Klarheit / Dunst
    lc = x["local_contrast"]
    cl = _clip((9 - lc) * 2, 0, 10) if lc < 9 else 0
    if "landscape" in tags:
        cl += 5
    if "portrait" in tags:
        cl = min(cl, 3 if s.get("face_area_frac", 0) > 0.03 else 6)
    g["clarity"] = _clip(cl * noise_factor, 0, 20)
    if "haze" in tags:
        g["dehaze"] = _clip((x["dark_channel_median"] - 50) * 0.6, 5, 30)
        notes.append("Dunst reduzieren")

    # --- Schärfen
    sp = 25.0
    if x["sharpness"] > 40:
        sp = 12
    elif x["sharpness"] < 8:
        sp = 30
    if "portrait" in tags:
        sp = min(sp, 18)
    g["sharpen"] = _clip(sp * noise_factor, 0, 40)

    # --- Motiv (lokal)
    sa = corr["subject_adjustments"]
    sa["region"] = s["region"]
    sa["source"] = s["source"]
    if s["source"] in ("face", "saliency") or "backlight" in tags:
        subj_target = 120.0 if s["source"] == "face" else max(lo, 0.85 * tgt)
        after_global = s["subject_mean"] * 2 ** (ev / 2.2)  # grobe Gamma-Näherung, wird später simuliert
        if after_global < 0.78 * subj_target or "backlight" in tags:
            sev = _clip(_ev_between(max(after_global, 5), subj_target) * 0.5, 0, 0.4)
            if sev >= 0.1:
                sa["apply"] = True
                sa["exposure_ev"] = sev
                sa["shadows"] = _clip(15 * noise_factor, 0, 25)
                notes.append(f"Motiv ({'Gesicht' if s['source'] == 'face' else 'Hauptmotiv'}) gezielt aufhellen")
        elif s["source"] == "face" and after_global > 205:
            sa["apply"] = True
            sa["exposure_ev"] = -0.3
            notes.append("Gesicht etwas zu hell → lokal abdunkeln")
    corr["_subject_target"] = 120.0 if s["source"] == "face" else None

    # --- Horizont
    tl = m.get("tilt") or {"angle": 0.0, "support": 0.0, "lines": 0}
    if tl["support"] >= 0.18 and tl["lines"] >= 1 and 0.3 <= abs(tl["angle"]) <= 6:
        corr["crop"]["straighten_deg"] = round(tl["angle"], 2)
        notes.append(f"Horizont um {tl['angle']:+.1f}° begradigen")

    return corr, notes


# ---------------------------------------------------------------- Gegenprüfung

def verify(rgb: np.ndarray, m: dict, corr: dict, scene: dict, notes: list[str], iterations: int = 3) -> int:
    """Simuliert die Korrektur an der Vorschau und regelt nach (Belichtung, Clipping, Motiv, WB)."""
    t0 = m["tone"]
    lo, tgt, hi = _accept_range(scene["tags"])
    g = corr["global"]
    sa = corr["subject_adjustments"]
    wb_target = corr.get("_wb_target")
    subj_target = corr.get("_subject_target")
    # WB-Nachregelung höchstens im Bereich 0,5×–1,5× des Erstvorschlags
    t_init, m_init = g["temperature_shift"], g["tint_shift"]
    # Gegenprüfung darf den Weißabgleich nur abschwächen, nie verstärken
    t_lim = sorted((0.5 * t_init, t_init))
    m_lim = sorted((0.5 * m_init, m_init))
    # Simulation auf kleiner Vorschau – für Tonwerte völlig ausreichend und schnell
    small = _downscale(rgb, 800)
    used = 0
    for _ in range(iterations):
        sim = apply_corrections(small, sanitize(corr), simulate=True)
        t = M.tone_metrics(sim)
        changed = False
        used += 1

        key = _key(t)
        if g["exposure_ev"] != 0 or key < lo or key > hi:
            # nur ins Akzeptanzband zurück, nicht aufs Ziel zwingen
            if key < lo - 3 or (key > hi + 3 and (t["clip_hi_pct"] > 5 or g["exposure_ev"] > 0)):
                bound = lo if key < lo else hi
                d = _ev_between(key, bound + 0.3 * (tgt - bound)) * 0.7
                g["exposure_ev"] = _clip(g["exposure_ev"] + d, -0.3 if "backlight" not in scene["tags"] else 0.0, _ev_cap(scene["tags"], t0))
                changed = True

        # nur eingreifen, wenn die Korrektur NEUES Clipping erzeugt (altes ist nicht rettbar)
        allowed_hi = max(t0["clip_hi_pct"], 0.3)
        if t["clip_hi_pct"] > allowed_hi + 0.3:
            if g["exposure_ev"] > 0 and g["highlights"] <= -35:
                g["exposure_ev"] -= 0.1
            else:
                g["highlights"] = max(-35, g["highlights"] - 8 - 4 * (t["clip_hi_pct"] - allowed_hi))
            changed = True

        allowed_lo = max(t0["clip_lo_pct"] * 0.8, 0.8)
        if "night" not in scene["tags"] and t["clip_lo_pct"] > allowed_lo + 0.3 and g["shadows"] < 40:
            g["shadows"] = min(40, g["shadows"] + 6)
            changed = True

        if sa["apply"] and subj_target:
            sm = _region_mean(sim, m["subject"]["faces"][0] if m["subject"].get("faces") else sa["region"])
            if abs(sm - subj_target) > 15:
                d = _ev_between(sm, subj_target) * 0.5
                sa["exposure_ev"] = _clip(sa["exposure_ev"] + d, -0.6, 0.4)
                changed = True

        if wb_target and (g["temperature_shift"] or g["tint_shift"]):
            c = M.color_metrics(sim)
            ta, tb = wb_target
            if c["neutral_frac"] >= 0.03 and abs(c["neutral_b"] - tb) > 1.5 and g["temperature_shift"]:
                new = _clip(g["temperature_shift"] - 2.5 * (c["neutral_b"] - tb), *t_lim)
                changed |= abs(new - g["temperature_shift"]) > 0.5
                g["temperature_shift"] = new
            if c["neutral_frac"] >= 0.03 and abs(c["neutral_a"] - ta) > 1.5 and g["tint_shift"]:
                new = _clip(g["tint_shift"] - 3.0 * (c["neutral_a"] - ta), *m_lim)
                changed |= abs(new - g["tint_shift"]) > 0.5
                g["tint_shift"] = new

        if not changed:
            break
    return used


def _settle(corr: dict) -> None:
    """Totzonen: winzige Korrekturen weglassen („lieber zu wenig“)."""
    g = corr["global"]
    if abs(g["exposure_ev"]) < 0.1:
        g["exposure_ev"] = 0.0
    for k in g:
        if k not in ("exposure_ev", "sharpen") and abs(g[k]) < 4:
            g[k] = 0
    sa = corr["subject_adjustments"]
    if abs(sa["exposure_ev"]) < 0.1:
        sa["exposure_ev"] = 0.0
    if not sa["exposure_ev"] and abs(sa["shadows"]) < 5:
        sa["apply"] = False


def _confidence(m: dict, scene: dict, corr: dict, iterations: int) -> str:
    t = m["tone"]
    score = 3
    if t["p99"] < 60 or t["p1"] > 200 or t["clip_hi_pct"] > 15 or t["clip_lo_pct"] > 25:
        score -= 2  # extrem: viel Information fehlt
    if {"night", "golden"} & set(scene["tags"]) and (corr["global"]["temperature_shift"] or corr["global"]["tint_shift"]):
        score -= 1
    if "backlight" in scene["tags"] and m["subject"]["source"] != "face":
        score -= 1
    if iterations >= 3:
        score -= 1
    if m["color"]["neutral_frac"] < 0.03:
        score -= 1
    return "high" if score >= 3 else "medium" if score >= 2 else "low"


# ---------------------------------------------------------------- API

def analyze(rgb: np.ndarray, exif: dict | None = None) -> dict:
    exif = exif or {}
    m = M.measure(rgb)
    scene = classify(m, exif)
    corr, notes = initial_rules(m, exif, scene)
    iterations = verify(rgb, m, corr, scene, notes)
    _settle(corr)
    corr["confidence"] = _confidence(m, scene, corr, iterations)

    if not any(v for k, v in corr["global"].items() if k != "sharpen") and not corr["subject_adjustments"]["apply"]:
        notes.append("Foto ist bereits gut – nur minimale Nachschärfung")
    scene_txt = ", ".join(scene["reasons"]) or "ausgewogene Szene"
    corr["analysis"] = f"Szene: {scene_txt}. " + ("; ".join(notes) + "." if notes else "Keine Korrekturen nötig.")
    corr["metrics"] = _public_metrics(m, exif, scene, iterations)
    for k in ("_wb_target", "_subject_target"):
        corr.pop(k, None)
    return sanitize(corr)


def refine(processed_rgb: np.ndarray, previous: dict, exif: dict | None = None) -> dict:
    """Zweite Runde: das bereits bearbeitete Ergebnis prüfen und nur Tonwerte/WB nachjustieren.

    Rückgabe sind wieder *absolute* Werte (bezogen auf das Original), d. h. der
    Aufrufer wendet das neue JSON erneut auf das unbearbeitete Original an.
    """
    exif = exif or {}
    prev = sanitize(previous)
    m = M.measure(processed_rgb, with_tilt=False)
    scene = classify(m, exif)
    delta, notes = initial_rules(m, exif, scene)
    _settle(delta)
    out = sanitize(prev)
    changed = []
    for k in TONAL_KEYS:
        d = delta["global"][k] * 0.5  # halbe Schritte: nur kleine Korrekturen
        if (k == "exposure_ev" and abs(d) >= 0.05) or (k != "exposure_ev" and abs(d) >= 3):
            out["global"][k] = prev["global"][k] + d
            changed.append(k)
    ds = delta["subject_adjustments"]
    if ds["apply"] and abs(ds["exposure_ev"]) >= 0.1:
        sa = out["subject_adjustments"]
        sa["apply"] = True
        sa["region"] = prev["subject_adjustments"]["region"] if prev["subject_adjustments"]["apply"] else ds["region"]
        sa["exposure_ev"] = sa["exposure_ev"] + ds["exposure_ev"] * 0.5
        changed.append("subject.exposure_ev")
    out["analysis"] = (prev["analysis"] + " | Runde 2: " +
                       (("nachjustiert: " + ", ".join(changed)) if changed else "keine weiteren Korrekturen nötig."))
    out["metrics"] = {"round2": _public_metrics(m, exif, scene, 0)}
    return sanitize(out)


def _public_metrics(m: dict, exif: dict, scene: dict, iterations: int) -> dict:
    t = m["tone"]
    r = lambda v: round(float(v), 2)
    return {
        "exif": exif,
        "scene": scene["tags"],
        "luminance": {
            "mean": r(t["mean"]), "median": r(t["median"]),
            "p1": r(t["p1"]), "p5": r(t["p5"]), "p95": r(t["p95"]), "p99": r(t["p99"]),
            "clip_hi_pct": r(t["clip_hi_pct"]), "clip_lo_pct": r(t["clip_lo_pct"]),
            "center_mean": r(t["center_mean"]),
        },
        "color": {k: r(v) for k, v in m["color"].items()},
        "texture": {k: r(v) for k, v in m["texture"].items()},
        "subject": {k: v for k, v in m["subject"].items() if k != "faces"},
        "tilt": m.get("tilt"),
        "verify_iterations": iterations,
        "size": m["size"],
    }
