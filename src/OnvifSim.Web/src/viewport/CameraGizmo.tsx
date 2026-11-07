import { Line, TransformControls } from '@react-three/drei';
import { useFrame, useThree, type ThreeEvent } from '@react-three/fiber';
import { useRef, useState } from 'react';
import { Matrix4, Quaternion, Vector3, type Object3D } from 'three';
import {
  heightAt,
  kindColor,
  vehiclePoseAt,
  zoomToHFov,
  type CameraModel,
  type Environment,
  type Head,
  type Terrain,
  type Vehicle,
} from '../model';
import { clearSelection, pickFromOsm, select, updateCamera, useEditor } from '../store';
import { castFootprint, FOOTPRINT_COLUMNS, FOOTPRINT_ROWS, type Footprint } from './footprint';
import { Label } from './Label';
import { lookOf, yawDegrees } from './orientation';

// Above whatever the view lands on, as CameraMarkers lifts the grid in the camera pictures.
const wireLift = 0.2;

interface CameraGizmoProps {
  camera: CameraModel;
  head: Head | undefined;
  terrain: Terrain;
  environment: Environment;
  vehicles: Vehicle[];
  // What this camera's view is drawn in, one colour per camera so overlapping views stay apart.
  viewColor: string;
  selected: boolean;
}

