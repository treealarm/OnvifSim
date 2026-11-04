import { LinksDialog } from './LinksDialog';
import {
  cancelDrawing,
  finishDrawing,
  redo,
  setTool,
  setToolGroup,
  startDrawing,
  toolGroupOf,
  undo,
  useEditor,
  type BrushKind,
  type Drawable,
  type Placeable,
  type Tool,
  type ToolGroup,
} from '../store';

const groups: { group: ToolGroup; label: string }[] = [
  { group: 'select', label: 'Select' },
  { group: 'add', label: 'Add' },
  { group: 'draw', label: 'Draw' },
  { group: 'terrain', label: 'Terrain' },
];

const placeables: { what: Placeable; label: string }[] = [
  { what: 'tree.pine', label: 'Pine' },
  { what: 'tree.oak', label: 'Oak' },
  { what: 'house', label: 'House' },
  { what: 'rock', label: 'Rock' },
  { what: 'box', label: 'Box' },
  { what: 'car', label: 'Car' },
  { what: 'camera.fixed', label: 'Fixed camera' },
  { what: 'camera.ptz', label: 'PTZ camera' },
  // A lamp is put down like any other object, but it is a device: a relay drives it.
  { what: 'lamp', label: 'Lamp' },
];

const brushes: { brush: BrushKind; label: string }[] = [
  { brush: 'raise', label: 'Raise' },
  { brush: 'lower', label: 'Lower' },
  { brush: 'smooth', label: 'Smooth' },
  { brush: 'flatten', label: 'Flatten' },
];

const drawables: { what: Drawable; label: string }[] = [
  { what: 'road', label: 'Road' },
  { what: 'walker', label: 'Person' },
  { what: 'vehicle', label: 'Car route' },
];

function sameTool(a: Tool, b: Tool): boolean {
  if (a.kind === 'place' && b.kind === 'place') return a.what === b.what;
  if (a.kind === 'brush' && b.kind === 'brush') return a.brush === b.brush;
  if (a.kind === 'draw' && b.kind === 'draw') return a.what === b.what;
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

// Two rows: the palette tabs choose what the second row offers; the second row holds the tools, with
// undo and redo always at its left. A tab is only a filter: the active tool keeps doing what it did.
export function Toolbar() {
  const tool = useEditor((s) => s.tool);
  const palette = useEditor((s) => s.toolGroup);
  const activeGroup = toolGroupOf(tool);
  const gizmo = useEditor((s) => s.gizmo);
  const radius = useEditor((s) => s.brushRadius);
  const strength = useEditor((s) => s.brushStrength);
  const undoSteps = useEditor((s) => s.undoSteps);
  const redoSteps = useEditor((s) => s.redoSteps);
  const cutaway = useEditor((s) => s.cutaway);
  const linksOpen = useEditor((s) => s.linksOpen);

  return (
    <div className="toolbar">
      <div className="mode-tabs" role="group" aria-label="Tool groups">
        {groups.map(({ group, label }) => (
          <button key={group} type="button" aria-pressed={palette === group} onClick={() => setToolGroup(group)}>
            {label}
            {activeGroup === group && <span className="active-group-dot" aria-hidden="true" />}
          </button>
        ))}
      </div>
      <div className="palette" role="group" aria-label="Tools">
        <div className="group" role="group" aria-label="History">
          <button type="button" disabled={undoSteps === 0} onClick={undo} title="Ctrl+Z">
            Undo
          </button>
          <button type="button" disabled={redoSteps === 0} onClick={redo} title="Ctrl+Shift+Z, Ctrl+Y">
            Redo
          </button>
        </div>
        <div className="group" role="group" aria-label="View">
          <button
            type="button"
            aria-pressed={cutaway}
            onClick={() => useEditor.setState({ cutaway: !cutaway })}
            title="Hide everything above 2.5 m, to see and edit inside a building"
          >
            Cutaway
          </button>
        </div>
        {palette === 'select' && (
          <div className="group" role="group" aria-label="Select and transform">
            <ToolButton current={tool} value={{ kind: 'select' }} label="Select" />
            <button type="button" aria-pressed={gizmo === 'move'} onClick={() => useEditor.setState({ gizmo: 'move' })} title="G">
              Move
            </button>
            <button type="button" aria-pressed={gizmo === 'rotate'} onClick={() => useEditor.setState({ gizmo: 'rotate' })} title="R">
              Rotate
            </button>
          </div>
        )}
        {palette === 'add' && (
          <div className="group" role="group" aria-label="Add object">
            {placeables.map((p) => (
              <ToolButton current={tool} key={p.what} value={{ kind: 'place', what: p.what }} label={p.label} />
            ))}
          </div>
        )}
        {palette === 'draw' && (
          <div className="group" role="group" aria-label="Draw">
            {drawables.map((d) => {
              const active = tool.kind === 'draw' && tool.what === d.what;
              return (
                <button key={d.what} type="button" aria-pressed={active} onClick={() => (active ? cancelDrawing() : startDrawing(d.what))}>
                  {d.label}
                </button>
              );
            })}
            {tool.kind === 'draw' && (
              <>
                <span className="hint">
                  {tool.points.length === 0
                    ? 'Click the ground to set the first point'
                    : `${tool.points.length} point${tool.points.length > 1 ? 's' : ''}; double-click or Enter to finish`}
                </span>
                <button type="button" onClick={() => void finishDrawing()}>
                  Finish
                </button>
                <button type="button" onClick={cancelDrawing}>
                  Cancel
                </button>
              </>
            )}
          </div>
        )}
        {palette === 'terrain' && (
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
        )}
        {/* Not a tool: wiring joins devices across cameras, so it stands on the far side of a
            divider at the row's right end rather than among the tools. */}
        <div className="palette-action" role="group" aria-label="Wiring">
          <button
            type="button"
            onClick={() => useEditor.setState({ linksOpen: true })}
            title="Connect relay outputs to digital inputs and lamps"
          >
            Wiring…
          </button>
        </div>
      </div>
      <LinksDialog open={linksOpen} onClose={() => useEditor.setState({ linksOpen: false })} />
    </div>
  );
}
