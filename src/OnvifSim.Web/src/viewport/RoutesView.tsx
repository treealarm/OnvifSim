import { Line } from '@react-three/drei';
import { useFrame, useThree, type ThreeEvent } from '@react-three/fiber';
import { useEffect, useMemo, useRef } from 'react';
import { BufferAttribute, BufferGeometry, type Group, type Mesh } from 'three';
import { heightAt, vehiclePoseAt, walkerPoseAt, type GroundPoint, type Road, type Terrain, type Vehicle, type Walker } from '../model';
import { select, useEditor, type Selection } from '../store';
import { useVehicleGeometries, vehicleKey } from './vehicleGeometry';

// Roads, walkers and moving cars in the viewport. Roads lie on the ground as the server lays them;
// walkers and cars travel their routes on the browser's clock, by the same rule as the server's
// (RouteMotion.cs).

const roadLift = 0.12;
const sampleSpacing = 2;

export function RoutesView({
  roads,
  walkers,
  vehicles,
  terrain,
}: {
  roads: Road[];
  walkers: Walker[];
  vehicles: Vehicle[];
  terrain: Terrain;
}) {
  const selection = useEditor((s) => s.selection);
  const isSelected = (kind: Selection['kind'], id: string) => selection?.kind === kind && selection.id === id;
  const geometries = useVehicleGeometries(vehicles);
  return (
    <>
      {roads.map((road) => (
        <RoadView key={road.id} road={road} terrain={terrain} selected={isSelected('road', road.id)} />
      ))}
      {walkers.map((walker) => (
        <WalkerView key={walker.id} walker={walker} terrain={terrain} selected={isSelected('walker', walker.id)} />
      ))}
      {vehicles.map((vehicle) => (
        <VehicleView
          key={vehicle.id}
          vehicle={vehicle}
          terrain={terrain}
          geometry={geometries.get(vehicleKey(vehicle))}
          selected={isSelected('vehicle', vehicle.id)}
        />
      ))}
      <DrawingPreview terrain={terrain} />
    </>
  );
}

function selectOnClick(selection: Selection) {
  return (e: ThreeEvent<MouseEvent>) => {
    if (e.delta > 4 || useEditor.getState().tool.kind !== 'select') return;
    e.stopPropagation();
    select(selection);
  };
}

function RoadView({ road, terrain, selected }: { road: Road; terrain: Terrain; selected: boolean }) {
  const geometry = useMemo(() => roadGeometry(road, terrain), [road, terrain]);
  useEffect(() => () => geometry.dispose(), [geometry]);
  return (
    <>
      <mesh geometry={geometry} onClick={selectOnClick({ kind: 'road', id: road.id })}>
        <meshLambertMaterial color={road.color ?? '#55575a'} emissive={selected ? '#333322' : '#000000'} />
      </mesh>
      {selected && <PointHandles points={road.points} terrain={terrain} />}
    </>
  );
}

