from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageChops, ImageColor, ImageDraw, ImageFilter, ImageFont


OUT_DIR = Path(r"C:\Users\ReneeDeng\Desktop\BP0108\tmp\replacement_textures")
OUT_DIR.mkdir(parents=True, exist_ok=True)


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = [
        r"C:\Windows\Fonts\bahnschrift.ttf",
        r"C:\Windows\Fonts\bahnschriftbd.ttf" if bold else r"C:\Windows\Fonts\bahnschrift.ttf",
        r"C:\Windows\Fonts\segoeuib.ttf" if bold else r"C:\Windows\Fonts\segoeui.ttf",
        r"C:\Windows\Fonts\arialbd.ttf" if bold else r"C:\Windows\Fonts\arial.ttf",
    ]
    for candidate in candidates:
        path = Path(candidate)
        if path.exists():
            try:
                return ImageFont.truetype(str(path), size=size)
            except Exception:
                continue
    return ImageFont.load_default()


def lerp(a: int, b: int, t: float) -> int:
    return int(a + (b - a) * t)


def gradient(size: tuple[int, int], top: str, bottom: str) -> Image.Image:
    w, h = size
    img = Image.new("RGBA", size)
    px = img.load()
    top_rgba = ImageColor.getrgb(top)
    bottom_rgba = ImageColor.getrgb(bottom)
    for y in range(h):
        t = y / max(1, h - 1)
        color = tuple(lerp(top_rgba[i], bottom_rgba[i], t) for i in range(3)) + (255,)
        for x in range(w):
            px[x, y] = color
    return img


