import io, json, time, urllib.request
from PIL import Image, ImageOps
import sys; sys.path.insert(0, ".")
from engine.jpegio import _app_segments
URL = "http://127.0.0.1:8765"
orig = open("test/original_24mp.jpg", "rb").read()

def post(path, body, ctype, headers=None):
    req = urllib.request.Request(URL + path, data=body, headers={"Content-Type": ctype, **(headers or {})}, method="POST")
    t = time.perf_counter()
    with urllib.request.urlopen(req, timeout=300) as r:
        return r.read(), dict(r.headers), (time.perf_counter() - t) * 1000

# 1) Analyse: aufrechte 1600er Vorschau mit EXIF
im = Image.open(io.BytesIO(orig)); ex = im.info["exif"]
p = ImageOps.exif_transpose(im).convert("RGB"); p.thumbnail((1600, 1600))
b = io.BytesIO(); p.save(b, "JPEG", quality=90, exif=ex)
body, _, ms = post("/analyze", b.getvalue(), "image/jpeg")
corr = json.loads(body)
print(f"analyze {ms:.0f} ms:", {k: v for k, v in corr["global"].items() if v}, "|", corr["metrics"]["exif"])
print("  ", corr["analysis"])

# 2) Vorschau 1400 px (dreimal, Zeiten)
for i in range(3):
    body, h, ms = post("/develop?max_edge=1400&quality=93", orig, "image/jpeg", {"X-Corrections": json.dumps(corr)})
    print(f"develop preview {ms:.0f} ms (server {h['X-Processing-Ms']} ms) {h['X-Image-Width']}x{h['X-Image-Height']} orient={h['X-Orientation']}")
pv = Image.open(io.BytesIO(body)); print("   preview exif?", bool(pv.info.get("exif")), "icc?", bool(pv.info.get("icc_profile")))
pv.save("test/dev_preview.jpg")

# 3) Vollauflösung
body, h, ms = post("/develop?quality=93", orig, "image/jpeg", {"X-Corrections": json.dumps(corr)})
print(f"develop full {ms:.0f} ms {h['X-Image-Width']}x{h['X-Image-Height']} orient={h['X-Orientation']} size={len(body)//1024} KB")
open("test/dev_full.jpg", "wb").write(body)
so, sn = _app_segments(orig), _app_segments(body)
print("   Metadaten-Segmente identisch:", so == sn, [s[4:16] for s in sn])
full = Image.open(io.BytesIO(body))
print("   gespeichert", full.size, "Orientation", full.getexif().get(0x0112), "→ aufrecht", ImageOps.exif_transpose(full).size)

# 4) JSON-Variante + Fehler
import base64
body, h, ms = post("/develop", json.dumps({"image": base64.b64encode(orig).decode(), "corrections": {"global": {"exposure_ev": 0.5}}, "max_edge": 800}).encode(), "application/json")
print("json-variante ok", h["X-Image-Width"], "x", h["X-Image-Height"], f"{ms:.0f} ms")
try:
    post("/develop", orig, "image/jpeg")
except urllib.error.HTTPError as e:
    print("ohne Korrekturen →", e.code, e.read().decode())
