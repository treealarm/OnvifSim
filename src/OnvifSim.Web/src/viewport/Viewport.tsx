import { OrbitControls } from '@react-three/drei';
import { Canvas } from '@react-three/fiber';
import { Color, Vector3 } from 'three';
import type { Environment, World } from '../model';
import { useEditor } from '../store';
import { CameraGizmo } from './CameraGizmo';
import { ObjectsView } from './ObjectsView';
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
      <TerrainView terrain={world.terrain} />
      <ObjectsView objects={world.objects} terrain={world.terrain} />
      {world.cameras.map((camera) => (
        <CameraGizmo
          key={camera.id}
          camera={camera}
          head={heads[camera.id]}
          terrain={world.terrain}
          selected={selection?.kind === 'camera' && selection.id === camera.id}
        />
      ))}
    </Canvas>
  );
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
