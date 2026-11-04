import { create } from 'zustand';
import { api, ApiError } from './api';
import {
  cameraIoOf,
  decodeWorld,
  nextId,
  sameId,
  type CameraIo,
  type CameraKind,
  type CameraModel,
  type Environment,
  type Geo,
  type Head,
  type GroundPoint,
  type ObjectType,
  type Road,
  type ServerStatus,
  type Terrain,
  type Vec3,
  type Vehicle,
  type Walker,
  type World,
  type WorldObject,
  type WorldsInfo,
  type WorldSwitchSignal,
} from './model';

export type Selection = { kind: 'object' | 'camera' | 'road' | 'walker' | 'vehicle'; id: string };
export type Placeable = ObjectType | 'camera.fixed' | 'camera.ptz';
export type BrushKind = 'raise' | 'lower' | 'smooth' | 'flatten';
export type Drawable = 'road' | 'walker' | 'vehicle';
// Which tool palette the toolbar shows. Choosing a palette does not change the active tool.
export type ToolGroup = 'select' | 'add' | 'draw' | 'terrain';
// Drawing a road or a walker's route point by point; extending adds the points to the end of an
// existing one instead of making a new one.
export interface DrawTool {
  kind: 'draw';
  what: Drawable;
  points: GroundPoint[];
  extending?: string;
}
export type Tool = { kind: 'select' } | { kind: 'place'; what: Placeable } | { kind: 'brush'; brush: BrushKind } | DrawTool;

interface EditorState {
  connected: boolean;
  // The server's world with the edits it has not confirmed yet laid over it.
  world: World | null;
  status: ServerStatus | null;
  // The world library: one active world and the ones kept beside it.
  worlds: string[];
  trash: string[];
  activeWorld: string | null;
  recordings: { hasRecordings: boolean; cameraCount: number };
  // Missing for a camera without a PTZ head.
  heads: Partial<Record<string, Head>>;
  selection: Selection | null;
  tool: Tool;
  toolGroup: ToolGroup;
  gizmo: 'move' | 'rotate';
  // Hide everything above a horizontal plane, so a room's inside can be seen and edited.
  cutaway: boolean;
  // The wiring window: every input and lamp on the left, every relay on the right.
  linksOpen: boolean;
  brushRadius: number;
  brushStrength: number;
  // The point of the selected road or route being dragged over the ground.
  draggedPoint: number | null;
  error: string | null;
  undoSteps: number;
  redoSteps: number;
}

export const useEditor = create<EditorState>()(() => ({
  connected: false,
  world: null,
  status: null,
  worlds: [],
  trash: [],
  activeWorld: null,
  recordings: { hasRecordings: false, cameraCount: 0 },
  heads: {},
  selection: null,
  tool: { kind: 'select' },
  toolGroup: 'select',
  gizmo: 'move',
  cutaway: false,
  linksOpen: false,
  brushRadius: 12,
  brushStrength: 0.5,
  draggedPoint: null,
  error: null,
  undoSteps: 0,
  redoSteps: 0,
}));

const set = useEditor.setState;

export function select(selection: Selection | null) {
  set({ selection, tool: { kind: 'select' }, toolGroup: 'select' });
}

// Which palette a tool belongs to; the toolbar marks that group so switching tabs never hides what the
// viewport will do.
export function toolGroupOf(tool: Tool): ToolGroup {
  switch (tool.kind) {
    case 'select':
      return 'select';
    case 'place':
      return 'add';
    case 'draw':
      return 'draw';
    case 'brush':
      return 'terrain';
  }
}

export function setTool(tool: Tool) {
  set({ tool, toolGroup: toolGroupOf(tool) });
}

