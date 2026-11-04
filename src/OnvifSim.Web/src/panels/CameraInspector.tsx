import { Fragment, useState } from 'react';
import {
  effectiveStreams,
  kindColor,
  streamH264,
  streamVideo,
  type CameraModel,
  type PtzSettings,
  type ServerStatus,
  type StreamModel,
  type Vec3,
} from '../model';
import { changeCameraKind, removeSelected, updateCamera, useEditor } from '../store';
import { CameraPicture } from './CameraPicture';
import { DeviceIoSection } from './DeviceIoSection';
import { BlurTextField, CheckboxField, ColorField, NumberField, Section, SelectField, TextField } from './fields';
import { PtzControl } from './PtzControl';

// The sizes the Media service offers ONVIF clients (CameraMedia.StandardResolutions).
const standardResolutions = ['320x240', '640x360', '640x480', '1280x720', '1920x1080'];

const kindOptions = [
  { value: 'fixed', label: 'Fixed' },
  { value: 'ptz', label: 'PTZ' },
] as const;

const rotateOptions = [
  { value: '0', label: 'Upright' },
  { value: '90', label: '90° clockwise' },
  { value: '180', label: '180°' },
  { value: '270', label: '90° anticlockwise' },
] as const;

export function CameraInspector({ camera }: { camera: CameraModel }) {
  const head = useEditor((s) => s.heads[camera.id]);
  const status = useEditor((s) => s.status);
  const vehicles = useEditor((s) => s.world?.vehicles ?? []);
  const mounted = !!camera.mountedOnVehicleId;
  const change = (patch: Partial<CameraModel>) => updateCamera({ ...camera, ...patch });
  const changePosition = (patch: Partial<Vec3>) => change({ position: { ...camera.position, ...patch } });
  const changeMetadata = (patch: Partial<CameraModel['metadata']>) => change({ metadata: { ...camera.metadata, ...patch } });
  const [showBoxes, setShowBoxes] = useState(true);

  return (
    <>
      {/* Pinned: while a property is being changed the picture stays in sight, showing the change
          at once instead of scrolling away with the form. */}
      <div className="inspector-head">
        <h2>
          {camera.name || camera.id} <span className="muted">{camera.id}</span>
        </h2>
        <CameraPicture camera={camera} showBoxes={showBoxes} />
      </div>
      {camera.kind === 'ptz' && camera.ptz && <PtzControl camera={camera} ptz={camera.ptz} head={head} />}
      {status && <Addresses camera={camera} status={status} />}

      <Section title="Camera">
        <TextField label="Name" value={camera.name} onChange={(name) => change({ name })} />
        <SelectField label="Kind" value={camera.kind} options={kindOptions} onChange={(kind) => void changeCameraKind(camera, kind)} />
        <CheckboxField label="Degree grid" checked={camera.angleGrid} onChange={(angleGrid) => change({ angleGrid })} />
        <ColorField
          label="Colour"
          value={camera.color ?? kindColor(camera.kind)}
          onChange={(color) => change({ color })}
        />
      </Section>

      <Section title={mounted ? 'Mount on the vehicle' : camera.kind === 'ptz' ? 'Mount' : 'Placement'}>
        <SelectField
          label="Mounted on"
          value={camera.mountedOnVehicleId ?? ''}
          options={[{ value: '', label: 'Standing on the ground' }, ...vehicles.map((v) => ({ value: v.id, label: v.id }))]}
          onChange={(id) => change({ mountedOnVehicleId: id === '' ? null : id })}
        />
        <p className="muted">On a car, the camera rides it: its place and picture move along the route.</p>
        <NumberField label={mounted ? 'X (right)' : 'X (east)'} value={camera.position.x} step={0.5} onChange={(x) => changePosition({ x })} />
        <NumberField label={mounted ? 'Z (back)' : 'Z (south)'} value={camera.position.z} step={0.5} onChange={(z) => changePosition({ z })} />
        <NumberField label={mounted ? 'Above the car' : 'Above ground'} value={camera.position.y} min={0} step={0.5} onChange={(y) => changePosition({ y })} />
        <NumberField label="Heading" value={camera.heading} min={0} max={360} slider onChange={(heading) => change({ heading })} />
        <NumberField label="Pitch" value={camera.pitch} min={-90} max={90} slider onChange={(pitch) => change({ pitch })} />
        <NumberField label="Roll" value={camera.roll} min={-180} max={180} slider onChange={(roll) => change({ roll })} />
        <SelectField
          label="Picture turned"
          value={String(camera.video.rotate) as (typeof rotateOptions)[number]['value']}
          options={rotateOptions}
          onChange={(rotate) => change({ video: { ...camera.video, rotate: Number(rotate) } })}
        />
        {camera.kind === 'fixed' && (
          <NumberField label="Field of view" value={camera.hFov} min={1} max={170} slider onChange={(hFov) => change({ hFov })} />
        )}
      </Section>

      {camera.kind === 'ptz' && camera.ptz && <PtzLimits ptz={camera.ptz} onChange={(ptz) => change({ ptz })} />}

      {/* Before the streams: a relay and the input it drives are what an installer wires, and the
          section is of no use buried under two panels of encoder settings. */}
      <DeviceIoSection camera={camera} />

      <StreamsSection camera={camera} h264Available={status?.h264 !== false} onChange={(streams) => change({ streams })} />

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

      <Section title="Recording">
        <NumberField label="Kept, hours" value={camera.recordingRetentionHours} min={1} max={8760}
          onChange={(recordingRetentionHours) => change({ recordingRetentionHours })} />
      </Section>

      <Section title="Access">
        <BlurTextField
          label="Bind address (empty: any)"
          value={camera.bindAddress ?? ''}
          onChange={(value) => change({ bindAddress: value.trim() === '' ? null : value.trim() })}
          placeholder="e.g. 192.168.1.20"
        />
        <NumberField label="ONVIF port (0: auto)" value={camera.onvifPort} min={0} max={65535} onChange={(onvifPort) => change({ onvifPort })} />
        <NumberField label="RTSP port (0: default)" value={camera.rtspPort} min={0} max={65535} onChange={(rtspPort) => change({ rtspPort })} />
        <Users users={camera.users} onChange={(users) => change({ users })} />
      </Section>

      <button type="button" className="danger" onClick={removeSelected}>
        Delete camera
      </button>
    </>
  );
}

