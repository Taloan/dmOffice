import sys, glob, json, time
sys.path.insert(0, ".")
from engine.metrics import load_image
from engine.analyzer import analyze
for f in sorted(glob.glob("test/*.jpg")):
    if "_edit" in f: continue
    rgb, exif = load_image(open(f,"rb").read())
    t=time.perf_counter(); c = analyze(rgb, exif); dt=time.perf_counter()-t
    g = {k:v for k,v in c["global"].items() if v}
    print(f"\n== {f} ({dt*1000:.0f} ms) conf={c['confidence']} scene={c['metrics']['scene']}")
    print("  ", g, "| subject:", c["subject_adjustments"]["apply"], c["subject_adjustments"]["exposure_ev"], "| straighten:", c["crop"]["straighten_deg"])
    print("  ", c["analysis"]); print("   tilt", c["metrics"]["tilt"], "subj", c["metrics"]["subject"]["source"], c["metrics"]["subject"]["region"])
