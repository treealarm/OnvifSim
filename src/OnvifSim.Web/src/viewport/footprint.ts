import { Vector3 } from 'three';
import { heightAt, type Terrain } from '../model';
import type { Basis } from './orientation';

// Rays across and down the picture, corners included, as ViewFootprint.cs casts them.
export const columns = 5;
export const rows = 4;

export interface Footprint {
  points: Vector3[][];
  onGround: boolean[][];
}

// Where a camera's field of view meets the ground: each ray from the eye is followed until it reaches
// the terrain, or stops at length when it meets none within reach (ViewFootprint.Cast).
export function castFootprint(eye: Vector3, basis: Basis, hFov: number, aspect: number, terrain: Terrain, reach: number, length: number): Footprint {
  const tanH = Math.tan((hFov * Math.PI) / 360);
  const tanV = tanH / aspect;
  const points: Vector3[][] = [];
  const onGround: boolean[][] = [];
  for (let i = 0; i < columns; i++) {
    points.push([]);
    onGround.push([]);
    for (let j = 0; j < rows; j++) {
      const u = (2 * i) / (columns - 1) - 1;
      const v = 1 - (2 * j) / (rows - 1);
      const direction = basis.forward.clone().addScaledVector(basis.right, u * tanH).addScaledVector(basis.up, v * tanV).normalize();
      const [point, ground] = ray(terrain, eye, direction, reach, length);
      points[i].push(point);
      onGround[i].push(ground);
    }
  }
  return { points, onGround };
}

function ray(terrain: Terrain, eye: Vector3, direction: Vector3, reach: number, length: number): [Vector3, boolean] {
  const half = terrain.size / 2;
  const at = (s: number) => eye.clone().addScaledVector(direction, s);
  const above = (s: number) => {
    const p = at(s);
    return p.y - heightAt(terrain, p.x, p.z);
  };
  const inside = (s: number) => {
    const p = at(s);
    return Math.abs(p.x) <= half && Math.abs(p.z) <= half;
  };
  if (above(0) > 0) {
    let previous = 0;
    for (let s = 0.25; s <= reach && inside(s); s += Math.max(0.25, s * 0.02)) {
      if (above(s) > 0) {
        previous = s;
        continue;
      }
      let low = previous;
      let high = s;
      for (let k = 0; k < 16; k++) {
        const mid = (low + high) / 2;
        if (above(mid) > 0) low = mid;
        else high = mid;
      }
      const hit = at(high);
      hit.y = heightAt(terrain, hit.x, hit.z);
      return [hit, true];
    }
  }
  return [at(length), false];
}

// The wire frame of a footprint as line segments, lifted off the ground like CameraMarkers does: the
// four edges from the eye, the rim, and the grid over the ground covered.
export function footprintSegments(eye: Vector3, footprint: Footprint, lift: number): [number, number, number][] {
  const { points, onGround } = footprint;
  const at = (i: number, j: number): [number, number, number] => {
    const p = points[i][j];
    return [p.x, onGround[i][j] ? p.y + lift : p.y, p.z];
  };
  const segments: [number, number, number][] = [];
  const e: [number, number, number] = [eye.x, eye.y, eye.z];
  for (const [i, j] of [[0, 0], [columns - 1, 0], [columns - 1, rows - 1], [0, rows - 1]]) segments.push(e, at(i, j));
  for (let i = 0; i < columns; i++)
    for (let j = 0; j < rows; j++) {
      const topOrBottom = j === 0 || j === rows - 1;
      const leftOrRight = i === 0 || i === columns - 1;
      if (i + 1 < columns && (topOrBottom || (onGround[i][j] && onGround[i + 1][j]))) segments.push(at(i, j), at(i + 1, j));
      if (j + 1 < rows && (leftOrRight || (onGround[i][j] && onGround[i][j + 1]))) segments.push(at(i, j), at(i, j + 1));
    }
  return segments;
}
