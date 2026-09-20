# City presentation and departure flow

Press Play in `Assets/Scenes/SampleScene.unity`. The game opens at a 55-degree angle,
with the camera at world Y = 480 metres. The session starts only after departure.

| Input | City overview | Driving |
| --- | --- | --- |
| Mouse wheel | Zoom between 140 and 1,800 metres | Adjust follow distance |
| WASD / arrows | Pan relative to the view | Drive and balance |
| Right mouse drag | Pan | Orbit |
| Middle mouse | Drag to pan | Reset orbit |
| Q / E | Rotate overview | — |
| Home | Recenter selected stop at 480 metres | — |
| Enter / Depart | Spawn at selected stop | — |
| Escape | — | Return to stop selection |
| R after a run ends | — | Restart at the same stop |

Choose an existing named tram stop from the sidebar or its map pin. Duplicate
platform names are grouped; spawn positions snap to the closest track edge. Maps
without generated stops offer a rail-access fallback.

The runtime presentation preserves the generated assets: it hides the old flat
track renderer and builds one narrow steel balance rail per network path, with a
dark web, concrete supports, brown gravel ballast, sloped shoulders, and continuous outer retaining edges. Roads receive asphalt detail and granite edges.
Warm directional light, atmospheric fog, ACES tone mapping, restrained bloom,
vignette, and FXAA complete the presentation. Desktop shadows extend to 700 metres
so the default overview can show building shadows.

Future map generation also produces narrow silver track previews, wider vehicle
roads, and cumulative-distance road UVs. Existing scenes receive the runtime
materials and rail geometry without regeneration; their road widths stay intact.

Camera collision queries use a reusable hit buffer. Rail meshes are grouped into
batches, distant labels skip occlusion raycasts, HUD styles are cached, and the
pedestrian crowd pauses while choosing a stop. The longer shadow range and post
processing add GPU work; no frame-rate improvement has been measured.

## Verification

Runtime and editor sources compile against the installed Unity 6000.3.10f1
assemblies with zero warnings or errors. The changed files pass `git diff --check`.
Play Mode and visual verification were unavailable because the desktop automation
connection was unavailable.

Play Mode checks still to run:

- Confirm the opening altitude is 480 m; pan, scroll, and use Home.
- Select different stops via the list and map; confirm Depart spawns on a rail.
- Confirm the timer starts at departure and Escape returns to selection.
- Drive forward/reverse, orbit near buildings, zoom, derail, and restart with R.
- Inspect road intersections and rail joins at close range and the overview.
- Compare CPU/GPU frame time in the Profiler at the same stop and camera height.

Both interfaces share a safe-area UI scale that fits a 960 × 640 minimum logical canvas to the window. The stop list scrolls independently and map pins avoid overlap.

Non-stop labels mount on nearby building walls at a fixed size and orientation. Raised letter layers and depth-tested font rendering replace billboards. Source sign colours are used when tagged; otherwise a muted name-stable palette is used. This is layered lettering, not solid extruded glyph geometry. Labels without a suitable wall are hidden.
