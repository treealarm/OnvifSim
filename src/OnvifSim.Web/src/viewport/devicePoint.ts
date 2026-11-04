import { heightAt, vehiclePoseAt, type CameraModel, type Terrain, type Vec3, type Vehicle } from '../model';

// Where a device stands, as DeviceScene.DevicePoint places it on the server: x to the camera's right and
// z behind it in the mount's level frame, metres, and y above the ground there. A camera on a car
// carries its devices with it, and the camera's pitch and roll do not tilt them.
export function devicePoint(
  camera: CameraModel,
  offset: Vec3,
  terrain: Terrain,
  vehicles: Vehicle[],
  timeMs: number,
): { x: number; y: number; z: number } {
  const vehicle = vehicles.find((v) => v.id === camera.mountedOnVehicleId);
  const drive = vehicle ? vehiclePoseAt(vehicle, timeMs) : null;
  const turn = drive ? (-drive.heading * Math.PI) / 180 : 0;
  const cos = Math.cos(turn);
  const sin = Math.sin(turn);
  const eyeX = drive ? drive.x + camera.position.x * cos + camera.position.z * sin : camera.position.x;
  const eyeZ = drive ? drive.z - camera.position.x * sin + camera.position.z * cos : camera.position.z;
  const heading = ((camera.heading + (drive?.heading ?? 0)) * Math.PI) / 180;
  // The level frame's right and back axes at the mount heading.
  const x = eyeX + Math.cos(heading) * offset.x - Math.sin(heading) * offset.z;
  const z = eyeZ + Math.sin(heading) * offset.x + Math.cos(heading) * offset.z;
  return { x, y: heightAt(terrain, x, z) + offset.y, z };
}

// A camera whose devices move with the world rather than standing still.
export function isMounted(camera: CameraModel): boolean {
  return !!camera.mountedOnVehicleId;
}
