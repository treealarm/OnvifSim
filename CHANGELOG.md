# Changelog

## 0.5.0 (2026-11-12)

- **The demo's landmarks stand in the world.** The Maryland world marks the ten calibration
  landmarks of ta_vms's demo — five for the hall pair, five for the yard pair, the points the demo's
  map puts a landmark on — with a half-metre block of the landmark's own colour (`lm-hall-l1` …
  `lm-road-l5`). A landmark on the map and the thing a camera's picture shows are one point, which
  is what an operator aims and calibrates a camera by; a made-up world has to be told, since it has
  no corners and hatches of its own. The blocks are as small as the bench world's, so a person
  walking over one is still a person.
- **A prism: any footprint extruded to a height.** A world object of type `prism` takes a ring of
  points and draws walls, a roof and a floor from both sides — a building of any shape is one object
  rather than a box. `openings` cut a wall into panels and the band above them, which is how a doorway
  is made; `texture: "tiles"` lays the top out as a tiled floor. Footprints are normalised, checked
  (a crossing or a nearly empty one is refused) and triangulated by ear clipping; the editor draws one
  point by point and edits its openings in the object's card. Imported objects keep their `sourceOsm`.
- **A raster map under the ground.** `terrain.map` names a picture drawn under the terrain, sampled
  through per-vertex UVs with perspective-correct interpolation, bilinear filtering and fog after the
  sample; the terrain chunk is unlit so a map reads as a map. The picture is a local cache under the
  data directory (`maps/<name>.png`), fetched once per world from any `{z}/{x}/{y}` tile provider
  (`ONVIFSIM_MAP_TILE_URL`) and never part of a world file. Every output pixel is a world coordinate
  taken back to WGS84 and then to a tile, so an anchor, a true north and any terrain size come out
  right; a map missing, mismatched or broken is not drawn, and the world stays valid. The editor
  bakes it, shows it and switches it off.
- **Real objects out of OpenStreetMap.** The editor's Map → Pick from OSM tool reads the world's
  whole extent from Overpass once — `nwr` for buildings, parking and water (multipolygons included),
  `way` for roads, `node` for trees — and keeps it on this machine beside the ground map's cache
  (`osm/<world>.json`), so every click answers at once instead of waiting on a public server that
  takes tens of seconds. A click on an object asks about the object's own place, not the ground behind
  its roof; the footprint that holds the click comes before the road or tree beside it; the picked
  object is outlined before it is built. Buildings with a courtyard are refused with a message.
- **The real world as one mode.** A single **OpenStreetMap** switch in the Map palette turns the open
  map on and reads whatever is missing — the picture under the ground and the objects a click can
  build — both kept on this machine. Switched off, the ground is plain and the camera frames show
  grass; what was read stays cached for the next time.
- **The Maryland world is the real campus.** The building is Biomolecular Sciences (OSM way
  24303349): its 20-corner footprint at 11.78 m, with a doorway on the north wall. The yard is Lot T,
  the parking lot the person crosses; the car drives the real ring of Regents, Paint Branch and
  Technology Drive; the neighbours, the lots and the pond are their OSM footprints. The ground carries
  the campus map, and the demo's cameras, Room 101 and the walker's route moved with the building.
- **A doorway in the Maryland world, and a person who walks out of it.** The building's north wall
  has a real opening (two plates and a lintel), and the walker's route leaves Room 101 through the
  hall and the doorway, walks out into the yard, turns right and follows the ring road for a short
  stretch, then comes back in. Both pairs follow that person, each from its own side: the hall
  master stands south of the room on the line out to the doorway, and the yard pair works the walk
  outside — its master on a pole between the doorway and the road, and its slave on a pole of its
  own between the door and that master, so it holds the doorway at one side of its picture and the
  yard the person turns into at the other. The walk turns back before the master's picture runs
  out, so the whole of it stays inside it. `data/worlds/maryland.json` is rebuilt from the builder.
- **Surfaces carry a texture.** A box is no longer one flat colour: its faces are cut into cells a
  few metres across and shaded slightly differently, with the bottom a touch darker, so a wall reads
  as a wall. A road darkens towards its edges and shifts from metre to metre; steep terrain keeps its
  rock and its dry patches. A new object type, **floor**, lays its top out in tiles with seams and a
  gentle gradient from the middle outwards; the Maryland building's floor is one. Every pattern is
  drawn from the colours — no pictures, nothing to load — and follows an object's seed and a cell's
  place, so two builds of a world draw the same thing.
