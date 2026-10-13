import { useEffect, useState } from 'react';
import type { CameraModel } from '../model';

// A person the camera's analytics sees, as its metadata stream reports them: the object id and the box in
// ONVIF's normalized frame coordinates (x -1..1 left to right, y -1..1 bottom to top).
interface SeenObject {
  id: number;
  walker: string;
  left: number;
  top: number;
  right: number;
  bottom: number;
}

// The server's own picture, exactly what RTSP and ONVIF clients get, with the people its analytics
// sees boxed over it.
export function CameraPicture({ camera, showBoxes }: { camera: CameraModel; showBoxes: boolean }) {
  const objects = useSeenObjects(camera.id, showBoxes && camera.metadata.analytics);
  const percent = (fraction: number) => `${fraction * 100}%`;
  return (
    <div className="preview" style={{ aspectRatio: `${camera.video.width} / ${camera.video.height}` }}>
      <img src={`/api/cameras/${encodeURIComponent(camera.id)}/mjpeg`} alt={`What ${camera.name || camera.id} sees`} />
      {objects.map((o) => (
        <div
          key={o.id}
          className="seen"
          title={`Object ${o.id}: ${o.walker}`}
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
