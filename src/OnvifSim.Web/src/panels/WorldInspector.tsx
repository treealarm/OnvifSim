import type { Environment, World } from '../model';
import { select, updateEnvironment, updateTerrain } from '../store';
import { CheckboxField, ColorField, NumberField, Section } from './fields';

// What shows when nothing is selected: settings of the world as a whole.
export function WorldInspector({ world }: { world: World }) {
  const environment = world.environment;
  const change = (patch: Partial<Environment>) => updateEnvironment({ ...environment, ...patch });
  return (
    <>
      <h2>World</h2>
      <p className="muted">
        Pick something from the toolbar and click the ground to place it; click an object or a camera to edit it.
      </p>
      <Section title="Cameras">
        <ul className="camera-list">
          {world.cameras.map((camera) => (
            <li key={camera.id}>
              <button type="button" onClick={() => select({ kind: 'camera', id: camera.id })}>
                {camera.name || camera.id}
              </button>{' '}
              <span className="muted">
                {camera.id}, {camera.kind === 'ptz' ? 'PTZ' : 'fixed'}
              </span>
            </li>
          ))}
        </ul>
      </Section>
      <Section title="Sun and sky">
        <NumberField label="Sun from (° from north)" value={environment.sunAzimuth} min={0} max={360} slider onChange={(sunAzimuth) => change({ sunAzimuth })} />
        <NumberField label="Sun height" value={environment.sunElevation} min={0} max={90} slider onChange={(sunElevation) => change({ sunElevation })} />
        <ColorField label="Sky overhead" value={environment.skyZenith} onChange={(skyZenith) => change({ skyZenith })} />
        <ColorField label="Sky at horizon" value={environment.skyHorizon} onChange={(skyHorizon) => change({ skyHorizon })} />
        <NumberField label="Fog from, m" value={environment.fogStart} min={0} step={10} onChange={(fogStart) => change({ fogStart })} />
        <NumberField label="Fog hides at, m" value={environment.fogEnd} min={1} step={10} onChange={(fogEnd) => change({ fogEnd })} />
      </Section>
      <Section title="In the camera pictures">
        <CheckboxField label="Other cameras" checked={environment.showCameras} onChange={(showCameras) => change({ showCameras })} />
        <CheckboxField label="Where they look (wire frame)" checked={environment.showFrustums} onChange={(showFrustums) => change({ showFrustums })} />
        <NumberField label="View reaches, m (no ground)" value={environment.frustumLength} min={1} max={1000} step={5} onChange={(frustumLength) => change({ frustumLength })} />
      </Section>
      <Section title="Terrain">
        <NumberField label="Size, m" value={world.terrain.size} min={10} max={10000} step={10} onChange={(size) => updateTerrain({ ...world.terrain, size })} />
      </Section>
      <p className="muted">
        {world.objects.length} objects, {world.roads.length} roads, {world.walkers.length} people, {world.cameras.length} cameras.
      </p>
    </>
  );
}