- **Texture is a setting, and it is on by default.** An object's `texture` chooses "grain", "tiles"
  or "none"; a terrain's, "grass" or "none"; a road's, "asphalt" or "none". Left out, a surface keeps
  its type's own: a box and a building's walls grained, a floor tiled, the terrain and roads
  textured, and a tree, a rock or a car plain — its shape tells it apart. The editor offers the
  choice on an object, a road and the terrain, so a world can be made flat-coloured where a pattern
  is not wanted.
- **The editor's view prism is a grid.** Each camera's view is cast as rays across its picture, and
  the grid is drawn where they land on the ground or an object: the patch a camera covers is visible
  even when part of its picture looks over the horizon. Rays that meet nothing stop at the "view
  reaches" length, now found in the camera views section of the world inspector.
- **A digital input's logical state follows the Device IO spec.** A camera that supports
  `SetDigitalInputConfigurations` — OnvifSim does — reports `true` for the active state and `false`
  for the idle one, and `IdleState` names the circuit condition the input rests in. Changing that
  idle state now flips the value the input reports, as a real camera does. The editor's "reads 1/0"
  is the same state, so it no longer disagrees with the ONVIF events.
- **Device IO wire details.** `GetRelayOutputOptions` writes its `DelayTimes` as the schema's
  `tt:FloatList` — space-separated seconds — not as durations; the event topic set declares a
  relay's `LogicalState` as `tt:RelayLogicalState` and an input's as `xs:boolean`;
  `GetDigitalInputConfigurationOptions` without a token returns the device's generic options; and
  the Device IO faults carry their spec subcodes (`ter:RelayToken`, `ter:ModeError`). A bistable
  relay ignores a malformed `DelayTime`, as the spec says it may.

## 0.4.0 (2026-11-05)

- **A world with a place on Earth.** The world file now carries a geographic anchor (`geo`: its
  latitude, longitude, elevation and which way is true north), so a camera's ONVIF
  `GetGeoLocation` answers with where it really stands and which way it faces. `SetGeoLocation`
  moves a fixed camera in the world, and a camera mounted on a moving car reports the car's position
  and heading at that moment — the way a client placing cameras on a map checks them.
- **Relay outputs and digital inputs.** A camera carries ONVIF relay outputs and digital inputs
  (`relayOutputs` and `digitalInputs` in its world-file entry), and a wire joins a relay of one
  camera to an input or a lamp of any other. A wire carries the contact: a closed contact pulls its
  input to 0, and a closed relay lights the lamps it drives. The editor edits them in the camera's
  **Devices** section and in the **Wiring** window; the server switches a relay through the same
  controller ONVIF does (`PUT /api/cameras/{id}/io`, `PUT /api/cameras/{id}/relays/{token}`).
- **ONVIF DeviceIO.** The Device service answers `GetRelayOutputs`, `SetRelayOutputState` and
  `SetRelayOutputSettings` and advertises their counts in its capabilities; the DeviceIO service
  answers `GetDigitalInputs`, `GetDigitalInputConfigurationOptions`, `SetDigitalInputConfigurations`
  and `GetRelayOutputOptions`. Relays and inputs report on `tns1:Device/Trigger/Relay` and
  `tns1:Device/Trigger/DigitalInput`: `Initialized` when a pull point opens, `Changed` when a
  contact, an input or a relay's idle state moves — on the relay's camera and on the camera whose
  input a wire drives. `GetEventProperties` describes both topics.
- **A relay's coil and its contact are told apart.** `Active` is the logical state that
  `SetRelayOutputState` and the editor's switch set; the contact follows from it and the relay's
  `IdleState` polarity, and a monostable relay de-energises after its `DelayTime`, its contact
  returning to the idle state on its own. As ONVIF's `RelayOutput` carries settings only,
  `GetRelayOutputs` reports both states as `ons:LogicalState` and `ons:PhysicalState` extensions,
  and the relay event carries the physical state beside the standard logical one.
- **Lamps and a wiring window.** A lamp is an object of the world — a post with a shade that glows
  in a colour of its own — put down from the palette and driven by relays, either normally open or
  normally closed. The **Wiring** window lists the world's relays on the left and the inputs and
  lamps on the right: pick the relay and tick what it drives. A camera's picture can caption the
  state of its relays and inputs (**States on the picture**), and its devices now come before its
  streams in the inspector.
- **Cameras in the editor.** Each camera's view is drawn as the prism its eye and the four corners
  of its picture make, ending on the first thing in its way — the ground, a floor, a wall — so the
  base shows what the camera actually sees. Every camera's view takes a palette colour of its own
  (its own colour still wins), so overlapping views stay apart, and unselected ones are drawn
  fainter. **Find** in an inspector flies the editor to what is being edited.
