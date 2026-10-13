import type { GroundPoint, Road, Walker, WalkerReturn } from '../model';
import { removeSelected, startDrawing, updateRoad, updateWalker } from '../store';
import { CheckboxField, ColorField, NumberField, Section, SelectField } from './fields';

const returnOptions = [
  { value: 'loop', label: 'Round in a loop' },
  { value: 'reverse', label: 'There and back' },
] as const satisfies readonly { value: WalkerReturn; label: string }[];

export function RoadInspector({ road }: { road: Road }) {
  const change = (patch: Partial<Road>) => updateRoad({ ...road, ...patch });
  return (
    <>
      <h2>
        Road <span className="muted">{road.id}</span>
      </h2>
      <Section title="Road">
        <NumberField label="Width, m" value={road.width} min={0.5} max={50} step={0.5} onChange={(width) => change({ width })} />
        <ColorField label="Surface" value={road.color ?? '#55575a'} onChange={(color) => change({ color })} />
      </Section>
      <Points
        points={road.points}
        minimum={2}
        onChange={(points) => change({ points })}
        onExtend={() => startDrawing('road', road.id)}
      />
      <button type="button" className="danger" onClick={removeSelected}>
        Delete road
      </button>
    </>
  );
}

export function WalkerInspector({ walker }: { walker: Walker }) {
  const change = (patch: Partial<Walker>) => updateWalker({ ...walker, ...patch });
  return (
    <>
      <h2>
        Person <span className="muted">{walker.id}</span>
      </h2>
      <Section title="Walk">
        <NumberField label="Speed, m/s" value={walker.speed} min={0} max={10} step={0.1} onChange={(speed) => change({ speed })} />
        <SelectField label="At the end" value={walker.return} options={returnOptions} onChange={(value) => change({ return: value })} />
        <NumberField label="Height, m" value={walker.height} min={0.5} max={3} step={0.05} onChange={(height) => change({ height })} />
        <ColorField label="Top" value={walker.color ?? '#3b6ea8'} onChange={(color) => change({ color })} />
        <ColorField label="Trousers" value={walker.color2 ?? '#3a3a44'} onChange={(color2) => change({ color2 })} />
        <CheckboxField label="Seen by analytics" checked={walker.detectable} onChange={(detectable) => change({ detectable })} />
      </Section>
      <Points
        points={walker.route}
        minimum={1}
        onChange={(route) => change({ route })}
        onExtend={() => startDrawing('walker', walker.id)}
      />
      <button type="button" className="danger" onClick={removeSelected}>
        Delete person
      </button>
    </>
  );
}

interface PointsProps {
  points: GroundPoint[];
  // Points the road or route cannot do without.
  minimum: number;
  onChange: (points: GroundPoint[]) => void;
  onExtend: () => void;
}

// The points in order, editable; in the viewport they are the handles to drag.
function Points({ points, minimum, onChange, onExtend }: PointsProps) {
  const changePoint = (index: number, patch: Partial<GroundPoint>) => onChange(points.map((p, i) => (i === index ? { ...p, ...patch } : p)));
  return (
    <Section title="Points">
      <ol className="points">
        {points.map((point, index) => (
          // Points have no identity of their own; their place in the line is what they are.
          <li key={index}>
            <NumberField label={`${index + 1}: X`} value={point.x} step={0.5} onChange={(x) => changePoint(index, { x })} />
            <NumberField label="Z" value={point.z} step={0.5} onChange={(z) => changePoint(index, { z })} />
            <button type="button" disabled={points.length <= minimum} onClick={() => onChange(points.filter((_, i) => i !== index))}>
              Remove point {index + 1}
            </button>
          </li>
        ))}
      </ol>
      <button type="button" onClick={onExtend}>
        Add points at the end
      </button>
    </Section>
  );
}
