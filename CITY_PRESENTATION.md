# Playing Spårvagn Rush

Open `Assets/Scenes/SampleScene.unity` and press Play.

A loading screen holds the camera until the city is genuinely ready: the whole
pedestrian crowd is built, deferred label mounting and the first render have
finished, and frame times have stayed inside target for 1.25 seconds. It gives up
waiting after 25 seconds so a slow machine still reaches the game. The screen
names what it is waiting for. The main scene renders behind it, warming materials,
and none of that counts as intro time.

The intro is then a 36-second, three-shot cinematic flight along the city's stored
road centre lines. Each 12-second shot eases in from where the previous one ended,
arcs sideways, rises and falls, and breathes the field of view between 57 and 62
degrees; the camera is damped rather than snapped between frames. Road overlays
remain hidden. Click Skip, or press Space, Enter, or Escape, to reach the station
selection screen; skipping is refused while startup work is still running. The
intro plays once per scene load; returning to station selection does not replay it.

## Station selection

- The initial overview is 480 metres high, angled at 55 degrees.
- Select a station on the map or in the list to fly closer and orbit it. The
  preview is approximately 95 metres above that station.
- The search box finds any named place in the city, not just stations: shop and
  building signs are collected from the generated map labels alongside the stops.
  Matching ignores case and accents. Clicking a result flies the camera down to it.
- Searching a shop also arms the nearest tram station, because a run always departs
  from a station. The panel names the place being shown and how far that stop is.
- Left-mouse dragging pans; A/D rotates; the wheel zooms. Manual input stops the orbit.
  Typing in the search box never rotates or launches the tram.
- Home restores the overview. Enter or Start run spawns at the selected station.
- A control strip along the bottom edge spells out every one of these controls.

## Your run

Complete three deliveries within 120 seconds. Follow the blue route on the rails
and the orange arrow on the minimap. The information window names the destination,
shows route distance, and gives a direction. The destination has a tall beacon
and a boarding ring.

Slow below 9 km/h inside the ring for 1.25 seconds to board or unload. Pickups show
three passengers boarding and award 25 points with sound, particles, and a message.
Deliveries award 100 times the current combo. Reaching three deliveries wins the
run. Derailing, running somebody down, or running out of time ends it; retry or
choose another station. Anybody the tram meets while it is crawling or stopped is
boarding, not being hit.

W/S drives; A/D balances and chooses branches, and a control strip along the
bottom edge names them on screen.

Balance is fully manual. The tram is an inverted pendulum on a single rail, and
nothing rights it on the driver's behalf: there is no correction near upright, no
share of the curve taken off them, and no lean angle the controls settle at by
themselves. Any lean, however small, keeps growing until it is answered, and a
curve throws the tram towards the outside on top of that. A/D shifts weight at a
constant rate, so holding a lean steady means feeding in exactly as much
counter-weight as gravity is taking away. Damping only slows how fast a lean
changes; it never pushes back towards upright.

Leaning tilts the tram and nothing else. It rolls about the underside of its own
collider, which is measured off the real hull on the first frame rather than
guessed at, so the bottom of the body stays on the rail at every angle and the
tram never slides sideways off the track.

Lean past 15 degrees and the next junction that offers a choice takes that side.
The threshold is deliberately high: at the old 2.5 degrees the wobble of holding
the tram up was enough to throw a switch, so branches picked themselves.

Past 60 degrees the tram is gone, and it gets there fast. The pendulum runs at the
physically honest sensitivity with light damping, so an unanswered lean reaches
the limit in roughly three seconds. At 60 degrees gravity pulls at about
270 deg/s squared against 360 of full counter-input, so the edge is recoverable,
but only just; overcorrecting into the opposite lean is as likely to finish the
run as the lean you started with.

The wreck is then thrown hard the way it was already falling: up to 40 m/s
sideways, a 15 m/s lift, and 520 deg/s of tumble, all carrying its forward
momentum with it. The throw scales with how fast it was going, so losing the tram
at speed flings it clean across the street and usually into a building, while a
slow topple just falls over.

When reversing, control input, the physical lean used for branch decisions, and
the curvature sampled ahead of travel all use the reverse direction. The body
still faces along its own forward axis. The last travel direction is retained at
rest to avoid switching the controls while braking.

Right-mouse dragging orbits the driving camera. The wheel zooms and middle mouse
resets the orbit. Escape returns to station selection.

## Interface and minimap

The interface uses Unity Canvas, Text, Button, ScrollRect and CanvasScaler
components: white information windows, dark text, standard buttons, no custom
skin. The canvas expands from a 960 by 640 reference and respects the screen safe
area, so every window has at least that much room whatever the monitor; the whole
layout is laid out to fit inside it, and a minimised window reporting a zero-sized
screen is ignored rather than sending the interface to NaN. Every label is free to
shrink to 9 point before it would be cut off, so text gives way before layout does.

Driving shows one panel, not three. The objective, the direction and distance, the
score and clocks, the boarding bar, the speed and the balance meter all live in a
single window at the top left, with the minimap opposite and one control strip
along the bottom.

The control strips carry six key/action pairs each, spread evenly so they fit any
window width: a filled accent plate with the key in white, and what it does
underneath. The station selector is one column — name, search box, result count,
the scrolling list, then what you are looking at and Start run — with its own
strip along the bottom.

