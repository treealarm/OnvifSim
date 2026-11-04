import {
  cameraIoOf,
  inputActive,
  nextId,
  sameId,
  wireToInput,
  type CameraIo,
  type CameraModel,
  type DigitalInput,
  type RelayOutput,
} from '../model';
import { updateCameraIo, updateRelay, useEditor } from '../store';
import { BlurTextField, CheckboxField, NumberField, Section, SelectField } from './fields';

const modeOptions = [
  { value: 'bistable', label: 'Bistable' },
  { value: 'monostable', label: 'Monostable' },
] as const;

const idleOptions = [
  { value: 'open', label: 'Open' },
  { value: 'closed', label: 'Closed' },
] as const;

// The camera's relay outputs and digital inputs: relays as switches with lamps, inputs as sensors they
// may be wired to, on this camera or another. The live state of an input follows its wire.
export function DeviceIoSection({ camera }: { camera: CameraModel }) {
  const world = useEditor((s) => s.world);
  if (!world) return null;
  const io = cameraIoOf(world, camera);
  const save = (next: CameraIo) => updateCameraIo(camera, next);

  function changeRelay(index: number, patch: Partial<RelayOutput>) {
    const relays = io.relays.map((relay, i) => (i === index ? { ...relay, ...patch } : relay));
    // Renaming a token takes its wire along, or the wire would name a relay that no longer exists.
    const wires =
      patch.token === undefined
        ? io.wires
        : io.wires.map((wire) =>
            sameId(wire.relayToken, io.relays[index].token) ? { ...wire, relayToken: patch.token! } : wire,
          );
    save({ ...io, relays, wires });
  }

  function addRelay() {
    const token = nextId('relay', io.relays.map((relay) => relay.token));
    save({
      ...io,
      relays: [
        ...io.relays,
        { token, name: `Relay ${token}`, mode: 'bistable', delaySeconds: 5, idleState: 'open', active: false, position: { x: 2, y: 0, z: 2 } },
      ],
    });
  }

  function removeRelay(index: number) {
    const token = io.relays[index].token;
    save({
      ...io,
      relays: io.relays.filter((_, i) => i !== index),
      wires: io.wires.filter((wire) => !sameId(wire.relayToken, token)),
    });
  }

  // One wire per relay here; the model allows a relay to drive several inputs, and one written by hand
  // is left alone unless this relay is wired again.
  function wireRelay(relay: RelayOutput, target: string) {
    const others = io.wires.filter((wire) => !sameId(wire.relayToken, relay.token));
    if (target === '') {
      save({ ...io, wires: others });
      return;
    }
    const [inputCameraId, inputToken] = splitTarget(target);
    save({ ...io, wires: [...others, { relayCameraId: camera.id, relayToken: relay.token, inputCameraId, inputToken }] });
  }

  function changeInput(index: number, patch: Partial<DigitalInput>) {
    const inputs = io.inputs.map((input, i) => (i === index ? { ...input, ...patch } : input));
    save({ ...io, inputs });
  }

  function addInput() {
    const token = nextId('input', io.inputs.map((input) => input.token));
    save({
      ...io,
      inputs: [...io.inputs, { token, name: `Input ${token}`, idleState: 'open', position: { x: 2, y: 0, z: 0 } }],
    });
  }

  // A wire from another camera into the input goes with it; the server drops it the same way.
  function removeInput(index: number) {
    const token = io.inputs[index].token;
    save({
      ...io,
      inputs: io.inputs.filter((_, i) => i !== index),
      wires: io.wires.filter((wire) => !(sameId(wire.inputCameraId, camera.id) && sameId(wire.inputToken, token))),
    });
  }

  return (
    <>
      <Section title="Relay outputs">
        {io.relays.length === 0 && <p className="muted">No relay outputs.</p>}
        {io.relays.map((relay, index) => {
          const wire = io.wires.find((w) => sameId(w.relayToken, relay.token));
          const target = wire ? joinTarget(wire.inputCameraId, wire.inputToken) : '';
          return (
            <div className="io-card" key={relay.token}>
              <div className="io-head">
                <strong>{relay.name || relay.token}</strong>
                <span className="muted">{relay.token}</span>
                <button type="button" onClick={() => removeRelay(index)}>
                  Remove
                </button>
              </div>
              <BlurTextField label="Token" value={relay.token} onChange={(token) => changeRelay(index, { token })} />
              <BlurTextField label="Name" value={relay.name} onChange={(name) => changeRelay(index, { name })} />
              <SelectField
                label="Mode"
                value={relay.mode}
                options={modeOptions}
                onChange={(mode) => changeRelay(index, { mode })}
              />
              {relay.mode === 'monostable' && (
                <NumberField
                  label="On for, s"
                  value={relay.delaySeconds}
                  min={0.05}
                  max={3600}
                  step={0.5}
                  onChange={(delaySeconds) => changeRelay(index, { delaySeconds })}
                />
              )}
              <SelectField
                label="Idle state"
                value={relay.idleState}
                options={idleOptions}
                onChange={(idleState) => changeRelay(index, { idleState })}
              />
              <CheckboxField
                label="Active (the lamp is lit)"
                checked={relay.active}
                onChange={(active) => updateRelay(camera.id, relay.token, active)}
              />
              <SelectField
                label="Wired to"
                value={target}
                options={wireOptions(world.cameras, relay, io)}
                onChange={(value) => wireRelay(relay, value)}
              />
              <DevicePosition offset={relay.position} onChange={(position) => changeRelay(index, { position })} />
            </div>
          );
        })}
        <button type="button" onClick={addRelay}>
          Add relay output
        </button>
      </Section>

      <Section title="Digital inputs">
        {io.inputs.length === 0 && <p className="muted">No digital inputs.</p>}
        {io.inputs.map((input, index) => {
          const wire = wireToInput(world, camera.id, input.token);
          const active = inputActive(world, camera.id, input.token);
          return (
            <div className="io-card" key={input.token}>
              <div className="io-head">
                <strong>{input.name || input.token}</strong>
                <span className="muted">{input.token}</span>
                <button type="button" onClick={() => removeInput(index)}>
                  Remove
                </button>
              </div>
              <BlurTextField label="Name" value={input.name} onChange={(name) => changeInput(index, { name })} />
              <SelectField
                label="Idle state"
                value={input.idleState}
                options={idleOptions}
                onChange={(idleState) => changeInput(index, { idleState })}
              />
              <p className={active === true ? 'io-state on' : 'io-state'}>
                {wire
                  ? `${wire.relayCameraId}/${wire.relayToken} — ${active ? 'on' : 'off'}`
                  : 'Not wired: no relay drives it.'}
              </p>
              <DevicePosition offset={input.position} onChange={(position) => changeInput(index, { position })} />
            </div>
          );
        })}
        <button type="button" onClick={addInput}>
          Add digital input
        </button>
      </Section>
    </>
  );
}

