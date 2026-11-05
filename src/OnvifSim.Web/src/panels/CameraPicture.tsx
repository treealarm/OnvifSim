import { useEffect, useState } from 'react';
import { inputActive, relayContactClosed, type CameraModel } from '../model';
import { useEditor } from '../store';

// A person or car the camera's analytics sees, as its metadata stream reports them: the object id and
// the box in ONVIF's normalized frame coordinates (x -1..1 left to right, y -1..1 bottom to top).
interface SeenObject {
  id: number;
  kind: 'person' | 'vehicle';
  objectId: string;
  left: number;
  top: number;
  right: number;
  bottom: number;
}

// The server's own picture, exactly what RTSP and ONVIF clients get, with the people and cars its
// analytics sees boxed over it — and, on request, the states of its relays and inputs. Those are
// settings of the camera rather than shapes in the world, so the picture is where they are shown.
export function CameraPicture({ camera, showBoxes, showDevices }: { camera: CameraModel; showBoxes: boolean; showDevices: boolean }) {
  const world = useEditor((s) => s.world);
  const objects = useSeenObjects(camera.id, showBoxes && camera.metadata.analytics);
  const percent = (fraction: number) => `${fraction * 100}%`;
  const devices = world && showDevices ? [
    ...camera.relayOutputs.map((relay) => `${relay.name || relay.token}: ${relayContactClosed(relay) ? 'closed' : 'open'}`),
    ...camera.digitalInputs.map((input) => {
      const active = inputActive(world, camera.id, input.token);
      return `${input.name || input.token}: ${active === null ? 'not wired' : active ? '1' : '0'}`;
    }),
  ] : [];
  return (
    <div className="preview" style={{ aspectRatio: `${camera.video.width} / ${camera.video.height}` }}>
      <img src={`/api/cameras/${encodeURIComponent(camera.id)}/mjpeg`} alt={`What ${camera.name || camera.id} sees`} />
      {devices.length > 0 && (
        <div className="preview-devices">
          {devices.map((line) => (
            <div key={line}>{line}</div>
          ))}
        </div>
      )}
      {objects.map((o) => (
        <div
          key={o.id}
          className="seen"
          title={`Object ${o.id}: ${o.kind} ${o.objectId}`}
          style={{
            left: percent((o.left + 1) / 2),
            top: percent((1 - o.top) / 2),
            width: percent((o.right - o.left) / 2),
            height: percent((o.top - o.bottom) / 2),
          }}
        >
          <span>{o.id}</span>
        </div>
      ))}
    </div>
  );
}

// The camera's analytics, look by look, while enabled.
function useSeenObjects(cameraId: string, enabled: boolean): SeenObject[] {
  const [objects, setObjects] = useState<SeenObject[]>([]);
  useEffect(() => {
    setObjects([]);
    if (!enabled) return;
    const source = new EventSource(`/api/cameras/${encodeURIComponent(cameraId)}/analytics`);
    source.addEventListener('objects', (event) => setObjects(JSON.parse((event as MessageEvent<string>).data) as SeenObject[]));
    return () => source.close();
  }, [cameraId, enabled]);
  return objects;
}
