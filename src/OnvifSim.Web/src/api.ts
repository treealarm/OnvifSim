import { BufferAttribute, BufferGeometry } from 'three';
import {
  encodeTerrain,
  type CameraIo,
  type CameraModel,
  type Environment,
  type Geo,
  type GroundPoint,
  type Lamp,
  type PtzPosition,
  type PtzSettings,
  type Road,
  type Terrain,
  type Vehicle,
  type Walker,
  type WorldObject,
  type WorldsInfo,
} from './model';

// What PUT takes: anything left out gets the server's default.
export type ObjectDraft = Partial<WorldObject> & Pick<WorldObject, 'id'>;
export type CameraDraft = Omit<Partial<CameraModel>, 'ptz'> & Pick<CameraModel, 'id'> & { ptz?: Partial<PtzSettings> | null };

// What the ground map cache says it covers, and the credit it needs.
export interface MapMeta {
  south: number;
  west: number;
  north: number;
  east: number;
  zoom: number;
  attribution: string;
  fetchedAt: string;
}

// One real object under a click, as the server reads it out of OpenStreetMap: a footprint, a line or
// a point, ready to become something in this world.
export interface OsmCandidate {
  osmType: string;
  osmId: number;
  kind: string;
  name: string;
  points: GroundPoint[];
  height: number;
  width: number;
  hasHoles: boolean;
}

// Everything of the real world inside one world's own extent, read from OpenStreetMap once and kept
// on this machine. The rectangle is what it was read from: a moved anchor or a resized terrain makes
// it stale.
export interface OsmArea {
  south: number;
  west: number;
  north: number;
  east: number;
  fetchedAt: string;
  count: number;
}

// The server's editor API (Editor/EditorApi.cs and WorldLibraryApi.cs). Every call throws ApiError with
// the server's reason when the change is refused; code is set where the client branches on it.
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code?: string,
  ) {
    super(message);
  }
}

interface ProblemDetails {
  detail?: string;
  title?: string;
  code?: string;
}

