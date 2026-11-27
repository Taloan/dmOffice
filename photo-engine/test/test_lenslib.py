"""Profilbibliothek (lenslib): Angaben für den Index aus erfundenen .lcp-Dateien (ohne Server).

Aufruf (im Image):  python test/test_lenslib.py
"""

import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from engine import lenslib  # noqa: E402

fails = []


def check(cond, msg):
    print(("OK   " if cond else "FAIL ") + msg)
    if not cond:
        fails.append(msg)


LCP = """<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
<rdf:Description rdf:about="" xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/" xmlns:stCamera="http://ns.adobe.com/photoshop/1.0/camera-profile">
<photoshop:CameraProfiles><rdf:Seq>{items}</rdf:Seq></photoshop:CameraProfiles></rdf:Description></rdf:RDF></x:xmpmeta>"""
HEAD = """<rdf:li rdf:parseType="Resource">
<stCamera:Author>Test</stCamera:Author><stCamera:Make>TESTCAM</stCamera:Make><stCamera:Model>TC 1</stCamera:Model>
<stCamera:UniqueCameraModel>TESTCAM TC 1</stCamera:UniqueCameraModel><stCamera:CameraPrettyName>Testcam TC 1</stCamera:CameraPrettyName>
<stCamera:Lens>Erfunden 20-40mm F4</stCamera:Lens><stCamera:LensPrettyName>Erfunden 20-40mm F4 Zoom</stCamera:LensPrettyName>
<stCamera:LensID>4711</stCamera:LensID><stCamera:CameraRawProfile>{raw}</stCamera:CameraRawProfile>
<stCamera:SensorFormatFactor>1.5</stCamera:SensorFormatFactor>"""
WITH_FOCAL = HEAD + """<stCamera:FocalLength>{f}</stCamera:FocalLength><stCamera:ApertureValue>{av}</stCamera:ApertureValue>
<stCamera:PerspectiveModel rdf:parseType="Resource"><stCamera:Version>2</stCamera:Version>
<stCamera:FocalLengthX>{fx}</stCamera:FocalLengthX><stCamera:FocalLengthY>{fx}</stCamera:FocalLengthY>
<stCamera:RadialDistortParam1>-0.1</stCamera:RadialDistortParam1>
<stCamera:VignetteModel rdf:parseType="Resource"><stCamera:FocalLengthX>{fx}</stCamera:FocalLengthX>
<stCamera:VignetteModelParam1>-0.3</stCamera:VignetteModelParam1></stCamera:VignetteModel>
</stCamera:PerspectiveModel></rdf:li>"""

items = "".join(WITH_FOCAL.format(raw="True", f=f, av=av, fx=fx) for f, fx in ((20, 0.6), (40, 1.2)) for av in (4.0, 8.0))
out = lenslib.read(LCP.format(items=items).encode())
check(len(out) == 1, "eine Datei, ein Profil")
m = out[0]["meta"]
check(m["make"] == "TESTCAM" and m["model"] == "TC 1" and m["camera"] == "Testcam TC 1", "Kamera gelesen")
check(m["lens"] == "Erfunden 20-40mm F4" and m["lensName"] == "Erfunden 20-40mm F4 Zoom" and m["lensId"] == "4711", "Objektiv gelesen")
check(m["raw"] is True and m["crop"] == 1.5, "RAW und Sensorformat gelesen")
check(m["focalMin"] == 20 and m["focalMax"] == 40, f"Brennweitenbereich {m['focalMin']}–{m['focalMax']}")
check(m["apertureMin"] is not None and m["apertureMax"] is not None and m["apertureMin"] < m["apertureMax"], "Blendenbereich")
json.dumps(out)  # muss als JSON gehen

# JPEG-Profil
out = lenslib.read(LCP.format(items=WITH_FOCAL.format(raw="False", f=35, av=4.0, fx=1.0)).encode())
check(out[0]["meta"]["raw"] is False, "JPEG-Profil erkannt")

# Manuelles Objektiv ohne FocalLength: Brennweite aus FocalLengthX und Sensorformat
NO_FOCAL = HEAD + """<stCamera:PerspectiveModel rdf:parseType="Resource"><stCamera:Version>2</stCamera:Version>
<stCamera:FocalLengthX>0.8</stCamera:FocalLengthX><stCamera:FocalLengthY>0.8</stCamera:FocalLengthY>
<stCamera:RadialDistortParam1>-0.05</stCamera:RadialDistortParam1></stCamera:PerspectiveModel></rdf:li>"""
out = lenslib.read(LCP.format(items=NO_FOCAL.format(raw="True")).encode())
f = out[0]["meta"]["focalMin"]
check(f is not None and abs(f - 0.8 * 36 / 1.5) < 0.5, f"Brennweite ohne FocalLength geschätzt ({f})")

# Fischauge: klare Meldung
FISHEYE = HEAD + """<stCamera:FocalLength>8</stCamera:FocalLength>
<stCamera:FisheyeModel rdf:parseType="Resource"><stCamera:FocalLengthX>0.4</stCamera:FocalLengthX>
<stCamera:RadialDistortParam1>0.01</stCamera:RadialDistortParam1></stCamera:FisheyeModel></rdf:li>"""
try:
    lenslib.read(LCP.format(items=FISHEYE.format(raw="True")).encode())
    check(False, "Fischauge abgelehnt")
except ValueError as e:
    check("FisheyeModel" in str(e), "Fischauge abgelehnt mit Hinweis")

# Kaputte Datei
for bad in (b"", b"kein xml", b"<x>" * 10):
    try:
        lenslib.read(bad)
        check(False, f"kaputte Datei {bad[:10]!r} abgelehnt")
    except Exception as e:  # noqa: BLE001 - der Server fängt ValueError/SyntaxError
        check(isinstance(e, (ValueError, SyntaxError, KeyError, TypeError, UnicodeDecodeError)), f"kaputte Datei {bad[:10]!r}: {type(e).__name__}")

# Eigenes JSON
own = {"profiles": [{"name": "Eigenes 50mm", "lens": "Eigen 50", "distortion": [{"focal": 50, "k": [0.01, 0, 0]}]}]}
out = lenslib.read(json.dumps(own).encode())
check(out[0]["meta"]["lensName"] == "Eigenes 50mm" and out[0]["meta"]["raw"] is None, "eigenes JSON")

print()
print("FEHLER: " + "; ".join(fails) if fails else "Alle Prüfungen bestanden.")
sys.exit(1 if fails else 0)
