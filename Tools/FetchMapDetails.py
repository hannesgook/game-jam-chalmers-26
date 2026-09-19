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
  nwr["public_transport"="platform"]["tram"="yes"]({SOUTH},{WEST},{NORTH},{EAST});
  nwr["railway"="platform"]["tram"="yes"]({SOUTH},{WEST},{NORTH},{EAST});
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
    stop_records = []
    for element in payload.get("elements", []):
        tags = element.get("tags", {})
        name = tags.get("name")
        point = element if "lon" in element else element.get("center", {})
        if not name or "lon" not in point or "lat" not in point:
            continue
        platform = tags.get("public_transport") == "platform" or tags.get("railway") == "platform"
        stop = platform or tags.get("railway") == "tram_stop" or tags.get("public_transport") == "stop_position"
        kind = "tram_stop" if stop else "shop" if "shop" in tags else "place"
        key = (name.casefold(), kind, round(point["lon"], 5), round(point["lat"], 5))
        if key in seen:
            continue
        seen.add(key)
        feature = {
            "type": "Feature",
            "properties": {"name": name, "kind": kind},
            "geometry": {"type": "Point", "coordinates": [point["lon"], point["lat"]]},
        }
        if stop:
            # A mapped platform is already on the correct side of the rails. Prefer it
            # over stop-position nodes, which deliberately sit on the track centreline.
            priority = 0 if platform else 1 if tags.get("railway") == "tram_stop" else 2
            stop_records.append((priority, feature))
        else:
            features.append(feature)

    # Platform ways around the central hub are often called just A, B, A1, etc.
    # Associate each platform with the nearest named stop-position before grouping.
    centres = [(priority, feature) for priority, feature in stop_records if priority > 0]
    for priority, feature in stop_records:
        if priority != 0:
            continue
        lon, lat = feature["geometry"]["coordinates"]
        nearest = min(centres, key=lambda item:
            ((item[1]["geometry"]["coordinates"][0] - lon) * 0.53) ** 2 +
            (item[1]["geometry"]["coordinates"][1] - lat) ** 2,
            default=None)
        if nearest is not None:
            nlon, nlat = nearest[1]["geometry"]["coordinates"]
            distance2 = ((nlon - lon) * 0.53) ** 2 + (nlat - lat) ** 2
            if distance2 < 0.0008 ** 2:
                feature["properties"]["name"] = nearest[1]["properties"]["name"]

    stop_candidates = {}
    for priority, feature in stop_records:
        final_name = feature["properties"]["name"]
        generic_platform_ref = (priority == 0 and len(final_name) <= 2 and
                                final_name[0].upper() in "ABCD" and
                                (len(final_name) == 1 or final_name[1].isdigit()))
        if generic_platform_ref:
            continue
        stop_candidates.setdefault(final_name.casefold(), []).append((priority, feature))

    for candidates in stop_candidates.values():
        best_priority = min(item[0] for item in candidates)
        chosen = [feature for priority, feature in candidates if priority == best_priority]
        # Keep every real platform. Centreline nodes are discarded when platform
        # geometry exists, but a large interchange retains all of its platform sides.
        features.extend(chosen)

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
