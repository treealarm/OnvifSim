import { BufferAttribute, BufferGeometry } from 'three';
import { encodeTerrain, type CameraModel, type Environment, type PtzPosition, type PtzSettings, type Road, type Terrain, type Walker, type WorldObject } from './model';

// What PUT takes: anything left out gets the server's default.
export type ObjectDraft = Partial<WorldObject> & Pick<WorldObject, 'id'>;
export type CameraDraft = Omit<Partial<CameraModel>, 'ptz'> & Pick<CameraModel, 'id'> & { ptz?: Partial<PtzSettings> | null };

// The server's editor API (Editor/EditorApi.cs). Every call throws ApiError with the server's reason
// when the change is refused.

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

async function send(method: string, path: string, body?: unknown): Promise<Response> {
  const response = await fetch(path, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new ApiError(await reasonOf(response), response.status);
  return response;
}

async function reasonOf(response: Response): Promise<string> {
  try {
    const problem = (await response.json()) as { detail?: string; title?: string };
    return problem.detail ?? problem.title ?? response.statusText;
  } catch {
    return `${response.status} ${response.statusText}`;
  }
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
  putTerrain: (terrain: Terrain) => send('PUT', '/api/terrain', encodeTerrain(terrain)),
  putEnvironment: (environment: Environment) => send('PUT', '/api/environment', environment),

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
