import { Line, TransformControls } from '@react-three/drei';
import type { ThreeEvent } from '@react-three/fiber';
import { useState } from 'react';
import { Matrix4, Quaternion, Vector3, type Object3D } from 'three';
import { heightAt, kindColor, zoomToHFov, type CameraModel, type Environment, type Head, type Terrain } from '../model';
import { select, updateCamera, useEditor } from '../store';
import { castFootprint, footprintSegments } from './footprint';
import { Label } from './Label';
import { lookOf, yawDegrees } from './orientation';

// Above the ground and the roads, as CameraMarkers lifts the grid in the camera pictures.
const wireLift = 0.2;

interface CameraGizmoProps {
  camera: CameraModel;
  head: Head | undefined;
  terrain: Terrain;
  environment: Environment;
  selected: boolean;
}

// A camera in the viewport: a body on a mast and the wire frame of what it sees, down to the ground it
// covers, which follows a PTZ head as it moves. The group turns with the mount heading, so the rotate
// gizmo sets the heading; the wire frame is drawn in world space, where the ground is.
export function CameraGizmo({ camera, head, terrain, environment, selected }: CameraGizmoProps) {
  const [group, setGroup] = useState<Object3D | null>(null);
  const gizmo = useEditor((s) => s.gizmo);
  const ground = heightAt(terrain, camera.position.x, camera.position.z);
  const { pan, tilt, hFov } = viewOf(camera, head);
  // The body inside the group, which carries the heading already.
  const body = lookOf(camera, pan, tilt, 0).head;
  const lens = new Quaternion().setFromRotationMatrix(new Matrix4().makeBasis(body.right, body.up, body.forward.clone().negate()));
  const eye = new Vector3(camera.position.x, ground + camera.position.y, camera.position.z);
  const footprint = castFootprint(eye, lookOf(camera, pan, tilt).picture, hFov, camera.video.width / camera.video.height, terrain,
    environment.fogEnd, environment.frustumLength);
  const color = camera.color ?? kindColor(camera.kind);

  function onClick(e: ThreeEvent<MouseEvent>) {
    if (e.delta > 4 || useEditor.getState().tool.kind !== 'select') return;
    e.stopPropagation();
    select({ kind: 'camera', id: camera.id });
  }

  function followGround() {
    if (group) group.position.y = heightAt(terrain, group.position.x, group.position.z) + camera.position.y;
  }

  function commit() {
    if (!group) return;
    updateCamera({
      ...camera,
      position: { x: group.position.x, y: camera.position.y, z: group.position.z },
      heading: yawDegrees(group.quaternion),
    });
  }

  return (
    <>
      <group
        ref={setGroup}
        position={[camera.position.x, ground + camera.position.y, camera.position.z]}
        rotation-y={(-camera.heading * Math.PI) / 180}
      >
        <Line points={[[0, -camera.position.y, 0], [0, 0, 0]]} color="#555555" lineWidth={1} />
        <mesh quaternion={lens} onClick={onClick}>
          <boxGeometry args={[1, 0.8, 1.6]} />
          <meshLambertMaterial color={color} emissive={selected ? '#444444' : '#000000'} />
        </mesh>
        <Label text={camera.name || camera.id} position={[0, 1.2, 0]} onClick={onClick} />
      </group>
      <Line points={footprintSegments(eye, footprint, wireLift)} segments color={color} lineWidth={selected ? 2.5 : 1.5} />
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

// Where the camera looks relative to its mount: a PTZ head where it is now (or where the world last
// stored it), a fixed camera straight along the mount.
function viewOf(camera: CameraModel, head: Head | undefined) {
  if (camera.kind !== 'ptz' || !camera.ptz) return { pan: 0, tilt: 0, hFov: camera.hFov };
  const position = head ?? camera.ptz;
  return { pan: position.pan, tilt: position.tilt, hFov: zoomToHFov(position.zoom, camera.ptz.fovWide, camera.ptz.fovTele) };
}
