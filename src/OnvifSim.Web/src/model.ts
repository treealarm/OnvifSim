// The world as the server stores it (World/WorldModel.cs), with the terrain heights decoded. Coordinates
// follow three.js: X east, Y up, Z south, metres; object and camera Y is the height above the ground.

export interface Vec3 {
  x: number;
  y: number;
  z: number;
}

export interface Terrain {
  size: number;
  resolution: number;
  // (resolution + 1)^2 samples, row-major from -X,-Z.
  heights: Float32Array;
  // Grass colour; steep slopes still turn to rock.
  color: string;
}

// Where the world sits on the Earth. North is the true bearing of the world's -Z direction, degrees
// clockwise from true north; 0 means -Z is north.
export interface Geo {
  latitude: number;
  longitude: number;
  elevation: number;
  north: number;
}

export interface Environment {
  sunAzimuth: number;
  sunElevation: number;
  skyZenith: string;
  skyHorizon: string;
  fogStart: number;
  fogEnd: number;
  // Whether every camera's picture shows the other cameras, and also where they look (a wire frame).
  showCameras: boolean;
  showFrustums: boolean;
  // How far a field of view reaches where it meets no ground, metres.
  frustumLength: number;
}

export const objectTypes = ['tree.pine', 'tree.oak', 'house', 'office', 'rock', 'box', 'car'] as const;
export type ObjectType = (typeof objectTypes)[number];

export const objectTypeNames: Record<ObjectType, string> = {
  'tree.pine': 'Pine',
  'tree.oak': 'Oak',
  house: 'House',
  office: 'Office',
  rock: 'Rock',
  box: 'Box',
  car: 'Car',
};

export interface WorldObject {
  id: string;
  type: ObjectType;
  position: Vec3;
  rotationY: number;
  scale: Vec3;
  color: string | null;
  color2: string | null;
  seed: number;
  // A car's licence plate: absent or null keeps the default, an empty string draws a blank plate.
  plate?: string | null;
}

// A spot on the ground; the height comes from the terrain.
export interface GroundPoint {
  x: number;
  z: number;
}

export interface Road {
  id: string;
  points: GroundPoint[];
  width: number;
  color: string | null;
  // Whether the last point joins the first, making a ring.
  closed: boolean;
}

// Loop: from the last point back to the first; reverse: back the same way.
export type RouteReturn = 'loop' | 'reverse';

export interface Walker {
  id: string;
  route: GroundPoint[];
  speed: number;
  return: RouteReturn;
  height: number;
  color: string | null;
  color2: string | null;
  // Whether cameras' analytics report this person.
  detectable: boolean;
}

export interface Vehicle {
  id: string;
  route: GroundPoint[];
  speed: number;
  return: RouteReturn;
  // Body size: width across, height up, length along the way it faces.
  width: number;
  height: number;
  length: number;
  // Paint, and the plate's background.
  color: string | null;
  color2: string | null;
  plate?: string | null;
  // Whether cameras' analytics report this car.
  detectable: boolean;
}

export type CameraKind = 'fixed' | 'ptz';

export type StreamCodec = 'jpeg' | 'h264';

export interface VideoSettings {
  width: number;
  height: number;
  fps: number;
  quality: number;
  rotate: number;
}

export interface H264Settings {
  width: number;
  height: number;
  fps: number;
  bitrateKbps: number;
  govLength: number;
}

// One video stream of a camera, which is also its ONVIF profile. The encoder token is derived from the
// profile token. Source rotation stays on the camera (video.rotate), not per stream.
export interface StreamModel {
  token: string;
  name: string;
  codec: StreamCodec;
  path: string;
  video?: VideoSettings | null;
  h264?: H264Settings | null;
}

export interface PtzPosition {
  pan: number;
  tilt: number;
  zoom: number;
}

export interface PtzSettings {
  panMin: number;
  panMax: number;
  tiltMin: number;
  tiltMax: number;
  fovWide: number;
  fovTele: number;
  panSpeed: number;
  tiltSpeed: number;
  zoomSpeed: number;
  // ONVIF counts the axis the other way round; the head and its degrees do not change.
  panReverse: boolean;
  tiltReverse: boolean;
  pan: number;
  tilt: number;
  zoom: number;
  home: PtzPosition;
  presets: { token: string; name: string; position: PtzPosition }[];
}

// A relay output: a switch with a lamp, whose state the editor or ONVIF sets and a wire may pass on.
export type RelayMode = 'bistable' | 'monostable';

// The electrical state a relay sits in when nothing drives it; closed carries current (ONVIF: active).
export type IdleState = 'closed' | 'open';

