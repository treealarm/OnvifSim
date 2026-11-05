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

// Every relay of the world, on the left, and every digital input and lamp on the right: an operator
// picks a relay and ticks the sinks it drives. A relay may drive several sinks and a sink several
// relays, so this is the one place that shows the whole wiring; the camera's own cards keep a quick
// control for a single link.
export function LinksDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const world = useEditor((s) => s.world);
  const [selected, setSelected] = useState<string | null>(null);
  const relays = useMemo(() => (world ? Relays(world) : []), [world]);
  const sinks = useMemo(() => (world ? Sinks(world) : []), [world]);
  const current = relays.find((entry) => entry.key === selected);

  function toggle(sink: Sink, linked: boolean) {
    const live = useEditor.getState().world;
    if (!live || !current) return;
    const camera = live.cameras.find((c) => sameId(c.id, current.camera.id));
    if (!camera) return;
    const io = cameraIoOf(live, camera);
    const wires = io.wires.filter((wire) => !(sameId(wire.relayToken, current.token) && sameSink(wire, sink)));
    if (linked) wires.push(wireTo(camera.id, current.token, sink));
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
            <h3>Relays</h3>
            <p className="muted">A relay drives the inputs and lamps ticked beside it.</p>
            <ul className="links-list">
              {relays.map((entry) => (
                <li key={entry.key}>
                  <button
                    type="button"
                    aria-pressed={selected === entry.key}
                    onClick={() => setSelected(entry.key)}
                  >
                    <span>{entry.label}</span>
                    <span className={entry.closed ? 'io-state on' : 'io-state'}>{entry.state}</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
          <div>
            <h3>Inputs and lamps</h3>
            {!current ? (
              <p className="muted">Select a relay on the left.</p>
            ) : (
              <>
                <p className="muted">
                  A closed relay contact reads 0 on an input; a normally-open lamp is lit while one is
                  closed.
                </p>
                <ul className="links-list">
                  {sinks.map((entry) => {
                    const linked = world.wires.some(
                      (wire) =>
                        sameId(wire.relayCameraId, current.camera.id) &&
                        sameId(wire.relayToken, current.token) &&
                        sameSink(wire, entry.sink),
                    );
                    return (
                      <li key={keyOf(entry.sink)}>
                        <label>
                          <input
                            type="checkbox"
                            checked={linked}
                            onChange={(e) => toggle(entry.sink, e.target.checked)}
                          />
                          <span>{entry.label}</span>
                          <span className={entry.on ? 'io-state on' : 'io-state'}>{entry.state}</span>
                        </label>
                      </li>
                    );
                  })}
                </ul>
              </>
            )}
          </div>
        </div>
      )}
    </Dialog>
  );
}

interface RelayEntry {
  key: string;
  camera: CameraModel;
  token: string;
  label: string;
  state: string;
  closed: boolean;
}

// Every relay in the world, with how its contact stands now.
function Relays(world: World): RelayEntry[] {
  const entries: RelayEntry[] = [];
  for (const camera of world.cameras) {
    for (const relay of camera.relayOutputs) {
      const closed = relayContactClosed(relay);
      entries.push({
        key: `${camera.id}/${relay.token}`,
        camera,
        token: relay.token,
        label: `${camera.name || camera.id} / ${relay.name || relay.token}`,
        state: closed ? 'closed' : 'open',
        closed,
      });
    }
  }
  return entries;
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
