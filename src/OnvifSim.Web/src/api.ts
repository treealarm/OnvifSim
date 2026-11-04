import { BufferAttribute, BufferGeometry } from 'three';
import {
  encodeTerrain,
  type CameraModel,
  type Environment,
  type Geo,
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
  putRoad: (road: Partial<Road> & Pick<Road, 'id'>) => send('PUT', `/api/roads/${encodeURIComponent(road.id)}`, road),
  deleteRoad: (id: string) => send('DELETE', `/api/roads/${encodeURIComponent(id)}`),
  putWalker: (walker: Partial<Walker> & Pick<Walker, 'id'>) => send('PUT', `/api/walkers/${encodeURIComponent(walker.id)}`, walker),
  deleteWalker: (id: string) => send('DELETE', `/api/walkers/${encodeURIComponent(id)}`),
  putVehicle: (vehicle: Partial<Vehicle> & Pick<Vehicle, 'id'>) => send('PUT', `/api/vehicles/${encodeURIComponent(vehicle.id)}`, vehicle),
  deleteVehicle: (id: string) => send('DELETE', `/api/vehicles/${encodeURIComponent(id)}`),
  putTerrain: (terrain: Terrain) => send('PUT', '/api/terrain', encodeTerrain(terrain)),
  putEnvironment: (environment: Environment) => send('PUT', '/api/environment', environment),
  putGeo: (geo: Geo) => send('PUT', '/api/geo', geo),

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
