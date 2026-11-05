import { useMemo, useState } from 'react';
import {
  cameraIoOf,
  inputActive,
  lampLit,
  relayContactClosed,
  sameId,
  sameSink,
  wireTo,
  type CameraModel,
  type Sink,
  type World,
} from '../model';
import { updateCameraIo, useEditor } from '../store';
import { Dialog } from './Dialog';

// Every digital input and lamp of the world, on the left, and every relay on the right: an operator
// picks a sink and ticks the relays wired to it. A relay may drive several sinks and a sink several
// relays, so this is the one place that shows the whole wiring; the camera's own cards keep a quick
// control for a single link.
export function LinksDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const world = useEditor((s) => s.world);
  const [selected, setSelected] = useState<string | null>(null);
  const sinks = useMemo(() => (world ? Sinks(world) : []), [world]);
  const current = sinks.find((entry) => keyOf(entry.sink) === selected);

  function toggle(relayCamera: CameraModel, relayToken: string, linked: boolean) {
    const live = useEditor.getState().world;
    const sink = current?.sink;
    if (!live || !sink) return;
    const camera = live.cameras.find((c) => sameId(c.id, relayCamera.id));
    if (!camera) return;
    const io = cameraIoOf(live, camera);
    const wires = io.wires.filter((wire) => !(sameId(wire.relayToken, relayToken) && sameSink(wire, sink)));
    if (linked) wires.push(wireTo(camera.id, relayToken, sink));
    updateCameraIo(camera, { ...io, wires });
  }

  return (
    <Dialog
      open={open}
      title="Wiring"
      className="wide"
      onClose={onClose}
      actions={
        <button type="button" onClick={onClose}>
          Close
        </button>
      }
    >
      {!world ? null : (
        <div className="links">
          <div>
            <h3>Inputs and lamps</h3>
            <p className="muted">
              A closed relay reads 0 on an input; a normally-open lamp is lit while one is closed.
            </p>
            <ul className="links-list">
              {sinks.map((entry) => (
                <li key={keyOf(entry.sink)}>
                  <button
                    type="button"
                    aria-pressed={selected === keyOf(entry.sink)}
                    onClick={() => setSelected(keyOf(entry.sink))}
                  >
                    <span>{entry.label}</span>
                    <span className={entry.on ? 'io-state on' : 'io-state'}>{entry.state}</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
          <div>
            <h3>Relays</h3>
            {!current ? (
              <p className="muted">Select an input or a lamp on the left.</p>
            ) : (
              <ul className="links-list">
                {world.cameras.flatMap((camera) =>
                  camera.relayOutputs.map((relay) => {
                    const linked = world.wires.some(
                      (wire) =>
                        sameId(wire.relayCameraId, camera.id) &&
                        sameId(wire.relayToken, relay.token) &&
                        sameSink(wire, current.sink),
                    );
                    return (
                      <li key={`${camera.id}/${relay.token}`}>
                        <label>
                          <input
                            type="checkbox"
                            checked={linked}
                            onChange={(e) => toggle(camera, relay.token, e.target.checked)}
                          />
                          <span>
                            {camera.name || camera.id} / {relay.name || relay.token}
                          </span>
                          <span className={relayContactClosed(relay) ? 'io-state on' : 'io-state'}>
                            {relayContactClosed(relay) ? 'closed' : 'open'}
                          </span>
                        </label>
                      </li>
                    );
                  }),
                )}
              </ul>
            )}
          </div>
        </div>
      )}
    </Dialog>
  );
}

interface SinkEntry {
  sink: Sink;
  label: string;
  state: string;
  on: boolean;
}

// Every sink in the world, with what it reads now.
function Sinks(world: World): SinkEntry[] {
  const entries: SinkEntry[] = [];
  for (const camera of world.cameras) {
    for (const input of camera.digitalInputs) {
      const active = inputActive(world, camera.id, input.token);
      entries.push({
        sink: { kind: 'input', cameraId: camera.id, token: input.token },
        label: `${camera.name || camera.id} / ${input.name || input.token}`,
        state: active === null ? 'not wired' : active ? '1' : '0',
        on: active === true,
      });
    }
  }
  // Lamps stand in the world, not on a camera.
  for (const lamp of world.lamps) {
    const lit = lampLit(world, lamp.id);
    entries.push({
      sink: { kind: 'lamp', lampId: lamp.id },
      label: `${lamp.name || lamp.id} (lamp)`,
      state: lit ? 'lit' : 'off',
      on: lit,
    });
  }
  return entries;
}

function keyOf(sink: Sink): string {
  return sink.kind === 'lamp' ? `l:${sink.lampId}` : `i:${sink.cameraId}/${sink.token}`;
}
