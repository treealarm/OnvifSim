import { useEffect, useState } from 'react';
import type { BufferGeometry } from 'three';
import { api } from '../api';
import type { Vehicle, WorldObject } from '../model';
import { showError, useEditor } from '../store';

// A moving car is not a world object, but the server draws it from the same CarMesh: it needs only
// the body size, the paint and the plate. Geometries are cached by those settings, so a route change
// or a turn of the car does not fetch the mesh again.
const geometries = new Map<string, BufferGeometry>();
const requested = new Set<string>();

export function vehicleKey(vehicle: Vehicle): string {
  return JSON.stringify([vehicle.width, vehicle.height, vehicle.length, vehicle.color, vehicle.color2, vehicle.plate ?? '']);
}

function meshDraft(vehicle: Vehicle, key: string): WorldObject {
  return {
    id: key,
    type: 'car',
    position: { x: 0, y: 0, z: 0 },
    rotationY: 0,
    scale: { x: vehicle.width, y: vehicle.height, z: vehicle.length },
    color: vehicle.color,
    color2: vehicle.color2,
    seed: 0,
    plate: vehicle.plate ?? null,
  };
}

export function useVehicleGeometries(vehicles: Vehicle[]): Map<string, BufferGeometry> {
  const [, setLoads] = useState(0);
  useEffect(() => {
    const missing = new Map<string, WorldObject>();
    for (const vehicle of vehicles) {
      const key = vehicleKey(vehicle);
      if (!geometries.has(key) && !requested.has(key)) missing.set(key, meshDraft(vehicle, key));
    }
    if (missing.size === 0) return;
    for (const key of missing.keys()) requested.add(key);
    api
      .meshes([...missing.values()])
      .then((loaded) => {
        // Drop meshes no car uses any more, once their replacements are here.
        const used = new Set(useEditor.getState().world?.vehicles.map(vehicleKey));
        for (const [key, geometry] of geometries)
          if (!used.has(key)) {
            geometry.dispose();
            geometries.delete(key);
          }
        for (const [key, geometry] of loaded) geometries.set(key, geometry);
        setLoads((n) => n + 1);
      })
      .catch(showError)
      .finally(() => {
        for (const key of missing.keys()) requested.delete(key);
      });
  }, [vehicles]);
  return geometries;
}
