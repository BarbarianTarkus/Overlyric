r"""Render src/Overlyric/Assets/Overlyric.svg into the multi-resolution Overlyric.ico.

Windows asks for the same icon at a handful of sizes - 16 px in Explorer's list view, 20 and 24 px
in the shell's DPI-scaled views, 32 px for the notification area, 48 px and 256 px for the large
tiles - and scaling one bitmap up or down produces mush at the small end. Each size is therefore
rasterised on its own, supersampled 4x and downsampled with LANCZOS.

The frames are stored the way Windows expects: 32-bit DIBs for the small sizes, PNG for 128 px and
256 px. The resulting file is referenced by the app (<ApplicationIcon>) and by the installer, so it
is the single source of the product icon.

Requirements: Python 3.9+ with Pillow, and Chrome or Edge for rendering.

Usage:
    python tools\make-icon.py
    python tools\make-icon.py --svg path\to\art.svg --out path\to\art.ico
"""
import argparse
import io
import os
import shutil
import struct
import subprocess
import tempfile

from PIL import Image

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

# The page the browser renders is always this large. Headless Chrome refuses to honour small
# window sizes (it silently clamps them and the screenshot comes back empty), so the icon is
# drawn in the top-left corner of a fixed page and cropped out instead.
PAGE = 1024

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_SVG = os.path.join(REPO_ROOT, 'src', 'Overlyric', 'Assets', 'Overlyric.svg')
DEFAULT_ICO = os.path.join(REPO_ROOT, 'src', 'Overlyric', 'Assets', 'Overlyric.ico')

BROWSERS = (
    r'C:\Program Files\Google\Chrome\Application\chrome.exe',
    r'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe',
    r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    r'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
)


def find_browser(explicit):
    if explicit:
        return explicit
    for candidate in BROWSERS:
        if os.path.exists(candidate):
            return candidate
    found = shutil.which('chrome') or shutil.which('msedge')
    if found:
        return found
    raise SystemExit('No Chrome or Edge found; pass --chrome <path>.')


def render(browser, svg_path, size, work):
    scale = min(size * 4, PAGE)
    html = (
        '<!doctype html><html><head><meta charset="utf-8"><style>'
        'html,body{margin:0;padding:0;background:transparent;overflow:hidden}'
        f'img{{display:block;width:{scale}px;height:{scale}px}}'
        '</style></head><body>'
        f'<img src="{os.path.basename(svg_path)}">'
        '</body></html>'
    )
    page_path = os.path.join(work, f'page-{size}.html')
    with open(page_path, 'w', encoding='utf-8') as handle:
        handle.write(html)

    shot = os.path.join(work, f'page-{size}.png')
    profile = os.path.join(work, f'profile-{size}')
    url = 'file:///' + page_path.replace('\\', '/')

    for attempt in range(4):
        if os.path.exists(shot):
            os.remove(shot)
        subprocess.run(
            [browser, '--headless=new', '--disable-gpu', '--no-first-run',
             '--no-default-browser-check', '--hide-scrollbars',
             '--force-device-scale-factor=1', '--default-background-color=00000000',
             f'--user-data-dir={profile}', f'--window-size={PAGE},{PAGE}',
             f'--screenshot={shot}', url],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False)
        if not os.path.exists(shot):
            continue
        page = Image.open(shot).convert('RGBA')
        if page.size != (PAGE, PAGE):
            continue
        frame = page.crop((0, 0, scale, scale))
        # A blank or clipped capture is the failure mode to guard against: the browser can return a
        # transparent page instead of failing outright.
        painted = frame.getchannel('A').getbbox()
        if painted is None or painted[2] < scale * 0.9 or painted[3] < scale * 0.9:
            continue
        return frame.resize((size, size), Image.Resampling.LANCZOS)

    raise SystemExit(f'Rendering the {size} px frame failed four times.')


def dib(image):
    """A 32-bit bottom-up DIB with its (unused) AND mask, as an icon frame expects."""
    side = image.size[0]
    rows = image.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes()
    pixels = bytearray(len(rows))
    pixels[0::4] = rows[2::4]  # RGBA -> BGRA
    pixels[1::4] = rows[1::4]
    pixels[2::4] = rows[0::4]
    pixels[3::4] = rows[3::4]
    header = struct.pack('<IiiHHIIiiII', 40, side, side * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
    mask_row = ((side + 31) // 32) * 4
    return header + bytes(pixels) + b'\x00' * (mask_row * side)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--svg', default=DEFAULT_SVG, help='source artwork (default: %(default)s)')
    parser.add_argument('--out', default=DEFAULT_ICO, help='icon to write (default: %(default)s)')
    parser.add_argument('--chrome', help='browser executable to render with')
    args = parser.parse_args()

    if not os.path.exists(args.svg):
        raise SystemExit(f'No such SVG: {args.svg}')
    browser = find_browser(args.chrome)

    work = tempfile.mkdtemp(prefix='overlyric-icon-')
    svg_copy = os.path.join(work, 'icon.svg')
    shutil.copyfile(args.svg, svg_copy)

    frames = []
    for size in SIZES:
        image = render(browser, svg_copy, size, work)
        buffer = io.BytesIO()
        if size >= 128:
            image.save(buffer, format='PNG', optimize=True)
        else:
            buffer.write(dib(image))
        frames.append((size, buffer.getvalue()))
        print(f'  {size:>3} px')

    data = io.BytesIO()
    data.write(struct.pack('<HHH', 0, 1, len(frames)))
    offset = 6 + 16 * len(frames)
    for size, payload in frames:
        # A stored size of 0 means 256.
        data.write(struct.pack('<BBBBHHII',
                               0 if size == 256 else size,
                               0 if size == 256 else size,
                               0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    for _, payload in frames:
        data.write(payload)

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, 'wb') as handle:
        handle.write(data.getvalue())

    print(f'wrote {args.out} ({os.path.getsize(args.out):,} bytes)')


if __name__ == '__main__':
    main()
