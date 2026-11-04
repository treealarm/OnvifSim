import { Line } from '@react-three/drei';
import { useFrame } from '@react-three/fiber';
import { useRef, type ComponentRef, type RefObject } from 'react';
import type { Group, Plane } from 'three';
import {
  inputActive,
  sameId,
  type CameraModel,
  type DigitalInput,
  type RelayOutput,
  type Terrain,
  type Vec3,
  type Vehicle,
  type Wire,
  type World,
} from '../model';
import { useCutawayPlanes } from './cutaway';
import { devicePoint, isMounted } from './devicePoint';

// As Geometry/DeviceMesh.cs builds them on the server: the lamp's shade sits at the top of its post, and
// the sensor's lit top at sensorHeight.
const lampHeight = 2.6;
const sensorHeight = 0.42;
const lampLit = '#ffd866';
const lampDark = '#5c5e62';
const sensorLit = '#52d06c';
const sensorDark = '#5e6a76';
const wireColor = '#2a2a2e';

// The lamps and sensor blocks that stand for every camera's relay outputs and digital inputs, and the
// wires between them. A camera on a car carries its devices, and the wires touching them, along.
export function DeviceIoView({ world }: { world: World }) {
  const clippingPlanes = useCutawayPlanes();
  return (
    <>
      {world.cameras.flatMap((camera) => [
        ...camera.relayOutputs.map((relay) => (
          <Lamp
            key={`${camera.id}/relay/${relay.token}`}
            camera={camera}
            relay={relay}
            terrain={world.terrain}
            vehicles={world.vehicles}
            clippingPlanes={clippingPlanes}
          />
        )),
        ...camera.digitalInputs.map((input) => (
          <Sensor
            key={`${camera.id}/input/${input.token}`}
            camera={camera}
            input={input}
            active={inputActive(world, camera.id, input.token) === true}
            terrain={world.terrain}
            vehicles={world.vehicles}
            clippingPlanes={clippingPlanes}
          />
        )),
      ])}
      {world.wires.map((wire) => (
        <DeviceWire
          key={`${wire.relayCameraId}/${wire.relayToken}->${wire.inputCameraId}/${wire.inputToken}`}
          wire={wire}
          world={world}
        />
      ))}
    </>
  );
}

function Lamp({
  camera,
  relay,
  terrain,
  vehicles,
  clippingPlanes,
}: {
  camera: CameraModel;
  relay: RelayOutput;
  terrain: Terrain;
  vehicles: Vehicle[];
  clippingPlanes: Plane[];
}) {
  const { group, initial } = useDeviceGroup(camera, relay.position, terrain, vehicles);
  const color = relay.active ? lampLit : lampDark;
  return (
    <group ref={group} position={[initial.x, initial.y, initial.z]}>
      <mesh position={[0, (lampHeight - 0.3) / 2, 0]}>
        <cylinderGeometry args={[0.05, 0.08, lampHeight - 0.3, 6]} />
        <meshLambertMaterial color="#424448" clippingPlanes={clippingPlanes} />
      </mesh>
      <mesh position={[0, lampHeight - 0.25, 0]} rotation={[Math.PI, 0, 0]}>
        <coneGeometry args={[0.38, 0.45, 8]} />
        <meshLambertMaterial
          color={color}
          emissive={relay.active ? color : '#000000'}
          emissiveIntensity={0.6}
          clippingPlanes={clippingPlanes}
        />
      </mesh>
    </group>
  );
}

function Sensor({
  camera,
  input,
  active,
  terrain,
  vehicles,
  clippingPlanes,
}: {
  camera: CameraModel;
  input: DigitalInput;
  active: boolean;
  terrain: Terrain;
  vehicles: Vehicle[];
  clippingPlanes: Plane[];
}) {
  const { group, initial } = useDeviceGroup(camera, input.position, terrain, vehicles);
  const color = active ? sensorLit : sensorDark;
  return (
    <group ref={group} position={[initial.x, initial.y, initial.z]}>
      <mesh position={[0, (sensorHeight - 0.12) / 2, 0]}>
        <boxGeometry args={[0.26, sensorHeight - 0.12, 0.26]} />
        <meshLambertMaterial color="#34363a" clippingPlanes={clippingPlanes} />
      </mesh>
      <mesh position={[0, sensorHeight - 0.06, 0]}>
        <boxGeometry args={[0.34, 0.12, 0.34]} />
        <meshLambertMaterial
          color={color}
          emissive={active ? color : '#000000'}
          emissiveIntensity={0.6}
          clippingPlanes={clippingPlanes}
        />
      </mesh>
    </group>
  );
}

function DeviceWire({ wire, world }: { wire: Wire; world: World }) {
  const line = useRef<ComponentRef<typeof Line>>(null);
  const ends = wireEnds(wire, world, Date.now());
  const moves = movesWithCar(wire, world);
  useFrame(() => {
    const current = moves ? wireEnds(wire, world, Date.now()) : null;
    if (current && line.current) line.current.geometry.setPositions([...current[0], ...current[1]]);
  });
  return ends ? <Line ref={line} points={[ends[0], ends[1]]} color={wireColor} lineWidth={1.5} /> : null;
}

// From just under the lamp's shade to the top of the sensor block.
function wireEnds(wire: Wire, world: World, timeMs: number): [[number, number, number], [number, number, number]] | null {
  const relayCamera = world.cameras.find((camera) => sameId(camera.id, wire.relayCameraId));
  const relay = relayCamera?.relayOutputs.find((output) => sameId(output.token, wire.relayToken));
  const inputCamera = world.cameras.find((camera) => sameId(camera.id, wire.inputCameraId));
  const input = inputCamera?.digitalInputs.find((device) => sameId(device.token, wire.inputToken));
  if (!relayCamera || !relay || !inputCamera || !input) return null;
  const from = devicePoint(relayCamera, relay.position, world.terrain, world.vehicles, timeMs);
  const to = devicePoint(inputCamera, input.position, world.terrain, world.vehicles, timeMs);
  return [
    [from.x, from.y + lampHeight - 0.2, from.z],
    [to.x, to.y + sensorHeight, to.z],
  ];
}

function movesWithCar(wire: Wire, world: World): boolean {
  const relayCamera = world.cameras.find((camera) => sameId(camera.id, wire.relayCameraId));
  const inputCamera = world.cameras.find((camera) => sameId(camera.id, wire.inputCameraId));
  return (!!relayCamera && isMounted(relayCamera)) || (!!inputCamera && isMounted(inputCamera));
}

// A group that stays where its device stands: placed once, and moved every frame when its camera rides
// on a car, as CameraGizmo moves the camera itself.
function useDeviceGroup(
  camera: CameraModel,
  offset: Vec3,
  terrain: Terrain,
  vehicles: Vehicle[],
): { group: RefObject<Group | null>; initial: { x: number; y: number; z: number } } {
  const group = useRef<Group>(null);
  const mounted = isMounted(camera);
  useFrame(() => {
    if (!mounted || !group.current) return;
    const point = devicePoint(camera, offset, terrain, vehicles, Date.now());
    group.current.position.set(point.x, point.y, point.z);
  });
  return { group, initial: devicePoint(camera, offset, terrain, vehicles, Date.now()) };
}