The minimap is a live, colour, orthographic camera covering 360 metres. Its camera
tracks the tram without smoothing, keeping the marked tram exactly at the centre.
A white-ringed blue tram marker shows heading; the separate orange arrow points
along the recommended route. The blue route, orange destination marker, station
name, and distance explain where to go. The 320 by 320 camera disables its own
shadows and post processing, and stops rendering during the intro and selection.

The rail network supplies connected routes to named stations. Disconnected jobs
are rejected. Routes refresh once per second. Lines with no other reachable
station show an explanation instead of assigning an impossible job.

## Pedestrians

The city carries 220 people. They walk at 2.4 to 3.8 m/s and break into a run at
1.7 times that once they notice the tram. Everybody shares one palette of 14
outfits and 6 skin tones rather than owning their own materials, so a crowd this
size can still batch instead of costing a draw call each.

Anybody within 90 metres turns and walks the tram down, taking the straightest
line they can and fanning out around whatever blocks it. They will step onto the
rails on purpose — that is the whole threat — while water and buildings still stop
them. Once somebody has locked on they stay locked on until they are recycled, so
nobody snaps back onto the street graph behind you.

The crowd is built behind the loading screen and spread over the whole city, so
nobody is watching when it appears. Replacements are placed 90 to 200 metres out,
near enough to reach you but only ever where the camera cannot see: off screen, or
behind a building. The overhead minimap deliberately does not count as a view,
because 360 metres across a few hundred pixels puts a person under one pixel.
A person is recycled once they are beyond 300 metres and out of sight, and
somebody the tram knocks over is recycled as soon as nobody is looking.

## The horn

Space sounds the horn, on a 1.1 second cooldown. Everybody inside a corridor
32 metres ahead of the tram and 7.5 metres either side is thrown clear: towards
whichever pavement they are already nearest, so the horn opens the rails instead
of shoving the whole street one way. The corridor follows the direction of travel,
so it works in reverse too.

Somebody thrown stops being a hazard the moment they leave the ground — their
collider goes off on launch — and they fly a plain ballistic arc, tumbling, until
they land. The horn moves people, it does not kill them: they pick themselves up
where they came down, re-anchor to whichever street they landed beside on the side
they landed on, and walk on. They will not lock back onto the tram for six
seconds, so a honk buys real room rather than a moment.

## Hitting somebody

Only a tram with speed behind it runs anybody down. Below 3 m/s a person who
reaches the tram is boarding it instead: they are taken out of the world and
placed somewhere out of sight on the crowd manager's next sweep. That threshold
sits just above the 2.5 m/s the game already counts as stopped at a station, so
there is no band where the tram is slow enough to pick passengers up but fast
enough to be killed by one walking towards it. Hunters converging on a tram
waiting at a stop therefore read as passengers boarding, which is what they are.

Above that speed, touching a pedestrian ends the run on the spot. The tram stops where it is, the
game freezes at `timeScale` zero, and a camera orbits the impact on unscaled time:
it opens tight on the side the tram came in on and eases out over five seconds,
pulling in if a wall would come between it and the crash. The driving HUD and the
minimap clear off for the first 2.6 seconds so the shot has the screen, then the
result window opens over it reading "You hit a pedestrian". Retry, Escape and the
station buttons all restore the time scale and hand the camera back.

There is no longer a cry when somebody is hit; the sound and the four recorded
clips the prefab pointed at are gone.

Walking is smoothed so nobody teleports. The lateral pavement offset is walked out
rather than switched between frames, and a hard per-frame travel cap turns the
swing of the pavement line around a junction into a walk round the corner. Where a
building or the water juts into a stretch, the walker steps in toward the kerb and
drifts back out once it is behind them; only a genuinely closed street turns
somebody around, and turning mirrors every offset so they stay where they are.
Placement tries both pavements and narrows toward the kerb before giving up.
Building footprints now count as unwalkable, not just the rails and the water.

## City presentation

One steel balance rail sits on a brown gravel bed with sloped shoulders, concrete
supports, and outer retaining edges. Generated road overlays are hidden, while
roads in the ground imagery remain. Building-mounted shop signs use layered raised
lettering and depth-tested rendering. Source sign colours are used when tagged;
otherwise signs use a muted palette. These are layered letters, not solid extruded
glyph meshes.

## Verification

Runtime and editor C# compilation: zero warnings and errors.

The routing and balance checks recorded here previously (connected routes, partial
edges, same-edge routes, junction routing, disconnected destinations, reverse A/D
balance and branch selection, neutral lean, curvature look-ahead in reverse) predate
the current balance and pedestrian changes and have not been re-run against them.
The neutral-lean check in particular assumed the stability assist that has since
been removed.
There is no automated test suite in the project to re-run them with.

Live Play Mode, shader/render appearance, the introductory flight, the pedestrian
smoothing and hunting, the horn and the launches it causes, the crash camera, the
derail throw, the measured lean pivot, and the Canvas layout still need verification: none of this has been run in the editor.
The balance figures above are derived from the equations in `TramController`, not
measured in play, and the frame cost of 220 people has not been measured either.
The live minimap adds a render pass; no GPU performance improvement is claimed.
