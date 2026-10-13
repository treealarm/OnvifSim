import { useState } from 'react';
import type { CameraModel, PtzSettings, ServerStatus, Vec3 } from '../model';
import { changeCameraKind, removeSelected, updateCamera, useEditor } from '../store';
import { CameraPicture } from './CameraPicture';
import { CheckboxField, NumberField, Section, SelectField, TextField } from './fields';
import { PtzControl } from './PtzControl';

// The sizes the Media service offers ONVIF clients (CameraMedia.StandardResolutions).
const standardResolutions = ['320x240', '640x360', '640x480', '1280x720', '1920x1080'];

const kindOptions = [
  { value: 'fixed', label: 'Fixed' },
  { value: 'ptz', label: 'PTZ' },
] as const;

export function CameraInspector({ camera }: { camera: CameraModel }) {
  const head = useEditor((s) => s.heads[camera.id]);
  const status = useEditor((s) => s.status);
  const change = (patch: Partial<CameraModel>) => updateCamera({ ...camera, ...patch });
  const changePosition = (patch: Partial<Vec3>) => change({ position: { ...camera.position, ...patch } });
  const changeMetadata = (patch: Partial<CameraModel['metadata']>) => change({ metadata: { ...camera.metadata, ...patch } });
  const [showBoxes, setShowBoxes] = useState(true);

  return (
    <>
      <h2>
        {camera.name || camera.id} <span className="muted">{camera.id}</span>
      </h2>
      <CameraPicture camera={camera} showBoxes={showBoxes} />
      {camera.kind === 'ptz' && camera.ptz && <PtzControl camera={camera} ptz={camera.ptz} head={head} />}
      {status && <Addresses camera={camera} status={status} />}

      <Section title="Camera">
        <TextField label="Name" value={camera.name} onChange={(name) => change({ name })} />
        <SelectField label="Kind" value={camera.kind} options={kindOptions} onChange={(kind) => void changeCameraKind(camera, kind)} />
        <CheckboxField label="Degree grid" checked={camera.angleGrid} onChange={(angleGrid) => change({ angleGrid })} />
      </Section>

      <Section title={camera.kind === 'ptz' ? 'Mount' : 'Placement'}>
        <NumberField label="X (east)" value={camera.position.x} step={0.5} onChange={(x) => changePosition({ x })} />
        <NumberField label="Z (south)" value={camera.position.z} step={0.5} onChange={(z) => changePosition({ z })} />
        <NumberField label="Above ground" value={camera.position.y} min={0} step={0.5} onChange={(y) => changePosition({ y })} />
        <NumberField label="Heading" value={camera.heading} min={0} max={360} slider onChange={(heading) => change({ heading })} />
        <NumberField label="Pitch" value={camera.pitch} min={-90} max={90} slider onChange={(pitch) => change({ pitch })} />
        <NumberField label="Roll" value={camera.roll} min={-180} max={180} slider onChange={(roll) => change({ roll })} />
        {camera.kind === 'fixed' && (
          <NumberField label="Field of view" value={camera.hFov} min={1} max={170} slider onChange={(hFov) => change({ hFov })} />
        )}
      </Section>

      {camera.kind === 'ptz' && camera.ptz && <PtzLimits ptz={camera.ptz} onChange={(ptz) => change({ ptz })} />}

      <Section title="JPEG stream">
        <ResolutionField width={camera.video.width} height={camera.video.height} onChange={(width, height) => change({ video: { ...camera.video, width, height } })} />
        <NumberField label="Frame rate" value={camera.video.fps} min={1} max={30} onChange={(fps) => change({ video: { ...camera.video, fps } })} />
        <NumberField label="Quality" value={camera.video.quality} min={1} max={100} slider onChange={(quality) => change({ video: { ...camera.video, quality } })} />
      </Section>

      <Section title={status?.h264 === false ? 'H.264 stream (needs ffmpeg)' : 'H.264 stream'}>
        <ResolutionField width={camera.h264.width} height={camera.h264.height} onChange={(width, height) => change({ h264: { ...camera.h264, width, height } })} />
        <NumberField label="Frame rate" value={camera.h264.fps} min={1} max={30} onChange={(fps) => change({ h264: { ...camera.h264, fps } })} />
        <NumberField label="Bitrate, kbit/s" value={camera.h264.bitrateKbps} min={64} max={16384} step={64} onChange={(bitrateKbps) => change({ h264: { ...camera.h264, bitrateKbps } })} />
        <NumberField label="Key frame every" value={camera.h264.govLength} min={1} max={300} onChange={(govLength) => change({ h264: { ...camera.h264, govLength } })} />
      </Section>

      <Section title="Image">
        <NumberField label="Brightness" value={camera.imaging.brightness} min={0} max={100} slider onChange={(brightness) => change({ imaging: { ...camera.imaging, brightness } })} />
        <NumberField label="Contrast" value={camera.imaging.contrast} min={0} max={100} slider onChange={(contrast) => change({ imaging: { ...camera.imaging, contrast } })} />
        <NumberField label="Saturation" value={camera.imaging.colorSaturation} min={0} max={100} slider onChange={(colorSaturation) => change({ imaging: { ...camera.imaging, colorSaturation } })} />
      </Section>

      <Section title="Analytics">
        <CheckboxField label="Detect people" checked={camera.metadata.analytics} onChange={(analytics) => changeMetadata({ analytics })} />
        {camera.kind === 'ptz' && (
          <CheckboxField label="PTZ position in metadata" checked={camera.metadata.ptzStatus} onChange={(ptzStatus) => changeMetadata({ ptzStatus })} />
        )}
        <CheckboxField label="Boxes on the picture" checked={showBoxes} onChange={setShowBoxes} />
      </Section>

      <Section title="Access">
        <NumberField label="ONVIF port (0: auto)" value={camera.onvifPort} min={0} max={65535} onChange={(onvifPort) => change({ onvifPort })} />
        <Users users={camera.users} onChange={(users) => change({ users })} />
      </Section>

      <button type="button" className="danger" onClick={removeSelected}>
        Delete camera
      </button>
    </>
  );
}

