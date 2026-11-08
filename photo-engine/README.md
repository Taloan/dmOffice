# Photo-Engine

HTTP-Dienst, der ein Foto beurteilt und Korrekturwerte als JSON liefert – **ohne KI-Dienst**.
Die Engine misst selbst (Histogramm, Clipping, Weißabgleich an neutralen Flächen, Dunst,
Schärfe/Rauschen, Gesicht/Motiv, Horizont), ordnet die Szene ein (Gegenlicht, Nacht,
Abendlicht, High-Key, Dunst, Porträt, Landschaft, flau) und leitet daraus Reglerwerte ab.
Anschließend simuliert sie ihre Vorschläge an einer Vorschau und regelt nach, bis
Helligkeit, Clipping, Motiv und Farbe im Zielbereich liegen.

Grundsätze: Natürlichkeit vor Effekt, Stimmung bleibt, Motiv gut erkennbar, Lichter nicht
ausbrennen, Schatten nicht absaufen, JPG-gerecht moderat, lieber zu wenig als zu viel.

## Start

```bash
pip install -r requirements.txt
python -m engine.server --host 127.0.0.1 --port 8765
```

Docker: `docker build -t photo-engine . && docker run -p 8765:8765 photo-engine`

| Umgebungsvariable | Standard | Bedeutung |
|---|---|---|
| `PHOTO_ENGINE_HOST` / `PHOTO_ENGINE_PORT` | 127.0.0.1 / 8765 (Docker: 0.0.0.0) | Adresse |
| `PHOTO_ENGINE_TOKEN` | – | wenn gesetzt, ist Header `X-Api-Key` Pflicht (POST) |
| `PHOTO_ENGINE_MAX_JOBS` | 2 | gleichzeitige `/develop`-Aufträge (Speicher) |
| `PHOTO_ENGINE_MODELS` | `./models` | Ordner für optionales Gesichtsmodell |
| `PHOTO_ENGINE_MAX_PIXELS` | 210000000 | Größte Bildfläche für `/develop` in Vollauflösung, darüber HTTP 413 |

Speicherbedarf (streifenweise Verarbeitung): 24 MP ≈ 0,3 GB, 40 MP ≈ 0,55 GB Spitze pro
Auftrag zuzüglich ~150 MB Grundbedarf. Empfehlung: 1,5 GB Limit bei `PHOTO_ENGINE_MAX_JOBS=1`,
2,5 GB bei 2 Aufträgen.

## API

### `POST /analyze` → Korrektur-JSON
- Body = JPG/PNG, `Content-Type: image/jpeg`, lange Kante ≤ 1600 px (größer wird verkleinert).
  EXIF im Bild wird gelesen; zusätzlich/ersatzweise Header
  `X-Exif: {"iso":320,"exposure_time":0.002,"f_number":3.2,"focal_length":20.8,"datetime":"2026:10:27 17:30:00"}`
- oder JSON `{"image": "<base64>", "exif": {...}}`

```json
{
  "schema_version": "1.0",
  "analysis": "Szene: Gegenlicht, Motiv zu dunkel. …",
  "global": {"exposure_ev": 0.3, "contrast": -5, "highlights": -30, "shadows": 35, "whites": 5,
             "blacks": 0, "temperature_shift": 0, "tint_shift": 0, "vibrance": 5, "saturation": 0,
             "clarity": 5, "dehaze": 0, "sharpen": 20},
  "subject_adjustments": {"apply": true, "exposure_ev": 0.4, "shadows": 20,
                          "region": {"x": 0.31, "y": 0.12, "w": 0.38, "h": 0.6}, "source": "face"},
  "crop": {"straighten_deg": 0.0},
  "confidence": "high",
  "metrics": {"scene": ["backlight"], "luminance": {…}, "exif": {…}, "processing_ms": 640}
}
```

Bereiche: `exposure_ev` ±2 · Regler ±100 · `temperature_shift`/`tint_shift` ±50 · `sharpen` 0–100 ·
`straighten_deg` ±10 (positiv = gegen den Uhrzeigersinn) · `subject_adjustments.exposure_ev` ±1.
`region` ist normiert (0–1, Ursprung oben links) und bezieht sich auf das **aufrechte** Bild.
In der Praxis bleiben die Vorschläge deutlich innerhalb dieser Grenzen.

### `POST /refine` → nachjustiertes JSON (Runde 2, optional)
JSON `{"image": "<base64 der bearbeiteten Vorschau>", "previous": <JSON aus /analyze>, "exif": {...}}`.
Liefert wieder **absolute** Werte (bezogen auf das Original); nur Tonwerte, WB und Motiv werden
in halben Schritten nachgeregelt.

### `POST /develop` → fertiges JPG
- Body = Original-JPG, Header `X-Corrections: <JSON>`, Query `?max_edge=1400&quality=93`
- oder JSON `{"image": "<base64>", "corrections": {...}, "max_edge": 1400, "quality": 93}`

`corrections` darf unvollständig sein – fehlende Werte sind neutral.

| | ohne `max_edge` (Speichern) | mit `max_edge` (Vorschau) |
|---|---|---|
| Ausrichtung | Pixel in gespeicherter Ausrichtung, **Orientation bleibt** | aufrecht gedreht |
| Metadaten | EXIF/XMP/IPTC/ICC/COM byteweise übernommen (ohne APP14 Adobe, MPF) | nur ICC |
| Zeit (24 MP) | ≈ 10–15 s | ≈ 0,7–1,2 s bei 1400 px |

