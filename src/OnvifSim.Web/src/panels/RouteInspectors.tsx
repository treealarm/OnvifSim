import { plateText, type GroundPoint, type Road, type Vehicle, type Walker } from '../model';
import { removeSelected, startDrawing, updateRoad, updateVehicle, updateWalker } from '../store';
import { CheckboxField, ColorField, NumberField, Section, TextField } from './fields';

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
        closed={road.closed}
        closedHint="Joins the last point to the first, so the road goes round as a ring."
        onClosed={(closed) => change({ closed })}
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
        <NumberField label="Height, m" value={walker.height} min={0.5} max={3} step={0.05} onChange={(height) => change({ height })} />
        <ColorField label="Top" value={walker.color ?? '#3b6ea8'} onChange={(color) => change({ color })} />
        <ColorField label="Trousers" value={walker.color2 ?? '#3a3a44'} onChange={(color2) => change({ color2 })} />
        <CheckboxField label="Seen by analytics" checked={walker.detectable} onChange={(detectable) => change({ detectable })} />
      </Section>
      <Points
        points={walker.route}
        minimum={1}
        closed={walker.return === 'loop'}
        closedHint="Closed, they go round the route; open, they turn round at the last point and come back."
        onClosed={(closed) => change({ return: closed ? 'loop' : 'reverse' })}
        onChange={(route) => change({ route })}
        onExtend={() => startDrawing('walker', walker.id)}
      />
      <button type="button" className="danger" onClick={removeSelected}>
        Delete person
      </button>
    </>
  );
}

export function VehicleInspector({ vehicle }: { vehicle: Vehicle }) {
  const change = (patch: Partial<Vehicle>) => updateVehicle({ ...vehicle, ...patch });
  return (
    <>
      <h2>
        Car <span className="muted">{vehicle.id}</span>
      </h2>
      <Section title="Drive">
        <NumberField label="Speed, m/s" value={vehicle.speed} min={0} max={50} step={0.5} onChange={(speed) => change({ speed })} />
        <CheckboxField label="Seen by analytics" checked={vehicle.detectable} onChange={(detectable) => change({ detectable })} />
      </Section>
      <Section title="Body">
        <NumberField label="Width, m" value={vehicle.width} min={0.5} max={5} step={0.05} onChange={(width) => change({ width })} />
        <NumberField label="Height, m" value={vehicle.height} min={0.5} max={5} step={0.05} onChange={(height) => change({ height })} />
        <NumberField label="Length, m" value={vehicle.length} min={1} max={15} step={0.1} onChange={(length) => change({ length })} />
        <ColorField label="Paint" value={vehicle.color ?? '#3a6ea5'} onChange={(color) => change({ color })} />
        <ColorField label="Plate background" value={vehicle.color2 ?? '#f0f0ea'} onChange={(color2) => change({ color2 })} />
      </Section>
      <Section title="License plate">
        <TextField label="Plate text" value={vehicle.plate ?? 'A123BC'} onChange={(text) => change({ plate: plateText(text) })} />
        <p className="muted">Latin letters, digits, spaces and hyphens, up to 8 characters. Clearing the field leaves it blank.</p>
      </Section>
      <Points
        points={vehicle.route}
        minimum={1}
        closed={vehicle.return === 'loop'}
        closedHint="Closed, it drives round the route; open, it turns round at the last point and comes back."
        onClosed={(closed) => change({ return: closed ? 'loop' : 'reverse' })}
        onChange={(route) => change({ route })}
        onExtend={() => startDrawing('vehicle', vehicle.id)}
      />
      <button type="button" className="danger" onClick={removeSelected}>
        Delete car
      </button>
    </>
  );
}

interface PointsProps {
  points: GroundPoint[];
  // Points the road or route cannot do without.
  minimum: number;
  // Whether the last point joins the first; belongs here because it is the route's own shape.
  closed: boolean;
  closedHint: string;
  onClosed: (closed: boolean) => void;
  onChange: (points: GroundPoint[]) => void;
  onExtend: () => void;
}

// The points in order, editable; in the viewport they are the handles to drag.
function Points({ points, minimum, closed, closedHint, onClosed, onChange, onExtend }: PointsProps) {
  const changePoint = (index: number, patch: Partial<GroundPoint>) => onChange(points.map((p, i) => (i === index ? { ...p, ...patch } : p)));
  return (
    <Section title="Points">
      <CheckboxField label="Closed loop" checked={closed} onChange={onClosed} />
      <p className="muted">{closedHint}</p>
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
