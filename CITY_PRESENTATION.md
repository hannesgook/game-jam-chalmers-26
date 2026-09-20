# Playing Spårvagn Rush

Open `Assets/Scenes/SampleScene.unity` and press Play.

The game opens with a 10.5-second, three-shot flight along the city's stored road
centre lines at up to 85 m/s. Road overlays remain hidden. Click Skip, or press
Space, Enter, or Escape, to reach the station selection screen. The intro plays
once per scene load; returning to station selection does not replay it.

## Station selection

- The initial overview is 480 metres high, angled at 55 degrees.
- Select a station on the map or in the scrollable list to fly closer and orbit it.
  The preview is approximately 95 metres above that station.
- Left-mouse dragging pans; A/D rotates; the wheel zooms. Manual input stops the orbit.
- Home restores the overview. Enter or Start run spawns at the selected station.

## Your run

Complete three deliveries within 120 seconds. Follow the blue route on the rails
and the orange arrow on the minimap. The information window names the destination,
shows route distance, and gives a direction. The destination has a tall beacon
and a boarding ring.

Slow below 9 km/h inside the ring for 1.25 seconds to board or unload. Pickups show
three passengers boarding and award 25 points with sound, particles, and a message.
Deliveries award 100 times the current combo. Reaching three deliveries wins the
run. Derailing or running out of time ends it; retry or choose another station.

W/S drives; A/D balances and chooses branches. When reversing, control input,
physical lean used for branch decisions, and the curvature sampled ahead of travel
all use the reverse direction. The body still faces along its own forward axis.
The last travel direction is retained at rest to avoid switching the controls while braking.

Right-mouse dragging orbits the driving camera. The wheel zooms and middle mouse
resets the orbit. Escape returns to station selection.

## Interface and minimap

The old immediate-mode HUD is removed. The interface uses Unity Canvas, Text,
Button, ScrollRect, and CanvasScaler components: white information windows, dark
text, standard buttons, and no custom dark HUD skin. The canvas expands from a
960 by 640 reference and respects the screen safe area.

The minimap is a live, colour, orthographic camera covering 360 metres. Its camera
tracks the tram without smoothing, keeping the marked tram exactly at the centre.
A white-ringed blue tram marker shows heading; the separate orange arrow points
along the recommended route. The blue route, orange destination marker, station
name, and distance explain where to go. The 320 by 320 camera disables its own
shadows and post processing, and stops rendering during the intro and selection.

The rail network supplies connected routes to named stations. Disconnected jobs
are rejected. Routes refresh once per second. Lines with no other reachable
station show an explanation instead of assigning an impossible job.

## City presentation

One steel balance rail sits on a brown gravel bed with sloped shoulders, concrete
supports, and outer retaining edges. Generated road overlays are hidden, while
roads in the ground imagery remain. Building-mounted shop signs use layered raised
lettering and depth-tested rendering. Source sign colours are used when tagged;
otherwise signs use a muted palette. These are layered letters, not solid extruded
glyph meshes.

## Verification

Runtime and editor C# compilation: zero warnings and errors.
Compiled-assembly checks passed for connected routes, partial edges, same-edge
routes, junction routing, disconnected destinations, reverse A/D balance and actual
branch selection, neutral lean, and curvature look-ahead in reverse.
Changed source files pass the whitespace check.

Live Play Mode, shader/render appearance, the introductory flight, and the Canvas
layout still need visual verification: the desktop automation connection was unavailable.
The live minimap adds a render pass; no GPU performance improvement is claimed.
