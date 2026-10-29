"""Builds the README title banner (1280 x 420) from the app logo.

The source artwork is not part of this repository; make-icon.py reads the same file. Writes .github/images/banner.png and the 1280 x 640 social-preview.png. Kept short so the README opens without a tall empty band.

Requires Pillow (`pip install pillow`) and the Segoe UI fonts that ship with Windows. Run from the
repository root:
    python tools/make-banner.py
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

REPO_ROOT = Path(__file__).resolve().parent.parent
OUT = REPO_ROOT / ".github" / "images" / "banner.png"
OUT_SOCIAL = REPO_ROOT / ".github" / "images" / "social-preview.png"
SCREENSHOT = REPO_ROOT / ".github" / "images" / "home-claude-full.png"
FONTS = Path("C:/Windows/Fonts")

WIDTH, HEIGHT = 1280, 420
# Vertical offset of the whole layout.
SHIFT = -110
# Nebula theme: Bg.Base, Bg.Raised, Border, Accent, Text.Primary, Text.Secondary.
BACKGROUND = (23, 18, 31)
PILL = (44, 34, 64)
PILL_BORDER = (58, 46, 82)
ACCENT = (168, 85, 247)
TEXT = (242, 237, 250)
TEXT_SECONDARY = (183, 171, 203)

AGENTS = ["Claude", "Codex", "Cursor", "Gemini", "Copilot"]


def font(name: str, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(FONTS / name), size)


def main() -> None:
    canvas = Image.new("RGBA", (WIDTH, HEIGHT), BACKGROUND + (255,))

    # A soft accent glow behind the logo, so the mark does not float on a flat field.
    glow = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((60, 110 + SHIFT, 480, 530 + SHIFT), fill=ACCENT + (70,))
    canvas.alpha_composite(glow.filter(ImageFilter.GaussianBlur(90)))

    with Image.open(REPO_ROOT / "design" / "logo.png") as image:
        logo = image.convert("RGBA")
    logo = logo.crop(logo.getchannel("A").getbbox())
    logo.thumbnail((300, 300), Image.Resampling.LANCZOS)
    canvas.alpha_composite(logo, (120 + (300 - logo.width) // 2, (HEIGHT - logo.height) // 2))

    draw = ImageDraw.Draw(canvas)
    left = 500
    draw.text((left, 150 + SHIFT), "AI-Usage", font=font("segoeuib.ttf", 104), fill=TEXT)
    tagline = font("segoeuisl.ttf", 40)
    draw.text((left, 292 + SHIFT), "Your AI coding quotas,", font=tagline, fill=TEXT)
    draw.text((left, 342 + SHIFT), "live in one small window.", font=tagline, fill=TEXT)

    pill_font = font("segoeui.ttf", 26)
    x, y = left, 440 + SHIFT
    for agent in AGENTS:
        text_width = draw.textlength(agent, font=pill_font)
        box = (x, y, x + text_width + 36, y + 48)
        draw.rounded_rectangle(box, radius=24, fill=PILL, outline=PILL_BORDER, width=2)
        draw.text((x + 18, y + 8), agent, font=pill_font, fill=TEXT_SECONDARY)
        x = box[2] + 12

    OUT.parent.mkdir(parents=True, exist_ok=True)
    canvas.convert("RGB").save(OUT, format="PNG", optimize=True)
    print(f"wrote {OUT} ({WIDTH}x{HEIGHT})")


def social_preview() -> None:
    """1280 x 640 repository card; all content stays inside a 40 px margin."""
    width, height, margin = 1280, 640, 40
    canvas = Image.new("RGBA", (width, height), BACKGROUND + (255,))

    glow = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((700, 120, 1240, 560), fill=ACCENT + (80,))
    canvas.alpha_composite(glow.filter(ImageFilter.GaussianBlur(90)))

    with Image.open(SCREENSHOT) as image:
        shot = image.convert("RGBA")
    shot_width = 540
    shot = shot.resize((shot_width, round(shot.height * shot_width / shot.width)), Image.Resampling.LANCZOS)
    shot_x = width - margin - shot_width
    shot_y = (height - shot.height) // 2
    mask = Image.new("L", shot.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, shot.width - 1, shot.height - 1), radius=14, fill=255)
    canvas.paste(shot, (shot_x, shot_y), mask)
    ImageDraw.Draw(canvas).rounded_rectangle(
        (shot_x, shot_y, shot_x + shot.width - 1, shot_y + shot.height - 1),
        radius=14, outline=PILL_BORDER, width=2)

    draw = ImageDraw.Draw(canvas)
    left = margin
    draw.text((left, 150), "AI-Usage", font=font("segoeuib.ttf", 92), fill=TEXT)
    tagline = font("segoeuisl.ttf", 38)
    for index, line in enumerate(("Your AI coding quotas,", "live in one small", "Windows widget")):
        draw.text((left, 270 + index * 52), line, font=tagline, fill=TEXT)

    pill_font = font("segoeui.ttf", 24)
    x, y = left, 450
    for agent in AGENTS[:3]:
        text_width = draw.textlength(agent, font=pill_font)
        box = (x, y, x + text_width + 32, y + 44)
        draw.rounded_rectangle(box, radius=22, fill=PILL, outline=PILL_BORDER, width=2)
        draw.text((x + 16, y + 6), agent, font=pill_font, fill=TEXT_SECONDARY)
        x = box[2] + 10

    canvas.convert("RGB").save(OUT_SOCIAL, format="PNG", optimize=True)
    print(f"wrote {OUT_SOCIAL} ({width}x{height})")


if __name__ == "__main__":
    main()
    social_preview()
