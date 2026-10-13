import { Line, TransformControls } from '@react-three/drei';
import type { ThreeEvent } from '@react-three/fiber';
import { useState } from 'react';
import { Matrix4, Quaternion, Vector3, type Object3D } from 'three';
import { heightAt, zoomToHFov, type CameraModel, type Head, type Terrain } from '../model';
import { select, updateCamera, useEditor } from '../store';
import { Label } from './Label';
import { cameraBasis, yawDegrees } from './orientation';

const frustumLength = 20;

interface CameraGizmoProps {
  camera: CameraModel;
  head: Head | undefined;
  terrain: Terrain;
  selected: boolean;
}

// A camera in the viewport: a body on a mast and the pyramid it sees, which follows a PTZ head as it
// moves. The group turns with the mount heading, so the rotate gizmo sets the heading.
export function CameraGizmo({ camera, head, terrain, selected }: CameraGizmoProps) {
  const [group, setGroup] = useState<Object3D | null>(null);
  const gizmo = useEditor((s) => s.gizmo);
  const ground = heightAt(terrain, camera.position.x, camera.position.z);
  const { pan, tilt, hFov } = viewOf(camera, head);
  const { forward, right, up } = cameraBasis(pan, camera.pitch + tilt, camera.roll);
  const lens = new Quaternion().setFromRotationMatrix(new Matrix4().makeBasis(right, up, forward.clone().negate()));
  const corners = frustumCorners(forward, right, up, hFov, camera.video.height / camera.video.width);
  const color = camera.kind === 'ptz' ? '#ff9f1c' : '#3aa0ff';

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
        <Line points={corners.flatMap((corner, i) => [[0, 0, 0], corner, corner, corners[(i + 1) % 4]])} segments color={color} lineWidth={selected ? 2.5 : 1.5} />
        <Label text={camera.name || camera.id} position={[0, 1.2, 0]} onClick={onClick} />
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

// Where the camera looks relative to its mount: a PTZ head where it is now (or where the world last
// stored it), a fixed camera straight along the mount.
function viewOf(camera: CameraModel, head: Head | undefined) {
  if (camera.kind !== 'ptz' || !camera.ptz) return { pan: 0, tilt: 0, hFov: camera.hFov };
  const position = head ?? camera.ptz;
  return { pan: position.pan, tilt: position.tilt, hFov: zoomToHFov(position.zoom, camera.ptz.fovWide, camera.ptz.fovTele) };
}

function frustumCorners(forward: Vector3, right: Vector3, up: Vector3, hFov: number, aspect: number): [number, number, number][] {
  const halfWidth = Math.tan((hFov * Math.PI) / 360) * frustumLength;
  const halfHeight = halfWidth * aspect;
  const centre = forward.clone().multiplyScalar(frustumLength);
  return [
    [1, 1],
    [-1, 1],
    [-1, -1],
    [1, -1],
  ].map(([sx, sy]) => {
    const corner = centre.clone().addScaledVector(right, sx * halfWidth).addScaledVector(up, sy * halfHeight);
    return [corner.x, corner.y, corner.z];
  });
}
