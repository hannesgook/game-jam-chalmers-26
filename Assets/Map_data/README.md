# Göteborg map data

`export.geojson` was exported from OpenStreetMap through Overpass Turbo for the
Spårvagn Rush game-jam project.

- Source: https://www.openstreetmap.org/
- Copyright: OpenStreetMap contributors
- Data licence: Open Data Commons Open Database License 1.0 (ODbL)
- Licence information: https://www.openstreetmap.org/copyright

The game must display: **Map data © OpenStreetMap contributors**.

## Stops, stores, and labels

`map_details.geojson` contains named tram stops, shops, amenities, tourist
features, and offices. The map generator turns these into billboard labels and
physical tram-stop signs. Refresh it with **Tools > Göteborg > Download Stops
and Store Names**, or run `python Tools/FetchMapDetails.py`, then regenerate the
map. Coverage follows what contributors have mapped in OpenStreetMap.

Named building footprints are labelled automatically too, so they do not need
to be duplicated in the detail file.

## Optional aerial ground image

`aerial.jpg` is the official Göteborg 2025 orthophoto for exactly the map bounds
`57.695, 11.965` to `57.710, 11.985`. It comes from Göteborgs Stad's open WMS
and is CC0. The terrain UVs are georeferenced to those bounds. Do not replace it
with tiles copied from consumer map websites; their terms commonly prohibit
repackaging them in a game.

The file is Git-ignored, so a fresh clone has to fetch it. Either use the menu
item

    Tools > Göteborg > Download Aerial Imagery

or, if you would rather not open the editor, run

    python Tools/FetchAerial.py

Both request the same image from the same bounds; the menu item exists so the
project does not need Python on the machine, and it also applies the import
settings the ground needs (the default 2048 cap would throw away three quarters
of the resolution) and repoints the generated `Ground` and `BuildingRoofs`
materials at the freshly imported texture.

That last step matters because those two materials are committed and reference
the photo by GUID, while `aerial.jpg.meta` is ignored: a newly imported copy gets
a new GUID, so without the repoint the materials resolve to nothing and the
ground stays grey. The repoint writes your local GUID into the two `.mat` files,
so do not commit that change unless `aerial.jpg` and its `.meta` are committed
too — otherwise it simply moves the problem to the next person.

The generator applies the same geographic projection to building roofs, so
roof details in the orthophoto align with the extruded 3D footprints.

## Buildings

`buildings.geojson` holds the 924 building footprints inside the same window,
fetched from Overpass and reassembled into closed rings (courtyards included) by
`Tools/FetchBuildings.py`. The file is committed, so a fresh clone needs no
refetch. To pull it again, either use the menu item

    Tools > Göteborg > Download Building Data

or, if you would rather not open the editor, run

    python Tools/FetchBuildings.py

Both query the same bounds and write the same footprints; the menu item exists
so the project does not need Python on the machine.

The generator extrudes each footprint to the height OSM gives it: the `height`
tag where it exists (46 footprints), otherwise `building:levels` (296), and
otherwise a default for that kind of building. Only the tags the extruder reads
are kept in the file.

- Source: https://www.openstreetmap.org/
- Licence: ODbL 1.0, same as `export.geojson`

## Elevation data

`gothenburg_height_513.bytes` is a 513×513 game-ready crop generated from
Göteborgs Stad's 2022 height model (0.5 m source GeoTIFF, SWEREF 99 12 00 /
RH2000). The source is released under CC0 and may be used commercially.

- Source: https://opengeodata.goteborg.se/files/hojdmodell/2022/hojdmodell_2022.html
- Licence: CC0 1.0
- Regenerate locally: `Tools/PrepareHeightmap.ps1`

The original `Assets/Map_data/639_14` package is intentionally Git-ignored.
An optional credit is: **Elevation data: Göteborgs Stad, 2022**.
