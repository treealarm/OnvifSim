import { setTool, useEditor, type BrushKind, type Placeable, type Tool } from '../store';

const placeables: { what: Placeable; label: string }[] = [
  { what: 'tree.pine', label: 'Pine' },
  { what: 'tree.oak', label: 'Oak' },
  { what: 'house', label: 'House' },
  { what: 'rock', label: 'Rock' },
  { what: 'box', label: 'Box' },
  { what: 'camera.fixed', label: 'Fixed camera' },
  { what: 'camera.ptz', label: 'PTZ camera' },
];

const brushes: { brush: BrushKind; label: string }[] = [
  { brush: 'raise', label: 'Raise' },
  { brush: 'lower', label: 'Lower' },
  { brush: 'smooth', label: 'Smooth' },
  { brush: 'flatten', label: 'Flatten' },
];

function sameTool(a: Tool, b: Tool): boolean {
  if (a.kind === 'place' && b.kind === 'place') return a.what === b.what;
  if (a.kind === 'brush' && b.kind === 'brush') return a.brush === b.brush;
  return a.kind === b.kind;
}

// Pressing the active tool again goes back to selecting.
function ToolButton({ current, value, label }: { current: Tool; value: Tool; label: string }) {
  const active = sameTool(current, value);
  return (
    <button type="button" aria-pressed={active} onClick={() => setTool(active ? { kind: 'select' } : value)}>
      {label}
    </button>
  );
}

export function Toolbar() {
  const tool = useEditor((s) => s.tool);
  const gizmo = useEditor((s) => s.gizmo);
  const radius = useEditor((s) => s.brushRadius);
  const strength = useEditor((s) => s.brushStrength);

  return (
    <nav className="toolbar" aria-label="Tools">
      <div className="group" role="group" aria-label="Edit">
        <ToolButton current={tool} value={{ kind: 'select' }} label="Select" />
        <button type="button" aria-pressed={gizmo === 'move'} onClick={() => useEditor.setState({ gizmo: 'move' })} title="G">
          Move
        </button>
        <button type="button" aria-pressed={gizmo === 'rotate'} onClick={() => useEditor.setState({ gizmo: 'rotate' })} title="R">
          Rotate
        </button>
      </div>
      <div className="group" role="group" aria-label="Place">
        {placeables.map((p) => (
          <ToolButton current={tool} key={p.what} value={{ kind: 'place', what: p.what }} label={p.label} />
        ))}
      </div>
      <div className="group" role="group" aria-label="Terrain brushes">
        {brushes.map((b) => (
          <ToolButton current={tool} key={b.brush} value={{ kind: 'brush', brush: b.brush }} label={b.label} />
        ))}
        {tool.kind === 'brush' && (
          <>
            <label>
              Radius
              <input type="range" min={2} max={60} value={radius} onChange={(e) => useEditor.setState({ brushRadius: Number(e.target.value) })} />
            </label>
            <label>
              Strength
              <input type="range" min={0.05} max={2} step={0.05} value={strength} onChange={(e) => useEditor.setState({ brushStrength: Number(e.target.value) })} />
            </label>
          </>
        )}
      </div>
    </nav>
  );
}
