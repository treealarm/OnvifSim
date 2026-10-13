import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { api } from '../api';
import { clamp, type CameraModel, type Head, type PtzPosition, type PtzSettings } from '../model';
import { showError } from '../store';
import { NumberField } from './fields';

// A server move lasts a second (EditorApi.JoystickTimeout); a held control renews it this often.
const renewMs = 400;

// Sends values one request at a time, skipping to the latest: a slider dragged fast sends a few
// requests, ending with where it stopped, instead of queueing one per pixel.
function useLatestSender<T>(send: (value: T) => Promise<unknown>) {
  const busy = useRef(false);
  const next = useRef<{ value: T } | null>(null);
  function push(value: T) {
    if (busy.current) {
      next.current = { value };
      return;
    }
    busy.current = true;
    send(value)
      .catch(showError)
      .finally(() => {
        busy.current = false;
        const queued = next.current;
        next.current = null;
        if (queued) push(queued.value);
      });
  }
  return push;
}

// Drives a PTZ camera through the same simulator ONVIF clients use: a joystick for continuous moves
// (drag it, or focus it and hold the arrow keys), zoom buttons, and boxes that send the head to a position.
export function PtzControl({ camera, ptz, head }: { camera: CameraModel; ptz: PtzSettings; head: Head | undefined }) {
  const position = head ?? ptz;
  // Moves and stops share one queue: a stop must not be overtaken by a renewal still waiting to go.
  const drive = useLatestSender((velocity: Partial<PtzPosition> | 'stop') =>
    velocity === 'stop' ? api.ptzStop(camera.id) : api.ptzMove(camera.id, velocity),
  );
  const goTo = useLatestSender((target: Partial<PtzPosition>) => api.ptzGoto(camera.id, target));
  const move = (velocity: Partial<PtzPosition>) => drive(velocity);
  const stop = () => drive('stop');

  return (
    <div className="ptz">
      <div className="ptz-row">
        <Joystick onMove={(pan, tilt) => move({ pan, tilt })} onStop={stop} />
        <div className="ptz-buttons">
          <HoldButton label="Zoom in" onHold={() => move({ zoom: 1 })} onRelease={stop} />
          <HoldButton label="Zoom out" onHold={() => move({ zoom: -1 })} onRelease={stop} />
          <button type="button" onClick={() => api.ptzHome(camera.id).catch(showError)}>
            Home
          </button>
          <span className="muted">{head?.moving ? 'moving' : 'idle'}</span>
        </div>
      </div>
      <NumberField label="Pan" value={position.pan} min={ptz.panMin} max={ptz.panMax} step={0.5} slider onChange={(pan) => goTo({ pan })} />
      <NumberField label="Tilt" value={position.tilt} min={ptz.tiltMin} max={ptz.tiltMax} step={0.5} slider onChange={(tilt) => goTo({ tilt })} />
      <NumberField label="Zoom" value={position.zoom} min={0} max={1} step={0.01} slider onChange={(zoom) => goTo({ zoom })} />
    </div>
  );
}

const arrowKeys: Record<string, [number, number]> = {
  ArrowLeft: [-1, 0],
  ArrowRight: [1, 0],
  ArrowUp: [0, 1],
  ArrowDown: [0, -1],
};

// A pad whose knob sets pan and tilt velocities (-1..1) by how far it is dragged from the centre.
function Joystick({ onMove, onStop }: { onMove: (pan: number, tilt: number) => void; onStop: () => void }) {
  const [knob, setKnob] = useState({ x: 0, y: 0 });
  const renew = useRef<ReturnType<typeof setInterval> | null>(null);

  function hold(pan: number, tilt: number) {
    setKnob({ x: pan, y: -tilt });
    onMove(pan, tilt);
    if (renew.current) clearInterval(renew.current);
    renew.current = setInterval(() => onMove(pan, tilt), renewMs);
  }

  function release() {
    if (!renew.current) return;
    clearInterval(renew.current);
    renew.current = null;
    setKnob({ x: 0, y: 0 });
    onStop();
  }

  // Deselecting the camera mid-move must not leave it renewing the move for ever.
  useEffect(() => release, []);

  function track(e: PointerEvent<HTMLDivElement>) {
    const rect = e.currentTarget.getBoundingClientRect();
    const x = clamp(((e.clientX - rect.left) / rect.width) * 2 - 1, -1, 1);
    const y = clamp(((e.clientY - rect.top) / rect.height) * 2 - 1, -1, 1);
    hold(Number(x.toFixed(2)), Number((-y).toFixed(2)));
  }

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    const direction = arrowKeys[e.key];
    if (!direction || e.repeat) return;
    e.preventDefault();
    hold(direction[0] * 0.5, direction[1] * 0.5);
  }

  return (
    <div
      className="joystick"
      role="slider"
      aria-label="Pan and tilt joystick: drag, or hold the arrow keys"
      aria-valuetext={`pan ${knob.x}, tilt ${-knob.y}`}
      tabIndex={0}
      onPointerDown={(e) => {
        e.currentTarget.setPointerCapture(e.pointerId);
        track(e);
      }}
      onPointerMove={(e) => {
        if (e.currentTarget.hasPointerCapture(e.pointerId)) track(e);
      }}
      onPointerUp={release}
      onPointerCancel={release}
      onKeyDown={onKeyDown}
      onKeyUp={(e) => arrowKeys[e.key] && release()}
      onBlur={release}
    >
      <div className="joystick-knob" style={{ left: `${50 + knob.x * 40}%`, top: `${50 + knob.y * 40}%` }} />
    </div>
  );
}

function HoldButton({ label, onHold, onRelease }: { label: string; onHold: () => void; onRelease: () => void }) {
  const renew = useRef<ReturnType<typeof setInterval> | null>(null);
  function press() {
    onHold();
    renew.current = setInterval(onHold, renewMs);
  }
  function release() {
    if (!renew.current) return;
    clearInterval(renew.current);
    renew.current = null;
    onRelease();
  }
  useEffect(() => release, []);
  return (
    <button
      type="button"
      onPointerDown={press}
      onPointerUp={release}
      onPointerLeave={release}
      onKeyDown={(e) => (e.key === ' ' || e.key === 'Enter') && !e.repeat && press()}
      onKeyUp={(e) => (e.key === ' ' || e.key === 'Enter') && release()}
    >
      {label}
    </button>
  );
}
