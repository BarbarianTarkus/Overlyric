"""Crop and optionally upscale a region of an image, for close inspection.

Usage: python crop.py <src> <dst> <x1> <y1> <x2> <y2> [scale]
"""
import sys

from PIL import Image


def main() -> int:
    if len(sys.argv) < 7:
        print("usage: crop.py <src> <dst> <x1> <y1> <x2> <y2> [scale]")
        return 2

    src, dst = sys.argv[1], sys.argv[2]
    x1, y1, x2, y2 = (int(v) for v in sys.argv[3:7])
    scale = float(sys.argv[7]) if len(sys.argv) > 7 else 1.0

    image = Image.open(src).convert("RGB")
    crop = image.crop((x1, y1, x2, y2))

    if scale != 1.0:
        new_size = (int(crop.width * scale), int(crop.height * scale))
        crop = crop.resize(new_size, Image.LANCZOS)

    crop.save(dst)
    print(f"{dst} {crop.size[0]}x{crop.size[1]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
