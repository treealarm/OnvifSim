import { OrbitControls } from '@react-three/drei';
import { Canvas, useFrame, useThree } from '@react-three/fiber';
import { useEffect, useRef } from 'react';
import { Color, Vector3 } from 'three';
import type { Environment, World } from '../model';
import { useEditor } from '../store';
import { CameraGizmo } from './CameraGizmo';
import { DeviceIoView } from './DeviceIoView';
import { ObjectsView } from './ObjectsView';
import { RoutesView } from './RoutesView';
import { TerrainView } from './TerrainView';

// The 3D view of the world, drawn by the browser for interactive speed. What a camera actually sees is
// the server's picture in the camera panel.
export function Viewport({ world }: { world: World }) {
  const heads = useEditor((s) => s.heads);
  const selection = useEditor((s) => s.selection);
  const placing = useEditor((s) => s.tool.kind !== 'select');
  const sun = sunDirection(world.environment).multiplyScalar(300);
  const sky = new Color(world.environment.skyHorizon);

  return (
    <Canvas
      className={placing ? 'viewport placing' : 'viewport'}
      camera={{ position: [180, 150, 220], fov: 50, near: 0.5, far: 5000 }}
    >
      <color attach="background" args={[sky]} />
      <hemisphereLight args={[new Color(world.environment.skyZenith), new Color('#6b6455'), 1.2]} />
      <directionalLight position={sun} intensity={2.2} />
      <OrbitControls makeDefault maxPolarAngle={Math.PI / 2 - 0.02} />
      <FocusFlight />
      <TerrainView terrain={world.terrain} />
      <ObjectsView objects={world.objects} terrain={world.terrain} />
      <RoutesView roads={world.roads} walkers={world.walkers} vehicles={world.vehicles} terrain={world.terrain} />
      <DeviceIoView world={world} />
      {world.cameras.map((camera) => (
        <CameraGizmo
          key={camera.id}
          camera={camera}
          head={heads[camera.id]}
          terrain={world.terrain}
          environment={world.environment}
          vehicles={world.vehicles}
          selected={selection?.kind === 'camera' && selection.id === camera.id}
        />
      ))}
    </Canvas>
  );
}

const flightMs = 450;

// The editor camera flying to a point, from the direction it is looked at now — what an inspector's
// Find button asks for. The flight is short and eased, so the eye follows the move instead of
// arriving somewhere unfamiliar.
function FocusFlight() {
  const camera = useThree((state) => state.camera);
  const controls = useThree((state) => state.controls) as unknown as
    | { target: Vector3; update: () => void }
    | null;
  const request = useEditor((state) => state.focus);
  const flight = useRef<{ from: Vector3; to: Vector3; fromTarget: Vector3; toTarget: Vector3; start: number } | null>(null);

  useEffect(() => {
    if (!request || !controls) return;
    const target = new Vector3(...request.point);
    const direction = new Vector3().subVectors(camera.position, controls.target).normalize();
    flight.current = {
      from: camera.position.clone(),
      to: target.clone().addScaledVector(direction, request.distance),
      fromTarget: controls.target.clone(),
      toTarget: target,
      start: performance.now(),
    };
  }, [request, controls, camera]);

  useFrame(() => {
    const move = flight.current;
    if (!move || !controls) return;
    const progress = Math.min(1, (performance.now() - move.start) / flightMs);
    const eased = progress * progress * (3 - 2 * progress);
    camera.position.lerpVectors(move.from, move.to, eased);
    controls.target.lerpVectors(move.fromTarget, move.toTarget, eased);
    controls.update();
    if (progress >= 1) flight.current = null;
  });

  return null;
}

// Towards the sun: azimuth clockwise from north (-Z), as RenderScene lights the server's picture.
function sunDirection(env: Environment): Vector3 {
  const azimuth = (env.sunAzimuth * Math.PI) / 180;
  const elevation = (env.sunElevation * Math.PI) / 180;
  return new Vector3(
    Math.sin(azimuth) * Math.cos(elevation),
    Math.sin(elevation),
    -Math.cos(azimuth) * Math.cos(elevation),
  ).normalize();
}
