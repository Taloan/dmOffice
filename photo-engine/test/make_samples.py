import numpy as np, cv2
from PIL import Image
rng = np.random.default_rng(1)
H, W = 1067, 1600
yy, xx = np.mgrid[:H, :W]
def save(name, rgb): Image.fromarray(np.clip(rgb,0,255).astype(np.uint8)).save(f"test/{name}.jpg", quality=92)
# Szene: Himmel oben, Wiese unten, Objekt in der Mitte
base = np.zeros((H,W,3), np.float32)
base[:H//2] = [120,160,220]; base[H//2:] = [70,130,60]
base += (yy[...,None]/H*30)
cv2.circle(base, (800,620), 160, (200,90,60), -1)
base[600:1000, 1150:1500] = np.linspace(60,210,350,dtype=np.float32)[None,:,None]
tex = rng.normal(0, 12, (H,W,1)).astype(np.float32)
tex = cv2.GaussianBlur(tex, (0,0), 2)[...,None]
good = base + tex*2
save("good", good)
save("dark", good*0.35)
save("bright", np.clip(good*1.7+30,0,255))
save("bluecast", good*[0.8,0.95,1.25])
save("flat", 100 + (good-100)*0.4)
# Gegenlicht: heller Himmel, dunkles Motiv
bl = good.copy(); bl[:H//2] = 252; bl[H//2:] *= 0.45; cv2.circle(bl,(800,620),160,(55,35,30),-1)
save("backlight", bl)
# schiefer Horizont
M = cv2.getRotationMatrix2D((W/2,H/2), 3.0, 1.15)
save("tilted", cv2.warpAffine(good, M, (W,H), borderMode=cv2.BORDER_REFLECT))