function Addresses({ camera, status }: { camera: CameraModel; status: ServerStatus }) {
  const host = window.location.hostname;
  const port = status.onvifPorts[camera.id];
  const user = camera.users[0];
  const credentials = user ? `${encodeURIComponent(user.username)}:${encodeURIComponent(user.password)}@` : '';
  const rtsp = `rtsp://${credentials}${host}:${status.rtspPort}/${camera.id}`;
  return (
    <dl className="addresses">
      <dt>ONVIF</dt>
      <dd>{port ? `http://${host}:${port}/onvif/device_service` : 'not served (port taken?)'}</dd>
      <dt>RTSP JPEG</dt>
      <dd>{rtsp}</dd>
      {status.h264 && (
        <>
          <dt>RTSP H.264</dt>
          <dd>{rtsp}/h264</dd>
        </>
      )}
    </dl>
  );
}

function ResolutionField({ width, height, onChange }: { width: number; height: number; onChange: (width: number, height: number) => void }) {
  const current = `${width}x${height}`;
  const sizes = standardResolutions.includes(current) ? standardResolutions : [...standardResolutions, current];
  return (
    <SelectField
      label="Resolution"
      value={current}
      options={sizes.map((size) => ({ value: size, label: size.replace('x', ' × ') }))}
      onChange={(size) => {
        const [w, h] = size.split('x').map(Number);
        onChange(w, h);
      }}
    />
  );
}

function PtzLimits({ ptz, onChange }: { ptz: PtzSettings; onChange: (ptz: PtzSettings) => void }) {
  const change = (patch: Partial<PtzSettings>) => onChange({ ...ptz, ...patch });
  return (
    <Section title="PTZ head">
      <NumberField label="Pan from" value={ptz.panMin} min={-360} max={360} onChange={(panMin) => change({ panMin })} />
      <NumberField label="Pan to" value={ptz.panMax} min={-360} max={360} onChange={(panMax) => change({ panMax })} />
      <NumberField label="Tilt from" value={ptz.tiltMin} min={-90} max={90} onChange={(tiltMin) => change({ tiltMin })} />
      <NumberField label="Tilt to" value={ptz.tiltMax} min={-90} max={90} onChange={(tiltMax) => change({ tiltMax })} />
      <NumberField label="Wide view" value={ptz.fovWide} min={1} max={170} onChange={(fovWide) => change({ fovWide })} />
      <NumberField label="Tele view" value={ptz.fovTele} min={1} max={170} onChange={(fovTele) => change({ fovTele })} />
      <NumberField label="Pan speed, °/s" value={ptz.panSpeed} min={1} max={720} onChange={(panSpeed) => change({ panSpeed })} />
      <NumberField label="Tilt speed, °/s" value={ptz.tiltSpeed} min={1} max={720} onChange={(tiltSpeed) => change({ tiltSpeed })} />
      <NumberField label="Zoom speed, range/s" value={ptz.zoomSpeed} min={0.01} max={10} step={0.05} onChange={(zoomSpeed) => change({ zoomSpeed })} />
    </Section>
  );
}

function Users({ users, onChange }: { users: CameraModel['users']; onChange: (users: CameraModel['users']) => void }) {
  const changeUser = (index: number, patch: Partial<CameraModel['users'][number]>) =>
    onChange(users.map((user, i) => (i === index ? { ...user, ...patch } : user)));
  return (
    <>
      {users.length === 0 && <p className="muted">No users: the camera asks for no credentials.</p>}
      {users.map((user, index) => (
        <div className="user" key={index}>
          <TextField label="User" value={user.username} onChange={(username) => changeUser(index, { username })} />
          <TextField label="Password" type="password" value={user.password} onChange={(password) => changeUser(index, { password })} />
          <button type="button" onClick={() => onChange(users.filter((_, i) => i !== index))}>
            Remove user
          </button>
        </div>
      ))}
      <button type="button" onClick={() => onChange([...users, { username: `user${users.length + 1}`, password: '' }])}>
        Add user
      </button>
    </>
  );
}