// A camera in the viewport: a body on a mast and a prism of what it sees, standing on the first thing
// in its way, which follows a PTZ head as it moves. The group turns with the mount heading, so the
// rotate gizmo sets the heading; the prism is drawn in world space, where the things it meets are.
export function CameraGizmo({ camera, head, terrain, environment, vehicles, viewColor, selected }: CameraGizmoProps) {
  const [group, setGroup] = useState<Object3D | null>(null);
  const [footprint, setFootprint] = useState<Footprint | null>(null);
  const drawn = useRef('');
  const scene = useThree((s) => s.scene);
  const gizmo = useEditor((s) => s.gizmo);
  const mountedVehicle = vehicles.find((v) => v.id === camera.mountedOnVehicleId);
  const drive = mountedVehicle ? vehiclePoseAt(mountedVehicle, Date.now()) : null;
  const turn = drive ? (-drive.heading * Math.PI) / 180 : 0;
  const cos = Math.cos(turn);
  const sin = Math.sin(turn);
  const x = drive ? drive.x + camera.position.x * cos + camera.position.z * sin : camera.position.x;
  const z = drive ? drive.z - camera.position.x * sin + camera.position.z * cos : camera.position.z;
  const ground = drive ? heightAt(terrain, drive.x, drive.z) : heightAt(terrain, x, z);
  const worldHeading = camera.heading + (drive?.heading ?? 0);
  const { pan, tilt, hFov } = viewOf(camera, head);
  // The body inside the group, which carries the heading already.
  const body = lookOf(camera, pan, tilt, 0).head;
  const lens = new Quaternion().setFromRotationMatrix(new Matrix4().makeBasis(body.right, body.up, body.forward.clone().negate()));
  const eye = new Vector3(x, ground + camera.position.y, z);
  const color = camera.color ?? kindColor(camera.kind);

  // A camera on a car drives with it; the group is moved directly rather than through React state, so
  // the editor follows the car at frame rate. Its field of view is not redrawn every frame: the
  // picture in the camera panel is the ground truth for where it looks.
  useFrame(() => {
    if (!mountedVehicle || !group) return;
    const pose = vehiclePoseAt(mountedVehicle, Date.now());
    const heading = (-pose.heading * Math.PI) / 180;
    const c = Math.cos(heading);
    const s = Math.sin(heading);
    group.position.set(
      pose.x + camera.position.x * c + camera.position.z * s,
      heightAt(terrain, pose.x, pose.z) + camera.position.y,
      pose.z - camera.position.x * s + camera.position.z * c);
    group.rotation.y = -((camera.heading + pose.heading) * Math.PI) / 180;
  });

  // The prism's base is where the view meets the world, so it is cast against the scene's own meshes:
  // the ground and the objects standing on it. They arrive from the server asynchronously, and the
  // camera, a PTZ head or an object may have moved since, so this looks for a change every frame and
  // casts only when there is one.
  useFrame(() => {
    if (mountedVehicle) return;
    const occluders: Object3D[] = [];
    scene.traverse((object) => {
      if (object.userData.occluder) occluders.push(object);
    });
    const aspect = camera.video.width / camera.video.height;
    let stamp = `${eye.x.toFixed(1)},${eye.y.toFixed(1)},${eye.z.toFixed(1)},${pan.toFixed(1)},${tilt.toFixed(1)},${hFov.toFixed(1)},${aspect},`
      + `${environment.fogEnd},${environment.frustumLength}`;
    for (const occluder of occluders) stamp += '|' + occluder.matrixWorld.elements.map((value) => value.toFixed(2)).join(',');
    if (stamp === drawn.current) return;
    drawn.current = stamp;
    setFootprint(castFootprint(eye, lookOf(camera, pan, tilt).picture, hFov, aspect, occluders,
      environment.fogEnd, environment.frustumLength));
  });

  function onClick(e: ThreeEvent<MouseEvent>) {
    if (e.delta > 4) return;
    const tool = useEditor.getState().tool;
    // Picking from the real world: a camera stands on the ground it is mounted over, so that is the
    // spot the click asks about.
    if (tool.kind === 'osm') {
      e.stopPropagation();
      clearSelection();
      void pickFromOsm(camera.position.x, camera.position.z);
      return;
    }
    if (tool.kind !== 'select') return;
    e.stopPropagation();
    select({ kind: 'camera', id: camera.id });
  }

  function followGround() {
    if (!mountedVehicle && group) group.position.y = heightAt(terrain, group.position.x, group.position.z) + camera.position.y;
  }

  function commit() {
    if (!group || mountedVehicle) return;
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
        position={[x, ground + camera.position.y, z]}
        rotation-y={(-worldHeading * Math.PI) / 180}
      >
        {!mountedVehicle && <Line points={[[0, -camera.position.y, 0], [0, 0, 0]]} color="#555555" lineWidth={1} />}
        <mesh quaternion={lens} onClick={onClick}>
          <boxGeometry args={[1, 0.8, 1.6]} />
          <meshLambertMaterial color={color} emissive={selected ? '#444444' : '#000000'} />
        </mesh>
        <Label text={camera.name || camera.id} position={[0, 1.2, 0]} onClick={onClick} />
      </group>
      {!mountedVehicle && footprint && <FootprintPrism eye={eye} footprint={footprint} color={viewColor} selected={selected} />}
      {!mountedVehicle && selected && group && (
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

// Where a camera looks, as the prism its eye and its picture make: the four edges down to the picture's
// corners, the rim where the picture meets the world, and the grid of rays inside it wherever they land
// on something. That last part is the point — the patch of ground (or wall) the camera covers — because
// a rim alone, on a camera looking at the horizon, floats in the air with no floor under it. Rays that
// land on nothing are left out of the interior: a grid of lines into the sky hides what it is drawn on.
function FootprintPrism({ eye, footprint, color, selected }: {
  eye: Vector3; footprint: Footprint; color: string; selected: boolean
}) {
  const at = (p: Vector3): [number, number, number] => [p.x, p.y + wireLift, p.z];
  const columns = FOOTPRINT_COLUMNS, rows = FOOTPRINT_ROWS;
  const point = (i: number, j: number) => footprint.points[i * rows + j];
  const hit = (i: number, j: number) => footprint.hit[i * rows + j];
  const V = (i: number, j: number) => at(point(i, j));

  // The rim, anticlockwise as seen in the picture: the top row, the right column, the bottom row
  // backwards, the left column upwards.
  const rim: [number, number, number][] = [];
  for (let i = 0; i < columns; i++) rim.push(V(i, 0));
  for (let j = 1; j < rows; j++) rim.push(V(columns - 1, j));
  for (let i = columns - 2; i >= 0; i--) rim.push(V(i, rows - 1));
  for (let j = rows - 2; j >= 1; j--) rim.push(V(0, j));

  const rays = [[0, 0], [columns - 1, 0], [columns - 1, rows - 1], [0, rows - 1]]
    .flatMap(([i, j]) => [[eye.x, eye.y, eye.z] as [number, number, number], V(i, j)]);

  // Lines across the picture, kept to those whose two ends both landed on something: down a column,
  // and along a row.
  const grid: [number, number, number][] = [];
  for (let i = 0; i < columns; i++)
    for (let j = 0; j < rows - 1; j++)
      if (hit(i, j) && hit(i, j + 1)) grid.push(...[V(i, j), V(i, j + 1)]);
  for (let i = 0; i < columns - 1; i++)
    for (let j = 0; j < rows; j++)
      if (hit(i, j) && hit(i + 1, j)) grid.push(...[V(i, j), V(i + 1, j)]);

  const width = selected ? 2.2 : 1;
  return (
    <>
      <Line points={[...rim, rim[0]]} color={color} lineWidth={width} transparent opacity={selected ? 0.95 : 0.55} />
      <Line points={rays} segments color={color} lineWidth={width} transparent opacity={selected ? 0.95 : 0.55} />
      {grid.length > 0 && (
        <Line points={grid} segments color={color} lineWidth={width} transparent opacity={selected ? 0.85 : 0.4} />
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
