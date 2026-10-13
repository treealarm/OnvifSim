import { objectTypes, type ObjectType, type Vec3, type WorldObject } from '../model';
import { defaultScale, removeSelected, updateObject } from '../store';
import { ColorField, NumberField, Section, SelectField } from './fields';

const typeNames: Record<ObjectType, string> = {
  'tree.pine': 'Pine',
  'tree.oak': 'Oak',
  house: 'House',
  rock: 'Rock',
  box: 'Box',
};

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
  rock: [{ label: 'Colour', fallback: '#807c76' }],
  box: [{ label: 'Colour', fallback: '#c8c8c8' }],
};

const typeOptions = objectTypes.map((type) => ({ value: type, label: typeNames[type] }));

export function ObjectInspector({ obj }: { obj: WorldObject }) {
  const change = (patch: Partial<WorldObject>) => updateObject({ ...obj, ...patch });
  const changePosition = (patch: Partial<Vec3>) => change({ position: { ...obj.position, ...patch } });
  const changeScale = (patch: Partial<Vec3>) => change({ scale: { ...obj.scale, ...patch } });
  const [first, second] = colorSlots[obj.type];

  return (
    <>
      <h2>
        {typeNames[obj.type]} <span className="muted">{obj.id}</span>
      </h2>
      <Section title="Placement">
        <SelectField label="Type" value={obj.type} options={typeOptions} onChange={(type) => change({ type, scale: defaultScale[type], color: null, color2: null })} />
        <NumberField label="X (east)" value={obj.position.x} step={0.5} onChange={(x) => changePosition({ x })} />
        <NumberField label="Z (south)" value={obj.position.z} step={0.5} onChange={(z) => changePosition({ z })} />
        <NumberField label="Above ground" value={obj.position.y} step={0.1} onChange={(y) => changePosition({ y })} />
        <NumberField label="Rotation" value={obj.rotationY} min={0} max={360} slider onChange={(rotationY) => change({ rotationY })} />
      </Section>
      <Section title="Shape">
        <NumberField label="Width" value={obj.scale.x} min={0.1} max={1000} step={0.1} onChange={(x) => changeScale({ x })} />
        <NumberField label="Height" value={obj.scale.y} min={0.1} max={1000} step={0.1} onChange={(y) => changeScale({ y })} />
        <NumberField label="Depth" value={obj.scale.z} min={0.1} max={1000} step={0.1} onChange={(z) => changeScale({ z })} />
        <ColorField label={first.label} value={obj.color ?? first.fallback} onChange={(color) => change({ color })} />
        {second && <ColorField label={second.label} value={obj.color2 ?? second.fallback} onChange={(color2) => change({ color2 })} />}
        <div className="field">
          <span>Variation</span>
          <button type="button" onClick={() => change({ seed: Math.floor(Math.random() * 2 ** 31) })}>
            Shuffle
          </button>
        </div>
      </Section>
      <button type="button" className="danger" onClick={removeSelected}>
        Delete object
      </button>
    </>
  );
}