export interface RelayOutput {
  // URL-safe and unique within the camera: it addresses the relay in the API.
  token: string;
  name: string;
  mode: RelayMode;
  // Seconds a monostable relay stays switched on.
  delaySeconds: number;
  idleState: IdleState;
  // The live state, which travels as ONVIF Device/Trigger events.
  active: boolean;
  // Offset from the camera: x to its right, z behind it, y above the ground there — as on the server.
  position: Vec3;
}

// A digital input: it has no state of its own, a wire from a relay output drives it.
export interface DigitalInput {
  token: string;
  name: string;
  idleState: IdleState;
  position: Vec3;
}

// A lamp a relay drives: an actuator standing in the world, not a part of a camera. It has no ONVIF
// service of its own: it lights while a linked relay is closed, or, wired normally closed, while none
// is.
export interface Lamp {
  id: string;
  name: string;
  normallyClosed: boolean;
  // A place in the world: x east, z south, y above the ground there.
  position: Vec3;
}

// A wire runs from a relay output of one camera to one sink — a digital input of any camera or a lamp
// of the world. Exactly one of the two sinks is set.
export interface Wire {
  relayCameraId: string;
  relayToken: string;
  inputCameraId?: string | null;
  inputToken?: string | null;
  lampId?: string | null;
}

// A camera's devices and the wires from its relays, as the editor edits them at once. The field names
// are the server's: PUT /api/cameras/{id}/io binds relayOutputs, digitalInputs and wires.
export interface CameraIo {
  relayOutputs: RelayOutput[];
  digitalInputs: DigitalInput[];
  wires: Wire[];
}

export interface CameraModel {
  id: string;
  name: string;
  kind: CameraKind;
  // A camera bolted to a car: position, heading, pitch and roll are then the mount's offset and
  // orientation in the car's own frame (X right, Y up, Z backwards), not a place on the ground.
  mountedOnVehicleId?: string | null;
  position: Vec3;
  heading: number;
  pitch: number;
  roll: number;
  hFov: number;
  angleGrid: boolean;
  // The camera's colour, its body's and its field of view's; null or absent takes its kind's (kindColor).
  color?: string | null;
  // rotate: the picture turned clockwise, 0, 90, 180 or 270 degrees.
  video: VideoSettings;
  h264: H264Settings;
  imaging: { brightness: number; contrast: number; colorSaturation: number };
  // What the metadata track of the camera's RTSP streams carries: people as boxes, the PTZ position.
  metadata: { analytics: boolean; ptzStatus: boolean };
  ptz: PtzSettings | null;
  // The camera's relay outputs and digital inputs, with the lamps and sensor blocks that stand for them.
  relayOutputs: RelayOutput[];
  digitalInputs: DigitalInput[];
  onvifPort: number;
  // The address the camera listens on; null or absent is every interface.
  bindAddress?: string | null;
  // The camera's RTSP port; 0 uses the simulator's global port.
  rtspPort: number;
  // The camera's streams. Absent keeps the legacy pair built from video and h264.
  streams?: StreamModel[] | null;
  users: { username: string; password: string }[];
  // How far back the camera's own recording (Profile G replay) reaches, in hours.
  recordingRetentionHours: number;
}

// The streams a camera actually serves: its list, or the legacy Profile_1/Profile_2 pair.
export function effectiveStreams(camera: CameraModel): StreamModel[] {
  if (camera.streams) return camera.streams;
  return [
    { token: 'Profile_1', name: 'MainStream', codec: 'jpeg', path: camera.id, video: camera.video },
    { token: 'Profile_2', name: 'H264Stream', codec: 'h264', path: `${camera.id}/h264`, h264: camera.h264 },
  ];
}

export function streamVideo(camera: CameraModel, stream: StreamModel): VideoSettings {
  return stream.video ?? camera.video;
}

export function streamH264(camera: CameraModel, stream: StreamModel): H264Settings {
  return stream.h264 ?? camera.h264;
}

export function encoderTokenOf(stream: StreamModel): string {
  return `VideoEncoder_${stream.token.replace(/^Profile_/, '')}`;
}

// A camera's colour when it has none of its own (CameraMesh.ColorOf).
export const kindColor = (kind: CameraKind) => (kind === 'ptz' ? '#ff9f1c' : '#3aa0ff');

export interface World {
  version: number;
  geo: Geo;
  terrain: Terrain;
  environment: Environment;
  objects: WorldObject[];
  roads: Road[];
  walkers: Walker[];
  vehicles: Vehicle[];
  cameras: CameraModel[];
  // Lamps a relay drives: actuators standing in the world, not parts of a camera.
  lamps: Lamp[];
  wires: Wire[];
}