// Where a wire may go: every input no other wire drives, plus the one this relay already drives.
function wireOptions(cameras: CameraModel[], relay: RelayOutput, io: CameraIo): { value: string; label: string }[] {
  const taken = new Set(io.wires.filter((w) => !sameId(w.relayToken, relay.token)).map((w) => joinTarget(w.inputCameraId, w.inputToken).toLowerCase()));
  const options = [{ value: '', label: 'Not wired' }];
  for (const other of cameras)
    for (const input of other.digitalInputs) {
      const value = joinTarget(other.id, input.token);
      if (taken.has(value.toLowerCase())) continue;
      options.push({ value, label: `${other.name || other.id} / ${input.name || input.token}` });
    }
  return options;
}

// Ids and tokens are URL-safe, so a slash cannot appear in either.
function joinTarget(cameraId: string, token: string): string {
  return `${cameraId}/${token}`;
}

function splitTarget(value: string): [string, string] {
  const at = value.indexOf('/');
  return [value.slice(0, at), value.slice(at + 1)];
}

// The device's place relative to its camera: to the right and behind it, and above the ground there.
function DevicePosition({ offset, onChange }: { offset: { x: number; y: number; z: number }; onChange: (offset: { x: number; y: number; z: number }) => void }) {
  return (
    <>
      <NumberField label="Right of camera, m" value={offset.x} step={0.5} onChange={(x) => onChange({ ...offset, x })} />
      <NumberField label="Behind camera, m" value={offset.z} step={0.5} onChange={(z) => onChange({ ...offset, z })} />
      <NumberField label="Above ground, m" value={offset.y} step={0.5} onChange={(y) => onChange({ ...offset, y })} />
    </>
  );
}
