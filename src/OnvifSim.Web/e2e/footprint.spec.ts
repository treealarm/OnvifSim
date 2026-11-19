import { expect, test } from '@playwright/test';
import {
  insertOnEdge, normalizeRings, remapOpenings, toLocalPoint, toWorldPoint, withPoint, type Footprint,
} from '../src/footprint';
import { normalizeOutline, type GroundPoint, type Opening } from '../src/model';

// The footprint arithmetic behind dragging a building's vertices: no browser, no server. It is what
// keeps an opening on its wall while the wall moves, splits or turns.

const same = (a: GroundPoint, b: GroundPoint) => Math.hypot(a.x - b.x, a.z - b.z) < 1e-6;

function rectangle(): GroundPoint[] {
  return normalizeOutline([{ x: 0, z: 0 }, { x: 40, z: 0 }, { x: 40, z: 10 }, { x: 0, z: 10 }]);
}

// The wall between two corners as the ring stores it, whichever way round it runs.
function wallOf(ring: GroundPoint[], p: GroundPoint, q: GroundPoint) {
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i], b = ring[(i + 1) % ring.length];
    if ((same(a, p) && same(b, q)) || (same(a, q) && same(b, p))) return { edge: i, from: a, to: b };
  }
  throw new Error('no such wall');
}

function span(ring: GroundPoint[], opening: Opening): [GroundPoint, GroundPoint] {
  const a = ring[opening.edge];
  const b = ring[(opening.edge + 1) % ring.length];
  const length = Math.hypot(b.x - a.x, b.z - a.z);
  const at = (distance: number) => ({ x: a.x + ((b.x - a.x) * distance) / length, z: a.z + ((b.z - a.z) * distance) / length });
  return [at(opening.offset), at(opening.offset + opening.width)];
}

function signedArea(ring: GroundPoint[]): number {
  let sum = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i], b = ring[(i + 1) % ring.length];
    sum += a.x * b.z - b.x * a.z;
  }
  return sum / 2;
}

function movedCorner(ring: GroundPoint[], corner: GroundPoint, by: GroundPoint): GroundPoint[] {
  return ring.map((p) => (same(p, corner) ? { x: p.x + by.x, z: p.z + by.z } : p));
}

test('an opening keeps its place when a corner of its wall is pulled out', () => {
  const before = rectangle();
  const wall = wallOf(before, { x: 0, z: 0 }, { x: 40, z: 0 });
  const opening = { edge: wall.edge, offset: 10, width: 2, height: 2.2, sill: 0 };
  const old = span(before, opening);

  // Pull the far corner ten metres further along the wall: its length changes, the door does not.
  const direction = { x: (wall.to.x - wall.from.x) / 40, z: (wall.to.z - wall.from.z) / 40 };
  const after = movedCorner(before, wall.to, { x: direction.x * 10, z: direction.z * 10 });
  const result = remapOpenings(before, after, [opening]);
  if (!('openings' in result)) throw new Error(result.error);

  const [start, end] = span(after, result.openings[0]);
  expect(start.x).toBeCloseTo(old[0].x, 6);
  expect(start.z).toBeCloseTo(old[0].z, 6);
  expect(end.x).toBeCloseTo(old[1].x, 6);
  expect(end.z).toBeCloseTo(old[1].z, 6);
});

test('an opening keeps its place when the corner it was measured from moves', () => {
  const before = rectangle();
  const wall = wallOf(before, { x: 0, z: 0 }, { x: 40, z: 0 });
  const opening = { edge: wall.edge, offset: 10, width: 2, height: 2.2, sill: 0 };
  const old = span(before, opening);

  // The corner the offset is measured from moves away; the door stays with the other corner.
  const direction = { x: (wall.from.x - wall.to.x) / 40, z: (wall.from.z - wall.to.z) / 40 };
  const after = movedCorner(before, wall.from, { x: direction.x * 10, z: direction.z * 10 });
  const result = remapOpenings(before, after, [opening]);
  if (!('openings' in result)) throw new Error(result.error);

  const [start, end] = span(after, result.openings[0]);
  expect(start.x).toBeCloseTo(old[0].x, 6);
  expect(start.z).toBeCloseTo(old[0].z, 6);
  expect(end.x).toBeCloseTo(old[1].x, 6);
  expect(end.z).toBeCloseTo(old[1].z, 6);
});