// What the server says about the running services.
export interface ServerStatus {
  rtspPort: number;
  // The port each camera's streams are served on; null when its listener could not be opened.
  rtspPorts: Partial<Record<string, number | null>>;
  h264: boolean;
  // Null for a camera whose port could not be opened.
  onvifPorts: Partial<Record<string, number | null>>;
}

// The worlds in the data directory, as GET /api/worlds and the "worlds" event carry them.
export interface WorldsInfo {
  active: string;
  worlds: string[];
  trash: string[];
  recordings: { hasRecordings: boolean; cameraCount: number };
}

// A switch the server has already made, sent before the new world.
export interface WorldSwitchSignal {
  active: string;
  previous: string;
  clearRecordings: boolean;
  switchVersion: number;
}

// Where a PTZ head points right now, which may be ahead of the world while it moves.
export interface Head extends PtzPosition {
  moving: boolean;
}

// The world as JSON carries the heights as base64 of little-endian float32.
type WireWorld = Omit<World, 'terrain'> & { terrain: Omit<Terrain, 'heights'> & { heights: string } };

export function decodeWorld(json: string): World {
  const wire = JSON.parse(json) as WireWorld;
  return { ...wire, terrain: decodeTerrain(wire.terrain) };
}

function decodeTerrain(wire: WireWorld['terrain']): Terrain {
  const samples = (wire.resolution + 1) ** 2;
  const bytes = Uint8Array.from(atob(wire.heights), (c) => c.charCodeAt(0));
  const decoded = new Float32Array(bytes.buffer);
  // No heights is a flat terrain.
  return { ...wire, heights: decoded.length === samples ? decoded : new Float32Array(samples) };
}

export function encodeTerrain(terrain: Terrain) {
  const bytes = new Uint8Array(terrain.heights.buffer, terrain.heights.byteOffset, terrain.heights.byteLength);
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return { size: terrain.size, resolution: terrain.resolution, heights: btoa(binary), color: terrain.color };
}

// Height of the ground under (x, z), bilinear over the cell, clamped to the edge: as Terrain.cs does.
export function heightAt(terrain: Terrain, x: number, z: number): number {
  const { resolution, size, heights } = terrain;
  const cell = size / resolution;
  const fx = clamp((x + size / 2) / cell, 0, resolution);
  const fz = clamp((z + size / 2) / cell, 0, resolution);
  const ix = Math.min(Math.floor(fx), resolution - 1);
  const iz = Math.min(Math.floor(fz), resolution - 1);
  const tx = fx - ix;
  const tz = fz - iz;
  const row = resolution + 1;
  const sample = (cx: number, cz: number) => heights[cz * row + cx];
  const h0 = sample(ix, iz) + (sample(ix + 1, iz) - sample(ix, iz)) * tx;
  const h1 = sample(ix, iz + 1) + (sample(ix + 1, iz + 1) - sample(ix, iz + 1)) * tx;
  return h0 + (h1 - h0) * tz;
}

// The horizontal field of view at a zoom position: focal length goes geometrically from the wide to
// the tele end, as CameraPose.ZoomToHFov does.
export function zoomToHFov(zoom: number, fovWide: number, fovTele: number): number {
  const focal = (fov: number) => 1 / Math.tan((fov * Math.PI) / 360);
  const f = focal(fovWide) * Math.pow(focal(fovTele) / focal(fovWide), clamp(zoom, 0, 1));
  return (2 * Math.atan(1 / f) * 180) / Math.PI;
}

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

// The next free id like "o12" or "cam3" for a prefix.
export function nextId(prefix: string, taken: readonly string[]): string {
  let highest = 0;
  for (const id of taken) {
    const number = id.startsWith(prefix) ? Number(id.slice(prefix.length)) : NaN;
    if (Number.isInteger(number)) highest = Math.max(highest, number);
  }
  return `${prefix}${highest + 1}`;
}

export interface WalkerPose {
  x: number;
  z: number;
  // Degrees clockwise from north (-Z).
  heading: number;
}

