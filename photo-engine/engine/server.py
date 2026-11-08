"""HTTP-Dienst der Photo-Engine (nur Standardbibliothek + numpy/OpenCV/Pillow).

Start:  python -m engine.server --host 127.0.0.1 --port 8765

Endpunkte
  GET  /health            → {"status": "ok", ...}
  GET  /schema            → JSON-Schema der Antwort
  POST /analyze           → Korrektur-JSON
       a) Body = JPG/PNG-Bytes (Content-Type image/jpeg), optional Header
          X-Exif: {"iso": 320, "exposure_time": 0.002, ...}
       b) Body = JSON {"image": "<base64>", "exif": {...}}
  POST /refine            → nachjustiertes Korrektur-JSON (Runde 2)
       Body = JSON {"image": "<base64 der bearbeiteten Vorschau>",
                    "previous": {<JSON aus /analyze>}, "exif": {...}}
  POST /develop           → fertiges JPG (image/jpeg)
       a) Body = Original-JPG, Header X-Corrections: <JSON>,
          Query ?max_edge=1400&quality=93 (beides optional)
       b) Body = JSON {"image": "<base64>", "corrections": {...}, "max_edge": 1400, "quality": 93}
  POST /faces             → Gesichter mit Rahmen und Fingerabdruck (SFace, 128 Werte)
       Body = Bild-Bytes (JPG/PNG/RAW) oder JSON {"image": "<base64>"}

Optional: Umgebungsvariable PHOTO_ENGINE_TOKEN → Header X-Api-Key erforderlich.
PHOTO_ENGINE_MAX_JOBS (Standard 2) begrenzt gleichzeitige /develop-Aufträge (Speicher).
"""

from __future__ import annotations

import argparse
import base64
import binascii
import json
import logging
import os
import threading
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

from . import __version__
from . import faces
from .analyzer import analyze, refine
from .jpegio import ImageTooLarge, develop_jpeg
from .metrics import face_detector_status, load_image, merge_exif
from .schema import RESPONSE_SCHEMA, SCHEMA_VERSION, sanitize

MAX_BODY = 80 * 1024 * 1024
DEVELOP_SLOTS = threading.BoundedSemaphore(max(1, int(os.environ.get("PHOTO_ENGINE_MAX_JOBS", "2"))))
log = logging.getLogger("photo_engine")


class BadRequest(Exception):
    pass


