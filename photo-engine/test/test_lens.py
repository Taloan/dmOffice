"""Objektivkorrektur: synthetische Prüfungen (ohne Server).

Aufruf (im Image):  python test/test_lens.py [Ausgabeordner]
Mit Ausgabeordner werden die synthetischen Schachbrett-Fotos (mit EXIF-Brennweite) dort abgelegt –
für den Durchlauf in der Oberfläche.
"""

import io
import math
import os
import sys

import cv2
import numpy as np
from PIL import Image

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from engine import lens  # noqa: E402
from engine.develop import apply_corrections  # noqa: E402
from engine.schema import sanitize  # noqa: E402

W, H = 3000, 2000
TRUTH = {24: [-0.10, 0.03, 0.0], 35: [-0.04, 0.01, 0.0], 50: [0.03, 0.0, 0.0]}
fails = []


def check(cond, msg):
    print(("OK   " if cond else "FAIL ") + msg)
    if not cond:
        fails.append(msg)


def undistort_radius(k, rd):
    ru = rd.copy()
    for _ in range(30):
        r2 = ru * ru
        f = lens._poly(k, r2)
        df = f + 2 * r2 * (k[0] + 2 * k[1] * r2 + 3 * k[2] * r2 * r2)
        ru = ru - (ru * f - rd) / df
    return ru


def synth(k, tilt=0.0, rot=0.0, fill=0.92, focal=None):
    """Foto eines Schachbretts (16×9 Felder) mit bekannter Verzeichnung k."""
    sq = 120
    board = lens.checkerboard(16, 9, sq, 1)
    bh, bw = board.shape
    half, cx, cy, _, _ = lens._frame(W, H)
    # Brett-Ecken (Pixel) → ideales Bild (normiert): Rechteck, leicht gedreht/gekippt
    s = fill * min(2 * (W / 2 / half) / bw, 2 * (H / 2 / half) / bh)
    c, sn = math.cos(rot), math.sin(rot)
    A = np.array([[s * c, -s * sn, 0], [s * sn, s * c, 0], [tilt * s, 0, 1.0]])
    T0 = np.array([[1, 0, -bw / 2], [0, 1, -bh / 2], [0, 0, 1.0]])
    Hm = A @ T0  # Brett → ideal (normiert)
    Hinv = np.linalg.inv(Hm)
    xs = (np.arange(W) - cx) / half
    ys = (np.arange(H) - cy) / half
    X, Y = np.meshgrid(xs, ys)
    rd = np.sqrt(X * X + Y * Y)
    ru = undistort_radius(k, rd)
    sc = np.where(rd > 0, ru / np.maximum(rd, 1e-12), 1.0)
    U, V = X * sc, Y * sc
    P = np.stack([U, V, np.ones_like(U)], -1) @ Hinv.T
    mx = (P[..., 0] / P[..., 2]).astype(np.float32)
    my = (P[..., 1] / P[..., 2]).astype(np.float32)
    img = cv2.remap(board, mx, my, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=255)
    rgb = np.dstack([img] * 3)
    im = Image.fromarray(rgb)
    buf = io.BytesIO()
    ex = Image.Exif()
    ex[0x010F] = "NIKON CORPORATION"
    ex[0x0110] = "NIKON Z f"
    ifd = ex.get_ifd(0x8769)
    if focal:
        ifd[0x920A] = float(focal)
        ifd[0x829D] = 2.8
    ifd[0xA434] = "Testobjektiv"
    im.save(buf, "JPEG", quality=95, exif=ex.tobytes())
    return buf.getvalue(), rgb


out_dir = sys.argv[1] if len(sys.argv) > 1 else None
if out_dir:
    os.makedirs(out_dir, exist_ok=True)

# 1) Einmessen: Koeffizienten je Brennweite zurückgewinnen
fitted = {}
shots = {}
for focal, k in TRUTH.items():
    res = []
    for n, (tilt, rot) in enumerate(((0.0, 0.0), (0.05, 0.03))):
        jpg, rgb = synth(k, tilt, rot, fill=0.92 - 2 * tilt, focal=focal)
        shots.setdefault(focal, []).append((jpg, rgb))
        if out_dir:
            open(os.path.join(out_dir, f"schachbrett_{focal}mm_{n + 1}.jpg"), "wb").write(jpg)
        c = lens.find_corners(jpg)
        check(c["found"] and len(c["corners"]) == 120, f"{focal} mm Foto {n + 1}: Muster erkannt ({len(c.get('corners', []))} Ecken)")
        res.append(c)
    fit = lens.fit_distortion(res, 2)
    fitted[focal] = fit["k"]
    r = np.linspace(0, 1.2, 50)
    err = float(np.max(np.abs(lens._poly(fit["k"], r * r) - lens._poly(k, r * r)) * r)) * W / 2
    print(f"     {focal} mm: wahr {k[:2]}  gefunden {[round(x, 4) for x in fit['k'][:2]]}  "
          f"Rest {fit['residual_px']:.3f} px (vorher {fit['before_px']:.1f} px), max. Abweichung {err:.2f} px")
    check(abs(fit["k"][0] - k[0]) < 0.004 and abs(fit["k"][1] - k[1]) < 0.006, f"{focal} mm: k1/k2 zurückgewonnen")
    check(err < 1.5, f"{focal} mm: Verzeichnungskurve auf < 1,5 px genau")
    check(fit["residual_px"] < 0.5 < fit["before_px"], f"{focal} mm: Restfehler < 0,5 px, vorher deutlich größer")

