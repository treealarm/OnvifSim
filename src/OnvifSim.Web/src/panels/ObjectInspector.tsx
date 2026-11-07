import { objectTypeNames, objectTypes, plateText, type ObjectType, type Opening, type Vec3, type WorldObject } from '../model';
import { defaultScale, driveCar, removeSelected, updateObject } from '../store';
import { ColorField, NumberField, Section, SelectField, TextField } from './fields';
import { InspectorTitle } from './FindButton';

// What Color and Color2 paint for each type, and the colours the server uses when they are not set
// (SceneBuilder.ObjectMesh).
const colorSlots: Record<ObjectType, { label: string; fallback: string }[]> = {
  'tree.pine': [
    { label: 'Needles', fallback: '#265230' },
    { label: 'Trunk', fallback: '#5c402c' },
  ],
  'tree.oak': [
    { label: 'Leaves', fallback: '#466e2e' },
    { label: 'Trunk', fallback: '#624832' },
  ],
  house: [
    { label: 'Walls', fallback: '#d8c9a8' },
    { label: 'Roof', fallback: '#8a3b2e' },
  ],
  office: [
    { label: 'Walls', fallback: '#ccd0d6' },
    { label: 'Roof', fallback: '#585c62' },
  ],
  rock: [{ label: 'Colour', fallback: '#807c76' }],
  box: [{ label: 'Colour', fallback: '#c8c8c8' }],
  floor: [
    { label: 'Tile', fallback: '#b4b8be' },
    { label: 'Seam', fallback: '#8f939a' },
  ],
  car: [
    { label: 'Paint', fallback: '#3a6ea5' },
    { label: 'Plate background', fallback: '#f0f0ea' },
  ],
  prism: [
    { label: 'Walls', fallback: '#c8ccd2' },
    { label: 'Roof and floor', fallback: '#70747c' },
  ],
};

// A prism is drawn, not dropped: its footprint is the shape, so it is not offered as a type to switch
// an ordinary object into.
const typeOptions = objectTypes.filter((type) => type !== 'prism').map((type) => ({ value: type, label: objectTypeNames[type] }));

// The types whose surface the mesh builder can pattern, and the pattern they carry by default: a floor
// is tiled, a box or a building's walls are grained. "Type default" leaves the choice to the server.
const texturedTypes: ObjectType[] = ['box', 'floor', 'house', 'office', 'prism'];
const textureOptions = [
  { value: '', label: 'Type default' },
  { value: 'none', label: 'Smooth' },
  { value: 'grain', label: 'Grain' },
  { value: 'tiles', label: 'Tiles' },
  { value: 'parking', label: 'Parking bays' },
];

