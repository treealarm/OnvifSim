import { heightAt, routePoseAt, type World } from '../model';
import type { Selection } from '../store';

// Where the editor camera should look for a selection: a point in the world, and how far back to
// stand from it — near enough to see a lamp, far enough to see a house whole.
export interface FocusTarget {
  point: [number, number, number];
  distance: number;
}

export function focusPoint(world: World, selection: Selection): FocusTarget | null {
  const ground = (x: number, z: number) => heightAt(world.terrain, x, z);
  if (selection.kind === 'camera') {
    const camera = world.cameras.find((c) => c.id === selection.id);
    if (!camera) return null;
    // A camera on a car stands where the car is now, as CameraGizmo places it.
    const vehicle = world.vehicles.find((v) => v.id === camera.mountedOnVehicleId);
    const drive = vehicle ? routePoseAt(vehicle.route, vehicle.speed, vehicle.return, Date.now()) : null;
    const turn = drive ? (-drive.heading * Math.PI) / 180 : 0;
    const x = drive ? drive.x + camera.position.x * Math.cos(turn) + camera.position.z * Math.sin(turn) : camera.position.x;
    const z = drive ? drive.z - camera.position.x * Math.sin(turn) + camera.position.z * Math.cos(turn) : camera.position.z;
    return { point: [x, ground(drive ? drive.x : x, drive ? drive.z : z) + camera.position.y, z], distance: 25 };
  }
  if (selection.kind === 'object') {
    const obj = world.objects.find((o) => o.id === selection.id);
    if (!obj) return null;
    const size = Math.max(obj.scale.x, obj.scale.y, obj.scale.z);
    return {
      point: [obj.position.x, ground(obj.position.x, obj.position.z) + obj.position.y + obj.scale.y / 2, obj.position.z],
      distance: size * 2 + 12,
    };
  }
  if (selection.kind === 'lamp') {
    const lamp = world.lamps.find((l) => l.id === selection.id);
    if (!lamp) return null;
    return {
      point: [lamp.position.x, ground(lamp.position.x, lamp.position.z) + lamp.position.y + 1.3, lamp.position.z],
      distance: 14,
    };
  }
  if (selection.kind === 'walker') {
    const walker = world.walkers.find((w) => w.id === selection.id);
    if (!walker) return null;
    const pose = routePoseAt(walker.route, walker.speed, walker.return, Date.now());
    return { point: [pose.x, ground(pose.x, pose.z) + walker.height / 2, pose.z], distance: 14 };
  }
  if (selection.kind === 'vehicle') {
    const car = world.vehicles.find((v) => v.id === selection.id);
    if (!car) return null;
    const pose = routePoseAt(car.route, car.speed, car.return, Date.now());
    return { point: [pose.x, ground(pose.x, pose.z) + car.height / 2, pose.z], distance: 20 };
  }
  const road = world.roads.find((r) => r.id === selection.id);
  if (!road || road.points.length === 0) return null;
  const x = road.points.reduce((sum, p) => sum + p.x, 0) / road.points.length;
  const z = road.points.reduce((sum, p) => sum + p.z, 0) / road.points.length;
  const spread = Math.max(...road.points.map((p) => Math.hypot(p.x - x, p.z - z)));
  return { point: [x, ground(x, z), z], distance: spread * 2 + 30 };
}
