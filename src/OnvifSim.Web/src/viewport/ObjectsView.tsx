import { TransformControls } from '@react-three/drei';
import type { ThreeEvent } from '@react-three/fiber';
import { useEffect, useState } from 'react';
import type { BufferGeometry, Object3D } from 'three';
import { api } from '../api';
import { heightAt, type Terrain, type WorldObject } from '../model';
import { select, showError, updateObject, useEditor } from '../store';
import { yawDegrees } from './orientation';

// Meshes come from the server, which alone knows how to build a tree or a house. They depend on the
// shape settings only, so objects that are moved or turned keep theirs; a changed size or colour
// fetches a new one.
const geometries = new Map<string, BufferGeometry>();
const requested = new Set<string>();

function meshKey(obj: WorldObject): string {
  return JSON.stringify([obj.type, obj.scale.x, obj.scale.y, obj.scale.z, obj.color, obj.color2, obj.seed]);
}

function useObjectGeometries(objects: WorldObject[]): Map<string, BufferGeometry> {
  const [, setLoads] = useState(0);
  useEffect(() => {
    const missing = new Map<string, WorldObject>();
    for (const obj of objects) {
      const key = meshKey(obj);
      if (!geometries.has(key) && !requested.has(key)) missing.set(key, { ...obj, id: key });
    }
    if (missing.size === 0) return;
    for (const key of missing.keys()) requested.add(key);
    api
      .meshes([...missing.values()])
      .then((loaded) => {
        // Drop meshes nothing uses any more, once their replacements are here.
        const used = new Set(useEditor.getState().world?.objects.map(meshKey));
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
  }, [objects]);
  return geometries;
}

export function ObjectsView({ objects, terrain }: { objects: WorldObject[]; terrain: Terrain }) {
  const loaded = useObjectGeometries(objects);
  const selectedId = useEditor((s) => (s.selection?.kind === 'object' ? s.selection.id : null));
  return objects.map((obj) => (
    <ObjectView key={obj.id} obj={obj} terrain={terrain} geometry={loaded.get(meshKey(obj))} selected={obj.id === selectedId} />
  ));
}

interface ObjectViewProps {
  obj: WorldObject;
  terrain: Terrain;
  geometry: BufferGeometry | undefined;
  selected: boolean;
}

function ObjectView({ obj, terrain, geometry, selected }: ObjectViewProps) {
  const [group, setGroup] = useState<Object3D | null>(null);
  const gizmo = useEditor((s) => s.gizmo);
  const ground = heightAt(terrain, obj.position.x, obj.position.z);

  function onClick(e: ThreeEvent<MouseEvent>) {
    if (e.delta > 4 || useEditor.getState().tool.kind !== 'select') return;
    e.stopPropagation();
    select({ kind: 'object', id: obj.id });
  }

  // While dragged the object keeps to the ground, as it will once placed.
  function followGround() {
    if (group) group.position.y = heightAt(terrain, group.position.x, group.position.z) + obj.position.y;
  }

  function commit() {
    if (!group) return;
    updateObject({
      ...obj,
      position: { x: group.position.x, y: obj.position.y, z: group.position.z },
      rotationY: yawDegrees(group.quaternion),
    });
  }

  return (
    <>
      <group
        ref={setGroup}
        position={[obj.position.x, ground + obj.position.y, obj.position.z]}
        rotation-y={(-obj.rotationY * Math.PI) / 180}
      >
        {geometry && (
          <mesh geometry={geometry} onClick={onClick}>
            <meshLambertMaterial vertexColors emissive={selected ? '#333322' : '#000000'} />
          </mesh>
        )}
      </group>
      {selected && group && (
        <TransformControls
          object={group}
          mode={gizmo === 'move' ? 'translate' : 'rotate'}
          showX={gizmo === 'move'}
          showY={gizmo === 'rotate'}
          showZ={gizmo === 'move'}
          onObjectChange={followGround}
          onMouseUp={commit}
        />
      )}
    </>
  );
}
