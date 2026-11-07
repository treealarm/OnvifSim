import { Raycaster, Vector3, type Object3D } from 'three';
import type { Basis } from './orientation';

// Rays across and down a camera's picture, corners included — the same grid the server draws into the
// camera pictures (Geometry/ViewFootprint.cs), few because it is a sketch.
export const FOOTPRINT_COLUMNS = 5;
export const FOOTPRINT_ROWS = 4;

export interface Footprint {
  // Row-major, index i * FOOTPRINT_ROWS + j: corner (0, 0) is the top left of the picture.
  points: Vector3[];
  // Whether that ray ended on the ground or an object, rather than in mid-air at `length`.
  hit: boolean[];
}

// A camera's picture cast into the world as a grid of rays: each is followed from the eye until it
// meets an object or the ground, or stops at `length` where it meets neither within reach. The grid,
// not merely its four corners, is what shows which part of the ground a camera sees — a corner ray
// that climbs over the horizon ends in mid-air and alone would draw a prism with no floor under it.
export function castFootprint(
  eye: Vector3,
  basis: Basis,
  hFov: number,
  aspect: number,
  occluders: Object3D[],
  reach: number,
  length: number,
): Footprint {
  const tanH = Math.tan((hFov * Math.PI) / 360);
  const tanV = tanH / aspect;
  const raycaster = new Raycaster(eye, new Vector3(0, 0, -1), 0, reach);
  const points: Vector3[] = [];
  const hit: boolean[] = [];
  for (let i = 0; i < FOOTPRINT_COLUMNS; i++) {
    const u = (2 * i) / (FOOTPRINT_COLUMNS - 1) - 1;
    for (let j = 0; j < FOOTPRINT_ROWS; j++) {
      const v = 1 - (2 * j) / (FOOTPRINT_ROWS - 1);
      const direction = basis.forward.clone()
        .addScaledVector(basis.right, u * tanH)
        .addScaledVector(basis.up, v * tanV)
        .normalize();
      raycaster.set(eye, direction);
      const found = raycaster.intersectObjects(occluders, false)[0];
      points.push(found ? found.point.clone() : eye.clone().addScaledVector(direction, length));
      hit.push(found !== undefined);
    }
  }
  return { points, hit };
}