- **A demo world, Maryland.** `data/worlds/maryland.json` builds the A.V. Williams building as one
  hall, with a fixed master and a PTZ camera facing each other across it, a person walking between
  them, a ring road with a car and a camera mounted on it, and relays wired to a lamp and a sensor.
  **Cutaway** clips the objects and devices above 2.5 m so a room can be seen and edited from above,
  and a click above the cut plane passes through to what stands behind it. Offices join the object
  palette.
- The default village's road now carries a moving car as well as its walkers, so a fresh install
  shows both kinds of traffic sharing one road.

## 0.3.0 (2026-11-01)

- **Worlds you prepare and switch between.** The data directory now holds any number of named worlds
  (`worlds/<name>.json`), an `active-world.json` marker and a `trash/`. The editor's world manager
  creates an empty world, opens another one without a restart, moves worlds to the trash, restores
  them and empties the trash; an existing `world.json` becomes `worlds/default.json` on the first
  start. Switching clears the editor's undo history and asks whether to clear or keep the cameras'
  recordings; the server requires that choice when the outgoing world has recordings. The library is
  also available over HTTP (`/api/worlds`, `/api/trash`) and as `worlds` and `world-switched` events
  on `/api/events`.
- **A contents tree** on the left lists every camera, object, road, person and moving car and selects
  them with a click; objects are grouped by type, with their ids inside. A person now also has a
  larger invisible pick target in the 3D view, so they can be clicked there too.
- **A grouped toolbar**: Select, Add, Draw and Terrain choose which tools the second row offers; the
  active tool is marked with a dot, so changing the palette does not change what the viewport does.
- **Cars with licence plates**, for testing LPR clients: a new `car` object draws a low-poly car with
  real glyph geometry on its plates (5×7 bitmap font, no textures), and the inspector edits the plate
  text. The default village now includes a car in view of its fixed camera.
- **Cars that drive a route.** A `vehicle` is a moving car with a route, speed, loop-or-back
  behaviour, body size, paint and plate, drawn from the same mesh as a parked car. The editor's
  **Car route** tool draws its route point by point, and the car turns to follow it; its position
  follows from the clock like a walker's, so every stream, snapshot and replay agrees. Cameras'
  analytics box it with the class `Vehicle` in the ONVIF metadata track and raise motion while it is
  in view; `vehicles` is a list in the world file, with `/api/vehicles/{id}` and a ground-truth
  `/api/vehicles/{id}/pose`. The route rule is shared with walkers (`RouteMotion`). A parked car's
  panel has **Make it drive**, which turns it into a moving car on a straight route along its heading
  and keeps its paint, size and plate; the conversion is one Undo step.
- **Closed roads and routes.** A road's panel has **Closed loop**: the last point joins the first and
  the strip runs round as a ring (`closed` in the world file). People and moving cars have the same
  choice on their routes, next to the route's points: closed, they go round; open, they turn round at
  the last point and come back, which is the existing `return` loop/reverse setting with a clearer
  control.
- **Ground colour.** The world's terrain has a **Ground** colour (`terrain.color`): the grass takes
  it in both the editor and the cameras, while steep slopes still turn to rock.
- **Documentation**: the README is now the complete English documentation; the outdated
  `docs/PLAN.md` was removed (git history keeps it), and the README gained screenshots of the world
  manager, the contents tree, the car's plate and a moving car's route.
- **Streams are ONVIF profiles, and can be added and removed.** A camera starts with `Profile_1`
  (JPEG) and `Profile_2` (H.264); the camera panel adds and removes streams, names them and edits
  each one's RTSP path and encoder settings. Encoder tokens follow the profile tokens, deleted
  streams answer `NoProfile`, and H.264 streams are hidden when ffmpeg is absent. The RTSP paths are
  unique across the world.
- **Editable camera addresses.** A camera can bind its ONVIF listener and RTSP port to one address
  and use an RTSP port of its own (`rtspPort`, 0 keeps the global `--rtsp-port`); WS-Discovery
  advertises the bound address. Validation refuses an invalid address, a bound camera without an
  explicit RTSP port, and a wildcard and a specific listener on the same port.
- **Changed:** a new camera keeps an hour of recording by default (`recordingRetentionHours` was 30
  days); existing worlds keep the value they store.
- Profile G: every camera records on its own "SD card" and offers it through the Recording, Search
  and Replay services, with ONVIF replay over RTSP (`rtsp://<host>:8554/<camera id>/replay`: clock
  ranges, `Rate-Control: no`, the replay RTP header extension). The past is drawn again rather than
  stored, PTZ heads where they pointed then; a recording reaches back `recordingRetentionHours` (1
  hour by default). `PUT /api/cameras/{id}/network` cuts a camera off the network while it goes on
  recording, and `PUT /api/cameras/{id}/recording` stops and starts its recording, for testing how a
  VMS recovers what it missed.
