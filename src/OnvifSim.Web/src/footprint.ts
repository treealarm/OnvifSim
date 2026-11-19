// A prism's footprint and the edits an operator makes to it in the viewport: its rings in the
// object's own metres, how a world point becomes one of those, and how an opening keeps its place on
// a wall when the wall's corners move. Pure geometry, so the editor and the end-to-end tests share it.
import { normalizeHole, normalizeOutline, type GroundPoint, type Opening, type WorldObject } from './model';

// A ring of the footprint: -1 is the outline, 0 and up are its courtyards.
export type RingIndex = number;

// The outline and its courtyards, all as the object stores them.
export interface Footprint {
  points: GroundPoint[];
  holes: GroundPoint[][];
}

const epsilon = 1e-3;
const centimetre = 100;

export function footprintOf(obj: WorldObject): Footprint {
  return { points: obj.points ?? [], holes: obj.holes ?? [] };
}

export function ringOf(frame: Footprint, ring: RingIndex): GroundPoint[] {
  return ring < 0 ? frame.points : (frame.holes[ring] ?? []);
}

// The footprint with one vertex where the pointer has it now.
export function withPoint(frame: Footprint, ring: RingIndex, index: number, point: GroundPoint): Footprint {
  const replace = (points: GroundPoint[]) => points.map((p, i) => (i === index ? point : p));
  return ring < 0
    ? { ...frame, points: replace(frame.points) }
    : { ...frame, holes: frame.holes.map((hole, h) => (h === ring ? replace(hole) : hole)) };
}

// The footprint with a new vertex in the middle of one edge, and where that vertex went.
export function insertOnEdge(frame: Footprint, ring: RingIndex, edge: number): { frame: Footprint; index: number } {
  const points = ringOf(frame, ring);
  if (points.length < 2 || edge < 0 || edge >= points.length) return { frame, index: -1 };
  const a = points[edge], b = points[(edge + 1) % points.length];
  const point = { x: (a.x + b.x) / 2, z: (a.z + b.z) / 2 };
  const next = [...points.slice(0, edge + 1), point, ...points.slice(edge + 1)];
  return { frame: replaceRing(frame, ring, next), index: edge + 1 };
}

function replaceRing(frame: Footprint, ring: RingIndex, points: GroundPoint[]): Footprint {
  return ring < 0
    ? { ...frame, points }
    : { ...frame, holes: frame.holes.map((hole, h) => (h === ring ? points : hole)) };
}

// The rings as the server stores them: the outline in its direction, every courtyard the other way
// round, consecutive duplicates and points that only stand on a straight edge dropped.
export function normalizeRings(frame: Footprint): Footprint {
  return {
    points: normalizeOutline(frame.points),
    holes: frame.holes.map(normalizeHole),
  };
}

// Whether two footprints are the same ring for ring, within a millimetre.
export function sameFootprint(a: Footprint, b: Footprint): boolean {
  return sameRing(a.points, b.points)
    && a.holes.length === b.holes.length
    && a.holes.every((hole, i) => sameRing(hole, b.holes[i]));
}

function sameRing(a: GroundPoint[], b: GroundPoint[]): boolean {
  return a.length === b.length && a.every((p, i) => distance(p, b[i]) <= 1e-4);
}

// A place in the object's own metres as a place in the world: its rotation (degrees clockwise seen
// from above, the negative of the three.js group's rotation.y) and its position.
export function toWorldPoint(obj: Pick<WorldObject, 'position' | 'rotationY'>, point: GroundPoint): GroundPoint {
  const angle = (obj.rotationY * Math.PI) / 180;
  const cos = Math.cos(angle), sin = Math.sin(angle);
  return {
    x: obj.position.x + point.x * cos - point.z * sin,
    z: obj.position.z + point.x * sin + point.z * cos,
  };
}

export function toLocalPoint(obj: Pick<WorldObject, 'position' | 'rotationY'>, point: GroundPoint): GroundPoint {
  const angle = (obj.rotationY * Math.PI) / 180;
  const cos = Math.cos(angle), sin = Math.sin(angle);
  const dx = point.x - obj.position.x, dz = point.z - obj.position.z;
  return { x: dx * cos + dz * sin, z: -dx * sin + dz * cos };
}

export type OpeningRemap = { openings: Opening[] } | { error: string };

