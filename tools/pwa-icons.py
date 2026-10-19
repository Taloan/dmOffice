# Symbole fuer die installierbare App (PWA) zeichnen: violetter Grund, weisser Umschlag.
# Aufruf aus dem Projektordner: python tools/pwa-icons.py  -> schreibt nach web/public
# Gezeichnet wird vierfach gross und dann verkleinert, damit die Kanten weich werden.
from pathlib import Path
from PIL import Image, ImageDraw

BLUE = (123, 29, 211, 255)   # #7B1DD3 (Violett) wie theme_color
WHITE = (255, 255, 255, 255)
OUT = Path(__file__).resolve().parent.parent / 'web' / 'public'


def envelope(d: ImageDraw.ImageDraw, size: int, scale: float):
    """Umschlag mittig; scale = Breite des Umschlags im Verhaeltnis zur Kantenlaenge."""
    w = size * scale
    h = w * 0.68
    x0, y0 = (size - w) / 2, (size - h) / 2
    x1, y1 = x0 + w, y0 + h
    stroke = max(2, round(w * 0.085))
    r = stroke * 0.9
    d.rounded_rectangle([x0, y0, x1, y1], radius=r, outline=WHITE, width=stroke)
    # Klappe: von den oberen Ecken zur Mitte
    inset = stroke / 2
    pts = [(x0 + inset, y0 + inset), (size / 2, y0 + h * 0.56), (x1 - inset, y0 + inset)]
    d.line(pts, fill=WHITE, width=stroke, joint='curve')
    for p in pts[1:2]:
        d.ellipse([p[0] - stroke / 2, p[1] - stroke / 2, p[0] + stroke / 2, p[1] + stroke / 2], fill=WHITE)


def icon(size: int, *, rounded: bool, scale: float) -> Image.Image:
    big = size * 4
    img = Image.new('RGBA', (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if rounded:
        d.rounded_rectangle([0, 0, big - 1, big - 1], radius=big * 0.22, fill=BLUE)
    else:
        d.rectangle([0, 0, big, big], fill=BLUE)
    envelope(d, big, scale)
    return img.resize((size, size), Image.LANCZOS)


# "any": abgerundet, Umschlag gross. "maskable": randlos, Umschlag im sicheren Kreis (80 %).
# Apple: randlos, iOS rundet selbst ab.
icon(192, rounded=True, scale=0.62).save(OUT / 'icon-192.png')
icon(512, rounded=True, scale=0.62).save(OUT / 'icon-512.png')
icon(128, rounded=True, scale=0.62).save(OUT / 'icon-128.png')
icon(512, rounded=False, scale=0.50).save(OUT / 'icon-maskable-512.png')
icon(192, rounded=False, scale=0.50).save(OUT / 'icon-maskable-192.png')
icon(180, rounded=False, scale=0.58).convert('RGB').save(OUT / 'apple-touch-icon.png')
print('Symbole geschrieben nach', OUT)