profile = {"name": "Synthetisch", "distortion": [{"focal": f, "k": k} for f, k in fitted.items()]}


# 2) Korrektur macht Linien gerade (Messwert: Abweichung der Ecken von einer Ebene, nur Homographie)
def straightness(rgb):
    ok, buf = cv2.imencode(".png", cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR))
    c = lens.find_corners(buf.tobytes())
    if not c["found"]:
        return None
    pts = np.array(c["corners"]).reshape(c["rows"], c["cols"], 2)
    # größte Abweichung einer Eckenreihe von ihrer Ausgleichsgeraden
    worst = 0.0
    for line in list(pts) + list(pts.transpose(1, 0, 2)):
        m = line.mean(0)
        _, _, vt = np.linalg.svd(line - m)
        d = np.abs((line - m) @ vt[1])
        worst = max(worst, float(d.max()))
    return worst


for focal in TRUTH:
    jpg, rgb = shots[focal][0]
    before = straightness(rgb)
    corr = sanitize({"lens": {"profile": profile, "focal": focal}})
    out = apply_corrections(rgb.copy(), corr)
    after = straightness(out)
    print(f"     {focal} mm: Linienabweichung vorher {before:.2f} px, nachher {after:.2f} px")
    check(after is not None and after < 1.0 and before > 3 * after, f"{focal} mm: Linien nach Korrektur gerade")
    check(out.shape == rgb.shape, f"{focal} mm: Bildgröße unverändert")

# Zwischenbrennweite: 30 mm liegt zwischen 24 und 35
p30 = lens.params_for(profile, 30, None)["k"]
t = (30 - 24) / (35 - 24)
exp = [(1 - t) * a + t * b for a, b in zip(fitted[24], fitted[35])]
check(all(abs(a - b) < 1e-9 for a, b in zip(p30, exp)), "Interpolation 30 mm zwischen 24 und 35 mm")
check(lens.params_for(profile, 100, None)["k"] == fitted[50], "über 50 mm: Wert von 50 mm")

# 3) Kein leerer Rand: Kissen → hineinzoomen, Tonne → mehr vom Bild
for k, name in (([0.06, 0, 0], "Kissen"), ([-0.10, 0.03, 0], "Tonne")):
    z = lens.zoom_for(k, W, H)
    half, cx, cy, ex, ey = lens._frame(W, H)
    edge = np.array([[ex, y] for y in np.linspace(0, ey, 50)] + [[x, ey] for x in np.linspace(0, ex, 50)]) * z
    f = lens._poly(k, (edge ** 2).sum(1))
    src = edge * f[:, None]
    inside = bool(np.all(np.abs(src[:, 0]) <= ex * 1.001) and np.all(np.abs(src[:, 1]) <= ey * 1.001))
    touch = float(max(np.max(np.abs(src[:, 0]) / ex), np.max(np.abs(src[:, 1]) / ey)))
    check(inside and touch > 0.995, f"{name}: Zoom {z:.3f}, Rand liegt im Bild und nutzt es aus ({touch:.4f})")

# 4) Vignettierung: gleichmäßig graue Fläche mit künstlichem Randabfall wird wieder gleichmäßig
a = [-0.35, 0.05, 0.0]
flat = np.full((400, 600, 3), 150, np.uint8)
r2 = lens._radius(600, 400, 0, 400)
from engine.develop import lin_to_srgb, srgb_u8_to_lin  # noqa: E402
vig = (lin_to_srgb(srgb_u8_to_lin(flat) * lens._poly(a, r2)[..., None]) * 255 + 0.5).astype(np.uint8)
fixed = lens.apply(vig.copy(), lens.sanitize({"profile": {"vignetting": [{"focal": 24, "aperture": 2.8, "a": a}]}, "focal": 24, "aperture": 2.8}))
check(int(vig[0, 0, 0]) < 120 and abs(int(fixed[0, 0, 0]) - 150) <= 2 and abs(int(fixed[200, 300, 0]) - 150) <= 1,
      f"Vignettierung: Ecke {vig[0, 0, 0]} → {fixed[0, 0, 0]}, Mitte {fixed[200, 300, 0]}")
