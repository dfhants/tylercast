#!/usr/bin/env python3
"""Makes the Windows app icon (src/TylerCast.App/TylerCast.ico) from the Mac app's icon, so
both apps share one design (Sources/TylerCast/Icon.swift draws it).

  ../make-app.sh                                  # builds build/TylerCast.app with AppIcon.icns
  iconutil -c iconset ../build/TylerCast.app/Contents/Resources/AppIcon.icns -o /tmp/AppIcon.iconset
  python3 tools/make-icon.py /tmp/AppIcon.iconset/icon_512x512@2x.png src/TylerCast.App/TylerCast.ico

macOS icons leave a 100/1024 margin round the tile; Windows icons fill their square, so the
tile is cropped to a 2% margin before scaling to Windows' sizes (taskbar, Start, Settings, Explorer).
"""
import sys
from PIL import Image

src, out = sys.argv[1], sys.argv[2]
img = Image.open(src).convert("RGBA")
n = img.width
m = round(n * (100 - 0.02 * 824) / 1024)
img = img.crop((m, m, n - m, n - m))
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
img.resize((256, 256), Image.LANCZOS).save(out, format="ICO", sizes=[(s, s) for s in sizes])
img.resize((512, 512), Image.LANCZOS).save(out.rsplit(".", 1)[0] + "-512.png")
print(f"{out}: {sizes}")
