"""Derive platform icon formats from the selected ImageGen masters; no artistic edits."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
res = root / "android/app/src/main/res"
for mode in ("day", "night"):
    source = Image.open(root / f"Branding/Source/{mode}.png").convert("RGBA")
    source.resize((512, 512), Image.Resampling.LANCZOS).save(root / f"Branding/{mode}.png", optimize=True)
    source.save(root / f"Branding/{mode}.ico", sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
    for density, size in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)):
        folder = res / f"mipmap-{density}"
        folder.mkdir(parents=True, exist_ok=True)
        source.resize((size, size), Image.Resampling.LANCZOS).save(folder / f"nightcall_{mode}.png", optimize=True)
    folder = res / "drawable-nodpi"
    folder.mkdir(parents=True, exist_ok=True)
    source.resize((512, 512), Image.Resampling.LANCZOS).save(folder / f"nightcall_{mode}.png", optimize=True)

# Deliberately pixel-sized tray art: a brick-phone silhouette, no scene or glow.
def tray(size, night):
    image = Image.new("RGBA", (size, size))
    draw = ImageDraw.Draw(image)
    scale = size / 16
    body, edge, ink = ("#FF53B4", "#49DFDF", "#180C24") if night else ("#FFF3E8", "#087F85", "#BD2467")
    def box(x, y, w, h):
        return tuple(round(value * scale) for value in (x, y, x + w - 1, y + h - 1))
    draw.rectangle(box(10, 0, 2, 4), fill=edge)
    draw.rounded_rectangle(box(3, 3, 10, 13), radius=round(2 * scale), fill=body, outline=edge, width=max(1, round(scale)))
    draw.rectangle(box(5, 5, 6, 2), fill=ink)
    for y in (9, 11):
        for x in (5, 7.5, 10):
            draw.rectangle(box(x, y, 1, 1), fill=ink)
    draw.rectangle(box(5, 14, 6, 1), fill=ink)
    return image

for mode in ("day", "night"):
    sizes = (16, 20, 24, 32, 40, 48, 64)
    frames = [tray(n, mode == "night") for n in sizes]
    frames[0].save(root / f"Branding/tray-{mode}.png")
    frames[-1].save(root / f"Branding/tray-{mode}.ico", sizes=[(n, n) for n in sizes], append_images=frames[:-1])
