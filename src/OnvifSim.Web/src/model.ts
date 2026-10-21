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

export const objectTypes = ['tree.pine', 'tree.oak', 'house', 'rock', 'box'] as const;
export type ObjectType = (typeof objectTypes)[number];

export interface WorldObject {
  id: string;
  type: ObjectType;
  position: Vec3;
  rotationY: number;
  scale: Vec3;
  color: string | null;
  color2: string | null;
  seed: number;
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
}

// Loop: from the last point back to the first; reverse: back the same way.
export type WalkerReturn = 'loop' | 'reverse';

export interface Walker {
  id: string;
  route: GroundPoint[];
  speed: number;
  return: WalkerReturn;
  height: number;
  color: string | null;
  color2: string | null;
  // Whether cameras' analytics report this person.
  detectable: boolean;
}

export type CameraKind = 'fixed' | 'ptz';

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

export interface CameraModel {
  id: string;
  name: string;
  kind: CameraKind;
  position: Vec3;
  heading: number;
  pitch: number;
  roll: number;
  hFov: number;
  angleGrid: boolean;
  // The camera's colour, its body's and its field of view's; null or absent takes its kind's (kindColor).
  color?: string | null;
  // rotate: the picture turned clockwise, 0, 90, 180 or 270 degrees.
  video: { width: number; height: number; fps: number; quality: number; rotate: number };
  h264: { width: number; height: number; fps: number; bitrateKbps: number; govLength: number };
  imaging: { brightness: number; contrast: number; colorSaturation: number };
  // What the metadata track of the camera's RTSP streams carries: people as boxes, the PTZ position.
  metadata: { analytics: boolean; ptzStatus: boolean };
  ptz: PtzSettings | null;
  onvifPort: number;
  users: { username: string; password: string }[];
  // How far back the camera's own recording (Profile G replay) reaches, in hours.
  recordingRetentionHours: number;
}

// A camera's colour when it has none of its own (CameraMesh.ColorOf).
export const kindColor = (kind: CameraKind) => (kind === 'ptz' ? '#ff9f1c' : '#3aa0ff');

export interface World {
  version: number;
  terrain: Terrain;
  environment: Environment;
  objects: WorldObject[];
  roads: Road[];
  walkers: Walker[];
  cameras: CameraModel[];
}

// What the server says about the running services.
export interface ServerStatus {
  rtspPort: number;
  h264: boolean;
  // Null for a camera whose port could not be opened.
  onvifPorts: Partial<Record<string, number | null>>;
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
  return { size: terrain.size, resolution: terrain.resolution, heights: btoa(binary) };
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

// Where a walker is at a moment, as WalkerMotion.cs works it out: the route walked at a steady pace
// from the start of the clock, round in a loop or there and back.
export function walkerPoseAt(walker: Walker, timeMs: number): WalkerPose {
  const route = walker.route;
  if (route.length === 0) return { x: 0, z: 0, heading: 0 };
  const path = route.length < 2 ? route : walker.return === 'loop' ? [...route, route[0]] : [...route, ...route.slice(0, -1).reverse()];
  const segments = path.slice(1).map((to, i) => ({ from: path[i], to, length: Math.hypot(to.x - path[i].x, to.z - path[i].z) }));
  const total = segments.reduce((sum, s) => sum + s.length, 0);
  if (total === 0 || walker.speed <= 0) return { ...route[0], heading: route.length > 1 ? headingOf(route[0], route[1]) : 0 };
  let along = ((timeMs / 1000) * walker.speed) % total;
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

function headingOf(from: GroundPoint, to: GroundPoint): number {
  return (Math.atan2(to.x - from.x, -(to.z - from.z)) * 180) / Math.PI;
}