test('an opening stays on the piece of wall it fell on when a corner is inserted there', () => {
  const before = rectangle();
  const wall = wallOf(before, { x: 0, z: 0 }, { x: 40, z: 0 });
  const opening = { edge: wall.edge, offset: 10, width: 2, height: 2.2, sill: 0 };
  const frame: Footprint = { points: before, holes: [] };

  // Split the wall in its middle and bend the new corner a metre off the line. The door is 10 m from
  // the wall's first corner, so it sits well before the split and stays on the first piece.
  const inserted = insertOnEdge(frame, -1, wall.edge);
  const middle = inserted.frame.points[inserted.index];
  const bent = withPoint(inserted.frame, -1, inserted.index, { x: middle.x, z: middle.z + 1 });
  const result = remapOpenings(before, bent.points, [opening]);
  if (!('openings' in result)) throw new Error(result.error);

  // The first piece runs from the wall's first corner to the split: the opening is still 10 m along it.
  const [start] = span(bent.points, result.openings[0]);
  expect(Math.hypot(start.x - wall.from.x, start.z - wall.from.z)).toBeCloseTo(10, 2);
});

test('an opening whose wall is split inside it is refused, not moved', () => {
  const before = rectangle();
  const wall = wallOf(before, { x: 0, z: 0 }, { x: 40, z: 0 });
  const opening = { edge: wall.edge, offset: 19, width: 2, height: 2.2, sill: 0 };
  const frame: Footprint = { points: before, holes: [] };

  // The split lands on the door's 19..21 metres: there is no straight wall left for it.
  const inserted = insertOnEdge(frame, -1, wall.edge);
  const middle = inserted.frame.points[inserted.index];
  const bent = withPoint(inserted.frame, -1, inserted.index, { x: middle.x, z: middle.z + 1 });
  const result = remapOpenings(before, bent.points, [opening]);

  expect('error' in result).toBe(true);
});

test('an opening that no longer fits its wall is refused', () => {
  const before = rectangle();
  const wall = wallOf(before, { x: 0, z: 0 }, { x: 40, z: 0 });
  const opening = { edge: wall.edge, offset: 30, width: 5, height: 2.2, sill: 0 };

  // Pull the far corner in until the wall is only 20 m long: the door's 30..35 does not fit.
  const direction = { x: (wall.to.x - wall.from.x) / 40, z: (wall.to.z - wall.from.z) / 40 };
  const after = movedCorner(before, wall.to, { x: direction.x * -20, z: direction.z * -20 });
  const result = remapOpenings(before, after, [opening]);

  expect('error' in result).toBe(true);
});

test('normalizing keeps the outline one way and its courtyards the other way', () => {
  const frame: Footprint = {
    points: [{ x: 0, z: 0 }, { x: 10, z: 0 }, { x: 20, z: 0 }, { x: 20, z: 10 }, { x: 0, z: 10 }],
    holes: [[{ x: 5, z: 3 }, { x: 15, z: 3 }, { x: 15, z: 7 }, { x: 5, z: 7 }]],
  };
  const rings = normalizeRings(frame);

  expect(rings.points).toHaveLength(4); // the point on the straight edge is gone
  expect(signedArea(rings.points)).toBeLessThan(0);
  expect(signedArea(rings.holes[0])).toBeGreaterThan(0);
});

test('a point survives the turn and the place of the object it belongs to', () => {
  const obj = { position: { x: 10, y: 0, z: 20 }, rotationY: 37 };
  const local = { x: 3, z: -4 };
  const back = toLocalPoint(obj, toWorldPoint(obj, local));
  expect(back.x).toBeCloseTo(local.x, 9);
  expect(back.z).toBeCloseTo(local.z, 9);
});
