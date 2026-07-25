#!/usr/bin/env python3
"""Regenerate every raster asset that ships with Atom DPI Bypass.

Windows scales icons in a lot of places (tray, taskbar, alt-tab, the installer
wizard, Add/Remove Programs, jump lists). If an .ico only carries one bitmap,
Windows rescales it and the result looks muddy. So every size Windows asks for
is baked in at full quality instead.

    python3 tools/generate_assets.py assets/logo/source.png
"""

from __future__ import annotations

import struct
import sys
from io import BytesIO
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
LOGO_DIR = ROOT / "assets" / "logo"
INSTALLER_DIR = ROOT / "installer" / "assets"

# Every size Windows is known to request, plus the 512/1024 masters used by the
# WPF UI so it stays crisp at 300% scaling.
ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 72, 96, 128, 256]
PNG_SIZES = [16, 24, 32, 48, 64, 128, 256, 512, 1024]

# Inno Setup accepts a comma separated list of images and picks the closest one
# for the user's DPI, so provide the whole ladder.
WIZARD_SMALL = [(55, 55), (64, 64), (83, 83), (92, 92), (110, 110), (119, 119), (138, 138)]
WIZARD_LARGE = [(164, 314), (192, 386), (256, 459), (328, 604), (355, 700), (410, 797)]

BACKDROP = (17, 28, 46)  # #111C2E - matches the shield body


def square_master(source: Path, canvas_ratio: float = 0.90) -> Image.Image:
    """Trim the transparent border and re-center on a square canvas."""
    im = Image.open(source).convert("RGBA")
    bbox = im.getbbox()
    if bbox is None:
        raise SystemExit(f"{source} is fully transparent")
    art = im.crop(bbox)
    side = int(max(art.size) / canvas_ratio)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(art, ((side - art.width) // 2, (side - art.height) // 2), art)
    return canvas.resize((1024, 1024), Image.LANCZOS)


def write_ico(master: Image.Image, target: Path, sizes: list[int]) -> None:
    """Write a multi-image .ico by hand.

    Pillow's own writer is fine, but doing it here means the small sizes are
    guaranteed to be true-colour BMP (what every Windows shell surface expects)
    while the large ones are PNG compressed (what keeps the file small).
    """
    entries: list[tuple[int, bytes, bool]] = []
    for size in sizes:
        frame = master.resize((size, size), Image.LANCZOS)
        if size >= 128:
            buf = BytesIO()
            frame.save(buf, format="PNG", optimize=True)
            entries.append((size, buf.getvalue(), True))
        else:
            entries.append((size, _bmp_payload(frame), False))

    out = BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for size, payload, _ in entries:
        out.write(
            struct.pack(
                "<BBBBHHII",
                size if size < 256 else 0,
                size if size < 256 else 0,
                0,
                0,
                1,
                32,
                len(payload),
                offset,
            )
        )
        offset += len(payload)
    for _, payload, _ in entries:
        out.write(payload)
    target.write_bytes(out.getvalue())


def _bmp_payload(frame: Image.Image) -> bytes:
    """A DIB with a doubled height, BGRA rows bottom-up, plus the AND mask."""
    w, h = frame.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, w * h * 4, 0, 0, 0, 0)
    px = frame.load()
    colour = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            colour += bytes((b, g, r, a))
    # 1bpp AND mask, rows padded to 4 bytes. Fully transparent because the
    # alpha channel above already carries the shape.
    stride = ((w + 31) // 32) * 4
    mask = bytes(stride * h)
    return header + bytes(colour) + mask


def write_pngs(master: Image.Image, target_dir: Path, sizes: list[int]) -> None:
    for size in sizes:
        frame = master.resize((size, size), Image.LANCZOS)
        frame.save(target_dir / f"atomdpi-{size}.png", format="PNG", optimize=True)


def write_wizard_bitmaps(master: Image.Image) -> None:
    INSTALLER_DIR.mkdir(parents=True, exist_ok=True)

    for w, h in WIZARD_SMALL:
        canvas = Image.new("RGB", (w, h), BACKDROP)
        art = master.resize((w, h), Image.LANCZOS)
        canvas.paste(art, (0, 0), art)
        canvas.save(INSTALLER_DIR / f"wizard-small-{w}x{h}.bmp", format="BMP")

    for w, h in WIZARD_LARGE:
        canvas = Image.new("RGB", (w, h), BACKDROP)
        art_side = int(w * 0.72)
        art = master.resize((art_side, art_side), Image.LANCZOS)
        canvas.paste(art, ((w - art_side) // 2, int(h * 0.18)), art)
        canvas.save(INSTALLER_DIR / f"wizard-large-{w}x{h}.bmp", format="BMP")


def main() -> int:
    source = Path(sys.argv[1]) if len(sys.argv) > 1 else LOGO_DIR / "source.png"
    LOGO_DIR.mkdir(parents=True, exist_ok=True)

    master = square_master(source)
    master.save(LOGO_DIR / "atomdpi-1024.png", format="PNG", optimize=True)
    write_pngs(master, LOGO_DIR, [s for s in PNG_SIZES if s != 1024])
    write_ico(master, LOGO_DIR / "atomdpi.ico", ICON_SIZES)
    write_wizard_bitmaps(master)

    print(f"icon sizes : {', '.join(str(s) for s in ICON_SIZES)}")
    print(f"png sizes  : {', '.join(str(s) for s in PNG_SIZES)}")
    print(f"wizard     : {len(WIZARD_SMALL)} small + {len(WIZARD_LARGE)} large bitmaps")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