man = lens.apply(flat.copy(), lens.sanitize({"manual": {"vignetting": 50, "midpoint": 50}}))
check(man[0, 0, 0] > 170 and abs(int(man[200, 300, 0]) - 150) <= 1, f"Manuelle Vignette hellt die Ecken auf ({man[0, 0, 0]})")
md = lens.sanitize({"manual": {"distortion": 50}})
check(abs(lens.effective(md)["k"][0] + 0.1) < 1e-9, "Manuelle Verzeichnung +50 → k1 −0,1 (gleicht Tonne aus)")

# 5) Selbst erzeugte .lcp (Adobe-Format): Umrechnung, Blende (APEX), nächstliegende Blende
LCP = """<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
<rdf:Description rdf:about="" xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/" xmlns:stCamera="http://ns.adobe.com/photoshop/1.0/camera-profile">
<photoshop:CameraProfiles><rdf:Seq>{items}</rdf:Seq></photoshop:CameraProfiles></rdf:Description></rdf:RDF></x:xmpmeta>"""
ITEM = """<rdf:li rdf:parseType="Resource">
<stCamera:Lens>Test 20-40mm</stCamera:Lens><stCamera:LensPrettyName>Testzoom 20-40mm F4</stCamera:LensPrettyName>
<stCamera:CameraPrettyName>Testkamera</stCamera:CameraPrettyName>
<stCamera:FocalLength>{f}</stCamera:FocalLength><stCamera:ApertureValue>{av}</stCamera:ApertureValue>
<stCamera:PerspectiveModel rdf:parseType="Resource"><stCamera:Version>2</stCamera:Version>
<stCamera:FocalLengthX>{fx}</stCamera:FocalLengthX><stCamera:FocalLengthY>{fx}</stCamera:FocalLengthY>
<stCamera:ImageXCenter>0.5</stCamera:ImageXCenter><stCamera:ImageYCenter>0.5</stCamera:ImageYCenter>
<stCamera:RadialDistortParam1>{k1}</stCamera:RadialDistortParam1><stCamera:RadialDistortParam2>{k2}</stCamera:RadialDistortParam2>
<stCamera:ChromaticRedGreenModel rdf:parseType="Resource"><stCamera:ScaleFactor>1.0002</stCamera:ScaleFactor>
<stCamera:FocalLengthX>{fx}</stCamera:FocalLengthX><stCamera:RadialDistortParam1>0.001</stCamera:RadialDistortParam1></stCamera:ChromaticRedGreenModel>
<stCamera:ChromaticBlueGreenModel rdf:parseType="Resource"><stCamera:ScaleFactor>0.9998</stCamera:ScaleFactor>
<stCamera:FocalLengthX>{fx}</stCamera:FocalLengthX></stCamera:ChromaticBlueGreenModel>
<stCamera:VignetteModel rdf:parseType="Resource"><stCamera:FocalLengthX>{fx}</stCamera:FocalLengthX>
<stCamera:VignetteModelParam1>{v1}</stCamera:VignetteModelParam1></stCamera:VignetteModel>
</stCamera:PerspectiveModel></rdf:li>"""
items = ""
for f, fx, k1 in ((20, 0.56, -0.2), (40, 1.11, 0.05)):
    for av, v1 in ((4.0, -0.4), (6.0, -0.2)):  # APEX 4 = f/4, 6 = f/8
        items += ITEM.format(f=f, av=av, fx=fx, k1=k1, k2=0.01, v1=v1)
# VignetteModel steht im PerspectiveModel; auch daneben lesen
check(len(lens.parse_any(LCP.format(items=items).encode())[0]["vignetting"]) == 4, "LCP: VignetteModel im PerspectiveModel gelesen")
lcp_alt = items.replace("<stCamera:VignetteModel rdf", "</stCamera:PerspectiveModel><stCamera:VignetteModel rdf").replace(
    "</stCamera:VignetteModel>\n</stCamera:PerspectiveModel>", "</stCamera:VignetteModel>")
