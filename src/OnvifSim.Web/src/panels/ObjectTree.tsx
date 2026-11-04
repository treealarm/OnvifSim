import { type ReactNode } from 'react';
import { objectTypeNames, type ObjectType, type World, type WorldObject } from '../model';
import { select, useEditor, type Selection } from '../store';

// Every editable thing in the world, grouped. Clicking a row selects it exactly as clicking it in the
// viewport does, which is the reliable way to reach a person a few pixels tall.
export function ObjectTree({ world }: { world: World }) {
  const selection = useEditor((s) => s.selection);
  const isSelected = (kind: Selection['kind'], id: string) =>
    selection?.kind === kind && selection.id === id;

  return (
    <aside className="tree" aria-label="World contents">
      <h3>Contents</h3>
      <button type="button" className="entity" aria-pressed={selection === null} onClick={() => select(null)}>
        <span>World environment</span>
      </button>

      <Group title="Cameras" count={world.cameras.length}>
        {world.cameras.map((camera) => (
          <Row
            key={camera.id}
            label={camera.name || camera.id}
            detail={camera.id}
            active={isSelected('camera', camera.id)}
            onClick={() => select({ kind: 'camera', id: camera.id })}
          />
        ))}
      </Group>
      {/* Devices a relay can drive stand in the world like objects, so they are found the same way. */}
      <Group title="Lamps" count={world.lamps.length}>
        {world.lamps.map((lamp) => (
          <Row
            key={lamp.id}
            label={lamp.name || lamp.id}
            detail={lamp.id}
            active={isSelected('lamp', lamp.id)}
            onClick={() => select({ kind: 'lamp', id: lamp.id })}
          />
        ))}
      </Group>
      <Group title="Objects" count={world.objects.length}>
        <ObjectTypes
          objects={world.objects}
          selectedId={selection?.kind === 'object' ? selection.id : null}
          onSelect={(id) => select({ kind: 'object', id })}
        />
      </Group>
      <Group title="Roads" count={world.roads.length}>
        {world.roads.map((road) => (
          <Row
            key={road.id}
            label="Road"
            detail={road.id}
            active={isSelected('road', road.id)}
            onClick={() => select({ kind: 'road', id: road.id })}
          />
        ))}
      </Group>
      <Group title="People" count={world.walkers.length}>
        {world.walkers.map((walker) => (
          <Row
            key={walker.id}
            label="Person"
            detail={walker.id}
            active={isSelected('walker', walker.id)}
            onClick={() => select({ kind: 'walker', id: walker.id })}
          />
        ))}
      </Group>
      <Group title="Vehicles" count={world.vehicles.length}>
        {world.vehicles.map((vehicle) => (
          <Row
            key={vehicle.id}
            label="Car"
            detail={vehicle.id}
            active={isSelected('vehicle', vehicle.id)}
            onClick={() => select({ kind: 'vehicle', id: vehicle.id })}
          />
        ))}
      </Group>
    </aside>
  );
}

// Objects of one type open together: the type names the rows, and the ids are what the inspector
// calls them. The group of the selected object opens by itself, so a click in 3D shows its row.
function ObjectTypes({
  objects,
  selectedId,
  onSelect,
}: {
  objects: WorldObject[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  const selectedType = objects.find((object) => object.id === selectedId)?.type;
  const types = (Object.keys(objectTypeNames) as ObjectType[])
    .filter((type) => objects.some((object) => object.type === type))
    .sort((a, b) => objectTypeNames[a].localeCompare(objectTypeNames[b]));
  return (
    <>
      {types.map((type) => {
        const members = objects.filter((object) => object.type === type);
        return (
          <details key={type} className="tree-type" open={type === selectedType || undefined}>
            <summary>
              {objectTypeNames[type]} <span className="muted">{members.length}</span>
            </summary>
            {members.map((object) => (
              <Row key={object.id} label={object.id} active={object.id === selectedId} onClick={() => onSelect(object.id)} />
            ))}
          </details>
        );
      })}
    </>
  );
}

function Group({ title, count, children }: { title: string; count: number; children: ReactNode }) {
  return (
    <details open className="tree-group">
      <summary>
        {title} <span className="muted">{count}</span>
      </summary>
      {count === 0 ? <p className="muted">None</p> : children}
    </details>
  );
}

function Row({ label, detail, active, onClick }: { label: string; detail?: string; active: boolean; onClick: () => void }) {
  return (
    <button type="button" className="entity" aria-pressed={active} onClick={onClick}>
      <span>{label}</span>
      {detail && <span className="muted">{detail}</span>}
    </button>
  );
}
