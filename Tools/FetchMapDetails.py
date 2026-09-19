#!/usr/bin/env python3
"""Fetch named tram stops, stores, and public places for the generated city."""
import json
import os
import urllib.parse
import urllib.request

SOUTH, WEST, NORTH, EAST = 57.695, 11.965, 57.710, 11.985
QUERY = f"""[out:json][timeout:120];
(
  nwr["railway"="tram_stop"]({SOUTH},{WEST},{NORTH},{EAST});
  nwr["public_transport"="stop_position"]["tram"="yes"]({SOUTH},{WEST},{NORTH},{EAST});
  nwr["name"]["shop"]({SOUTH},{WEST},{NORTH},{EAST});
  nwr["name"]["amenity"]({SOUTH},{WEST},{NORTH},{EAST});
  nwr["name"]["tourism"]({SOUTH},{WEST},{NORTH},{EAST});
  nwr["name"]["office"]({SOUTH},{WEST},{NORTH},{EAST});
);
out center tags;
"""


def main():
    request = urllib.request.Request(
        "https://overpass-api.de/api/interpreter",
        data=urllib.parse.urlencode({"data": QUERY}).encode(),
        headers={"User-Agent": "SparvagnRush-gamejam/1.0 (OSM map detail import)"},
    )
    with urllib.request.urlopen(request, timeout=240) as response:
        payload = json.load(response)

    features = []
    seen = set()
    for element in payload.get("elements", []):
        tags = element.get("tags", {})
        name = tags.get("name")
        point = element if "lon" in element else element.get("center", {})
        if not name or "lon" not in point or "lat" not in point:
            continue
        stop = tags.get("railway") == "tram_stop" or tags.get("public_transport") == "stop_position"
        kind = "tram_stop" if stop else "shop" if "shop" in tags else "place"
        key = (name.casefold(), kind, round(point["lon"], 5), round(point["lat"], 5))
        if key in seen:
            continue
        seen.add(key)
        features.append({
            "type": "Feature",
            "properties": {"name": name, "kind": kind},
            "geometry": {"type": "Point", "coordinates": [point["lon"], point["lat"]]},
        })

    collection = {
        "type": "FeatureCollection",
        "generator": "Tools/FetchMapDetails.py",
        "copyright": "Map data © OpenStreetMap contributors, ODbL 1.0",
        "timestamp": payload.get("osm3s", {}).get("timestamp_osm_base"),
        "features": features,
    }
    destination = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                               "Assets", "Map_data", "map_details.geojson")
    with open(destination, "w", encoding="utf-8") as output:
        json.dump(collection, output, ensure_ascii=False, separators=(",", ":"))
    stops = sum(item["properties"]["kind"] == "tram_stop" for item in features)
    shops = sum(item["properties"]["kind"] == "shop" for item in features)
    print(f"Wrote {len(features)} details ({stops} tram stops, {shops} shops) to {destination}")


if __name__ == "__main__":
    main()