function Addresses({ camera, status }: { camera: CameraModel; status: ServerStatus }) {
  const host = camera.bindAddress?.trim() || window.location.hostname;
  const shownHost = host.includes(':') ? `[${host}]` : host;
  const port = status.onvifPorts[camera.id];
  const rtspPort = status.rtspPorts?.[camera.id] ?? status.rtspPort;
  const user = camera.users[0];
  const credentials = user ? `${encodeURIComponent(user.username)}:${encodeURIComponent(user.password)}@` : '';
  const streams = effectiveStreams(camera).filter((stream) => stream.codec !== 'h264' || status.h264);
  return (
    <dl className="addresses">
      <dt>ONVIF</dt>
      <dd>{port ? `http://${shownHost}:${port}/onvif/device_service` : 'not served (port taken?)'}</dd>
      {streams.map((stream) => (
        <Fragment key={stream.token}>
          <dt>RTSP {stream.name || (stream.codec === 'jpeg' ? 'JPEG' : 'H.264')}</dt>
          <dd>{rtspPort ? `rtsp://${credentials}${shownHost}:${rtspPort}/${stream.path}` : 'not served (port taken?)'}</dd>
        </Fragment>
      ))}
      <dt>RTSP replay</dt>
      <dd>{rtspPort ? `rtsp://${credentials}${shownHost}:${rtspPort}/${camera.id}/replay` : 'not served (port taken?)'}</dd>
    </dl>
  );
}

