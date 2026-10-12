"""Render the shared monochrome SVG geometry. Requires Pillow (pip install Pillow)."""

from io import BytesIO
from pathlib import Path
import struct
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
SVG = ROOT / "assets/branding/camorip.svg"


def render(size):
    # Read geometry from the source, with supersampling for crisp small tray icons.
    root = ET.parse(SVG).getroot()
    ns = {"svg": "http://www.w3.org/2000/svg"}
    rect = root.find("svg:rect", ns)
    circle = root.find("svg:circle", ns)
    scale = size * 4 / 1024
    image = Image.new("RGB", (size * 4, size * 4), rect.attrib["fill"])
    cx, cy, radius = (float(circle.attrib[key]) * scale for key in ("cx", "cy", "r"))
    ImageDraw.Draw(image).ellipse((cx - radius, cy - radius, cx + radius - 1, cy + radius - 1),
                                 fill=circle.attrib["fill"])
    return image.resize((size, size), Image.Resampling.LANCZOS)


def main():
    render(1024).save(ROOT / "ios/Media.xcassets/AppIcon.appiconset/universal_cam_ios_1024.png")
    sizes = (16, 20, 24, 32, 40, 48, 64, 128, 256)
    payloads = []
    for size in sizes:
        buffer = BytesIO()
        render(size).convert("RGBA").save(buffer, format="PNG")
        payloads.append(buffer.getvalue())
    # PNG-backed ICO entries preserve every requested Windows shell/tray size.
    offset = 6 + 16 * len(sizes)
    entries = []
    for size, payload in zip(sizes, payloads):
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0,
                                   1, 32, len(payload), offset))
        offset += len(payload)
    (ROOT / "src/UniversalCam/universal_cam.ico").write_bytes(
        struct.pack("<HHH", 0, 1, len(sizes)) + b"".join(entries) + b"".join(payloads))


if __name__ == "__main__":
    main()