// Openings after the outline changed. An opening is drawn on one wall at a distance from its first
// corner; when a corner moves it keeps its distance from the corner that stayed, and when the wall is
// split by an inserted point it stays on the piece it belongs to. A wall that can no longer hold it
// is a refusal — never a silent move or a silent shrink.
export function remapOpenings(before: GroundPoint[], after: GroundPoint[], openings: Opening[]): OpeningRemap {
  const remapped: Opening[] = [];
  for (let index = 0; index < openings.length; index++) {
    const opening = openings[index];
    const n = before.length;
    if (opening.edge < 0 || opening.edge >= n) return { error: `Opening ${index + 1} names a wall the outline no longer has.` };
    const a = before[opening.edge];
    const b = before[(opening.edge + 1) % n];
    const length = distance(a, b);
    if (length < epsilon || opening.offset < -epsilon || opening.offset + opening.width > length + epsilon)
      return { error: `Opening ${index + 1} does not fit the wall it was drawn on.` };

    const ia = findPoint(after, a);
    const ib = findPoint(after, b);
    let placed: { edge: number; offset: number } | null;
    if (ia >= 0 && ib >= 0) {
      // Both corners of the wall stayed. With nothing inserted between them the wall is the same
      // segment; with points inserted on it, the opening belongs to the piece it fell on.
      const path = wallPath(after, ia, ib, before);
      if (path === null) return { error: `Opening ${index + 1} no longer fits its wall; move the wall or the opening first.` };
      placed = path.length > 2
        ? placeOnSplitWall(after, a, b, length, path, opening)
        : placeUnchangedWall(after, a, b, length, opening);
    } else if (ib >= 0) {
      // A moved: the opening keeps its distance from B, the corner that stayed.
      placed = placeFromAnchor(after, ib, { x: a.x - b.x, z: a.z - b.z }, length - (opening.offset + opening.width), opening.width);
    } else if (ia >= 0) {
      // B moved: the opening keeps its distance from A.
      placed = placeFromAnchor(after, ia, { x: b.x - a.x, z: b.z - a.z }, opening.offset, opening.width);
    } else {
      placed = placeUnchangedWall(after, a, b, length, opening);
    }

    if (!placed) return { error: `Opening ${index + 1} no longer fits its wall; move the wall or the opening first.` };
    const wall = edgeLength(after, placed.edge);
    if (placed.offset < -epsilon || placed.offset + opening.width > wall + epsilon)
      return { error: `Opening ${index + 1} no longer fits its wall; move the wall or the opening first.` };
    remapped.push({ ...opening, edge: placed.edge, offset: round(placed.offset) });
  }
  return { openings: remapped };
}

// Both corners of the wall are still where they were: the opening's own two ends are too, so they
// are looked up on the new ring. This is what survives a reversed ring, a point dropped from a
// straight edge, or a point inserted beside the opening.
function placeUnchangedWall(after: GroundPoint[], a: GroundPoint, b: GroundPoint, length: number, opening: Opening) {
  const start = lerp(a, b, opening.offset / length);
  const end = lerp(a, b, (opening.offset + opening.width) / length);
  const starts = edgesContaining(after, start);
  const ends = edgesContaining(after, end);
  const common = starts.filter((edge) => ends.includes(edge));
  if (common.length !== 1) return null;
  return { edge: common[0], offset: offsetOnEdge(after, common[0], start) };
}

// The wall as the new ring stores it, from corner A to corner B: A, any corners inserted on that
// wall, B. Null when the way round is not this wall — the ring was turned inside out, or the path
// runs through corners that were there before, which is the rest of the building.
function wallPath(after: GroundPoint[], ia: number, ib: number, before: GroundPoint[]): number[] | null {
  const n = after.length;
  const path = [ia];
  while (path[path.length - 1] !== ib && path.length <= n) path.push((path[path.length - 1] + 1) % n);
  if (path[path.length - 1] !== ib) return null;
  for (const index of path.slice(1, -1))
    if (findPoint(before, after[index]) >= 0) return null;
  return path;
}