async function send(method: string, path: string, body?: unknown): Promise<Response> {
  const response = await fetch(path, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw await errorOf(response);
  return response;
}

async function errorOf(response: Response): Promise<ApiError> {
  try {
    const problem = (await response.json()) as ProblemDetails;
    return new ApiError(problem.detail ?? problem.title ?? response.statusText, response.status, problem.code);
  } catch {
    return new ApiError(`${response.status} ${response.statusText}`, response.status);
  }
}

async function worldsOf(response: Response): Promise<WorldsInfo> {
  return (await response.json()) as WorldsInfo;
}

export const api = {
  putObject: (obj: ObjectDraft) => send('PUT', `/api/objects/${encodeURIComponent(obj.id)}`, obj),
  deleteObject: (id: string) => send('DELETE', `/api/objects/${encodeURIComponent(id)}`),
  putCamera: (camera: CameraDraft) =>
    send('PUT', `/api/cameras/${encodeURIComponent(camera.id)}`, camera),
  deleteCamera: (id: string) => send('DELETE', `/api/cameras/${encodeURIComponent(id)}`),
  // Relays, inputs and wires are saved apart from the camera: their state changes on its own, and a
  // wire reaches another camera's input.
  putCameraIo: (id: string, io: CameraIo) => send('PUT', `/api/cameras/${encodeURIComponent(id)}/io`, io),
  putRelay: (id: string, token: string, active: boolean) =>
    send('PUT', `/api/cameras/${encodeURIComponent(id)}/relays/${encodeURIComponent(token)}`, { active }),
  putRoad: (road: Partial<Road> & Pick<Road, 'id'>) => send('PUT', `/api/roads/${encodeURIComponent(road.id)}`, road),
  deleteRoad: (id: string) => send('DELETE', `/api/roads/${encodeURIComponent(id)}`),
  putWalker: (walker: Partial<Walker> & Pick<Walker, 'id'>) => send('PUT', `/api/walkers/${encodeURIComponent(walker.id)}`, walker),
  deleteWalker: (id: string) => send('DELETE', `/api/walkers/${encodeURIComponent(id)}`),
  putVehicle: (vehicle: Partial<Vehicle> & Pick<Vehicle, 'id'>) => send('PUT', `/api/vehicles/${encodeURIComponent(vehicle.id)}`, vehicle),
  deleteVehicle: (id: string) => send('DELETE', `/api/vehicles/${encodeURIComponent(id)}`),
  putLamp: (lamp: Partial<Lamp> & Pick<Lamp, 'id'>) => send('PUT', `/api/lamps/${encodeURIComponent(lamp.id)}`, lamp),
  deleteLamp: (id: string) => send('DELETE', `/api/lamps/${encodeURIComponent(id)}`),
  putTerrain: (terrain: Terrain) => send('PUT', '/api/terrain', encodeTerrain(terrain)),
  putEnvironment: (environment: Environment) => send('PUT', '/api/environment', environment),
  putGeo: (geo: Geo) => send('PUT', '/api/geo', geo),

  // The picture of the ground under the world: a local cache on the server, fetched once per world,
  // and what it covers. Baking asks the tile provider for it again.
  mapUrl: (name: string) => `/api/maps/${encodeURIComponent(name)}`,
  bakeMap: (name: string) => send('POST', '/api/terrain/map/bake', { name }).then((r) => r.json() as Promise<MapMeta>),

  // What of the real world lies under a point of this one, nearest first, out of what the world keeps.
  osmPick: (x: number, z: number) =>
    send('POST', '/api/osm/pick', { x, z }).then((r) => r.json() as Promise<{ candidates: OsmCandidate[] }>),

  // The real world around this world: what is already kept, and a reading of the whole extent.
  osmArea: () => send('GET', '/api/osm/area').then((r) => r.json() as Promise<{ area: OsmArea | null }>),
  fetchOsmArea: () => send('POST', '/api/osm/area').then((r) => r.json() as Promise<{ area: OsmArea | null }>),

  // Velocities are fractions of the top speed, -1..1; a move lasts a second unless renewed.
  ptzMove: (id: string, velocity: Partial<PtzPosition>) => send('POST', `/api/cameras/${encodeURIComponent(id)}/ptz/move`, velocity),
  ptzGoto: (id: string, position: Partial<PtzPosition>) => send('POST', `/api/cameras/${encodeURIComponent(id)}/ptz/goto`, position),
  ptzStop: (id: string) => send('POST', `/api/cameras/${encodeURIComponent(id)}/ptz/stop`),
  ptzHome: (id: string) => send('POST', `/api/cameras/${encodeURIComponent(id)}/ptz/home`),

  // Meshes of objects in their own coordinates, keyed by the ids sent.
  async meshes(objects: WorldObject[]): Promise<Map<string, BufferGeometry>> {
    const response = await send('POST', '/api/meshes', objects);
    const data = (await response.json()) as Record<string, MeshData>;
    return new Map(Object.entries(data).map(([id, mesh]) => [id, toGeometry(mesh)]));
  },

  // The world library: one active world, the others kept in the data directory.
  listWorlds: () => send('GET', '/api/worlds').then(worldsOf),
  createWorld: (name: string, clearRecordings?: boolean) =>
    send('POST', '/api/worlds', { name, clearRecordings }).then(worldsOf),
  activateWorld: (name: string, clearRecordings?: boolean) =>
    send('POST', `/api/worlds/${encodeURIComponent(name)}/activate`, { clearRecordings }).then(worldsOf),
  trashWorld: (name: string, clearRecordings?: boolean) =>
    send('POST', `/api/worlds/${encodeURIComponent(name)}/trash`, { clearRecordings }).then(worldsOf),
  restoreWorld: (name: string, destinationName?: string) =>
    send('POST', `/api/trash/${encodeURIComponent(name)}/restore`, { name: destinationName }).then(worldsOf),
  emptyTrash: () => send('DELETE', '/api/trash').then(worldsOf),
};

interface MeshData {
  positions: string;
  normals: string;
  colors: string;
  indices: string;
}

function toGeometry(mesh: MeshData): BufferGeometry {
  const geometry = new BufferGeometry();
  geometry.setAttribute('position', new BufferAttribute(floats(mesh.positions), 3));
  geometry.setAttribute('normal', new BufferAttribute(floats(mesh.normals), 3));
  geometry.setAttribute('color', new BufferAttribute(linearColors(floats(mesh.colors)), 3));
  geometry.setIndex(new BufferAttribute(new Uint32Array(bytes(mesh.indices).buffer), 1));
  geometry.computeBoundingSphere();
  return geometry;
}

function bytes(base64: string): Uint8Array<ArrayBuffer> {
  return Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
}

function floats(base64: string): Float32Array<ArrayBuffer> {
  return new Float32Array(bytes(base64).buffer);
}

// The server's colours are sRGB, as its renderer writes them out unchanged; three.js lights in linear.
export function linearColors(srgb: Float32Array<ArrayBuffer>): Float32Array<ArrayBuffer> {
  return srgb.map((c) => (c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4)));
}