function WalkerView({ walker, terrain, selected }: { walker: Walker; terrain: Terrain; selected: boolean }) {
  const figure = useRef<Group>(null);
  const proxy = useRef<Mesh>(null);
  const camera = useThree((s) => s.camera);
  const size = useThree((s) => s.size);
  const h = walker.height;
  useFrame(() => {
    const body = figure.current;
    if (!body) return;
    const pose = walkerPoseAt(walker, Date.now());
    body.position.set(pose.x, heightAt(terrain, pose.x, pose.z), pose.z);
    body.rotation.y = (-pose.heading * Math.PI) / 180;

    // The figure is a few pixels tall from the editor's usual distance, too small to hit. An invisible
    // box keeps a target about 14 px across, whatever the distance: three's raycaster ignores visibility,
    // and the group's own click handler receives the hit.
    const target = proxy.current;
    if (!target) return;
    const fov = 'fov' in camera ? camera.fov : 50;
    const worldPerPixel = (2 * camera.position.distanceTo(body.position) * Math.tan((fov * Math.PI) / 360)) / size.height;
    const width = Math.max(1.2, 14 * worldPerPixel);
    const height = Math.max(h * 1.4, width * 1.4);
    target.scale.set(width, height, width);
    target.position.y = height / 2;
  });
  const route = walker.route;
  const closed = walker.return === 'loop' && route.length > 2 ? [...route, route[0]] : route;
  return (
    <>
      <group ref={figure} onClick={selectOnClick({ kind: 'walker', id: walker.id })}>
        <mesh ref={proxy} visible={false}>
          <boxGeometry args={[1, 1, 1]} />
          <meshBasicMaterial />
        </mesh>
        <mesh position-y={h * 0.24}>
          <boxGeometry args={[h * 0.18, h * 0.48, h * 0.12]} />
          <meshLambertMaterial color={walker.color2 ?? '#3a3a44'} emissive={selected ? '#333322' : '#000000'} />
        </mesh>
        <mesh position-y={h * 0.65}>
          <boxGeometry args={[h * 0.3, h * 0.34, h * 0.14]} />
          <meshLambertMaterial color={walker.color ?? '#3b6ea8'} emissive={selected ? '#333322' : '#000000'} />
        </mesh>
        <mesh position-y={h * 0.9}>
          <sphereGeometry args={[h * 0.08, 10, 8]} />
          <meshLambertMaterial color="#d6aa82" />
        </mesh>
      </group>
      {selected && closed.length > 1 && <GroundLine points={closed} terrain={terrain} color="#ffcc33" />}
      {selected && <PointHandles points={route} terrain={terrain} />}
    </>
  );
}

function VehicleView({
  vehicle,
  terrain,
  geometry,
  selected,
}: {
  vehicle: Vehicle;
  terrain: Terrain;
  geometry: BufferGeometry | undefined;
  selected: boolean;
}) {
  const body = useRef<Group>(null);
  useFrame(() => {
    const group = body.current;
    if (!group) return;
    const pose = vehiclePoseAt(vehicle, Date.now());
    group.position.set(pose.x, heightAt(terrain, pose.x, pose.z), pose.z);
    group.rotation.y = (-pose.heading * Math.PI) / 180;
  });
  const route = vehicle.route;
  const closed = vehicle.return === 'loop' && route.length > 2 ? [...route, route[0]] : route;
  return (
    <>
      <group ref={body} onClick={selectOnClick({ kind: 'vehicle', id: vehicle.id })}>
        {geometry && (
          <mesh geometry={geometry}>
            <meshLambertMaterial vertexColors emissive={selected ? '#333322' : '#000000'} />
          </mesh>
        )}
      </group>
      {selected && closed.length > 1 && <GroundLine points={closed} terrain={terrain} color="#ff9933" />}
      {selected && <PointHandles points={route} terrain={terrain} />}
    </>
  );
}

// The points of the selected road or route; dragging one moves it over the ground.
function PointHandles({ points, terrain }: { points: GroundPoint[]; terrain: Terrain }) {
  const controls = useThree((s) => s.controls);
  function grab(e: ThreeEvent<PointerEvent>, index: number) {
    if (e.button !== 0 || useEditor.getState().tool.kind !== 'select') return;
    e.stopPropagation();
    if (controls && 'enabled' in controls) controls.enabled = false;
    useEditor.setState({ draggedPoint: index });
    const release = () => {
      window.removeEventListener('pointerup', release);
      if (controls && 'enabled' in controls) controls.enabled = true;
      useEditor.setState({ draggedPoint: null });
    };
    window.addEventListener('pointerup', release);
  }
  return points.map((p, index) => (
    <mesh key={index} position={[p.x, heightAt(terrain, p.x, p.z) + 0.6, p.z]} onPointerDown={(e) => grab(e, index)}>
      <sphereGeometry args={[0.9, 12, 8]} />
      <meshBasicMaterial color={index === 0 ? '#44dd66' : '#ffcc33'} />
    </mesh>
  ));
}

