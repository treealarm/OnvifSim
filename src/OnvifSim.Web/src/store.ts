import { create } from 'zustand';
import { api, ApiError } from './api';
import {
  decodeWorld,
  nextId,
  type CameraKind,
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
  undoSteps: number;
  redoSteps: number;
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
  undoSteps: 0,
  redoSteps: 0,
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

// ---- Edits and their history ----
//
// Every edit replaces one part of the world: an object, a camera (either may be absent: not created
// yet, or deleted), the terrain or the environment. Undo puts back what the part was before; the
// server applies that like any other edit. Moving a PTZ head is driving the camera, not editing the
// world, and stays out of the history.

type Part =
  | { kind: 'object'; id: string; value: WorldObject | null }
  | { kind: 'camera'; id: string; value: CameraModel | null }
  | { kind: 'terrain'; value: Terrain }
  | { kind: 'environment'; value: Environment };

interface HistoryEntry {
  // The part as it was before the edit, to be put back.
  part: Part;
  // When the step was last added to; 0 for a step nothing may be merged into.
  at: number;
}

// Edits of one part this close together are one step: a dragged slider, a typed number.
const mergeWithinMs = 1000;
const historyLimit = 100;
const undoStack: HistoryEntry[] = [];
const redoStack: HistoryEntry[] = [];

function keyOf(part: Part): string {
  return part.kind === 'object' || part.kind === 'camera' ? `${part.kind}:${part.id}` : part.kind;
}

// The same part as it is in the world now.
function currentOf(world: World, part: Part): Part {
  switch (part.kind) {
    case 'object':
      return { ...part, value: world.objects.find((o) => o.id === part.id) ?? null };
    case 'camera':
      return { ...part, value: world.cameras.find((c) => c.id === part.id) ?? null };
    case 'terrain':
      return { kind: 'terrain', value: world.terrain };
    case 'environment':
      return { kind: 'environment', value: world.environment };
  }
}

function upsert<T extends { id: string }>(items: T[], id: string, item: T | null): T[] {
  if (item === null) return items.filter((i) => i.id !== id);
  return items.some((i) => i.id === id) ? items.map((i) => (i.id === id ? item : i)) : [...items, item];
}

function applyPart(world: World, part: Part): World {
  switch (part.kind) {
    case 'object':
      return { ...world, objects: upsert(world.objects, part.id, part.value) };
    case 'camera':
      return { ...world, cameras: upsert(world.cameras, part.id, part.value) };
    case 'terrain':
      return { ...world, terrain: part.value };
    case 'environment':
      return { ...world, environment: part.value };
  }
}

async function sendPart(part: Part): Promise<unknown> {
  switch (part.kind) {
    case 'object':
      return part.value ? api.putObject(part.value) : ignoreMissing(api.deleteObject(part.id));
    case 'camera':
      return part.value ? api.putCamera(part.value) : ignoreMissing(api.deleteCamera(part.id));
    case 'terrain':
      return api.putTerrain(part.value);
    case 'environment':
      return api.putEnvironment(part.value);
  }
}

// Deleting what is already gone (undoing a creation twice over, say) has nothing left to do.
async function ignoreMissing(request: Promise<unknown>): Promise<unknown> {
  try {
    return await request;
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return undefined;
    throw error;
  }
}

function commit(part: Part, delayMs = sendDelayMs) {
  edit(keyOf(part), (w) => applyPart(w, part), () => sendPart(part), delayMs);
}

function remember(before: Part, merge: boolean) {
  const now = Date.now();
  const last = undoStack.at(-1);
  if (merge && last && keyOf(last.part) === keyOf(before) && now - last.at < mergeWithinMs) last.at = now;
  else {
    // A step that must stay a step of its own (creating, deleting) takes no edits after it in.
    undoStack.push({ part: before, at: merge ? now : 0 });
    if (undoStack.length > historyLimit) undoStack.shift();
  }
  redoStack.length = 0;
  set({ undoSteps: undoStack.length, redoSteps: 0 });
}

function change(part: Part, { merge = true, delayMs = sendDelayMs } = {}) {
  const world = useEditor.getState().world;
  if (!world) return;
  remember(currentOf(world, part), merge);
  commit(part, delayMs);
}

// Puts the part of the newest step on one stack back, and keeps what it replaces on the other.
function step(from: HistoryEntry[], to: HistoryEntry[]) {
  const world = useEditor.getState().world;
  const entry = from.pop();
  if (!world || !entry) return;
  to.push({ part: currentOf(world, entry.part), at: 0 });
  commit(entry.part, 0);
  set({ undoSteps: undoStack.length, redoSteps: redoStack.length });
}

export function undo() {
  step(undoStack, redoStack);
}

export function redo() {
  step(redoStack, undoStack);
}

export function updateObject(obj: WorldObject) {
  change({ kind: 'object', id: obj.id, value: obj });
}

export function updateCamera(camera: CameraModel) {
  change({ kind: 'camera', id: camera.id, value: camera });
}

// Every stroke of a brush is a step of its own, however quickly the next one follows.
export function updateTerrain(terrain: Terrain) {
  change({ kind: 'terrain', value: terrain }, { merge: false });
}

export function updateEnvironment(environment: Environment) {
  change({ kind: 'environment', value: environment });
}

export function removeSelected() {
  const { selection } = useEditor.getState();
  if (!selection) return;
  change({ kind: selection.kind, id: selection.id, value: null }, { merge: false, delayMs: 0 });
  set({ selection: null });
}

// Switching kind is sent as it is, without showing it first: a new PTZ head gets the server's default
// limits, which only the server's next world brings.
export async function changeCameraKind(camera: CameraModel, kind: CameraKind) {
  try {
    await api.putCamera({ ...camera, kind, ptz: kind === 'ptz' ? (camera.ptz ?? {}) : null });
    remember({ kind: 'camera', id: camera.id, value: camera }, false);
  } catch (error) {
    showError(error);
  }
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
      remember({ kind: 'camera', id, value: null }, false);
      select({ kind: 'camera', id });
    } else {
      const id = nextId('o', world.objects.map((o) => o.id));
      await api.putObject({ id, type: what, position: { x, y: 0, z }, scale: defaultScale[what], seed: randomSeed() });
      remember({ kind: 'object', id, value: null }, false);
      select({ kind: 'object', id });
    }
  } catch (error) {
    showError(error);
  }
}

function randomSeed(): number {
  return Math.floor(Math.random() * 2 ** 31);
}
