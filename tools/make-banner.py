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

# Every agent the app reads, in the order of the README.
AGENTS = ["Claude", "Codex", "Cursor", "Gemini", "Antigravity", "Copilot"]


def font(name: str, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(FONTS / name), size)


def load_logo(size: int) -> Image.Image:
    with Image.open(REPO_ROOT / "design" / "logo.png") as image:
        logo = image.convert("RGBA")
    logo = logo.crop(logo.getchannel("A").getbbox())
    logo.thumbnail((size, size), Image.Resampling.LANCZOS)
    return logo


def pill_rows(draw: ImageDraw.ImageDraw, names: list[str], pill_font: ImageFont.FreeTypeFont,
              max_width: int, pad: int, gap: int) -> list[list[tuple[str, float]]]:
    """The agent names split into as few rows of pills no wider than max_width as fit, with the
    names spread evenly over the rows, so no row is left with a single pill."""
    pills = [(name, draw.textlength(name, font=pill_font) + 2 * pad) for name in names]

    def fits(row: list[tuple[str, float]]) -> bool:
        return sum(width for _, width in row) + gap * (len(row) - 1) <= max_width

    for count in range(1, len(pills) + 1):
        per_row = -(-len(pills) // count)
        rows = [pills[i:i + per_row] for i in range(0, len(pills), per_row)]
        if all(fits(row) for row in rows):
            return rows
    raise ValueError("a single pill is wider than max_width")


def draw_pills(draw: ImageDraw.ImageDraw, rows: list[list[tuple[str, float]]], left: int, top: int,
               pill_font: ImageFont.FreeTypeFont, height: int, pad: int, gap: int) -> None:
    y = top
    for row in rows:
        x = left
        for name, width in row:
            box = (x, y, x + width, y + height)
            draw.rounded_rectangle(box, radius=height // 2, fill=PILL, outline=PILL_BORDER, width=2)
            # Centred on the font's line box, not the word's ink, so every name sits at one height.
            draw.text((x + pad, y + height / 2), name, font=pill_font, fill=TEXT_SECONDARY, anchor="lm")
            x += width + gap
        y += height + gap


def main() -> None:
    canvas = Image.new("RGBA", (WIDTH, HEIGHT), BACKGROUND + (255,))

    # A soft accent glow behind the logo, so the mark does not float on a flat field.
    glow = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((60, 110 + SHIFT, 480, 530 + SHIFT), fill=ACCENT + (70,))
    canvas.alpha_composite(glow.filter(ImageFilter.GaussianBlur(90)))

    logo = load_logo(300)
    canvas.alpha_composite(logo, (120 + (300 - logo.width) // 2, (HEIGHT - logo.height) // 2))

    draw = ImageDraw.Draw(canvas)
    left = 500
    draw.text((left, 150 + SHIFT), "AI-Usage", font=font("segoeuib.ttf", 104), fill=TEXT)
    tagline = font("segoeuisl.ttf", 40)
    draw.text((left, 292 + SHIFT), "Your AI coding quotas,", font=tagline, fill=TEXT)
    draw.text((left, 342 + SHIFT), "live in one small window.", font=tagline, fill=TEXT)

    pill_font = font("segoeui.ttf", 24)
    rows = pill_rows(draw, AGENTS, pill_font, WIDTH - left - 40, pad=16, gap=10)
    assert len(rows) == 1, "the banner has room for one row of agents"
    draw_pills(draw, rows, left, 440 + SHIFT, pill_font, height=46, pad=16, gap=10)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    canvas.convert("RGB").save(OUT, format="PNG", optimize=True)
    print(f"wrote {OUT} ({WIDTH}x{HEIGHT})")


def social_preview() -> None:
    """1280 x 640 repository card: logo, name, tagline and every agent on the left, the widget on the
    right; all content stays inside a 64 px margin."""
    width, height, margin = 1280, 640, 64
    canvas = Image.new("RGBA", (width, height), BACKGROUND + (255,))

    glow = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((700, 120, 1240, 560), fill=ACCENT + (80,))
    ImageDraw.Draw(glow).ellipse((-120, 40, 360, 400), fill=ACCENT + (40,))
    canvas.alpha_composite(glow.filter(ImageFilter.GaussianBlur(100)))

    with Image.open(SCREENSHOT) as image:
        shot = image.convert("RGBA")
    shot_width = 520
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
    column = shot_x - 56 - left

    title_font = font("segoeuib.ttf", 84)
    tagline_font = font("segoeuisl.ttf", 36)
    pill_font = font("segoeui.ttf", 22)
    logo = load_logo(96)
    tagline = ("Your AI coding quotas,", "live in one small window.")
    rows = pill_rows(draw, AGENTS, pill_font, column, pad=15, gap=10)
    for line in tagline:
        assert draw.textlength(line, font=tagline_font) <= column, line

    # The block is measured first, then centred on the card's height.
    title_box = draw.textbbox((0, 0), "AI-Usage", font=title_font)
    header = max(logo.height, title_box[3] - title_box[1])
    line_height = 48
    pill_height, gap = 42, 10
    block = header + 36 + line_height * len(tagline) + 36 + len(rows) * (pill_height + gap) - gap
    top = (height - block) // 2

    canvas.alpha_composite(logo, (left, top + (header - logo.height) // 2))
    title_x = left + logo.width + 24
    draw.text((title_x, top + (header - (title_box[3] - title_box[1])) / 2 - title_box[1]),
              "AI-Usage", font=title_font, fill=TEXT)

    y = top + header + 36
    for line in tagline:
        draw.text((left, y), line, font=tagline_font, fill=TEXT)
        y += line_height

    draw_pills(draw, rows, left, y + 36 - (line_height - 40), pill_font, height=pill_height, pad=15, gap=gap)

    canvas.convert("RGB").save(OUT_SOCIAL, format="PNG", optimize=True)
    print(f"wrote {OUT_SOCIAL} ({width}x{height})")


if __name__ == "__main__":
    main()
    social_preview()
