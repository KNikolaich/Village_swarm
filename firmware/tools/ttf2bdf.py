#!/usr/bin/env python3
"""TTF -> BDF with Pillow (FreeType, no anti-aliasing: the hinted 1-bit shapes an e-ink screen wants).

    python ttf2bdf.py Roboto-Regular.ttf 18 reader-18.bdf [--ranges 32-126,1025,1040-1103,1105] [--axes weight=400,optical=size]

A variable font (Literata, Roboto Mono on Google Fonts) is cut at --axes: "weight=400" is the regular weight, "optical=size" sets the
optical-size axis to the pixel size (or give a number). Axes not mentioned keep the font's default.

One BDF per size; bdfconv (u8g2) turns it into a U8g2 font (make-reader-fonts.ps1 does both). Needs: pip install pillow.
"""
import argparse
import sys

from PIL import Image, ImageDraw, ImageFont


def expand(ranges):
    codes = []
    for part in ranges.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            a, b = part.split("-")
            codes.extend(range(int(a), int(b) + 1))
        else:
            codes.append(int(part))
    return sorted(set(codes))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("ttf")
    ap.add_argument("size", type=int, help="pixel size of the em square (the 18 of 'size 18')")
    ap.add_argument("out")
    ap.add_argument("--ranges", default="32-126,1025,1040-1103,1105", help="decimal code points: ASCII, Yo, A-ya, yo")
    ap.add_argument("--name", default="reader-roboto")
    ap.add_argument("--axes", default="", help="for a variable font: weight=400,optical=size")
    args = ap.parse_args()

    font = ImageFont.truetype(args.ttf, args.size, layout_engine=ImageFont.Layout.BASIC)
    if args.axes:
        wanted = dict(p.split("=", 1) for p in args.axes.split(",") if "=" in p)
        try:
            axes = font.get_variation_axes()
        except OSError:
            sys.exit("--axes given, but %s is not a variable font" % args.ttf)
        values = []
        for axis in axes:
            label = axis["name"].decode("ascii", "replace").lower()
            key = "weight" if "weight" in label else "optical" if "optical" in label else label
            value = wanted.get(key, axis["default"])
            value = args.size if value == "size" else float(value)
            values.append(min(max(value, axis["minimum"]), axis["maximum"]))
        font.set_variation_by_axes(values)
    ascent, descent = font.getmetrics()
    baseline = ascent + 2  # a little room above for tall marks
    origin = args.size      # room on the left for a negative bearing

    glyphs = []
    missing = []
    for cp in expand(args.ranges):
        ch = chr(cp)
        advance = max(0, int(round(font.getlength(ch))))
        img = Image.new("1", (advance + 2 * args.size, ascent + descent + 6), 0)
        ImageDraw.Draw(img).text((origin, baseline), ch, font=font, fill=1, anchor="ls")
        box = img.getbbox()
        if box is None:
            if cp != 32:
                missing.append(cp)
            glyphs.append((cp, advance, 0, 0, 0, 0, []))
            continue
        left, top, right, bottom = box
        w, h = right - left, bottom - top
        rows = []
        for y in range(top, bottom):
            bits = "".join("1" if img.getpixel((x, y)) else "0" for x in range(left, right))
            bits += "0" * (-len(bits) % 8)
            rows.append("".join("%02X" % int(bits[i:i + 8], 2) for i in range(0, len(bits), 8)))
        glyphs.append((cp, advance, w, h, left - origin, baseline - bottom, rows))

    if missing:
        print("warning: no shape for U+%s" % ", U+".join("%04X" % c for c in missing), file=sys.stderr)
    inked = [g for g in glyphs if g[2] and g[3]]
    if not inked:
        sys.exit("no glyphs were drawn: is %s the right font?" % args.ttf)
    min_x = min(g[4] for g in inked)
    max_x = max(g[4] + g[2] for g in inked)
    min_y = min(g[5] for g in inked)
    max_y = max(g[5] + g[3] for g in inked)

    with open(args.out, "w", encoding="ascii", newline="\n") as f:
        f.write("STARTFONT 2.1\n")
        f.write("FONT -%s-medium-r-normal--%d-%d-75-75-p-0-iso10646-1\n" % (args.name, args.size, args.size * 10))
        f.write("SIZE %d 75 75\n" % args.size)
        f.write("FONTBOUNDINGBOX %d %d %d %d\n" % (max_x - min_x, max_y - min_y, min_x, min_y))
        f.write("STARTPROPERTIES 2\nFONT_ASCENT %d\nFONT_DESCENT %d\nENDPROPERTIES\n" % (max(max_y, 1), max(-min_y, 0)))
        f.write("CHARS %d\n" % len(glyphs))
        for cp, advance, w, h, xo, yo, rows in glyphs:
            f.write("STARTCHAR U+%04X\nENCODING %d\n" % (cp, cp))
            f.write("SWIDTH %d 0\nDWIDTH %d 0\n" % (advance * 1000 // args.size, advance))
            f.write("BBX %d %d %d %d\nBITMAP\n" % (w, h, xo, yo))
            for r in rows:
                f.write(r + "\n")
            f.write("ENDCHAR\n")
        f.write("ENDFONT\n")
    print("%s: %d glyphs, height %d px, ascent %d, descent %d" % (args.out, len(glyphs), max_y - min_y, max_y, -min_y))


if __name__ == "__main__":
    main()
