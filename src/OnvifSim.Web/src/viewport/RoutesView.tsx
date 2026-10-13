import { Line } from '@react-three/drei';
import { useFrame, useThree, type ThreeEvent } from '@react-three/fiber';
import { useEffect, useMemo, useRef } from 'react';
import { BufferAttribute, BufferGeometry, type Group } from 'three';
import { heightAt, walkerPoseAt, type GroundPoint, type Road, type Terrain, type Walker } from '../model';
import { select, useEditor, type Selection } from '../store';

// Roads and walkers in the viewport. Roads lie on the ground as the server lays them; walkers walk
// their routes on the browser's clock, by the same rule as the server's (WalkerMotion.cs).

const roadLift = 0.12;
const sampleSpacing = 2;

export function RoutesView({ roads, walkers, terrain }: { roads: Road[]; walkers: Walker[]; terrain: Terrain }) {
  const selection = useEditor((s) => s.selection);
  const isSelected = (kind: Selection['kind'], id: string) => selection?.kind === kind && selection.id === id;
  return (
    <>
      {roads.map((road) => (
        <RoadView key={road.id} road={road} terrain={terrain} selected={isSelected('road', road.id)} />
      ))}
      {walkers.map((walker) => (
        <WalkerView key={walker.id} walker={walker} terrain={terrain} selected={isSelected('walker', walker.id)} />
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
  const h = walker.height;
  useFrame(() => {
    const body = figure.current;
    if (!body) return;
    const pose = walkerPoseAt(walker, Date.now());
    body.position.set(pose.x, heightAt(terrain, pose.x, pose.z), pose.z);
    body.rotation.y = (-pose.heading * Math.PI) / 180;
  });
  const route = walker.route;
  const closed = walker.return === 'loop' && route.length > 2 ? [...route, route[0]] : route;
  return (
    <>
      <group ref={figure} onClick={selectOnClick({ kind: 'walker', id: walker.id })}>
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
  let previousRow = -1;
  for (let i = 0; i < points.length - 1; i++) {
    const a = points[i];
    const b = points[i + 1];
    const length = Math.hypot(b.x - a.x, b.z - a.z);
    if (length === 0) continue;
    const steps = Math.max(1, Math.ceil(length / sampleSpacing));
    for (let s = previousRow < 0 ? 0 : 1; s <= steps; s++) {
      const t = s / steps;
      const centre = { x: a.x + (b.x - a.x) * t, z: a.z + (b.z - a.z) * t };
      const side = sideOf(points, i, s === steps ? i + 1 : -1);
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

function sideOf(points: GroundPoint[], segment: number, joint: number) {
  const across = (from: GroundPoint, to: GroundPoint) => {
    const length = Math.hypot(to.x - from.x, to.z - from.z);
    return { x: -(to.z - from.z) / length, z: (to.x - from.x) / length };
  };
  let side = across(points[segment], points[segment + 1]);
  const next = points[joint + 1];
  if (joint > 0 && next && (next.x !== points[joint].x || next.z !== points[joint].z)) {
    const other = across(points[joint], next);
    const sum = { x: side.x + other.x, z: side.z + other.z };
    const length = Math.hypot(sum.x, sum.z);
    if (length > 1e-3) side = { x: sum.x / length, z: sum.z / length };
  }
  return side;
}