// A wall split by inserted corners: the opening stays on the piece it fell on, at the same distance
// from that piece's first corner as it had on the old wall. The inserted corners' places on the old
// wall are read back from their new places, which is what puts the opening on the right piece.
function placeOnSplitWall(after: GroundPoint[], a: GroundPoint, b: GroundPoint, length: number,
  path: number[], opening: Opening): { edge: number; offset: number } | null {
  const dx = b.x - a.x, dz = b.z - a.z;
  const starts: { index: number; at: number }[] = [{ index: path[0], at: 0 }];
  let previous = 0;
  for (const index of path.slice(1, -1)) {
    const p = after[index];
    const at = Math.max(0, Math.min(length, ((p.x - a.x) * dx + (p.z - a.z) * dz) / length));
    if (at < previous - epsilon) return null; // the wall folded over itself
    starts.push({ index, at });
    previous = at;
  }
  starts.push({ index: path[path.length - 1], at: length });

  const doorStart = opening.offset, doorEnd = opening.offset + opening.width;
  for (let k = 0; k + 1 < starts.length; k++)
    if (doorStart >= starts[k].at - epsilon && doorEnd <= starts[k + 1].at + epsilon)
      return { edge: starts[k].index, offset: doorStart - starts[k].at };
  return null;
}

// The opening stays a fixed distance from one corner, along the wall that leaves it in the direction
// the old wall ran. When that edge is stored towards the corner, the distance is measured back from
// its end.
function placeFromAnchor(after: GroundPoint[], anchor: number, desired: GroundPoint, fromAnchor: number,
  width: number): { edge: number; offset: number } | null {
  const n = after.length;
  const next = (anchor + 1) % n;
  const previous = (anchor + n - 1) % n;
  const leaving = unit({ x: after[next].x - after[anchor].x, z: after[next].z - after[anchor].z });
  const arriving = unit({ x: after[previous].x - after[anchor].x, z: after[previous].z - after[anchor].z });
  const want = unit(desired);
  if (!leaving || !arriving || !want) return null;
  const forward = leaving.x * want.x + leaving.z * want.z;
  const backward = arriving.x * want.x + arriving.z * want.z;
  if (forward >= backward) {
    const edge = anchor;
    return { edge, offset: fromAnchor };
  }
  const edge = previous;
  return { edge, offset: edgeLength(after, edge) - fromAnchor - width };
}

function edgesContaining(ring: GroundPoint[], point: GroundPoint): number[] {
  const found: number[] = [];
  for (let i = 0; i < ring.length; i++)
    if (onSegment(ring[i], ring[(i + 1) % ring.length], point)) found.push(i);
  return found;
}

function offsetOnEdge(ring: GroundPoint[], edge: number, point: GroundPoint): number {
  const a = ring[edge];
  const b = ring[(edge + 1) % ring.length];
  const dx = b.x - a.x, dz = b.z - a.z;
  const length = Math.hypot(dx, dz);
  if (length < epsilon) return 0;
  return Math.max(0, ((point.x - a.x) * dx + (point.z - a.z) * dz) / length);
}

function edgeLength(ring: GroundPoint[], edge: number): number {
  return distance(ring[edge], ring[(edge + 1) % ring.length]);
}

function onSegment(a: GroundPoint, b: GroundPoint, point: GroundPoint): boolean {
  const dx = b.x - a.x, dz = b.z - a.z;
  const length = Math.hypot(dx, dz);
  if (length < epsilon) return distance(a, point) <= epsilon;
  const t = ((point.x - a.x) * dx + (point.z - a.z) * dz) / (length * length);
  if (t < -epsilon || t > 1 + epsilon) return false;
  return Math.hypot(point.x - (a.x + dx * t), point.z - (a.z + dz * t)) <= epsilon;
}

function findPoint(ring: GroundPoint[], point: GroundPoint): number {
  return ring.findIndex((p) => distance(p, point) <= epsilon);
}

function lerp(a: GroundPoint, b: GroundPoint, t: number): GroundPoint {
  return { x: a.x + (b.x - a.x) * t, z: a.z + (b.z - a.z) * t };
}

function distance(a: GroundPoint, b: GroundPoint): number {
  return Math.hypot(a.x - b.x, a.z - b.z);
}

function unit(v: GroundPoint): GroundPoint | null {
  const length = Math.hypot(v.x, v.z);
  return length < epsilon ? null : { x: v.x / length, z: v.z / length };
}

function round(value: number): number {
  return Math.round(value * centimetre) / centimetre;
}
