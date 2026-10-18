import { Quaternion, Vector3 } from 'three';
import type { CameraModel } from '../model';

const degrees = 180 / Math.PI;

// The turn around the vertical axis a gizmo left an object at, degrees clockwise seen from above
// (0..360): how world.json counts an object's rotation and a camera's heading. Read from a direction
// rather than from Euler angles, which flip to x = z = 180 past 90 degrees.
export function yawDegrees(rotation: Quaternion): number {
  const east = new Vector3(1, 0, 0).applyQuaternion(rotation);
  return (Math.atan2(east.z, east.x) * degrees + 360) % 360;
}

// A frame in world space: where it looks and the two axes across it (up = right x forward).
export interface Basis {
  forward: Vector3;
  right: Vector3;
  up: Vector3;
}

// Forward, right and up of a camera, as Orientation.FromAngles computes them: heading clockwise from
// north (-Z), pitch up from the horizon, roll clockwise seen from behind.
export function cameraBasis(heading: number, pitch: number, roll: number): Basis {
  const h = heading / degrees;
  const p = pitch / degrees;
  const forward = new Vector3(Math.sin(h) * Math.cos(p), Math.sin(p), -Math.cos(h) * Math.cos(p));
  // From the heading rather than from forward x world up, so looking straight down still works.
  const right = new Vector3(Math.cos(h), 0, Math.sin(h));
  const up = new Vector3().crossVectors(right, forward);
  return rolled({ forward, right, up }, roll);
}

// Turned about its own forward axis, degrees clockwise as seen from behind.
export function rolled(basis: Basis, roll: number): Basis {
  if (roll === 0) return basis;
  const turn = new Quaternion().setFromAxisAngle(basis.forward, roll / degrees);
  return { forward: basis.forward, right: basis.right.clone().applyQuaternion(turn), up: basis.up.clone().applyQuaternion(turn) };
}

// A frame given relative to another (in its terms: x right, y up, z backwards), in world space.
function compose(outer: Basis, inner: Basis): Basis {
  const toWorld = (v: Vector3) =>
    outer.right.clone().multiplyScalar(v.x).addScaledVector(outer.up, v.y).addScaledVector(outer.forward, -v.z);
  return { forward: toWorld(inner.forward), right: toWorld(inner.right), up: toWorld(inner.up) };
}

// Where a camera looks, as CameraPose.From computes it: the mount is a rigid body and a PTZ head pans
// about its up axis and tilts about its right axis, then the picture turns by the video's rotate.
// heading overrides the camera's, for drawing inside a group that is already turned by it.
export function lookOf(camera: CameraModel, pan: number, tilt: number, heading = camera.heading): { head: Basis; picture: Basis } {
  const mount = cameraBasis(heading, camera.pitch, camera.roll);
  const head = camera.kind === 'ptz' ? compose(mount, cameraBasis(pan, tilt, 0)) : mount;
  return { head, picture: rolled(head, -camera.video.rotate) };
}
