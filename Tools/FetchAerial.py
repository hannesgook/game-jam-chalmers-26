#!/usr/bin/env python3
"""Fetch the official Göteborg 2025 CC0 orthophoto for the game bounds."""
import os
import urllib.parse
import urllib.request

SOUTH, WEST, NORTH, EAST = 57.695, 11.965, 57.710, 11.985
ENDPOINT = "https://opengeodata.goteborg.se/services/ortofoto/wms/v1"


def main():
    # The projected map is about 0.704 as wide as it is tall at this latitude.
    # 5760x8192 therefore preserves the real-world aspect ratio closely while
    # giving the ground about 20 cm per source pixel.
    parameters = {
        "SERVICE": "WMS",
        "VERSION": "1.3.0",
        "REQUEST": "GetMap",
        "LAYERS": "orto_2025",
        "STYLES": "raster",
        "CRS": "CRS:84",
        "BBOX": f"{WEST},{SOUTH},{EAST},{NORTH}",
        "WIDTH": "5760",
        "HEIGHT": "8192",
        "FORMAT": "image/jpeg",
        "TRANSPARENT": "false",
    }
    request = urllib.request.Request(
        ENDPOINT + "?" + urllib.parse.urlencode(parameters),
        headers={"User-Agent": "SparvagnRush-gamejam/1.0 (CC0 orthophoto import)"},
    )
    with urllib.request.urlopen(request, timeout=300) as response:
        content_type = response.headers.get_content_type()
        payload = response.read()
    if content_type != "image/jpeg" or len(payload) < 100_000:
        raise RuntimeError(f"WMS returned {content_type} ({len(payload)} bytes), not an orthophoto")

    destination = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                               "Assets", "Map_data", "aerial.jpg")
    with open(destination, "wb") as output:
        output.write(payload)
    print(f"Wrote Göteborg 2025 CC0 orthophoto ({len(payload):,} bytes) to {destination}")


if __name__ == "__main__":
    main()
