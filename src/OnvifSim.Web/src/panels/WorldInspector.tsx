import { defaultLampColor, lampLit, wiresToLamp, type Environment, type Geo, type Lamp, type World } from '../model';
import { removeLamp, updateEnvironment, updateGeo, updateLamp, updateTerrain, useEditor } from '../store';
import { BlurTextField, CheckboxField, ColorField, NumberField, Section } from './fields';
import { InspectorTitle } from './FindButton';

// What shows when nothing is selected: settings of the world as a whole.
export function WorldInspector({ world }: { world: World }) {
  const environment = world.environment;
  const change = (patch: Partial<Environment>) => updateEnvironment({ ...environment, ...patch });
  const geo = world.geo;
  const changeGeo = (patch: Partial<Geo>) => updateGeo({ ...geo, ...patch });
  return (
    <>
      <h2>World</h2>
      <p className="muted">
        Pick something from the toolbar and click the ground to place it; click an object or a camera, or a row in
        Contents, to edit it.
      </p>
      <Section title="Geo origin">
        <NumberField label="Latitude" value={geo.latitude} min={-90} max={90} step={0.000001} decimals={6} onChange={(latitude) => changeGeo({ latitude })} />
        <NumberField label="Longitude" value={geo.longitude} min={-180} max={180} step={0.000001} decimals={6} onChange={(longitude) => changeGeo({ longitude })} />
        <NumberField label="Elevation, m" value={geo.elevation} step={1} onChange={(elevation) => changeGeo({ elevation })} />
        <NumberField label="North (° from true north)" value={geo.north} min={0} max={360} slider onChange={(north) => changeGeo({ north })} />
        <p className="muted">World −Z points this way; 0 means world −Z is true north.</p>
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
        <ColorField label="Ground" value={world.terrain.color} onChange={(color) => updateTerrain({ ...world.terrain, color })} />
        <p className="muted">Steep slopes still turn to rock, with lighter patches breaking up the colour.</p>
      </Section>
      {/* A lamp is a thing of the world, like a camera: it is placed from the palette and edited in
          its own card, not in the world's settings. */}
      <p className="muted">
        {world.objects.length} objects, {world.roads.length} roads, {world.walkers.length} people, {world.cameras.length} cameras,{' '}
        {world.lamps.length} lamps.
      </p>
    </>
  );
}

// The lamp picked in the viewport or in Contents: its card, with the devices it is wired to.
export function LampInspector({ lamp }: { lamp: Lamp }) {
  const world = useEditor((s) => s.world);
  return (
    <>
      <InspectorTitle>
        <h2>
          {lamp.name || lamp.id} <span className="muted">{lamp.id}</span>
        </h2>
      </InspectorTitle>
      {world && <LampCard lamp={lamp} world={world} />}
    </>
  );
}

// A lamp of the world: an actuator with a place of its own, lit by the relays wired to it. Its id is
// what a wire names, so it is not renamed here; the name and the place are.
function LampCard({ lamp, world }: { lamp: Lamp; world: World }) {
  const wires = wiresToLamp(world, lamp.id);
  const lit = lampLit(world, lamp.id);
  const move = (patch: Partial<Lamp['position']>) => updateLamp({ ...lamp, position: { ...lamp.position, ...patch } });
  return (
    <div className="io-card">
      <div className="io-head">
        <strong>{lamp.name || lamp.id}</strong>
        <span className="muted">{lamp.id}</span>
        <button type="button" onClick={() => removeLamp(lamp.id)}>
          Remove
        </button>
      </div>
      <BlurTextField label="Name" value={lamp.name} onChange={(name) => updateLamp({ ...lamp, name })} />
      <CheckboxField
        label="Normally closed (lit while no relay is closed)"
        checked={lamp.normallyClosed}
        onChange={(normallyClosed) => updateLamp({ ...lamp, normallyClosed })}
      />
      <ColorField
        label="Glow colour"
        value={lamp.color ?? defaultLampColor}
        onChange={(color) => updateLamp({ ...lamp, color })}
      />
      <NumberField label="X (east)" value={lamp.position.x} step={0.5} onChange={(x) => move({ x })} />
      <NumberField label="Z (south)" value={lamp.position.z} step={0.5} onChange={(z) => move({ z })} />
      <NumberField label="Above ground" value={lamp.position.y} min={0} step={0.5} onChange={(y) => move({ y })} />
      <p className={lit ? 'io-state on' : 'io-state'}>
        {wires.length === 0
          ? 'Not wired: open Wiring… on the toolbar to connect it to a relay.'
          : `${wires.map((wire) => `${wire.relayCameraId}/${wire.relayToken}`).join(', ')} — ${lit ? 'lit' : 'off'}`}
      </p>
    </div>
  );
}
