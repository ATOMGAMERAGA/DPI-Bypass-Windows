#!/usr/bin/env python3
"""Regenerates src/DpiBypass.App/Theme/Icons.xaml from Microsoft Fluent System Icons.

The icons are not redrawn by hand: every geometry in Icons.xaml is the path data of an
official SVG from https://github.com/microsoft/fluentui-system-icons (MIT), fetched at
the size it is going to be drawn at. Fluent ships a separately drawn asset per size -
a 24px glyph shrunk to 16px loses the optical weight the smaller drawing was made with
- so both the 20px and the 24px asset are emitted and FluentIcon picks the one that
matches the requested size.

Usage:  python3 tools/fetch-fluent-icons.py [--check]

--check re-fetches and compares against the committed file instead of writing it, so
CI (or a reviewer) can prove the checked-in geometries are still the upstream ones.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import pathlib
import re
import sys
import urllib.parse
import urllib.request

RAW = "https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/assets"

# (resource name, upstream asset folder, upstream file slug, variants)
# "both" = Regular and Filled; "regular" = Regular only; "filled" = Filled only.
# The folder and the slug are listed separately because upstream does not derive one
# from the other consistently ("WiFi 1" holds ic_fluent_wifi_1_*, "Text Bullet List
# LTR" holds ic_fluent_text_bullet_list_ltr_*), and guessing costs a 404 per glyph.
ICONS: list[tuple[str, str, str, str]] = [
    # Navigation rail
    ("Shield",            "Shield",                "shield",                "both"),
    ("ShieldCheckmark",   "Shield Checkmark",      "shield_checkmark",      "both"),
    ("Target",            "Target",                "target",                "both"),
    ("Globe",             "Globe",                 "globe",                 "both"),
    ("GlobeShield",       "Globe Shield",          "globe_shield",          "both"),
    ("Options",           "Options",               "options",               "both"),
    ("Settings",          "Settings",              "settings",              "both"),
    ("TextBulletList",    "Text Bullet List LTR",  "text_bullet_list_ltr",  "both"),
    ("TopSpeed",          "Top Speed",             "top_speed",             "both"),
    ("Wifi",              "WiFi 1",                "wifi_1",                "both"),
    ("Router",            "Router",                "router",                "both"),
    ("CellularData",      "Cellular Data 1",       "cellular_data_1",       "both"),

    # State and feedback
    ("Checkmark",         "Checkmark",             "checkmark",             "regular"),
    ("CheckmarkCircle",   "Checkmark Circle",      "checkmark_circle",      "both"),
    ("Warning",           "Warning",               "warning",               "both"),
    ("Info",              "Info",                  "info",                  "both"),
    ("ErrorCircle",       "Error Circle",          "error_circle",          "both"),
    ("Prohibited",        "Prohibited",            "prohibited",            "regular"),
    ("Pulse",             "Pulse",                 "pulse",                 "regular"),
    ("Timer",             "Timer",                 "timer",                 "regular"),
    ("Flash",             "Flash",                 "flash",                 "both"),
    ("Sparkle",           "Sparkle",               "sparkle",               "both"),

    # Commands
    ("Add",               "Add",                   "add",                   "regular"),
    ("Delete",            "Delete",                "delete",                "regular"),
    ("Dismiss",           "Dismiss",               "dismiss",               "regular"),
    ("ArrowClockwise",    "Arrow Clockwise",       "arrow_clockwise",       "regular"),
    ("ArrowSync",         "Arrow Sync",            "arrow_sync",            "regular"),
    ("ArrowLeft",         "Arrow Left",            "arrow_left",            "regular"),
    ("ArrowRight",        "Arrow Right",           "arrow_right",           "regular"),
    ("ArrowDownload",     "Arrow Download",        "arrow_download",        "regular"),
    ("ArrowReset",        "Arrow Reset",           "arrow_reset",           "regular"),
    ("ChevronDown",       "Chevron Down",          "chevron_down",          "regular"),
    ("ChevronRight",      "Chevron Right",         "chevron_right",         "regular"),
    ("Search",            "Search",                "search",                "regular"),
    ("Copy",              "Copy",                  "copy",                  "regular"),
    ("FolderOpen",        "Folder Open",           "folder_open",           "regular"),
    ("Save",              "Save",                  "save",                  "regular"),
    ("Play",              "Play",                  "play",                  "both"),
    ("Power",             "Power",                 "power",                 "regular"),
    ("Stop",              "Stop",                  "stop",                  "both"),
    ("Filter",            "Filter",                "filter",                "regular"),
    ("Open",              "Open",                  "open",                  "regular"),
    ("Eye",               "Eye",                   "eye",                   "regular"),
    ("Broom",             "Broom",                 "broom",                 "regular"),
    ("Beaker",            "Beaker",                "beaker",                "regular"),
    ("BookOpen",          "Book Open",             "book_open",             "regular"),
]

SIZES = (20, 24)

VARIANTS = {
    "both": ("regular", "filled"),
    "regular": ("regular",),
    "filled": ("filled",),
}

PATH_D = re.compile(r'\sd="([^"]+)"')


def fetch(folder: str, slug: str, size: int, variant: str) -> str:
    name = f"ic_fluent_{slug}_{size}_{variant}.svg"
    url = f"{RAW}/{urllib.parse.quote(folder)}/SVG/{name}"
    request = urllib.request.Request(url, headers={"User-Agent": "dpibypass-icon-sync"})
    with urllib.request.urlopen(request, timeout=60) as response:
        svg = response.read().decode("utf-8")

    figures = PATH_D.findall(svg)
    if not figures:
        raise RuntimeError(f"no path data in {url}")

    # "F1" is the nonzero fill rule. SVG's default is nonzero and WPF's path
    # mini-language defaults to evenodd, so dropping the prefix would punch holes in
    # every glyph whose subpaths happen to wind the same way.
    return "F1 " + " ".join(part.strip() for part in figures)


def build() -> str:
    jobs = [
        (name, folder, slug, size, variant)
        for name, folder, slug, kind in ICONS
        for size in SIZES
        for variant in VARIANTS[kind]
    ]

    data: dict[tuple[str, int, str], str] = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
        futures = {
            pool.submit(fetch, folder, slug, size, variant): (name, size, variant)
            for name, folder, slug, size, variant in jobs
        }
        for future in concurrent.futures.as_completed(futures):
            data[futures[future]] = future.result()

    lines = [
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        "",
        "    <!--",
        "      GENERATED FILE - do not hand-edit. Run tools/fetch-fluent-icons.py to refresh,",
        "      or run it with the check flag to prove it still matches upstream.",
        "",
        "      Microsoft Fluent System Icons (MIT), taken unmodified from the official SVG",
        "      assets: https://github.com/microsoft/fluentui-system-icons. Each key is the",
        "      path data of one asset at one size, so a glyph drawn at 16-20 DIP uses the",
        "      20px drawing and one drawn at 24 DIP uses the 24px drawing rather than a",
        "      scaled copy of the other. Infrastructure/FluentIcon.cs makes that choice;",
        "      markup asks for a symbol and a size, never for a geometry by name.",
        "",
        "      Licence notice: THIRD-PARTY-NOTICES.md.",
        "    -->",
        "",
    ]

    for name, _folder, _slug, kind in ICONS:
        for size in SIZES:
            for variant in VARIANTS[kind]:
                key = f"Icon.{name}.{size}.{variant.capitalize()}"
                lines.append(f'    <Geometry x:Key="{key}">{data[(name, size, variant)]}</Geometry>')
        lines.append("")

    lines.append("</ResourceDictionary>")
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    target = pathlib.Path(__file__).resolve().parent.parent / "src" / "DpiBypass.App" / "Theme" / "Icons.xaml"
    generated = build()

    if args.check:
        current = target.read_text(encoding="utf-8") if target.exists() else ""
        if current != generated:
            print(f"{target} is out of date with the upstream icon assets.", file=sys.stderr)
            return 1
        print(f"{target} matches the upstream icon assets.")
        return 0

    target.write_text(generated, encoding="utf-8")
    print(f"wrote {target} ({len(ICONS)} symbols)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
