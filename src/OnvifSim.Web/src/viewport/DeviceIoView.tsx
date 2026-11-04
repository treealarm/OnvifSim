import { DoubleSide, type Plane } from 'three';
import type { ThreeEvent } from '@react-three/fiber';
import { defaultLampColor, heightAt, lampLit as lampIsLit, type Lamp as LampDevice, type Terrain, type World } from '../model';
import { select, useEditor, type Selection } from '../store';
import { useCutawayPlanes } from './cutaway';
import { Label } from './Label';

// As Geometry/DeviceMesh.cs builds the lamp on the server.
const lampHeight = 2.6;
const bulbHeight = 2.32;
const bulbRadius = 0.28;
const lampDark = '#5c5e62';

// The lamps of the world: actuators a relay drives, standing where they were placed. A camera's own
// relay outputs and digital inputs are settings of the camera — a client reads and writes them over
// ONVIF — not shapes in the world, so they are shown on its picture instead (CameraPicture).
export function DeviceIoView({ world }: { world: World }) {
  const clippingPlanes = useCutawayPlanes();
  return (
    <>
      {world.lamps.map((lamp) => (
        <WorldLamp
          key={lamp.id}
          lamp={lamp}
          lit={lampIsLit(world, lamp.id)}
          terrain={world.terrain}
          clippingPlanes={clippingPlanes}
        />
      ))}
    </>
  );
}

// A classic lamp post: a pole, a collar, and a round bulb that glows in its colour while lit.
function LampPost({ lit, glow, clippingPlanes }: { lit: boolean; glow: string; clippingPlanes: Plane[] }) {
  const color = lit ? glow : lampDark;
  return (
    <>
      <mesh position={[0, 1.05, 0]}>
        <cylinderGeometry args={[0.045, 0.07, 2.1, 8]} />
        <meshLambertMaterial color="#424448" clippingPlanes={clippingPlanes} />
      </mesh>
      <mesh position={[0, 2.16, 0]}>
        <cylinderGeometry args={[0.1, 0.1, 0.12, 8]} />
        <meshLambertMaterial color="#424448" clippingPlanes={clippingPlanes} />
      </mesh>
      <mesh position={[0, bulbHeight, 0]}>
        <sphereGeometry args={[bulbRadius, 16, 12]} />
        <meshLambertMaterial
          color={color}
          emissive={lit ? color : '#000000'}
          emissiveIntensity={0.6}
          clippingPlanes={clippingPlanes}
        />
      </mesh>
    </>
  );
}

// A lamp of the world: it stands where it was put and never moves with a camera. It has a name tag of
// its own — a lamp is a metre of post at the world's scale, and one inside a building is under its
// roof — and a ring under it while it is the selection.
function WorldLamp({
  lamp,
  lit,
  terrain,
  clippingPlanes,
}: {
  lamp: LampDevice;
  lit: boolean;
  terrain: Terrain;
  clippingPlanes: Plane[];
}) {
  const selected = useEditor((s) => s.selection?.kind === 'lamp' && s.selection.id === lamp.id);
  const position: [number, number, number] = [
    lamp.position.x,
    heightAt(terrain, lamp.position.x, lamp.position.z) + lamp.position.y,
    lamp.position.z,
  ];
  const pickLamp = (e: ThreeEvent<MouseEvent>) => pick(e, { kind: 'lamp', id: lamp.id });
  return (
    <group position={position} onClick={pickLamp}>
      <LampPost lit={lit} glow={lamp.color ?? defaultLampColor} clippingPlanes={clippingPlanes} />
      <Label text={`${lamp.name || lamp.id} · ${lit ? 'lit' : 'off'}`} position={[0, lampHeight + 0.3, 0]} renderOrder={20} onClick={pickLamp} />
      {selected && (
        // A ring several metres wide, drawn over everything, roof included: a lamp is a metre of post
        // at the world's scale, and one inside a building is under its roof.
        <mesh rotation-x={-Math.PI / 2} position={[0, 0.06, 0]} renderOrder={9}>
          <ringGeometry args={[3, 3.6, 48]} />
          <meshBasicMaterial color="#2f6fde" side={DoubleSide} transparent opacity={0.8} depthTest={false} />
        </mesh>
      )}
    </group>
  );
}

// A click picks what it hits, as in the objects view: not while a tool is active, and not when the
// pointer was dragged over the viewport.
function pick(e: ThreeEvent<MouseEvent>, selection: Selection) {
  if (e.delta > 4 || useEditor.getState().tool.kind !== 'select') return;
  e.stopPropagation();
  select(selection);
}
