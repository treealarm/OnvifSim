import { type ReactNode } from 'react';
import { objectTypeNames, type World } from '../model';
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
      <Group title="Objects" count={world.objects.length}>
        {world.objects.map((object) => (
          <Row
            key={object.id}
            label={objectTypeNames[object.type]}
            detail={object.id}
            active={isSelected('object', object.id)}
            onClick={() => select({ kind: 'object', id: object.id })}
          />
        ))}
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
    </aside>
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

function Row({ label, detail, active, onClick }: { label: string; detail: string; active: boolean; onClick: () => void }) {
  return (
    <button type="button" className="entity" aria-pressed={active} onClick={onClick}>
      <span>{label}</span>
      <span className="muted">{detail}</span>
    </button>
  );
}
