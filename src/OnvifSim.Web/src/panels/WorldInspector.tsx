import type { OsmCandidate } from '../api';
import { defaultLampColor, lampLit, sameBounds, terrainBounds, wiresToLamp, type Environment, type Geo, type Lamp, type World } from '../model';
import {
  bakeGroundMap,
  buildOsmCandidate,
  cancelOsmPick,
  fetchOsmArea,
  pickOsmCandidate,
  removeLamp,
  updateEnvironment,
  updateGeo,
  updateLamp,
  updateTerrain,
  useEditor,
} from '../store';
import { BlurTextField, CheckboxField, ColorField, NumberField, Section, SelectField } from './fields';
import { InspectorTitle } from './FindButton';

// What shows when nothing is selected: settings of the world as a whole.
export function WorldInspector({ world }: { world: World }) {
  const environment = world.environment;
  const change = (patch: Partial<Environment>) => updateEnvironment({ ...environment, ...patch });
  const geo = world.geo;
  const changeGeo = (patch: Partial<Geo>) => updateGeo({ ...geo, ...patch });
  const terrain = world.terrain;
  const osm = useEditor((s) => s.osm);
  const osmFetching = useEditor((s) => s.osmFetching);
  const baking = useEditor((s) => s.mapBaking);
  const mapFailed = useEditor((s) => s.mapFailed);
  const mapName = terrain.map ?? null;
  // A picture fetched for another anchor, another size or another north is not this world's any
  // more: it is hidden, as the server hides it, until someone bakes it again.
  const staleMap = Boolean(mapName)
    && !(terrain.mapBounds && sameBounds(terrainBounds(geo, terrain.size), terrain.mapBounds));

  return (
    <>
      <h2>World</h2>
      {(osm || osmFetching) && (
        <OsmPickCard candidates={osm?.candidates ?? null} picked={osm?.picked ?? 0} fetching={osmFetching} />
      )}
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
      <Section title="Camera views">
        <CheckboxField label="Other cameras in the pictures" checked={environment.showCameras} onChange={(showCameras) => change({ showCameras })} />
        <CheckboxField label="Their views in the pictures (wire frame)" checked={environment.showFrustums} onChange={(showFrustums) => change({ showFrustums })} />
        <NumberField label="A view reaches where it meets nothing, m" value={environment.frustumLength} min={1} max={1000} step={5} onChange={(frustumLength) => change({ frustumLength })} />
        <p className="muted">
          The editor draws every camera's view as a wire frame: its edges, and the grid where the
          picture meets the ground or an object. A ray that meets neither (one pointed at the sky)
          stops at the length above.
        </p>
      </Section>
      <Section title="Terrain">
        <NumberField label="Size, m" value={world.terrain.size} min={10} max={10000} step={10} onChange={(size) => updateTerrain({ ...world.terrain, size })} />
        <ColorField label="Ground" value={world.terrain.color} onChange={(color) => updateTerrain({ ...world.terrain, color })} />
        <SelectField
          label="Surface texture"
          value={world.terrain.texture ?? 'grass'}
          options={[
            { value: 'grass', label: 'Grass and rock' },
            { value: 'none', label: 'Smooth' },
          ]}
          onChange={(texture) => updateTerrain({ ...world.terrain, texture: texture === 'grass' ? null : texture })}
        />
        <p className="muted">Textured, steep slopes turn to rock and lighter patches break up the colour; smooth is one flat colour.</p>
      </Section>
      <Section title="Ground map">
        {mapName ? (
          <>
            <CheckboxField
              label="Map under the ground"
              checked={terrain.showMap !== false}
              onChange={(showMap) => updateTerrain({ ...terrain, showMap })}
            />
            <p className="muted">
              “{mapName}” is a picture cached on this machine and fetched for this world's own extent — never part of a
              world file. It is not drawn if the world's origin, size or north has moved since it was fetched.
            </p>
            {staleMap && (
              <p className="muted">
                {baking
                  ? 'Reading the picture for the new extent…'
                  : mapFailed
                    ? 'The picture could not be read for the new extent: bake it again.'
                    : 'The picture belongs to the extent the world had before; it is read again for the new one.'}
              </p>
            )}
            <div className="field">
              <button type="button" disabled={baking} onClick={() => void bakeGroundMap()}>
                {baking ? 'Fetching…' : 'Bake it again'}
              </button>
              <button
                type="button"
                onClick={() => updateTerrain({ ...terrain, map: null, mapBounds: null, mapAttribution: null, showMap: null })}
              >
                Forget the map
              </button>
            </div>
          </>
        ) : (
          <>
            <div className="field">
              <button type="button" disabled={baking} onClick={() => void bakeGroundMap()}>
                {baking ? 'Fetching…' : 'Bake ground map'}
              </button>
            </div>
            <p className="muted">
              Fetches a raster map for this world's extent from the tile provider and keeps it on this machine; the
              internet is needed once.
            </p>
          </>
        )}
        {terrain.mapAttribution && <p className="muted">{terrain.mapAttribution}</p>}
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

// What lies under the click, in the real world: each candidate can be looked at before it is built,
// and a courtyard is refused here rather than built as a lid over it.
function OsmPickCard(
  { candidates, picked, fetching }: { candidates: OsmCandidate[] | null; picked: number; fetching: boolean },
) {
  const candidate = candidates?.[picked];
  if (!candidates) {
    return (
      <Section title="From OpenStreetMap">
        <p className="muted">
          {fetching
            ? 'Reading the real world around this world — one request for its whole extent. A public Overpass server takes a while at a busy hour; every click after this is instant.'
            : 'Looking under the point…'}
        </p>
      </Section>
    );
  }
  return (
    <Section title="From OpenStreetMap">
      <div className="io-card">
        {candidates.map((item, index) => (
          <button
            key={`${item.osmType}/${item.osmId}`}
            type="button"
            aria-pressed={index === picked}
            onClick={() => pickOsmCandidate(index)}
          >
            {candidateLabel(item)}
          </button>
        ))}
        {candidate?.hasHoles && (
          <p className="muted">This one is drawn with a courtyard, which a footprint of this world cannot hold; its parts import one at a time.</p>
        )}
        <div className="field">
          <button type="button" onClick={() => void buildOsmCandidate()}>
            Build in world
          </button>
          <button type="button" onClick={cancelOsmPick}>
            Cancel
          </button>
        </div>
        <p className="muted">
          Data © OpenStreetMap contributors.{' '}
          <button type="button" onClick={() => void fetchOsmArea()}>
            Read the surroundings again
          </button>
        </p>
      </div>
    </Section>
  );
}

function candidateLabel(candidate: OsmCandidate): string {
  const name = candidate.name || `${candidate.osmType} ${candidate.osmId}`;
  if (candidate.kind === 'tree') return `${name} · tree`;
  if (candidate.kind === 'highway') return `${name} · road ${Math.round(candidate.width * 10) / 10} m wide`;
  const xs = candidate.points.map((p) => p.x);
  const zs = candidate.points.map((p) => p.z);
  const width = Math.round(Math.max(...xs) - Math.min(...xs));
  const depth = Math.round(Math.max(...zs) - Math.min(...zs));
  return `${name} · ${candidate.kind} ${width}×${depth} m, ${Math.round(candidate.height * 10) / 10} m high`;
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
