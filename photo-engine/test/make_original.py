"""24-MP-Testoriginal mit EXIF (Orientation 6), ICC, XMP und IPTC."""
import io, struct
import numpy as np, cv2
from PIL import Image, ImageCms
W, H = 6000, 4000
yy, xx = np.mgrid[:H, :W].astype(np.float32)
img = np.zeros((H, W, 3), np.float32)
img[:H//2] = [120, 160, 220]; img[H//2:] = [70, 130, 60]
img += (yy / H * 30)[..., None]
cv2.circle(img, (3000, 2300), 600, (200, 90, 60), -1)
img[2200:3700, 4300:5600] = np.linspace(60, 210, 1300, dtype=np.float32)[None, :, None]
img = np.clip(img * 0.45, 0, 255).astype(np.uint8)  # unterbelichtet
im = Image.fromarray(img)
ex = Image.Exif(); ex[0x0112] = 6; ex[0x010F] = "FUJIFILM"; ex[0x0110] = "X-S20"
ex.get_ifd(0x8769)[0x8827] = 320; ex.get_ifd(0x8769)[0x829A] = (1, 480)
icc = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()
buf = io.BytesIO(); im.save(buf, "JPEG", quality=92, exif=ex.tobytes(), icc_profile=icc)
d = buf.getvalue()
def seg(marker, payload): return b"\xff" + bytes([marker]) + struct.pack(">H", len(payload) + 2) + payload
xmp = seg(0xE1, b"http://ns.adobe.com/xap/1.0/\x00<x:xmpmeta xmlns:x='adobe:ns:meta/'><dc:title>Test</dc:title></x:xmpmeta>")
iptc = seg(0xED, b"Photoshop 3.0\x008BIM\x04\x04\x00\x00\x00\x00\x00\x0b\x1c\x02\x05\x00\x05Titel")
d = d[:2] + xmp + iptc + d[2:]
open("test/original_24mp.jpg", "wb").write(d)
print("ok", len(d) // 1024, "KB")
