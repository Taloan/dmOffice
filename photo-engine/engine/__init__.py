"""Photo-Engine: regelbasierte Bildbeurteilung, liefert Korrektur-JSON."""

import os

__version__ = os.environ.get("PHOTO_ENGINE_VERSION") or "1.0.0"
