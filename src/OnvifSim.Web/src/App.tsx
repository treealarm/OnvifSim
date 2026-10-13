import { useEffect } from 'react';
import { CameraInspector } from './panels/CameraInspector';
import { ObjectInspector } from './panels/ObjectInspector';
import { Toolbar } from './panels/Toolbar';
import { WorldInspector } from './panels/WorldInspector';
import { connect, redo, removeSelected, select, undo, useEditor } from './store';
import { Viewport } from './viewport/Viewport';

export function App() {
  const world = useEditor((s) => s.world);
  const connected = useEditor((s) => s.connected);
  const error = useEditor((s) => s.error);

  useEffect(connect, []);
  useEffect(() => {
    window.addEventListener('keydown', onShortcut);
    return () => window.removeEventListener('keydown', onShortcut);
  }, []);

  return (
    <div className="app">
      <header>
        <h1>OnvifSim</h1>
        <Toolbar />
        <span className={connected ? 'connection online' : 'connection offline'} role="status">
          {connected ? 'connected' : 'reconnecting…'}
        </span>
      </header>
      {error && (
        <div className="error" role="alert">
          {error}
          <button type="button" onClick={() => useEditor.setState({ error: null })} aria-label="Dismiss">
            ×
          </button>
        </div>
      )}
      {world ? (
        <main>
          <Viewport world={world} />
          <aside className="inspector">
            <Inspector />
          </aside>
        </main>
      ) : (
        <p className="loading">Connecting to the simulator…</p>
      )}
    </div>
  );
}

function Inspector() {
  const world = useEditor((s) => s.world);
  const selection = useEditor((s) => s.selection);
  if (!world) return null;
  if (selection?.kind === 'object') {
    const obj = world.objects.find((o) => o.id === selection.id);
    if (obj) return <ObjectInspector key={obj.id} obj={obj} />;
  }
  if (selection?.kind === 'camera') {
    const camera = world.cameras.find((c) => c.id === selection.id);
    if (camera) return <CameraInspector key={camera.id} camera={camera} />;
  }
  return <WorldInspector world={world} />;
}

// Ctrl+Z undoes, Ctrl+Shift+Z and Ctrl+Y redo, Delete removes the selection, Escape drops it, G and R
// switch the gizmo. Not while typing in a field, where the keys belong to the field.
function onShortcut(e: KeyboardEvent) {
  if (e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement || e.target instanceof HTMLTextAreaElement) return;
  const key = e.key.toLowerCase();
  if (e.ctrlKey || e.metaKey) {
    if (key === 'z' && !e.shiftKey) undo();
    else if (key === 'y' || (key === 'z' && e.shiftKey)) redo();
    else return;
    e.preventDefault();
  } else if (e.key === 'Delete') removeSelected();
  else if (e.key === 'Escape') select(null);
  else if (key === 'g') useEditor.setState({ gizmo: 'move' });
  else if (key === 'r') useEditor.setState({ gizmo: 'rotate' });
}
