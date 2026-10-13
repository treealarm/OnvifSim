import { Quaternion, Vector3 } from 'three';

const degrees = 180 / Math.PI;

// The turn around the vertical axis a gizmo left an object at, degrees clockwise seen from above
// (0..360): how world.json counts an object's rotation and a camera's heading. Read from a direction
// rather than from Euler angles, which flip to x = z = 180 past 90 degrees.
export function yawDegrees(rotation: Quaternion): number {
  const east = new Vector3(1, 0, 0).applyQuaternion(rotation);
  return (Math.atan2(east.z, east.x) * degrees + 360) % 360;
}

// Forward, right and up of a camera, as CameraPose.Basis computes them: heading clockwise from north
// (-Z), pitch up from the horizon, roll clockwise seen from behind.
export function cameraBasis(heading: number, pitch: number, roll: number) {
  const h = heading / degrees;
  const p = pitch / degrees;
  const forward = new Vector3(Math.sin(h) * Math.cos(p), Math.sin(p), -Math.cos(h) * Math.cos(p));
  // From the heading rather than from forward x world up, so looking straight down still works.
  const right = new Vector3(Math.cos(h), 0, Math.sin(h));
  const up = new Vector3().crossVectors(right, forward);
  if (roll !== 0) {
    const turn = new Quaternion().setFromAxisAngle(forward, roll / degrees);
    right.applyQuaternion(turn);
    up.applyQuaternion(turn);
  }
  return { forward, right, up };
}
