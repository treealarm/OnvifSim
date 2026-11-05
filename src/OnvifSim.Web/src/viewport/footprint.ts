import { Raycaster, Vector3, type Object3D } from 'three';
import type { Basis } from './orientation';

// The four corners of a camera's picture cast into the world, in order round the picture: each ray is
// followed from the eye until it meets an object or the ground, or stops at length where it meets
// neither within reach. They are the base of the prism a camera's view is drawn as — the intersection
// with what stands in the way, which is what tells a camera looking at a wall from one looking across
// a field.
export function castCorners(
  eye: Vector3,
  basis: Basis,
  hFov: number,
  aspect: number,
  occluders: Object3D[],
  reach: number,
  length: number,
): Vector3[] {
  const tanH = Math.tan((hFov * Math.PI) / 360);
  const tanV = tanH / aspect;
  const raycaster = new Raycaster(eye, new Vector3(0, 0, -1), 0, reach);
  return ([[-1, 1], [1, 1], [1, -1], [-1, -1]] as const).map(([u, v]) => {
    const direction = basis.forward.clone()
      .addScaledVector(basis.right, u * tanH)
      .addScaledVector(basis.up, v * tanV)
      .normalize();
    raycaster.set(eye, direction);
    const hit = raycaster.intersectObjects(occluders, false)[0];
    return hit ? hit.point.clone() : eye.clone().addScaledVector(direction, length);
  });
}
