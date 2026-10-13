import { create } from 'zustand';
import { api } from './api';
import {
  decodeWorld,
  nextId,
  type CameraModel,
  type Environment,
  type Head,
  type ObjectType,
  type ServerStatus,
  type Terrain,
  type Vec3,
  type World,
  type WorldObject,
} from './model';

export type Selection = { kind: 'object' | 'camera'; id: string };
export type Placeable = ObjectType | 'camera.fixed' | 'camera.ptz';
export type BrushKind = 'raise' | 'lower' | 'smooth' | 'flatten';
export type Tool = { kind: 'select' } | { kind: 'place'; what: Placeable } | { kind: 'brush'; brush: BrushKind };

interface EditorState {
  connected: boolean;
  // The server's world with the edits it has not confirmed yet laid over it.
  world: World | null;
  status: ServerStatus | null;
  // Missing for a camera without a PTZ head.
  heads: Partial<Record<string, Head>>;
  selection: Selection | null;
  tool: Tool;
  gizmo: 'move' | 'rotate';
  brushRadius: number;
  brushStrength: number;
  error: string | null;
}

export const useEditor = create<EditorState>()(() => ({
  connected: false,
  world: null,
  status: null,
  heads: {},
  selection: null,
  tool: { kind: 'select' },
  gizmo: 'move',
  brushRadius: 12,
  brushStrength: 0.5,
  error: null,
}));

const set = useEditor.setState;

export function select(selection: Selection | null) {
  set({ selection, tool: { kind: 'select' } });
}

export function setTool(tool: Tool) {
  set({ tool });
}

export function showError(error: unknown) {
  set({ error: error instanceof Error ? error.message : String(error) });
}

// ---- Keeping in sync with the server ----
//
// The server streams its world whenever it changes. Local edits show at once and go to the server a
// moment later (a slider dragged across its range sends one request, not fifty); until the server's
// world includes an edit, the edit stays laid over it, so the picture never jumps back.

const sendDelayMs = 250;

interface PendingEdit {
  apply: (world: World) => World;
  send: () => Promise<unknown>;
  state: 'waiting' | 'sending' | 'sent';
}

const pending = new Map<string, PendingEdit>();
const timers = new Map<string, ReturnType<typeof setTimeout>>();
const sending = new Set<string>();
let serverWorld: World | null = null;

function withPending(world: World): World {
  let result = world;
  for (const edit of pending.values()) result = edit.apply(result);
  return result;
}

// Follows the server's events until the returned function is called.
export function connect(): () => void {
  const events = new EventSource('/api/events');
  events.onopen = () => set({ connected: true });
  // EventSource reconnects by itself, and the server starts every connection with the full state.
  events.onerror = () => set({ connected: false });
  events.addEventListener('world', (e) => receiveWorld(decodeWorld(e.data)));
  events.addEventListener('status', (e) => set({ status: JSON.parse(e.data) as ServerStatus }));
  events.addEventListener('ptz', (e) => set({ heads: JSON.parse(e.data) as Partial<Record<string, Head>> }));
  return () => events.close();
}

function receiveWorld(world: World) {
  serverWorld = world;
  set({ world: withPending(world) });
  // An edit the server confirmed is in this world, or in the next one if this one was on its way
  // before the edit landed; it was laid over this one, and the next one carries it.
  for (const [key, edit] of pending) if (edit.state === 'sent') pending.delete(key);
}

function edit(key: string, apply: (world: World) => World, send: () => Promise<unknown>, delayMs = sendDelayMs) {
  pending.set(key, { apply, send, state: 'waiting' });
  set((s) => ({ world: s.world && apply(s.world) }));
  clearTimeout(timers.get(key));
  timers.set(key, setTimeout(() => void flush(key), delayMs));
}

async function flush(key: string) {
  const entry = pending.get(key);
  // One request per key at a time, so the server applies edits in the order they were made; a newer
  // edit waits for the one in flight.
  if (!entry || entry.state !== 'waiting' || sending.has(key)) return;
  sending.add(key);
  entry.state = 'sending';
  try {
    await entry.send();
    if (pending.get(key) === entry) entry.state = 'sent';
  } catch (error) {
    if (pending.get(key) === entry) pending.delete(key);
    showError(error);
    if (serverWorld) set({ world: withPending(serverWorld) });
  } finally {
    sending.delete(key);
  }
  await flush(key);
}

// ---- Edits ----

export function updateObject(obj: WorldObject) {
  edit(
    `object:${obj.id}`,
    (w) => ({ ...w, objects: w.objects.map((o) => (o.id === obj.id ? obj : o)) }),
    () => api.putObject(obj),
  );
}

export function updateCamera(camera: CameraModel) {
  edit(
    `camera:${camera.id}`,
    (w) => ({ ...w, cameras: w.cameras.map((c) => (c.id === camera.id ? camera : c)) }),
    () => api.putCamera(camera),
  );
}

export function updateTerrain(terrain: Terrain) {
  edit('terrain', (w) => ({ ...w, terrain }), () => api.putTerrain(terrain));
}

export function updateEnvironment(environment: Environment) {
  edit('environment', (w) => ({ ...w, environment }), () => api.putEnvironment(environment));
}

export function removeSelected() {
  const { selection } = useEditor.getState();
  if (!selection) return;
  const { kind, id } = selection;
  if (kind === 'object')
    edit(`object:${id}`, (w) => ({ ...w, objects: w.objects.filter((o) => o.id !== id) }), () => api.deleteObject(id), 0);
  else edit(`camera:${id}`, (w) => ({ ...w, cameras: w.cameras.filter((c) => c.id !== id) }), () => api.deleteCamera(id), 0);
  set({ selection: null });
}

// Sizes a new object starts with: about what the default world has.
export const defaultScale: Record<ObjectType, Vec3> = {
  'tree.pine': { x: 4.5, y: 13, z: 4.5 },
  'tree.oak': { x: 6, y: 8.5, z: 6 },
  house: { x: 10, y: 5, z: 10 },
  rock: { x: 2.6, y: 2, z: 2 },
  box: { x: 3, y: 3, z: 3 },
};

// Adds an object or a camera on the ground at (x, z) and selects it. Only what differs from the
// server's defaults is sent; the full model arrives with the next world, as it does for any edit.
export async function place(what: Placeable, x: number, z: number) {
  const world = useEditor.getState().world;
  if (!world) return;
  try {
    if (what === 'camera.fixed' || what === 'camera.ptz') {
      const id = nextId('cam', world.cameras.map((c) => c.id));
      const ptz = what === 'camera.ptz';
      await api.putCamera({
        id,
        name: `${ptz ? 'PTZ' : 'Fixed'} ${id}`,
        kind: ptz ? 'ptz' : 'fixed',
        position: { x, y: ptz ? 12 : 6, z },
        pitch: ptz ? 0 : -10,
        ptz: ptz ? {} : null,
        users: [{ username: 'admin', password: 'admin' }],
      });
      select({ kind: 'camera', id });
    } else {
      const id = nextId('o', world.objects.map((o) => o.id));
      await api.putObject({ id, type: what, position: { x, y: 0, z }, scale: defaultScale[what], seed: randomSeed() });
      select({ kind: 'object', id });
    }
  } catch (error) {
    showError(error);
  }
}

function randomSeed(): number {
  return Math.floor(Math.random() * 2 ** 31);
}
