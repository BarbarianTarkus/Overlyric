"""Capture the primary screen so the overlay can be verified visually.

Usage: python screenshot.py <output.png> [--all]
"""
import sys

from PIL import ImageGrab


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: screenshot.py <output.png> [--all]")
        return 2

    output = sys.argv[1]
    all_screens = "--all" in sys.argv[3:] or "--all" in sys.argv[1:]

    image = ImageGrab.grab(all_screens=all_screens)
    image.save(output)
    print(f"saved {output} {image.size[0]}x{image.size[1]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