Antwort-Header: `X-Processing-Ms`, `X-Image-Width`, `X-Image-Height`, `X-Orientation`.
Hinweis: Ein eingebettetes EXIF-Vorschaubild zeigt weiter den alten Stand.
`crop.straighten_deg` wird angewendet – wer selbst dreht, schickt 0.

### RAW-Dateien (Fuji `.RAF`, Sony `.ARW` und alles, was LibRaw kennt)

`/analyze` und `/develop` nehmen RAW-Dateien genauso entgegen wie JPGs (Body = Datei,
`Content-Type` beliebig, z. B. `application/octet-stream`). Die Engine erkennt das Format selbst.

- **Grundentwicklung:** 16 Bit, Kamera-Weißabgleich, sRGB. Die Tonwertkurve wird an das
  eingebettete Kamera-JPG angeglichen (gewohnter Look), nur der oberste Lichterbereich behält
  die RAW-Abstufung statt auszubrennen. Darauf wirken die Korrekturen wie bei einem JPG.
- **Ergebnis von `/develop`:** immer ein JPG, aufrecht (Orientation 1), sRGB-Profil; in
  Vollauflösung mit den Kamera-EXIF aus dem RAW (Maße angepasst). XMP/IPTC aus Sidecars
  werden nicht übernommen. **Das RAW selbst bleibt unverändert – das Ergebnis ist eine neue Datei.**
- **Zeiten (33–40 MP):** `/analyze` 2–4 s, erste Vorschau ~3,5 s, weitere Vorschauen derselben
  Datei ~0,8 s (Grundbild wird für die letzten 4 Dateien gemerkt), Vollauflösung 20–30 s.
- **Speicher:** Vollauflösung ca. 0,8–1 GB Spitze (LibRaw entwickelt mit 16 Bit).
- EXIF (Kamera, ISO, Zeit, Uhrzeit) stammt aus dem eingebetteten Vorschau-JPG.

### `POST /faces` → Gesichter mit Fingerabdruck (Personen gruppieren)
- Body = Bild (JPG/PNG/RAW), oder JSON `{"image": "<base64>"}`. Empfohlen: Vorschau mit 2048 px
  langer Kante (größer wird auf 2048 px verkleinert).
- Gefunden wird mit YuNet, beschrieben mit SFace (128 Werte, L2-normiert). Nur Gesichter mit
  `score` ≥ 0,8 und mindestens 40 px kurzer Seite (im 2048-px-Bild). Die Engine speichert nichts.

```json
{"model": "sface-2021dec", "width": 2048, "height": 1368,
 "faces": [{"box": {"x": 0.45, "y": 0.29, "w": 0.08, "h": 0.12}, "score": 0.93, "size_px": 165,
            "embedding": [0.0123, -0.0456, …]}],
 "metrics": {"processing_ms": 250}}
```

- `box` ist relativ (0–1) zum **aufrechten** Bild (EXIF-Drehung angewendet), größtes Gesicht zuerst.
- Gleiche Person: Kosinus-Ähnlichkeit = Skalarprodukt der Vektoren ≥ 0,363 (SFace-Empfehlung).
  An echten Fotos: dieselbe Person 0,54–0,68, verschiedene Personen ≤ 0,15.
- Wechselt `model`, sind alte Fingerabdrücke nicht vergleichbar → neu einlesen.
- Zeit (CPU): ~0,25 s je 2048-px-JPG; bei RAW zusätzlich die Entwicklung (2–3 s, Wiederholung derselben Datei schneller).
- Profile, sehr kleine oder verdeckte Gesichter werden teils nicht gefunden.

### `GET /health`, `GET /schema`
Status, Version, aktiver Gesichtsdetektor, `faces` (true, wenn `/faces` verfügbar ist) und
`faces_model` · JSON-Schema der Antwort.
Fehler: `400 {"error": "…"}`, `401`, `500`.

## Aufbau

| Datei | Aufgabe |
|---|---|
| `engine/metrics.py` | „Auge“: Laden, EXIF, Kennzahlen, Motiv, Horizont |
| `engine/analyzer.py` | „Urteil“: Szene, Regeln, Gegenprüfung per Simulation, Runde 2 |
| `engine/develop.py` | Umsetzung der Regler (Referenz, Werte sind darauf kalibriert) |
| `engine/jpegio.py` | Original entwickeln, Orientierung, Metadaten durchreichen |
| `engine/faces.py` | Gesichter finden (YuNet) und beschreiben (SFace) für `/faces` |
| `engine/rawio.py` | RAW erkennen, mit Kamera-Look entwickeln, EXIF aus dem Vorschau-JPG |
| `engine/server.py` | HTTP (nur Standardbibliothek) |
| `client.py` | Beispiel-Aufrufer: Vorschau → /analyze → Vollauflösung (→ /refine) |
| `test/` | synthetische Testbilder und Testskripte |

Gesichtserkennung: YuNet und SFace (ONNX) sowie Haar-Kaskaden liegen in `models/` und sind im Image
enthalten – die Engine läuft autark ohne Nachladen. Details und Lizenzen: `models/README.md`.

## In dmOffice

Die Engine ist im dmOffice-Image enthalten und startet beim ersten Auftrag (`DmOffice.Server/Dav/PhotoEngineProcess.cs`): nur auf
127.0.0.1, mit einem Schlüssel je Start, ein Auftrag `/develop` gleichzeitig. Sie läuft auch für sich, etwa auf einem anderen Rechner;
dmOffice bekommt dann `PHOTO_ENGINE_URL` und optional `PHOTO_ENGINE_TOKEN`.

Lizenz: AGPL-3.0, wie dmOffice. Lizenzen der Modelle: `models/README.md`.