def rounded_panel(draw: ImageDraw.ImageDraw, box, radius, fill, outline=None, width=2):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def add_soft_glow(base: Image.Image, box, radius: int, color: tuple[int, int, int, int]) -> None:
    glow = Image.new("RGBA", base.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(glow)
    draw.rounded_rectangle(box, radius=radius, fill=color)
    glow = glow.filter(ImageFilter.GaussianBlur(24))
    base.alpha_composite(glow)


def draw_grid(draw: ImageDraw.ImageDraw, width: int, height: int) -> None:
    major = (38, 78, 108, 42)
    minor = (30, 64, 90, 26)
    for x in range(0, width, 160):
        draw.line((x, 0, x, height), fill=major, width=1)
    for y in range(0, height, 120):
        draw.line((0, y, width, y), fill=major, width=1)
    for x in range(0, width, 40):
        draw.line((x, 0, x, height), fill=minor, width=1)
    for y in range(0, height, 40):
        draw.line((0, y, width, y), fill=minor, width=1)


def draw_circuit_arcs(draw: ImageDraw.ImageDraw) -> None:
    arcs = [
        ((-240, 180, 1120, 1280), (24, 162, 255, 40), 16),
        ((180, 40, 1820, 980), (0, 227, 180, 32), 12),
        ((860, -120, 2140, 760), (255, 196, 79, 28), 10),
    ]
    for bounds, color, width in arcs:
        draw.arc(bounds, start=18, end=154, fill=color, width=width)
        draw.arc((bounds[0] + 40, bounds[1] + 40, bounds[2] - 40, bounds[3] - 40), start=210, end=328, fill=color, width=max(4, width // 2))


def generate_background() -> Path:
    size = (1920, 1080)
    img = gradient(size, "#071624", "#0d3550")
    draw = ImageDraw.Draw(img, "RGBA")

    draw_grid(draw, *size)
    draw_circuit_arcs(draw)

    draw.rectangle((0, 0, 1920, 96), fill=(4, 14, 24, 180))
    draw.rectangle((0, 96, 1920, 100), fill=(39, 196, 255, 180))

    score_track = (255, 255, 255, 28)
    draw.rounded_rectangle((320, 92, 980, 130), radius=18, fill=score_track)
    draw.rounded_rectangle((1140, 92, 1800, 130), radius=18, fill=score_track)
    duel_font = font(54, bold=True)
    draw.text((1038, 72), "VS", font=duel_font, fill=(234, 242, 248, 235))
    label_font = font(22, bold=True)
    draw.text((330, 58), "BLUE SIDE", font=label_font, fill=(113, 219, 255, 220))
    draw.text((1648, 58), "RED SIDE", font=label_font, fill=(255, 133, 133, 220))

    subtitle_font = font(17, bold=False)
    draw.text((72, 1040), "Trivia Arena", font=font(24, bold=True), fill=(239, 246, 251, 235))
    draw.text((240, 1044), "Live history duels with real-time score pressure", font=subtitle_font, fill=(179, 211, 230, 214))

    round_chip = (92, 42, 542, 156)
    add_soft_glow(img, round_chip, 30, (0, 184, 255, 20))
    rounded_panel(draw, round_chip, 30, (10, 26, 40, 226), outline=(80, 194, 255, 120), width=3)
    draw.text((122, 56), "ROUND", font=font(18, bold=True), fill=(106, 214, 255, 220))

    timer_chip = (172, 160, 594, 326)
    add_soft_glow(img, timer_chip, 26, (255, 255, 255, 18))
    rounded_panel(draw, timer_chip, 26, (244, 248, 252, 246), outline=(255, 255, 255, 255), width=2)
    rounded_panel(draw, (192, 180, 574, 306), 18, (233, 242, 250, 255), outline=(183, 209, 228, 255), width=2)

    avatar_box = (72, 184, 690, 1000)
    add_soft_glow(img, avatar_box, 46, (0, 180, 255, 26))
    rounded_panel(draw, avatar_box, 46, (6, 22, 36, 192), outline=(86, 198, 255, 90), width=3)
    draw.rounded_rectangle((96, 214, 664, 976), radius=34, fill=(9, 31, 50, 220))
    draw.arc((146, 240, 620, 944), start=262, end=32, fill=(26, 112, 180, 120), width=8)
    draw.arc((178, 258, 602, 926), start=210, end=348, fill=(52, 200, 255, 92), width=4)
    draw.text((118, 220), "HOST", font=label_font, fill=(100, 205, 255, 210))
    draw.text((118, 246), "Arena commentator", font=subtitle_font, fill=(191, 221, 236, 180))

    prompt_strip = (720, 188, 1688, 288)
    add_soft_glow(img, prompt_strip, 28, (255, 82, 82, 22))
    rounded_panel(draw, prompt_strip, 28, (18, 26, 38, 228), outline=(255, 255, 255, 38), width=2)
    draw.rectangle((742, 210, 766, 266), fill=(255, 90, 90, 255))
    draw.text((790, 214), "QUESTION IN PLAY", font=font(20, bold=True), fill=(255, 219, 219, 228))

    visual_box = (1240, 334, 1804, 934)
    add_soft_glow(img, visual_box, 40, (255, 211, 99, 26))
    rounded_panel(draw, visual_box, 40, (246, 243, 237, 255), outline=(255, 255, 255, 160), width=3)
    rounded_panel(draw, (1272, 366, 1772, 902), 24, (255, 246, 228, 255), outline=(217, 196, 149, 255), width=4)
    draw.text((1294, 876), "REFERENCE VISUAL", font=font(18, bold=True), fill=(130, 104, 54, 255))

    lower_panel = (480, 820, 1180, 984)
    add_soft_glow(img, lower_panel, 28, (0, 218, 255, 18))
    rounded_panel(draw, lower_panel, 28, (13, 43, 68, 222), outline=(36, 202, 255, 178), width=3)
    draw.text((524, 854), "MATCH FLOW", font=font(20, bold=True), fill=(112, 224, 255, 220))

    rank_box = (1492, 184, 1844, 1004)
    add_soft_glow(img, rank_box, 32, (0, 188, 255, 18))
    rounded_panel(draw, rank_box, 32, (244, 248, 252, 250), outline=(255, 255, 255, 160), width=3)
    draw.text((1540, 220), "LIVE RANKING", font=font(30, bold=True), fill=(22, 64, 92, 255))
    draw.text((1540, 262), "Top participants", font=font(18, bold=False), fill=(74, 118, 142, 240))

    row_y = 320
    for idx in range(8):
        fill = (18, 177, 225, 255) if idx == 0 else (216, 240, 249, 255)
        outline = (0, 132, 192, 255) if idx == 0 else (169, 212, 231, 255)
        rounded_panel(draw, (1520, row_y, 1818, row_y + 68), 18, fill, outline=outline, width=2)
        row_y += 84

    target = OUT_DIR / "screen_background.png"
    img.save(target)
    return target


def generate_popup() -> Path:
    size = (1401, 495)
    img = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(img, "RGBA")

    add_soft_glow(img, (22, 26, 1378, 470), 38, (0, 184, 255, 28))
    rounded_panel(draw, (10, 12, 1391, 483), 42, (11, 22, 35, 238), outline=(91, 204, 255, 166), width=4)
    rounded_panel(draw, (34, 38, 1367, 457), 30, (18, 32, 48, 246), outline=(255, 255, 255, 24), width=2)
    draw.rectangle((50, 58, 420, 66), fill=(49, 196, 255, 230))
    draw.text((56, 84), "MATCH UPDATE", font=font(42, bold=True), fill=(243, 248, 252, 255))
    draw.text((58, 156), "Use this modal for wins, ties, and round transitions.", font=font(22, bold=False), fill=(179, 213, 232, 255))

    target = OUT_DIR / "match_update_modal.png"
    img.save(target)
    return target


def generate_ui_sprite() -> Path:
    size = (32, 32)
    img = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(img, "RGBA")
    rounded_panel(draw, (1, 1, 30, 30), 8, (250, 247, 240, 255), outline=(255, 255, 255, 255), width=1)

    gloss = Image.new("RGBA", size, (0, 0, 0, 0))
    gdraw = ImageDraw.Draw(gloss, "RGBA")
    gdraw.rounded_rectangle((2, 2, 29, 15), radius=6, fill=(255, 255, 255, 70))
    img = Image.alpha_composite(img, gloss)

    target = OUT_DIR / "UISprite.png"
    img.save(target)
    return target


def main() -> None:
    outputs = [
        generate_background(),
        generate_popup(),
        generate_ui_sprite(),
    ]
    for output in outputs:
        print(output)


if __name__ == "__main__":
    main()