data = LCP.format(items=lcp_alt).encode()
pl = lens.parse_any(data)
check(len(pl) == 1 and pl[0]["name"] == "Testzoom 20-40mm F4" and pl[0]["source"] == "lcp", "LCP: ein Profil, Name aus LensPrettyName")
p = pl[0]
d20 = [e for e in p["distortion"] if e["focal"] == 20]
check(len(d20) == 2 and {e["aperture"] for e in d20} == {4.0, 8.0}, f"LCP: Blende aus APEX ({sorted(e['aperture'] for e in d20)})")
s = 1 / (2 * 0.56)
check(abs(d20[0]["k"][0] - (-0.2 * s * s)) < 1e-9 and abs(d20[0]["k"][1] - 0.01 * s ** 4) < 1e-9, "LCP: Koeffizienten auf halbe lange Seite umgerechnet")
check(len(p["vignetting"]) == 4 and len(p["tca"]) == 4 and abs(p["tca"][0]["red"][0] - 1.0002) < 1e-9, "LCP: Vignette und Farbsäume gelesen")
v56 = lens.params_for(p, 20, 2 ** 2.5)["a"][0]
check(abs(v56 - (-0.3 * s * s)) < 1e-6, f"LCP: f/5.6 liegt mitten zwischen f/4 und f/8 ({v56:.4f})")
check(lens.params_for(p, 20, 2.0)["a"] == lens.params_for(p, 20, 4.0)["a"], "LCP: fehlende Blende → nächstliegende (f/2 → f/4)")
k30 = lens.params_for(p, 30, 4.0)["k"][0]
check(abs(k30 - 0.5 * (-0.2 * s * s + 0.05 / (2 * 1.11) ** 2)) < 1e-9, "LCP: 30 mm mitten zwischen 20 und 40 mm")
check(not lens.sanitize({"profile": None}), "Leerer Block: keine Korrektur")
try:
    lens.parse_any(b'<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "b">]><lensdatabase/>')
    check(False, "XML mit ENTITY abgelehnt")
except ValueError:
    check(True, "XML mit ENTITY abgelehnt")

# 6) lensfun-XML (eigene Testwerte)
LF = b"""<lensdatabase version="2"><lens><maker>Test</maker><model>Test 28mm f/2</model><cropfactor>1</cropfactor>
<calibration>
<distortion model="poly5" focal="28" k1="-0.05" k2="0.01"/>
<distortion model="ptlens" focal="35" a="0.01" b="-0.03" c="0.0"/>
<vignetting model="pa" focal="28" aperture="2" distance="10" k1="-0.5" k2="0.1" k3="0"/>
<vignetting model="pa" focal="28" aperture="2" distance="1000" k1="-0.4" k2="0.1" k3="0"/>
<tca model="poly3" focal="28" vr="1.0003" vb="0.9997"/>
</calibration></lens></lensdatabase>"""
lp = lens.parse_any(LF)
check(len(lp) == 1 and lp[0]["name"] == "Test 28mm f/2", "lensfun: Profil gelesen")
d28 = lp[0]["distortion"][0]["k"]
check(abs(d28[0] - (-0.05 * 2.25)) < 1e-9 and abs(d28[1] - 0.01 * 1.5 ** 4) < 1e-9, "lensfun poly5: exakt umgerechnet (Seitenverhältnis 3:2)")
r = np.linspace(0, 1.2, 60)
rl = r * 1.5
pt = (0.01 * rl ** 3 - 0.03 * rl ** 2 + 1.02) / 1.02
kpt = lp[0]["distortion"][1]["k"]
ept = float(np.max(np.abs(lens._poly(kpt, r * r) - pt) * r)) * 3000
check(ept < 3.0, f"lensfun ptlens: Anpassung auf 6000 px bis {ept:.2f} px genau")
vg = lp[0]["vignetting"]
check(len(vg) == 1 and abs(vg[0]["a"][0] - (-0.4 / (1 + 1 / 2.25))) < 1e-9, "lensfun pa: größte Entfernung, halbe Diagonale umgerechnet")
check(abs(lp[0]["tca"][0]["red"][0] - 1.0003) < 1e-6, "lensfun TCA gelesen")

# 7) Eigenes JSON (Export → Import)
import json  # noqa: E402
rt = lens.parse_any(json.dumps({"profiles": [dict(profile, source="calibration")]}).encode())
check(rt[0]["distortion"][0]["k"] == profile["distortion"][0]["k"] and rt[0]["name"] == "Synthetisch", "JSON: Export/Import verlustfrei")

# 8) Farbsäume: verschobener Rotkanal wird zurückgeschoben
jpg, rgb = shots[50][0]
tc = rgb.copy()
red = lens.apply(rgb.copy(), lens.sanitize({"profile": {"tca": [{"focal": 50, "red": [1.004, 0, 0], "blue": [1, 0, 0]}]}, "focal": 50}))
d = np.abs(red[..., 0].astype(int) - red[..., 1].astype(int))
diff_edge = float(np.concatenate([d[:, :W // 6].ravel(), d[:, -W // 6:].ravel()]).mean())
diff_mid = float(d[:, W // 2 - 150:W // 2 + 150].mean())
check(diff_edge > diff_mid + 1, f"Farbsäume: Rotkanal am Rand verschoben ({diff_edge:.1f} gegenüber {diff_mid:.1f})")

print()
print("FEHLER: " + "; ".join(fails) if fails else "Alle Prüfungen bestanden.")
sys.exit(1 if fails else 0)