// The palette tab only chooses which tools are shown; the active tool stays whatever it was.
export function setToolGroup(toolGroup: ToolGroup) {
  set({ toolGroup });
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
const inFlight = new Map<string, Promise<boolean>>();
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
  events.addEventListener('worlds', (e) => receiveWorlds(JSON.parse(e.data) as WorldsInfo));
  events.addEventListener('world-switched', (e) => receiveSwitch(JSON.parse(e.data) as WorldSwitchSignal));
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

// The library changed: a switch, a create, a trash or a restore. A switch from another tab arrives
// here too when its world-switched event is missed on a reconnect, hence the active-name check.
function receiveWorlds(info: WorldsInfo) {
  const previous = useEditor.getState().activeWorld;
  if (previous !== null && previous !== info.active) resetForWorldSwitch();
  set({ worlds: info.worlds, trash: info.trash, activeWorld: info.active, recordings: info.recordings });
}

// The server has already switched. Everything that belonged to the world being left goes: its pending
// edits, its undo history and the selection. The next world event brings the new world.
function receiveSwitch(signal: WorldSwitchSignal) {
  resetForWorldSwitch();
  set({ activeWorld: signal.active });
}

function resetForWorldSwitch() {
  for (const timer of timers.values()) clearTimeout(timer);
  timers.clear();
  pending.clear();
  undoStack.length = 0;
  redoStack.length = 0;
  set({
    selection: null,
    tool: { kind: 'select' },
    toolGroup: 'select',
    linksOpen: false,
    draggedPoint: null,
    undoSteps: 0,
    redoSteps: 0,
    heads: {},
    error: null,
  });
}

function edit(key: string, apply: (world: World) => World, send: () => Promise<unknown>, delayMs = sendDelayMs) {
  pending.set(key, { apply, send, state: 'waiting' });
  set((s) => ({ world: s.world && apply(s.world) }));
  clearTimeout(timers.get(key));
  timers.set(key, setTimeout(() => void flush(key), delayMs));
}

// Sends one part's pending edit, then any edit that arrived while it was in flight. One request per key
// at a time, so the server applies edits in the order they were made.
async function flush(key: string): Promise<boolean> {
  const running = inFlight.get(key);
  if (running) {
    await running;
    return flush(key);
  }
  const entry = pending.get(key);
  if (!entry || entry.state !== 'waiting') return true;
  entry.state = 'sending';
  const task = (async () => {
    try {
      await entry.send();
      if (pending.get(key) === entry) entry.state = 'sent';
      return true;
    } catch (error) {
      if (pending.get(key) === entry) pending.delete(key);
      showError(error);
      if (serverWorld) set({ world: withPending(serverWorld) });
      return false;
    }
  })();
  inFlight.set(key, task);
  let saved: boolean;
  try {
    saved = await task;
  } finally {
    inFlight.delete(key);
  }
  return saved && flush(key);
}

// Sends everything waiting now and waits for the requests in flight, for an action that must not race
// with an edit (switching worlds, changing a camera's kind). False when any edit was refused.
export async function settleEdits(): Promise<boolean> {
  for (const timer of timers.values()) clearTimeout(timer);
  timers.clear();
  while (true) {
    const keys = [...new Set([...pending.keys(), ...inFlight.keys()])];
    if (keys.length === 0) return true;
    const saved = await Promise.all(keys.map((key) => flush(key)));
    if (saved.some((ok) => !ok)) return false;
    if (![...pending.values()].some((entry) => entry.state !== 'sent')) return true;
  }
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
  | { kind: 'road'; id: string; value: Road | null }
  | { kind: 'walker'; id: string; value: Walker | null }
  | { kind: 'vehicle'; id: string; value: Vehicle | null }
  // A camera's relays, inputs and the wires from its relays, which are saved together.
  | { kind: 'camera-io'; id: string; value: CameraIo | null }
  // One relay's switch position, saved on its own: ONVIF clients change it too.
  | { kind: 'relay'; id: string; token: string; value: { active: boolean } | null }
  // One parked car becoming a moving one, or the way back. Both ends are kept so the step undoes as
  // a whole: a car conversion is one action, not a creation and a deletion.
  | { kind: 'driving-car'; object: WorldObject; vehicle: Vehicle; moving: boolean }
  | { kind: 'terrain'; value: Terrain }
  | { kind: 'environment'; value: Environment }
  | { kind: 'geo'; value: Geo };

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
  if (part.kind === 'terrain' || part.kind === 'environment' || part.kind === 'geo') return part.kind;
  if (part.kind === 'driving-car') return `driving-car:${part.object.id}:${part.vehicle.id}`;
  if (part.kind === 'relay') return `relay:${part.id}:${part.token}`;
  return `${part.kind}:${part.id}`;
}

// The same part as it is in the world now.
function currentOf(world: World, part: Part): Part {
  switch (part.kind) {
    case 'object':
      return { ...part, value: world.objects.find((o) => o.id === part.id) ?? null };
    case 'camera':
      return { ...part, value: world.cameras.find((c) => c.id === part.id) ?? null };
    case 'road':
      return { ...part, value: world.roads.find((r) => r.id === part.id) ?? null };
    case 'walker':
      return { ...part, value: world.walkers.find((w) => w.id === part.id) ?? null };
    case 'vehicle':
      return { ...part, value: world.vehicles.find((v) => v.id === part.id) ?? null };
    case 'camera-io': {
      const camera = world.cameras.find((c) => sameId(c.id, part.id));
      return { ...part, value: camera ? cameraIoOf(world, camera) : null };
    }
    case 'relay': {
      const relay = world.cameras
        .find((c) => sameId(c.id, part.id))
        ?.relayOutputs.find((output) => sameId(output.token, part.token));
      return { ...part, value: { active: relay?.active ?? false } };
    }
    case 'driving-car': {
      const object = world.objects.find((o) => o.id === part.object.id);
      if (object) return { ...part, object, moving: false };
      const vehicle = world.vehicles.find((v) => v.id === part.vehicle.id);
      return vehicle ? { ...part, vehicle, moving: true } : { ...part, moving: false };
    }
    case 'terrain':
      return { kind: 'terrain', value: world.terrain };
    case 'environment':
      return { kind: 'environment', value: world.environment };
    case 'geo':
      return { kind: 'geo', value: world.geo };
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
    case 'road':
      return { ...world, roads: upsert(world.roads, part.id, part.value) };
    case 'walker':
      return { ...world, walkers: upsert(world.walkers, part.id, part.value) };
    case 'vehicle':
      return { ...world, vehicles: upsert(world.vehicles, part.id, part.value) };
    case 'camera-io': {
      const io = part.value;
      if (!io) return world;
      const inputs = new Set(io.digitalInputs.map((input) => input.token.toLowerCase()));
      const lamps = new Set(io.lamps.map((lamp) => lamp.token.toLowerCase()));
      return {
        ...world,
        cameras: world.cameras.map((camera) =>
          sameId(camera.id, part.id)
            ? { ...camera, relayOutputs: io.relayOutputs, digitalInputs: io.digitalInputs, lamps: io.lamps }
            : camera),
        // This camera's wires are the ones sent; a wire into a device that is gone goes too, as on the server.
        wires: [
          ...world.wires.filter(
            (wire) =>
              !sameId(wire.relayCameraId, part.id) &&
              !(sameId(wire.inputCameraId ?? '', part.id) && !inputs.has((wire.inputToken ?? '').toLowerCase())) &&
              !(sameId(wire.lampCameraId ?? '', part.id) && !lamps.has((wire.lampToken ?? '').toLowerCase())),
          ),
          ...io.wires,
        ],
      };
    }
    case 'relay': {
      const value = part.value;
      if (!value) return world;
      return {
        ...world,
        cameras: world.cameras.map((camera) =>
          sameId(camera.id, part.id)
            ? { ...camera, relayOutputs: camera.relayOutputs.map((relay) => (sameId(relay.token, part.token) ? { ...relay, active: value.active } : relay)) }
            : camera),
      };
    }
    case 'driving-car': {
      const objects = world.objects.filter((o) => o.id !== part.object.id);
      const vehicles = world.vehicles.filter((v) => v.id !== part.vehicle.id);
      return part.moving
        ? { ...world, objects, vehicles: [...vehicles, part.vehicle] }
        : { ...world, objects: [...objects, part.object], vehicles };
    }
    case 'terrain':
      return { ...world, terrain: part.value };
    case 'environment':
      return { ...world, environment: part.value };
    case 'geo':
      return { ...world, geo: part.value };
  }
}

async function sendPart(part: Part): Promise<unknown> {
  switch (part.kind) {
    case 'object':
      return part.value ? api.putObject(part.value) : ignoreMissing(api.deleteObject(part.id));
    case 'camera':
      return part.value ? api.putCamera(part.value) : ignoreMissing(api.deleteCamera(part.id));
    case 'road':
      return part.value ? api.putRoad(part.value) : ignoreMissing(api.deleteRoad(part.id));
    case 'walker':
      return part.value ? api.putWalker(part.value) : ignoreMissing(api.deleteWalker(part.id));
    case 'vehicle':
      return part.value ? api.putVehicle(part.value) : ignoreMissing(api.deleteVehicle(part.id));
    case 'camera-io':
      return part.value ? api.putCameraIo(part.id, part.value) : undefined;
    case 'relay':
      return part.value ? api.putRelay(part.id, part.token, part.value.active) : undefined;
    case 'driving-car':
      if (part.moving) {
        await api.putVehicle(part.vehicle);
        return ignoreMissing(api.deleteObject(part.object.id));
      }
      await api.putObject(part.object);
      return ignoreMissing(api.deleteVehicle(part.vehicle.id));
    case 'terrain':
      return api.putTerrain(part.value);
    case 'environment':
      return api.putEnvironment(part.value);
    case 'geo':
      return api.putGeo(part.value);
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

// Saves a camera's relay outputs, digital inputs and the wires from its relays in one edit, so a wire
// and the device it reaches always travel together.
export function updateCameraIo(camera: CameraModel, io: CameraIo) {
  change({ kind: 'camera-io', id: camera.id, value: io });
}

// Switches one relay. Its state is the camera's and ONVIF clients change it too, so it goes on its own
// rather than with the rest of the panel.
export function updateRelay(cameraId: string, token: string, active: boolean) {
  change({ kind: 'relay', id: cameraId, token, value: { active } });
}

export function updateRoad(road: Road) {
  change({ kind: 'road', id: road.id, value: road });
}

export function updateWalker(walker: Walker) {
  change({ kind: 'walker', id: walker.id, value: walker });
}

export function updateVehicle(vehicle: Vehicle) {
  change({ kind: 'vehicle', id: vehicle.id, value: vehicle });
}

// Turns the selected parked car into a moving one on a straight route along the way it faces, keeping
// its paint, size and plate. One edit, so one Undo brings the parked car back.
export function driveCar(obj: WorldObject) {
  const world = useEditor.getState().world;
  if (!world || obj.type !== 'car') return;
  const heading = (obj.rotationY * Math.PI) / 180;
  const vehicle: Vehicle = {
    id: nextId('v', world.vehicles.map((v) => v.id)),
    route: [
      { x: obj.position.x, z: obj.position.z },
      { x: obj.position.x + Math.sin(heading) * 30, z: obj.position.z - Math.cos(heading) * 30 },
    ],
    speed: 8,
    return: 'reverse',
    width: obj.scale.x,
    height: obj.scale.y,
    length: obj.scale.z,
    color: obj.color,
    color2: obj.color2,
    plate: obj.plate ?? null,
    detectable: true,
  };
  change({ kind: 'driving-car', object: obj, vehicle, moving: true }, { merge: false, delayMs: 0 });
  select({ kind: 'vehicle', id: vehicle.id });
}

// Every stroke of a brush is a step of its own, however quickly the next one follows.
export function updateTerrain(terrain: Terrain) {
  change({ kind: 'terrain', value: terrain }, { merge: false });
}

export function updateEnvironment(environment: Environment) {
  change({ kind: 'environment', value: environment });
}

export function updateGeo(geo: Geo) {
  change({ kind: 'geo', value: geo });
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

// ---- The world library ----
//
// Every action settles the pending edits first, so an edit of the world being left cannot land after
// the server has switched. The server asks for a recordings choice with a 409; the ApiError carries it.

function assertSettled(saved: boolean) {
  if (!saved) throw new Error('Pending edits could not be saved; nothing was switched.');
}

export async function createAndOpenWorld(name: string, clearRecordings?: boolean) {
  assertSettled(await settleEdits());
  receiveWorlds(await api.createWorld(name, clearRecordings));
}

export async function switchWorld(name: string, clearRecordings?: boolean) {
  assertSettled(await settleEdits());
  receiveWorlds(await api.activateWorld(name, clearRecordings));
}

export async function trashWorld(name: string, clearRecordings?: boolean) {
  if (name === useEditor.getState().activeWorld) assertSettled(await settleEdits());
  receiveWorlds(await api.trashWorld(name, clearRecordings));
}

export async function restoreWorld(name: string, destinationName?: string) {
  receiveWorlds(await api.restoreWorld(name, destinationName));
}

export async function emptyTrash() {
  receiveWorlds(await api.emptyTrash());
}

// Sizes a new object starts with: about what the default world has.
export const defaultScale: Record<ObjectType, Vec3> = {
  'tree.pine': { x: 4.5, y: 13, z: 4.5 },
  'tree.oak': { x: 6, y: 8.5, z: 6 },
  house: { x: 10, y: 5, z: 10 },
  office: { x: 24, y: 9, z: 16 },
  rock: { x: 2.6, y: 2, z: 2 },
  box: { x: 3, y: 3, z: 3 },
  car: { x: 1.8, y: 1.45, z: 4.4 },
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
      await api.putObject({
        id,
        type: what,
        position: { x, y: 0, z },
        scale: defaultScale[what],
        seed: randomSeed(),
        ...(what === 'car' ? { plate: 'A123BC' } : {}),
      });
      remember({ kind: 'object', id, value: null }, false);
      select({ kind: 'object', id });
    }
  } catch (error) {
    showError(error);
  }
}

// ---- Drawing roads and routes ----

export function startDrawing(what: Drawable, extending?: string) {
  set({ tool: { kind: 'draw', what, points: [], extending }, toolGroup: 'draw' });
}

export function addDrawingPoint(point: GroundPoint) {
  const { tool } = useEditor.getState();
  if (tool.kind !== 'draw') return;
  const last = tool.points.at(-1);
  // The two clicks of a double click land on one spot; one point is enough.
  if (last && Math.hypot(last.x - point.x, last.z - point.z) < 0.5) return;
  set({ tool: { ...tool, points: [...tool.points, point] } });
}

export function cancelDrawing() {
  set({ tool: { kind: 'select' }, toolGroup: 'select' });
}

// A road needs two points; a walker or a car needs one (standing there). Fewer just ends the drawing.
export async function finishDrawing() {
  const { tool, world } = useEditor.getState();
  if (tool.kind !== 'draw' || !world) return;
  set({ tool: { kind: 'select' }, toolGroup: 'select' });
  const { what, points, extending } = tool;
  if (points.length === 0) return;
  if (extending) {
    if (what === 'road') {
      const road = world.roads.find((r) => r.id === extending);
      if (road) updateRoad({ ...road, points: [...road.points, ...points] });
    } else if (what === 'walker') {
      const walker = world.walkers.find((w) => w.id === extending);
      if (walker) updateWalker({ ...walker, route: [...walker.route, ...points] });
    } else {
      const vehicle = world.vehicles.find((v) => v.id === extending);
      if (vehicle) updateVehicle({ ...vehicle, route: [...vehicle.route, ...points] });
    }
    return;
  }
  if (what === 'road' && points.length < 2) return;
  try {
    if (what === 'road') {
      const id = nextId('r', world.roads.map((r) => r.id));
      await api.putRoad({ id, points });
      remember({ kind: 'road', id, value: null }, false);
      select({ kind: 'road', id });
    } else if (what === 'walker') {
      const id = nextId('w', world.walkers.map((w) => w.id));
      await api.putWalker({ id, route: points });
      remember({ kind: 'walker', id, value: null }, false);
      select({ kind: 'walker', id });
    } else {
      const id = nextId('v', world.vehicles.map((v) => v.id));
      await api.putVehicle({ id, route: points });
      remember({ kind: 'vehicle', id, value: null }, false);
      select({ kind: 'vehicle', id });
    }
  } catch (error) {
    showError(error);
  }
}

// Moves one point of the selected road or route, as its handle is dragged over the ground.
export function movePoint(selection: Selection, index: number, point: GroundPoint) {
  const world = useEditor.getState().world;
  if (!world) return;
  const replace = (points: GroundPoint[]) => points.map((p, i) => (i === index ? point : p));
  if (selection.kind === 'road') {
    const road = world.roads.find((r) => r.id === selection.id);
    if (road) updateRoad({ ...road, points: replace(road.points) });
  } else if (selection.kind === 'walker') {
    const walker = world.walkers.find((w) => w.id === selection.id);
    if (walker) updateWalker({ ...walker, route: replace(walker.route) });
  } else if (selection.kind === 'vehicle') {
    const vehicle = world.vehicles.find((v) => v.id === selection.id);
    if (vehicle) updateVehicle({ ...vehicle, route: replace(vehicle.route) });
  }
}

function randomSeed(): number {
  return Math.floor(Math.random() * 2 ** 31);
}