- **Fixed:** the last frame an H.264 encoder produced was lost when it stopped.
- Replay starts at the key frame at or before the start asked for, as a real card does, not at a key
  frame made for exactly that moment: a client that stamps footage from where it asked for it rather
  than from the replay header's time is now off by up to a GOP, as it would be on a real camera.

- Ground truth for testing clients that calibrate cameras or aim one at a person:
  `GET /api/cameras/{id}/pose` (where a camera stands and looks, PTZ head included) and
  `GET /api/walkers/{id}/pose`; the boxes in `GET /api/cameras/{id}/analytics` say where on the
  ground each walker stands. The pose also gives the picture's own axes (`forward`, `right`, `up`),
  and `POST /api/cameras/{id}/ptz/aim` turns a head until a point of the world is in the middle of
  its picture.
- Cameras mounted any way up. A mount is a rigid body: a PTZ head pans about the mount's own up axis
  and tilts about its own right axis, so `roll: 180` hangs a camera upside down, `roll: 90` puts it
  on its side (pan sweeps up and down, tilt across) and `pitch` leans its pan axis. **Changed:** a PTZ
  camera's `pitch` used to be added to its tilt and its `roll` turned only the picture; a world with a
  PTZ camera whose pitch or roll is not 0 now looks elsewhere. Level mounts are unchanged.
- `video.rotate` (0, 90, 180, 270): the picture turned clockwise, as a camera rotates its image for
  an upside-down or corridor mount; both streams and the analytics boxes turn with it, and Media
  reports it as the video source's `Rotate`.
- `ptz.panReverse`, `ptz.tiltReverse`: ONVIF counts that axis the other way round (positions, steps
  and velocities), as cameras with a reversed control direction do; with both on, the PTZ
  configuration reports `PTControlDirection` `Reverse` ON.
- Cameras appear in each other's pictures: a pole, a bracket and the body turned the way the camera
  (or its head) looks, in the camera's own `color` or else orange for PTZ and blue for fixed
  (`environment.showCameras`, on by default).
  `environment.showFrustums` (off by default: no real camera sees them) also draws where each looks,
  as a wire frame in the same colour from the eye down to the ground it covers, ending at `environment.frustumLength`
  (40 m) where a ray meets no ground. Both cost about a millisecond of a 640×360 frame (`--bench`
  now prints them). The editor draws the same wire frame down to the ground instead of a 20 m
  pyramid, and the degree grid follows a mount that leans or lies on its side.

## 0.2.2 (2026-10-14)

- Windows: OnvifSim.exe and the MSI carry the camera icon (Installed apps, the Start menu entry, the
  installer's own dialogs).
- The RTSP server and the RTP payload formats are a library of their own in the solution,
  `src/OnvifSim.Rtsp`; nothing changes for clients except that the RTCP CNAME is now the session id.

## 0.2.1 (2026-10-13)

- Snap: an OnvifSim entry in the applications menu (and an icon in App Center) that opens the editor
  in the browser.

## 0.2.0 (2026-10-13)

- Analytics: every camera reports the people it sees in the ONVIF metadata track of its RTSP
  streams, boxes with object ids exact to the picture, people hidden behind houses and hills left out;
  optionally the PTZ position. Media and Media2 serve the metadata and analytics configurations, and
  the editor boxes the people over each camera's picture. Analytics can be turned off per camera,
  and a person can be left out of it.
- Events: pull point subscriptions with motion events, raised while a camera's analytics sees someone.

## 0.1.0 (2026-10-13)

First release: an ONVIF IP camera simulator.

- A low-poly world (terrain, trees, houses, rocks, roads, people walking their routes) drawn by a
  built-in software renderer; no GPU needed.
- Every camera is an ONVIF device on its own port: Device, Media, Media2, PTZ and Imaging, WS-Security
  and HTTP Digest, WS-Discovery.
- RTSP for all cameras: Motion JPEG always, H.264 through ffmpeg when available; TCP and UDP.
- Fixed and PTZ cameras; PTZ heads move at their set speeds (AbsoluteMove, RelativeMove,
  ContinuousMove, presets, home) and the picture follows; an optional pan/tilt degree grid.
- A browser editor: place and move objects and cameras, sculpt the terrain, draw roads and routes,
  see each camera's picture, drive PTZ cameras, undo and redo; every change is live.
- Docker image (amd64, arm64), single-file binaries for Windows and Linux, MSI, snap.