class Handler(BaseHTTPRequestHandler):
    server_version = f"PhotoEngine/{__version__}"
    protocol_version = "HTTP/1.1"

    # ------------------------------------------------------------ Infrastruktur
    def log_message(self, fmt, *args):  # über logging statt stderr
        log.info("%s - %s", self.address_string(), fmt % args)

    def _send(self, status: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_bytes(self, body: bytes, ctype: str, headers: dict) -> None:
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        for k, v in headers.items():
            self.send_header(k, str(v))
        self.end_headers()
        self.wfile.write(body)

    def _authorized(self) -> bool:
        token = os.environ.get("PHOTO_ENGINE_TOKEN")
        return not token or self.headers.get("X-Api-Key") == token

    def _body(self) -> bytes:
        n = int(self.headers.get("Content-Length") or 0)
        if n <= 0:
            raise BadRequest("Leerer Request-Body")
        if n > MAX_BODY:
            raise BadRequest(f"Body zu groß (> {MAX_BODY // 1024 // 1024} MB)")
        return self.rfile.read(n)

    def _json(self, raw: bytes) -> dict:
        try:
            data = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as e:
            raise BadRequest(f"Ungültiges JSON: {e}")
        if not isinstance(data, dict):
            raise BadRequest("JSON-Objekt erwartet")
        return data

    @staticmethod
    def _b64(s) -> bytes:
        if not isinstance(s, str) or not s:
            raise BadRequest("Feld 'image' (base64) fehlt")
        if s.startswith("data:"):
            s = s.split(",", 1)[-1]
        try:
            return base64.b64decode(s, validate=False)
        except (binascii.Error, ValueError):
            raise BadRequest("'image' ist kein gültiges base64")

    def _decode(self, img_bytes: bytes):
        try:
            return load_image(img_bytes)
        except Exception as e:
            raise BadRequest(f"Bild nicht lesbar: {e}")

    # ------------------------------------------------------------ Routen
    def do_GET(self):
        if self.path.rstrip("/") in ("", "/health"):
            return self._send(200, {"status": "ok", "version": __version__, "schema_version": SCHEMA_VERSION,
                                    "face_detector": face_detector_status(), "faces": faces.available(),
                                    "faces_model": faces.MODEL_NAME if faces.available() else None})
        if self.path.rstrip("/") == "/schema":
            return self._send(200, RESPONSE_SCHEMA)
        self._send(404, {"error": "not found"})

    def do_POST(self):
        if not self._authorized():
            return self._send(401, {"error": "unauthorized"})
        route = self.path.split("?", 1)[0].rstrip("/")
        t0 = time.perf_counter()
        try:
            if route == "/analyze":
                result = self._analyze()
            elif route == "/refine":
                result = self._refine()
            elif route == "/faces":
                result = self._faces()
            elif route == "/develop":
                body, info = self._develop()
                return self._send_bytes(body, "image/jpeg", {
                    "X-Processing-Ms": round((time.perf_counter() - t0) * 1000),
                    "X-Image-Width": info["width"], "X-Image-Height": info["height"],
                    "X-Orientation": info["orientation_kept"] if info["orientation_kept"] is not None else "applied",
                })
            else:
                return self._send(404, {"error": "not found"})
        except ImageTooLarge as e:
            return self._send(413, {"error": str(e)})
        except BadRequest as e:
            return self._send(400, {"error": str(e)})
        except Exception as e:  # pragma: no cover
            log.error("Fehler: %s\n%s", e, traceback.format_exc())
            return self._send(500, {"error": "internal error", "detail": str(e)})
        result.setdefault("metrics", {})["processing_ms"] = round((time.perf_counter() - t0) * 1000)
        self._send(200, result)

    def _analyze(self) -> dict:
        raw = self._body()
        ctype = (self.headers.get("Content-Type") or "").lower()
        supplied_exif = None
        if ctype.startswith("application/json"):
            data = self._json(raw)
            img_bytes = self._b64(data.get("image"))
            supplied_exif = data.get("exif")
        else:
            img_bytes = raw
            if self.headers.get("X-Exif"):
                try:
                    supplied_exif = json.loads(self.headers["X-Exif"])
                except json.JSONDecodeError:
                    raise BadRequest("Header X-Exif ist kein gültiges JSON")
        rgb, exif = self._decode(img_bytes)
        return analyze(rgb, merge_exif(exif, supplied_exif))

    def _refine(self) -> dict:
        data = self._json(self._body())
        if not isinstance(data.get("previous"), dict):
            raise BadRequest("Feld 'previous' (Ergebnis von /analyze) fehlt")
        rgb, exif = self._decode(self._b64(data.get("image")))
        return refine(rgb, data["previous"], merge_exif(exif, data.get("exif")))

    def _faces(self) -> dict:
        if not faces.available():
            raise BadRequest("Gesichtserkennung nicht verfügbar (Modelle fehlen)")
        raw = self._body()
        if (self.headers.get("Content-Type") or "").lower().startswith("application/json"):
            raw = self._b64(self._json(raw).get("image"))
        try:
            return faces.find_faces(raw)
        except (OSError, SyntaxError, ValueError) as e:
            raise BadRequest(f"Bild nicht lesbar: {e}")

    def _develop(self) -> tuple[bytes, dict]:
        raw = self._body()
        ctype = (self.headers.get("Content-Type") or "").lower()
        q = {k: v[-1] for k, v in parse_qs(urlsplit(self.path).query).items()}
        if ctype.startswith("application/json"):
            data = self._json(raw)
            img_bytes = self._b64(data.get("image"))
            corr = data.get("corrections")
            max_edge, quality = data.get("max_edge"), data.get("quality")
        else:
            img_bytes = raw
            try:
                corr = json.loads(self.headers.get("X-Corrections") or "null")
            except json.JSONDecodeError:
                raise BadRequest("Header X-Corrections ist kein gültiges JSON")
            max_edge, quality = q.get("max_edge"), q.get("quality")
        if not isinstance(corr, dict):
            raise BadRequest("Korrekturen fehlen (Feld 'corrections' bzw. Header X-Corrections)")
        try:
            max_edge = int(max_edge) if max_edge not in (None, "", 0, "0") else None
            quality = int(quality) if quality not in (None, "") else 93
        except (TypeError, ValueError):
            raise BadRequest("max_edge/quality müssen ganze Zahlen sein")
        if max_edge is not None and not 64 <= max_edge <= 8000:
            raise BadRequest("max_edge muss zwischen 64 und 8000 liegen")
        if not 50 <= quality <= 100:
            raise BadRequest("quality muss zwischen 50 und 100 liegen")
        with DEVELOP_SLOTS:
            try:
                return develop_jpeg(img_bytes, sanitize(corr), max_edge=max_edge, quality=quality)
            except ImageTooLarge:
                raise
            except (OSError, SyntaxError, ValueError) as e:  # kein/kaputtes Bild oder RAW
                raise BadRequest(f"Bild nicht lesbar: {e}")


def main() -> None:
    ap = argparse.ArgumentParser(description="Photo-Engine HTTP-Server")
    ap.add_argument("--host", default=os.environ.get("PHOTO_ENGINE_HOST", "127.0.0.1"))
    ap.add_argument("--port", type=int, default=int(os.environ.get("PHOTO_ENGINE_PORT", "8765")))
    args = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    srv = ThreadingHTTPServer((args.host, args.port), Handler)
    log.info("Photo-Engine %s läuft auf http://%s:%d", __version__, args.host, args.port)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        srv.server_close()


if __name__ == "__main__":
    main()