export function ObjectInspector({ obj }: { obj: WorldObject }) {
  const change = (patch: Partial<WorldObject>) => updateObject({ ...obj, ...patch });
  const changePosition = (patch: Partial<Vec3>) => change({ position: { ...obj.position, ...patch } });
  const changeScale = (patch: Partial<Vec3>) => change({ scale: { ...obj.scale, ...patch } });
  const [first, second] = colorSlots[obj.type];

  return (
    <>
      <InspectorTitle>
        <h2>
          {objectTypeNames[obj.type]} <span className="muted">{obj.id}</span>
        </h2>
      </InspectorTitle>
      <Section title="Placement">
        <SelectField
          label="Type"
          value={obj.type}
          options={typeOptions}
          onChange={(type) =>
            change({ type, scale: defaultScale[type], color: null, color2: null, plate: type === 'car' ? null : undefined })
          }
        />
        <NumberField label="X (east)" value={obj.position.x} step={0.5} onChange={(x) => changePosition({ x })} />
        <NumberField label="Z (south)" value={obj.position.z} step={0.5} onChange={(z) => changePosition({ z })} />
        <NumberField label="Above ground" value={obj.position.y} step={0.1} onChange={(y) => changePosition({ y })} />
        <NumberField label="Rotation" value={obj.rotationY} min={0} max={360} slider onChange={(rotationY) => change({ rotationY })} />
      </Section>
      <Section title="Shape">
        {obj.type === 'prism' ? (
          <>
            <NumberField label="Height" value={obj.scale.y} min={0.1} max={200} step={0.1} onChange={(y) => changeScale({ y })} />
            <p className="muted">
              The size across is the footprint drawn on the ground — {obj.points?.length ?? 0} points. Deleting this
              building and drawing it again is how the outline changes.
            </p>
          </>
        ) : (
          <>
            <NumberField label="Width" value={obj.scale.x} min={0.1} max={1000} step={0.1} onChange={(x) => changeScale({ x })} />
            <NumberField label="Height" value={obj.scale.y} min={0.1} max={1000} step={0.1} onChange={(y) => changeScale({ y })} />
            <NumberField label="Depth" value={obj.scale.z} min={0.1} max={1000} step={0.1} onChange={(z) => changeScale({ z })} />
          </>
        )}
        <ColorField label={first.label} value={obj.color ?? first.fallback} onChange={(color) => change({ color })} />
        {second && <ColorField label={second.label} value={obj.color2 ?? second.fallback} onChange={(color2) => change({ color2 })} />}
        {texturedTypes.includes(obj.type) && (
          <SelectField
            label="Surface texture"
            value={obj.texture ?? ''}
            options={textureOptions}
            onChange={(texture) => change({ texture: texture === '' ? null : texture })}
          />
        )}
        <div className="field">
          <span>Variation</span>
          <button type="button" onClick={() => change({ seed: Math.floor(Math.random() * 2 ** 31) })}>
            Shuffle
          </button>
        </div>
      </Section>
      {obj.type === 'prism' && (
        <Section title="Openings">
          {(obj.openings ?? []).map((opening, index) => {
            const openings = obj.openings ?? [];
            const changeOpening = (patch: Partial<Opening>) =>
              change({ openings: openings.map((o, i) => (i === index ? { ...o, ...patch } : o)) });
            return (
              <div key={index} className="io-card">
                <NumberField
                  label="Wall (segment)"
                  value={opening.edge}
                  min={0}
                  max={Math.max(0, (obj.points?.length ?? 1) - 1)}
                  step={1}
                  onChange={(edge) => changeOpening({ edge: Math.round(edge) })}
                />
                <NumberField label="Along the wall, m" value={opening.offset} min={0} step={0.1} onChange={(offset) => changeOpening({ offset })} />
                <NumberField label="Width, m" value={opening.width} min={0.2} max={200} step={0.1} onChange={(width) => changeOpening({ width })} />
                <NumberField label="Height, m" value={opening.height} min={0.2} max={200} step={0.1} onChange={(height) => changeOpening({ height })} />
                <NumberField label="Sill, m" value={opening.sill} min={0} max={200} step={0.1} onChange={(sill) => changeOpening({ sill })} />
                <button type="button" onClick={() => change({ openings: openings.filter((_, i) => i !== index) })}>
                  Remove opening
                </button>
              </div>
            );
          })}
          <button
            type="button"
            onClick={() => change({ openings: [...(obj.openings ?? []), { edge: 0, offset: 0, width: 1.8, height: 2.2, sill: 0 }] })}
          >
            Add a doorway
          </button>
          <p className="muted">
            An opening sits on one segment of the footprint, measured along it from the segment's first point; a
            raised sill makes it a window.
          </p>
        </Section>
      )}
      {obj.type === 'car' && (
        <Section title="License plate">
          <TextField label="Plate text" value={obj.plate ?? 'A123BC'} onChange={(text) => change({ plate: plateText(text) })} />
          <p className="muted">Latin letters, digits, spaces and hyphens, up to 8 characters. Clearing the field leaves it blank.</p>
        </Section>
      )}
      {obj.type === 'car' && (
        <Section title="Motion">
          <button type="button" onClick={() => driveCar(obj)}>
            Make it drive
          </button>
          <p className="muted">
            Replaces this parked car with a moving one on a straight route along its heading, keeping its paint, size and
            plate. Edit the route and speed in its panel; Undo parks it again.
          </p>
        </Section>
      )}
      <button type="button" className="danger" onClick={removeSelected}>
        Delete object
      </button>
    </>
  );
}