// Where something travelling a route is at a moment, as RouteMotion.cs works it out: the route walked
// at a steady pace from the start of the clock, round in a loop or there and back.
export function routePoseAt(route: GroundPoint[], speed: number, returnMode: RouteReturn, timeMs: number): WalkerPose {
  if (route.length === 0) return { x: 0, z: 0, heading: 0 };
  const path = route.length < 2 ? route : returnMode === 'loop' ? [...route, route[0]] : [...route, ...route.slice(0, -1).reverse()];
  const segments = path.slice(1).map((to, i) => ({ from: path[i], to, length: Math.hypot(to.x - path[i].x, to.z - path[i].z) }));
  const total = segments.reduce((sum, s) => sum + s.length, 0);
  if (total === 0 || speed <= 0) return { ...route[0], heading: route.length > 1 ? headingOf(route[0], route[1]) : 0 };
  let along = ((timeMs / 1000) * speed) % total;
  for (const { from, to, length } of segments) {
    if (along > length) {
      along -= length;
      continue;
    }
    const t = length === 0 ? 0 : along / length;
    return { x: from.x + (to.x - from.x) * t, z: from.z + (to.z - from.z) * t, heading: headingOf(from, to) };
  }
  return { ...route[0], heading: 0 };
}

export function walkerPoseAt(walker: Walker, timeMs: number): WalkerPose {
  return routePoseAt(walker.route, walker.speed, walker.return, timeMs);
}

export function vehiclePoseAt(vehicle: Vehicle, timeMs: number): WalkerPose {
  return routePoseAt(vehicle.route, vehicle.speed, vehicle.return, timeMs);
}

// ---- Relay outputs, digital inputs and the wires between them ----

// Case-insensitive comparison for ids and tokens, as the server compares them.
export function sameId(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase();
}

// A camera's devices and the wires running from its relays, which the editor saves in one go.
export function cameraIoOf(world: World, camera: CameraModel): CameraIo {
  return {
    relayOutputs: camera.relayOutputs,
    digitalInputs: camera.digitalInputs,
    wires: world.wires.filter((wire) => sameId(wire.relayCameraId, camera.id)),
  };
}

// The wires driving an input or a lamp; a sink may be driven by several relays.
export function wiresToInput(world: World, cameraId: string, token: string): Wire[] {
  return world.wires.filter((wire) => sameId(wire.inputCameraId ?? '', cameraId) && sameId(wire.inputToken ?? '', token));
}

export function wiresToLamp(world: World, lampId: string): Wire[] {
  return world.wires.filter((wire) => sameId(wire.lampId ?? '', lampId));
}

// Whether the relay a wire runs from is closed (carrying current, ONVIF active).
export function relayClosed(world: World, wire: Wire): boolean {
  const relay = world.cameras
    .find((camera) => sameId(camera.id, wire.relayCameraId))
    ?.relayOutputs.find((output) => sameId(output.token, wire.relayToken));
  return relay?.active === true;
}

// Whether an input is on. A closed relay contact shunts the input to zero and an open one leaves it
// pulled up, so a wired input reads inverted: any closed relay gives false, all open gives true. Null
// when no wire drives it, which is not the same as a wire from a relay that is off.
export function inputActive(world: World, cameraId: string, token: string): boolean | null {
  const wires = wiresToInput(world, cameraId, token);
  return wires.length === 0 ? null : !wires.some((wire) => relayClosed(world, wire));
}

// Whether a lamp is lit. A normally-open lamp lights while any linked relay is closed; a normally
// closed one while none is. A lamp with no wire at all is off.
export function lampLit(world: World, lampId: string): boolean {
  const wires = wiresToLamp(world, lampId);
  if (wires.length === 0) return false;
  const closed = wires.some((wire) => relayClosed(world, wire));
  const lamp = world.lamps.find((device) => sameId(device.id, lampId));
  return closed !== (lamp?.normallyClosed ?? false);
}

// A wire from a relay to a sink, for the editor's drafts.
export function wireTo(relayCameraId: string, relayToken: string, sink: Sink): Wire {
  return sink.kind === 'input'
    ? { relayCameraId, relayToken, inputCameraId: sink.cameraId, inputToken: sink.token }
    : { relayCameraId, relayToken, lampId: sink.lampId };
}

// One end of a wire: a digital input with its camera, or a lamp of the world.
export type Sink = { kind: 'input'; cameraId: string; token: string } | { kind: 'lamp'; lampId: string };

export function sameSink(a: Wire, b: Sink): boolean {
  return b.kind === 'input'
    ? sameId(a.inputCameraId ?? '', b.cameraId) && sameId(a.inputToken ?? '', b.token)
    : sameId(a.lampId ?? '', b.lampId);
}

// The font on a plate has no lower case and only Latin letters, digits, spaces and hyphens.
export function plateText(text: string): string {
  return text
    .toUpperCase()
    .replace(/[^A-Z0-9 -]/g, '')
    .slice(0, 8);
}

function headingOf(from: GroundPoint, to: GroundPoint): number {
  return (Math.atan2(to.x - from.x, -(to.z - from.z)) * 180) / Math.PI;
}
