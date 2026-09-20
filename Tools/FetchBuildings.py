#!/usr/bin/env python3
"""Fetch OSM building footprints for the TramRush map window and write
Assets/Map_data/buildings.geojson.

Overpass returns each multipolygon as loose member ways, so the ring assembly
and the tag pruning happen here; the Unity generator only ever sees closed
rings and the handful of tags it extrudes from.

Usage: python Tools/FetchBuildings.py [--raw cached_overpass.json]
"""
import argparse
import json
import os
import sys
import urllib.parse
import urllib.request

# Must stay in sync with the bounds in Assets/Editor/GothenburgMapGenerator.cs.
SOUTH, WEST, NORTH, EAST = 57.695, 11.965, 57.710, 11.985

# A slab of the footprint may fall outside the window; the generator clips it.
QUERY = f"""[out:json][timeout:180];
(
  way["building"]({SOUTH},{WEST},{NORTH},{EAST});
  relation["building"]({SOUTH},{WEST},{NORTH},{EAST});
);
out body geom;
"""

# Everything the extruder reads. Dropping the rest keeps the asset ~10x smaller.
KEPT_TAGS = (
    "building",
    "building:levels",
    "building:min_level",
    "height",
    "min_height",
    "roof:levels",
    "roof:height",
    "roof:shape",
    "name",
)


def fetch():
    request = urllib.request.Request(
        "https://overpass-api.de/api/interpreter",
        data=urllib.parse.urlencode({"data": QUERY}).encode(),
        headers={"User-Agent": "TramRush-gamejam/1.0 (OSM building import)"},
    )
    with urllib.request.urlopen(request, timeout=300) as response:
        return json.load(response)


def ring_of(way_geometry):
    return [[round(node["lon"], 7), round(node["lat"], 7)] for node in way_geometry]


def close(ring):
    if len(ring) > 2 and ring[0] != ring[-1]:
        ring = ring + [ring[0]]
    return ring


def is_ring(ring):
    return len(ring) >= 4 and ring[0] == ring[-1]


def assemble(segments):
    """Chain member ways end-to-end into closed rings.

    Overpass hands back a multipolygon's ways in arbitrary order and direction,
    and a Gothenburg city block is routinely split across four or five of them,
    so a ring only appears once the pieces are walked and flipped into place.
    """
    pending = [list(s) for s in segments if len(s) >= 2]
    rings = []
    while pending:
        current = pending.pop()
        extended = True
        while not is_ring(current) and extended:
            extended = False
            for index, candidate in enumerate(pending):
                if candidate[0] == current[-1]:
                    current += candidate[1:]
                elif candidate[-1] == current[-1]:
                    current += candidate[-2::-1]
                elif candidate[-1] == current[0]:
                    current = candidate[:-1] + current
                elif candidate[0] == current[0]:
                    current = candidate[:0:-1] + current
                else:
                    continue
                pending.pop(index)
                extended = True
                break
        if is_ring(current):
            rings.append(current)
    return rings


def signed_area(ring):
    total = 0.0
    for (x0, y0), (x1, y1) in zip(ring, ring[1:]):
        total += x0 * y1 - x1 * y0
    return total * 0.5


def to_features(elements):
    features = []
    for element in elements:
        tags = element.get("tags", {})
        if tags.get("building") in (None, "no"):
            continue
        # An underground garage has a footprint but nothing above the street.
        if tags.get("location") == "underground":
            continue

        if element["type"] == "way":
            ring = close(ring_of(element.get("geometry", [])))
            if not is_ring(ring):
                continue
            features.append(make_feature(element, [ring], tags))
            continue

        outers = assemble(
                ring_of(m["geometry"])
                for m in element.get("members", [])
                if m.get("role") == "outer" and m.get("geometry")
            )
        inners = assemble(
            ring_of(m["geometry"])
            for m in element.get("members", [])
            if m.get("role") == "inner" and m.get("geometry")
        )
        if not outers:
            continue
        # A relation with several outers is several buildings sharing one tag set,
        # and a courtyard belongs to whichever of them encloses it -- the largest
        # is not always the right one.
        for outer in outers:
            own = [hole for hole in inners if contains(outer, hole[0])]
            features.append(make_feature(element, [outer] + own, tags))
    return features


def contains(ring, point):
    """Crossing-number test; the rings never touch, so the boundary case is moot."""
    x, y = point
    inside = False
    for (x0, y0), (x1, y1) in zip(ring, ring[1:]):
        if (y0 > y) != (y1 > y) and x < x0 + (y - y0) / (y1 - y0) * (x1 - x0):
            inside = not inside
    return inside


def make_feature(element, rings, tags):
    # GeoJSON wants the outer ring counter-clockwise and holes clockwise.
    normalised = []
    for index, ring in enumerate(rings):
        outward = signed_area(ring) > 0
        wanted = index == 0
        normalised.append(ring if outward == wanted else ring[::-1])
    properties = {"@id": f"{element['type']}/{element['id']}"}
    properties.update({k: v for k, v in tags.items() if k in KEPT_TAGS})
    return {
        "type": "Feature",
        "properties": properties,
        "geometry": {"type": "Polygon", "coordinates": normalised},
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--raw", help="use a cached Overpass JSON response instead of querying")
    arguments = parser.parse_args()

    if arguments.raw:
        with open(arguments.raw, encoding="utf-8") as handle:
            payload = json.load(handle)
    else:
        payload = fetch()

    features = to_features(payload["elements"])
    collection = {
        "type": "FeatureCollection",
        "generator": "Tools/FetchBuildings.py",
        "copyright": "Map data \u00a9 OpenStreetMap contributors, ODbL 1.0",
        "timestamp": payload.get("osm3s", {}).get("timestamp_osm_base"),
        "features": features,
    }

    destination = os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        "Assets", "Map_data", "buildings.geojson")
    with open(destination, "w", encoding="utf-8") as handle:
        json.dump(collection, handle, ensure_ascii=False, separators=(",", ":"))
    holes = sum(len(f["geometry"]["coordinates"]) - 1 for f in features)
    print(f"Wrote {len(features)} buildings ({holes} courtyards) to {destination}")


if __name__ == "__main__":
    sys.exit(main())
