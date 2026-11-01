import { useId, useState } from 'react';
import { ApiError } from '../api';
import {
  createAndOpenWorld,
  emptyTrash,
  restoreWorld,
  switchWorld,
  trashWorld,
  useEditor,
} from '../store';
import { Dialog } from './Dialog';

type WorldAction = { kind: 'switch' | 'create' | 'trash'; name: string };

// The worlds kept in the data directory: open one, make an empty one, trash it, or bring one back.
// The server asks for a recordings choice with a 409; this is where that question is shown.
export function WorldMenu() {
  const activeWorld = useEditor((s) => s.activeWorld);
  const worlds = useEditor((s) => s.worlds);
  const trash = useEditor((s) => s.trash);
  const recordings = useEditor((s) => s.recordings);
  const [open, setOpen] = useState(false);
  const [newName, setNewName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [recordingsFor, setRecordingsFor] = useState<WorldAction | null>(null);
  const [emptyOpen, setEmptyOpen] = useState(false);
  const nameId = useId();

  async function run(action: WorldAction, clearRecordings?: boolean) {
    setBusy(true);
    setError(null);
    try {
      if (action.kind === 'switch') await switchWorld(action.name, clearRecordings);
      else if (action.kind === 'trash') await trashWorld(action.name, clearRecordings);
      else {
        await createAndOpenWorld(action.name, clearRecordings);
        setNewName('');
      }
      setRecordingsFor(null);
      if (action.kind !== 'trash') setOpen(false);
    } catch (failure) {
      if (failure instanceof ApiError && failure.code === 'recordings') setRecordingsFor(action);
      else {
        setError(failure instanceof Error ? failure.message : String(failure));
        // A switch picked in the header has no dialog open to show the failure in.
        setOpen(true);
      }
    } finally {
      setBusy(false);
    }
  }

  async function restore(name: string) {
    setBusy(true);
    setError(null);
    try {
      await restoreWorld(name);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      setBusy(false);
    }
  }

  async function clearTrash() {
    setBusy(true);
    setError(null);
    try {
      await emptyTrash();
      setEmptyOpen(false);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="world-menu">
      <select
        aria-label="World"
        value={activeWorld ?? ''}
        disabled={!activeWorld || busy}
        onChange={(e) => void run({ kind: 'switch', name: e.target.value })}
      >
        {!activeWorld && <option value="">…</option>}
        {worlds.map((name) => (
          <option key={name} value={name}>
            {name}
          </option>
        ))}
      </select>
      <button type="button" aria-haspopup="dialog" onClick={() => setOpen(true)}>
        Manage worlds
      </button>

      <Dialog
        open={open}
        title="Worlds"
        onClose={() => setOpen(false)}
        actions={
          <button type="button" onClick={() => setOpen(false)}>
            Close
          </button>
        }
      >
        <ul className="world-list">
          {worlds.map((name) => (
            <li key={name} className="world-row">
              <span className="name">
                {name}
                {name === activeWorld && <span className="muted"> active</span>}
              </span>
              <button type="button" className="danger" disabled={busy} onClick={() => void run({ kind: 'trash', name })}>
                Trash
              </button>
            </li>
          ))}
        </ul>

        <form
          className="world-new"
          onSubmit={(e) => {
            e.preventDefault();
            if (newName.trim()) void run({ kind: 'create', name: newName.trim() });
          }}
        >
          <label htmlFor={nameId}>New world</label>
          <input id={nameId} value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="e.g. office" />
          <button type="submit" disabled={busy || newName.trim().length === 0}>
            Create
          </button>
        </form>

        {trash.length > 0 && (
          <section className="trash-section">
            <h3>Trash</h3>
            <ul className="world-list">
              {trash.map((name) => (
                <li key={name} className="world-row">
                  <span className="name">{name}</span>
                  <button type="button" disabled={busy} onClick={() => void restore(name)}>
                    Restore
                  </button>
                </li>
              ))}
            </ul>
            <button type="button" className="danger" disabled={busy} onClick={() => setEmptyOpen(true)}>
              Empty trash
            </button>
          </section>
        )}

        {error && (
          <p className="dialog-error" role="alert">
            {error}
          </p>
        )}
      </Dialog>

      <Dialog
        open={recordingsFor !== null}
        title="Camera recordings"
        onClose={() => setRecordingsFor(null)}
        actions={
          <>
            <button type="button" disabled={busy} onClick={() => setRecordingsFor(null)}>
              Cancel
            </button>
            <button type="button" disabled={busy} onClick={() => recordingsFor && void run(recordingsFor, false)}>
              Keep recordings
            </button>
            <button type="button" className="danger" disabled={busy} onClick={() => recordingsFor && void run(recordingsFor, true)}>
              Clear recordings
            </button>
          </>
        }
      >
        <p>
          The cameras in “{activeWorld}” have recordings ({recordings.cameraCount}). Clear them before the switch, or
          keep them?
        </p>
      </Dialog>

      <Dialog
        open={emptyOpen}
        title="Empty trash"
        onClose={() => setEmptyOpen(false)}
        actions={
          <>
            <button type="button" disabled={busy} onClick={() => setEmptyOpen(false)}>
              Cancel
            </button>
            <button type="button" className="danger" disabled={busy} onClick={() => void clearTrash()}>
              Empty trash
            </button>
          </>
        }
      >
        <p>Every world in the trash is deleted permanently. This cannot be undone.</p>
        {error && (
          <p className="dialog-error" role="alert">
            {error}
          </p>
        )}
      </Dialog>
    </div>
  );
}