// The points clicked so far while drawing a road or a route.
function DrawingPreview({ terrain }: { terrain: Terrain }) {
  const tool = useEditor((s) => s.tool);
  if (tool.kind !== 'draw' || tool.points.length === 0) return null;
  return (
    <>
      {tool.points.length > 1 && <GroundLine points={tool.points} terrain={terrain} color="#ffffff" />}
      {tool.points.map((p, i) => (
        <mesh key={i} position={[p.x, heightAt(terrain, p.x, p.z) + 0.6, p.z]} raycast={() => null}>
          <sphereGeometry args={[0.7, 10, 8]} />
          <meshBasicMaterial color="#ffffff" />
        </mesh>
      ))}
    </>
  );
}

function GroundLine({ points, terrain, color }: { points: GroundPoint[]; terrain: Terrain; color: string }) {
  return <Line points={points.map((p) => [p.x, heightAt(terrain, p.x, p.z) + 0.4, p.z] as const)} color={color} lineWidth={2} />;
}

// The strip SceneBuilder.RoadMesh lays: sampled every couple of metres along and across, so it
// follows the ground, and turned halfway at each joint.
function roadGeometry(road: Road, terrain: Terrain): BufferGeometry {
  const points = road.points;
  const half = road.width / 2;
  const across = Math.max(2, Math.ceil(road.width / sampleSpacing));
  const positions: number[] = [];
  const indices: number[] = [];
  const segments = road.closed ? points.length : points.length - 1;
  let previousRow = -1;
  for (let i = 0; i < segments; i++) {
    const a = points[i];
    const b = points[(i + 1) % points.length];
    const length = Math.hypot(b.x - a.x, b.z - a.z);
    if (length === 0) continue;
    const steps = Math.max(1, Math.ceil(length / sampleSpacing));
    for (let s = previousRow < 0 ? 0 : 1; s <= steps; s++) {
      const t = s / steps;
      const centre = { x: a.x + (b.x - a.x) * t, z: a.z + (b.z - a.z) * t };
      // At a joint the two segments share one edge; a closed road's last row meets its first.
      const side =
        s === 0 && i === 0
          ? jointSide(points, 0, road.closed)
          : s === steps
            ? jointSide(points, (i + 1) % points.length, road.closed)
            : acrossOf(a, b);
      const row = positions.length / 3;
      for (let k = 0; k <= across; k++) {
        const offset = half * ((2 * k) / across - 1);
        const x = centre.x + side.x * offset;
        const z = centre.z + side.z * offset;
        positions.push(x, heightAt(terrain, x, z) + roadLift, z);
      }
      if (previousRow >= 0)
        for (let k = 0; k < across; k++) {
          const p0 = previousRow + k;
          const q0 = row + k;
          indices.push(p0, p0 + 1, q0, p0 + 1, q0 + 1, q0);
        }
      previousRow = row;
    }
  }
  const geometry = new BufferGeometry();
  geometry.setAttribute('position', new BufferAttribute(new Float32Array(positions), 3));
  geometry.setIndex(indices);
  geometry.computeVertexNormals();
  return geometry;
}

function acrossOf(from: GroundPoint, to: GroundPoint) {
  const length = Math.hypot(to.x - from.x, to.z - from.z);
  return { x: -(to.z - from.z) / length, z: (to.x - from.x) / length };
}

function jointSide(points: GroundPoint[], joint: number, closed: boolean) {
  const n = points.length;
  const incoming = (joint - 1 + n) % n;
  const outgoing = (joint + 1) % n;
  let x = 0;
  let z = 0;
  const add = (from: GroundPoint, to: GroundPoint) => {
    const side = acrossOf(from, to);
    x += side.x;
    z += side.z;
  };
  if ((closed || joint > 0) && (points[incoming].x !== points[joint].x || points[incoming].z !== points[joint].z))
    add(points[incoming], points[joint]);
  if ((closed || joint < n - 1) && (points[outgoing].x !== points[joint].x || points[outgoing].z !== points[joint].z))
    add(points[joint], points[outgoing]);
  const length = Math.hypot(x, z);
  if (length > 1e-3) return { x: x / length, z: z / length };
  // A joint surrounded by zero-length segments has no direction; take any segment that has one.
  for (let i = 0; i < (closed ? n : n - 1); i++) {
    const from = points[i];
    const to = points[(i + 1) % n];
    if (from.x !== to.x || from.z !== to.z) return acrossOf(from, to);
  }
  return { x: 1, z: 0 };
}
