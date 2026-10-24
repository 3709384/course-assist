#!/usr/bin/env python3
"""Generate src/CourseAssist.App/app.ico (sky-blue rounded square + white check).
Requires Pillow. The .ico is binary, so it is generated, not committed."""
from PIL import Image, ImageDraw

S = 256
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
top = (16, 185, 129)   # #10B981
bottom = (4, 120, 87)   # #047857
for y in range(S):
    t = y / (S - 1)
    c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    d.line([(0, y), (S, y)], fill=c + (255,))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([2, 2, S - 3, S - 3], radius=52, fill=255)
out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
out.paste(img, (0, 0), mask)
d = ImageDraw.Draw(out)
pts = [(66, 138), (110, 182), (192, 84)]
d.line(pts, fill=(255, 255, 255, 255), width=30, joint="curve")
for (x, y) in pts:
    d.ellipse([x - 15, y - 15, x + 15, y + 15], fill=(255, 255, 255, 255))
out.save("src/CourseAssist.App/app.ico", format="ICO",
         sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("wrote src/CourseAssist.App/app.ico")