// The camera's streams, each an ONVIF profile. Editing a stream of a legacy camera materializes the
// list; adding one picks the next free Profile_N token and a path under the camera id.
function StreamsSection({
  camera,
  h264Available,
  onChange,
}: {
  camera: CameraModel;
  h264Available: boolean;
  onChange: (streams: StreamModel[]) => void;
}) {
  const streams = effectiveStreams(camera);
  const update = (index: number, patch: Partial<StreamModel>) =>
    onChange(streams.map((stream, i) => (i === index ? { ...stream, ...patch } : stream)));
  const remove = (index: number) => onChange(streams.filter((_, i) => i !== index));
  function add(codec: 'jpeg' | 'h264') {
    const token = nextStreamToken(streams);
    const number = token.slice('Profile_'.length);
    const path = `${camera.id}/stream${number}`;
    const stream: StreamModel = codec === 'jpeg'
      ? { token, name: `JPEG ${number}`, codec, path, video: { ...camera.video, rotate: 0 } }
      : { token, name: `H.264 ${number}`, codec, path, h264: { ...camera.h264 } };
    onChange([...streams, stream]);
  }
  return (
    <Section title="Streams">
      {streams.map((stream, index) => (
        <StreamCard
          key={stream.token}
          camera={camera}
          stream={stream}
          h264Available={h264Available}
          onChange={(patch) => update(index, patch)}
          onRemove={streams.length > 1 ? () => remove(index) : undefined}
        />
      ))}
      {streams.length < 8 && (
        <div className="stream-add">
          <button type="button" onClick={() => add('jpeg')}>
            Add JPEG stream
          </button>
          <button type="button" onClick={() => add('h264')}>
            Add H.264 stream
          </button>
        </div>
      )}
    </Section>
  );
}

function StreamCard({
  camera,
  stream,
  h264Available,
  onChange,
  onRemove,
}: {
  camera: CameraModel;
  stream: StreamModel;
  h264Available: boolean;
  onChange: (patch: Partial<StreamModel>) => void;
  onRemove?: () => void;
}) {
  const video = streamVideo(camera, stream);
  const h264 = streamH264(camera, stream);
  return (
    <div className="stream-card">
      <div className="stream-head">
        <strong>{stream.name || stream.token}</strong>
        <span className="muted">
          {stream.token} · {stream.codec === 'jpeg' ? 'JPEG' : 'H.264'}
        </span>
        {onRemove && (
          <button type="button" onClick={onRemove}>
            Remove
          </button>
        )}
      </div>
      <BlurTextField label="Stream name" value={stream.name} onChange={(name) => onChange({ name })} />
      <BlurTextField label="RTSP path" value={stream.path} onChange={(path) => onChange({ path })} />
      {stream.codec === 'jpeg' ? (
        <>
          <ResolutionField
            width={video.width}
            height={video.height}
            onChange={(width, height) => onChange({ video: { ...video, width, height } })}
          />
          <NumberField label="Frame rate" value={video.fps} min={1} max={30} onChange={(fps) => onChange({ video: { ...video, fps } })} />
          <NumberField label="Quality" value={video.quality} min={1} max={100} slider onChange={(quality) => onChange({ video: { ...video, quality } })} />
        </>
      ) : (
        <>
          {!h264Available && <p className="muted">ffmpeg is unavailable; ONVIF and RTSP hide this stream until it is.</p>}
          <ResolutionField
            width={h264.width}
            height={h264.height}
            onChange={(width, height) => onChange({ h264: { ...h264, width, height } })}
          />
          <NumberField label="Frame rate" value={h264.fps} min={1} max={30} onChange={(fps) => onChange({ h264: { ...h264, fps } })} />
          <NumberField label="Bitrate, kbit/s" value={h264.bitrateKbps} min={64} max={16384} step={64} onChange={(bitrateKbps) => onChange({ h264: { ...h264, bitrateKbps } })} />
          <NumberField label="Key frame every" value={h264.govLength} min={1} max={300} onChange={(govLength) => onChange({ h264: { ...h264, govLength } })} />
        </>
      )}
    </div>
  );
}

function nextStreamToken(streams: StreamModel[]): string {
  let highest = 0;
  for (const stream of streams) {
    const number = /^Profile_(\d+)$/.exec(stream.token);
    if (number) highest = Math.max(highest, Number(number[1]));
  }
  return `Profile_${highest + 1}`;
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
      <CheckboxField label="ONVIF pan reversed" checked={ptz.panReverse} onChange={(panReverse) => change({ panReverse })} />
      <CheckboxField label="ONVIF tilt reversed" checked={ptz.tiltReverse} onChange={(tiltReverse) => change({ tiltReverse })} />
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
