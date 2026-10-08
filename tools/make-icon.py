"""Packages the approved ring logo into the app and tray icons.

design/logo.png is the source artwork (kept outside the repository). The ICO files, the in-app
PNG mark and the SVG wrapper are generated from it.

Requires Pillow (`pip install pillow`). Run from the repository root:
    python tools/make-icon.py
"""

from __future__ import annotations

import base64
import io
from pathlib import Path

from PIL import Image

REPO_ROOT = Path(__file__).resolve().parent.parent
ASSETS = REPO_ROOT / "src" / "AiUsage" / "Assets"

# Preserve the tray's existing warning colours.
STATUS_OK = (139, 92, 246, 255)      # Level.Ok #8B5CF6
STATUS_WARN = (245, 158, 11, 255)    # Level.Warn #F59E0B
STATUS_CRIT = (244, 63, 94, 255)     # Level.Crit #F43F5E

CANVAS = 1024
APP_ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
# Drawn inside the app at up to 48 px; 256 px leaves room for 400 % scaling and downsamples cleanly.
MARK_PNG_SIZE = 256
TRAY_ICON_SIZES = [16, 20, 24, 32, 40, 48]


def save_ico(img: Image.Image, path: Path, sizes: list[int]) -> None:
    img.save(path, format="ICO", sizes=[(s, s) for s in sizes])
    print(f"wrote {path} ({', '.join(f'{s}x{s}' for s in sizes)})")


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)

    source = REPO_ROOT / "design" / "logo.png"
    with Image.open(source) as image:
        logo = image.convert("RGBA")
    alpha = logo.getchannel("A")
    if alpha.getextrema() != (0, 255):
        raise ValueError("The source logo must have transparent and opaque pixels.")
    bounds = alpha.point(lambda value: 255 if value >= 128 else 0).getbbox()
    mark = logo.crop(bounds)
    mark.thumbnail((896, 896), Image.Resampling.LANCZOS)
    app_master = Image.new("RGBA", (CANVAS, CANVAS))
    app_master.alpha_composite(mark, ((CANVAS - mark.width) // 2, (CANVAS - mark.height) // 2))
    save_ico(app_master, ASSETS / "app.ico", APP_ICON_SIZES)
    app_master.resize((MARK_PNG_SIZE, MARK_PNG_SIZE), Image.Resampling.LANCZOS).save(ASSETS / "app-mark.png")
    print(f"wrote {ASSETS / 'app-mark.png'} ({MARK_PNG_SIZE}x{MARK_PNG_SIZE})")

    for name, color in (("tray-ok", STATUS_OK), ("tray-warn", STATUS_WARN), ("tray-crit", STATUS_CRIT)):
        tray_master = app_master.copy()
        if name != "tray-ok":
            tray_master = Image.new("RGBA", app_master.size, color)
            tray_master.putalpha(app_master.getchannel("A"))
        save_ico(tray_master, ASSETS / f"{name}.ico", TRAY_ICON_SIZES)

    write_svg(logo)


def write_svg(logo: Image.Image) -> None:
    # Same artwork as the icons, not a second drawing. Re-encoded from the pixels rather than
    # copying the source file, so no metadata chunk of the source file ends up in the repository.
    buffer = io.BytesIO()
    logo.save(buffer, format="PNG", optimize=True)
    encoded = base64.b64encode(buffer.getvalue()).decode("ascii")
    (ASSETS / "icon.svg").write_text(
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">\n'
        f'  <image width="1024" height="1024" href="data:image/png;base64,{encoded}"/>\n'
        '</svg>\n', encoding="utf-8",
    )


if __name__ == "__main__":
    main()
