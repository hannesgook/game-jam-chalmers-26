# Göteborg map data

`export.geojson` was exported from OpenStreetMap through Overpass Turbo for the
Spårvagn Rush game-jam project.

- Source: https://www.openstreetmap.org/
- Copyright: OpenStreetMap contributors
- Data licence: Open Data Commons Open Database License 1.0 (ODbL)
- Licence information: https://www.openstreetmap.org/copyright

The game must display: **Map data © OpenStreetMap contributors**.

## Elevation data

`gothenburg_height_513.bytes` is a 513×513 game-ready crop generated from
Göteborgs Stad's 2022 height model (0.5 m source GeoTIFF, SWEREF 99 12 00 /
RH2000). The source is released under CC0 and may be used commercially.

- Source: https://opengeodata.goteborg.se/files/hojdmodell/2022/hojdmodell_2022.html
- Licence: CC0 1.0
- Regenerate locally: `Tools/PrepareHeightmap.ps1`

The original `Assets/Map_data/639_14` package is intentionally Git-ignored.
An optional credit is: **Elevation data: Göteborgs Stad, 2022**.
