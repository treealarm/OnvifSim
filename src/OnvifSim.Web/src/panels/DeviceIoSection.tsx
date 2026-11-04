import {
  cameraIoOf,
  inputActive,
  lampLit,
  nextId,
  sameId,
  wireTo,
  wiresToInput,
  wiresToLamp,
  type CameraIo,
  type CameraModel,
  type DigitalInput,
  type Lamp,
  type RelayOutput,
  type Sink,
  type Wire,
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
    const relays = io.relayOutputs.map((relay, i) => (i === index ? { ...relay, ...patch } : relay));
    // Renaming a token takes its wire along, or the wire would name a relay that no longer exists.
    const wires =
      patch.token === undefined
        ? io.wires
        : io.wires.map((wire) =>
            sameId(wire.relayToken, io.relayOutputs[index].token) ? { ...wire, relayToken: patch.token! } : wire,
          );
    save({ ...io, relayOutputs: relays, wires });
  }

  function addRelay() {
    const token = nextId('relay', io.relayOutputs.map((relay) => relay.token));
    save({
      ...io,
      relayOutputs: [
        ...io.relayOutputs,
        { token, name: `Relay ${token}`, mode: 'bistable', delaySeconds: 5, idleState: 'open', active: false, position: { x: 2, y: 0, z: 2 } },
      ],
    });
  }

  function removeRelay(index: number) {
    const token = io.relayOutputs[index].token;
    save({
      ...io,
      relayOutputs: io.relayOutputs.filter((_, i) => i !== index),
      wires: io.wires.filter((wire) => !sameId(wire.relayToken, token)),
    });
  }

  // The quick control in the relay's card, good for one link. A relay wired to several sinks shows
  // how many and leaves the editing to the Links window, rather than silently dropping the rest.
  function wireRelay(relay: RelayOutput, target: string) {
    const others = io.wires.filter((wire) => !sameId(wire.relayToken, relay.token));
    if (target === '') {
      save({ ...io, wires: others });
      return;
    }
    save({ ...io, wires: [...others, wireTo(camera.id, relay.token, parseTarget(target))] });
  }

  function changeInput(index: number, patch: Partial<DigitalInput>) {
    const inputs = io.digitalInputs.map((input, i) => (i === index ? { ...input, ...patch } : input));
    save({ ...io, digitalInputs: inputs });
  }

  function addInput() {
    const token = nextId('input', io.digitalInputs.map((input) => input.token));
    save({
      ...io,
      digitalInputs: [...io.digitalInputs, { token, name: `Input ${token}`, idleState: 'open', position: { x: 2, y: 0, z: 0 } }],
    });
  }

  // A wire from another camera into the input goes with it; the server drops it the same way.
  function removeInput(index: number) {
    const token = io.digitalInputs[index].token;
    save({
      ...io,
      digitalInputs: io.digitalInputs.filter((_, i) => i !== index),
      wires: io.wires.filter((wire) => !(sameId(wire.inputCameraId ?? '', camera.id) && sameId(wire.inputToken ?? '', token))),
    });
  }

  function addLamp() {
    const token = nextId('lamp', io.lamps.map((lamp) => lamp.token));
    save({
      ...io,
      lamps: [...io.lamps, { token, name: `Lamp ${token}`, normallyClosed: false, position: { x: 2, y: 0, z: 0 } }],
    });
  }

  function changeLamp(index: number, patch: Partial<Lamp>) {
    const lamps = io.lamps.map((lamp, i) => (i === index ? { ...lamp, ...patch } : lamp));
    const wires =
      patch.token === undefined
        ? io.wires
        : io.wires.map((wire) =>
            sameId(wire.lampToken ?? '', io.lamps[index].token) ? { ...wire, lampToken: patch.token! } : wire,
          );
    save({ ...io, lamps, wires });
  }

  function removeLamp(index: number) {
    const token = io.lamps[index].token;
    save({
      ...io,
      lamps: io.lamps.filter((_, i) => i !== index),
      wires: io.wires.filter((wire) => !(sameId(wire.lampCameraId ?? '', camera.id) && sameId(wire.lampToken ?? '', token))),
    });
  }

  return (
    <>
      <Section title="Relay outputs">
        {io.relayOutputs.length === 0 && <p className="muted">No relay outputs.</p>}
        {io.relayOutputs.map((relay, index) => {
          const links = io.wires.filter((wire) => sameId(wire.relayToken, relay.token));
          const target = links.length === 1 ? targetOf(links[0]) : '';
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
              {links.length > 1 ? (
                <p className="muted">
                  Wired to {links.length} devices — edit the links in Links…
                </p>
              ) : (
                <SelectField
                  label="Wired to"
                  value={target}
                  options={sinkOptions(world.cameras)}
                  onChange={(value) => wireRelay(relay, value)}
                />
              )}
              <DevicePosition offset={relay.position} onChange={(position) => changeRelay(index, { position })} />
            </div>
          );
        })}
        <button type="button" onClick={addRelay}>
          Add relay output
        </button>
      </Section>

      <Section title="Digital inputs">
        {io.digitalInputs.length === 0 && <p className="muted">No digital inputs.</p>}
        {io.digitalInputs.map((input, index) => {
          const wires = wiresToInput(world, camera.id, input.token);
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
                {wires.length === 0
                  ? 'Not wired: no relay drives it.'
                  : `${wires.map((wire) => `${wire.relayCameraId}/${wire.relayToken}`).join(', ')} — ${active ? '1' : '0'}`}
              </p>
              <DevicePosition offset={input.position} onChange={(position) => changeInput(index, { position })} />
            </div>
          );
        })}
        <button type="button" onClick={addInput}>
          Add digital input
        </button>
      </Section>

      <Section title="Lamps">
        {io.lamps.length === 0 && <p className="muted">No lamps.</p>}
        {io.lamps.map((lamp, index) => {
          const wires = wiresToLamp(world, camera.id, lamp.token);
          const lit = lampLit(world, camera.id, lamp.token);
          return (
            <div className="io-card" key={lamp.token}>
              <div className="io-head">
                <strong>{lamp.name || lamp.token}</strong>
                <span className="muted">{lamp.token}</span>
                <button type="button" onClick={() => removeLamp(index)}>
                  Remove
                </button>
              </div>
              <BlurTextField label="Token" value={lamp.token} onChange={(token) => changeLamp(index, { token })} />
              <BlurTextField label="Name" value={lamp.name} onChange={(name) => changeLamp(index, { name })} />
              <CheckboxField
                label="Normally closed (lit while no relay is closed)"
                checked={lamp.normallyClosed}
                onChange={(normallyClosed) => changeLamp(index, { normallyClosed })}
              />
              <p className={lit ? 'io-state on' : 'io-state'}>
                {wires.length === 0
                  ? 'Not wired: no relay drives it.'
                  : `${wires.map((wire) => `${wire.relayCameraId}/${wire.relayToken}`).join(', ')} — ${lit ? 'lit' : 'off'}`}
              </p>
              <DevicePosition offset={lamp.position} onChange={(position) => changeLamp(index, { position })} />
            </div>
          );
        })}
        <button type="button" onClick={addLamp}>
          Add lamp
        </button>
      </Section>
    </>
  );
}

// Where a wire may go: any digital input or lamp of any camera. A sink may take several relays, so
// nothing is excluded.
function sinkOptions(cameras: CameraModel[]): { value: string; label: string }[] {
  const options = [{ value: '', label: 'Not wired' }];
  for (const other of cameras) {
    for (const input of other.digitalInputs)
      options.push({ value: `i:${other.id}/${input.token}`, label: `${other.name || other.id} / ${input.name || input.token} (input)` });
    for (const lamp of other.lamps ?? [])
      options.push({ value: `l:${other.id}/${lamp.token}`, label: `${other.name || other.id} / ${lamp.name || lamp.token} (lamp)` });
  }
  return options;
}

// Ids and tokens are URL-safe, so a slash cannot appear in either; "i:" or "l:" says which sink.
function targetOf(wire: Wire): string {
  return wire.lampCameraId
    ? `l:${wire.lampCameraId}/${wire.lampToken}`
    : `i:${wire.inputCameraId}/${wire.inputToken}`;
}

function parseTarget(value: string): Sink {
  const [cameraId, token] = value.slice(2).split('/');
  return { kind: value.startsWith('l:') ? 'lamp' : 'input', cameraId, token };
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
