using OnvifSim.World;

namespace OnvifSim.Ptz;

// Axes of a PTZ command; null leaves that axis alone. What the numbers mean depends on the command:
// degrees and zoom units for targets and steps, fractions of the top speed for speeds and velocities.
public readonly record struct PtzAxes(float? Pan, float? Tilt, float? Zoom);

public readonly record struct PtzState(PtzPosition Position, bool PanTiltMoving, bool ZoomMoving);

// Drives the PTZ heads. Every axis moves at a constant speed, so where a head is at any moment follows
// from the clock instead of a ticking loop: the stream reads the position once per frame, GetStatus when
// asked, and an idle camera costs nothing. When a move ends the position is written to world.json, so a
// restart and the editor see where the camera stopped.
public sealed class PtzSimulator : IDisposable
{
    // The least a save waits for the end of a move, so a rest too short for a timer cannot spin.
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(1);

    private readonly WorldStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    // Heads that have been commanded; any other camera is where world.json says. A head is kept after
    // its move ends, so a request that still holds an older world sees the live position, not the one
    // stored before the move.
    private readonly Dictionary<string, Head> _heads = new(StringComparer.OrdinalIgnoreCase);

    public PtzSimulator(WorldStore store, TimeProvider time, ILogger<PtzSimulator> logger)
    {
        _store = store;
        _time = time;
        _logger = logger;
        store.Changed += OnWorldChanged;
    }

    // Where the camera's head points now; a fixed camera has no head and gets the zero position.
    public PtzPosition PositionOf(CameraModel camera) =>
        camera.Kind == CameraKind.Ptz && camera.Ptz != null ? StateOf(camera).Position : default;

    public PtzState StateOf(CameraModel camera)
    {
        var limits = LimitsOf(camera);
        lock (_gate)
        {
            if (!_heads.TryGetValue(camera.Id, out var head)) return new PtzState(limits.Position, false, false);
            long now = _time.GetTimestamp();
            return new PtzState(head.PositionAt(now, limits, _time.TimestampFrequency), head.PanTiltMovingAt(now), head.ZoomMovingAt(now));
        }
    }

    // Moves to a position (degrees, zoom 0..1). An endless pan takes the shorter way round.
    public void MoveTo(CameraModel camera, PtzAxes target, PtzAxes speed)
    {
        Command(camera, (head, limits, current, now) =>
        {
            if (target.Pan is { } pan)
            {
                float step = limits.EndlessPan ? ShortestTurn(current.Pan, pan) : Math.Clamp(pan, limits.PanMin, limits.PanMax) - current.Pan;
                head.Pan = Travel(current.Pan, step, limits.PanSpeed * SpeedFraction(speed.Pan), now);
            }
            if (target.Tilt is { } tilt)
                head.Tilt = Travel(current.Tilt, Math.Clamp(tilt, limits.TiltMin, limits.TiltMax) - current.Tilt, limits.TiltSpeed * SpeedFraction(speed.Tilt), now);
            if (target.Zoom is { } zoom)
                head.Zoom = Travel(current.Zoom, Math.Clamp(zoom, 0, 1) - current.Zoom, limits.ZoomSpeed * SpeedFraction(speed.Zoom), now);
        });
    }

    // Moves by a step (degrees, zoom units). An endless pan keeps turning the way the step points.
    public void MoveBy(CameraModel camera, PtzAxes step, PtzAxes speed)
    {
        Command(camera, (head, limits, current, now) =>
        {
            if (step.Pan is { } pan)
            {
                float panStep = limits.EndlessPan ? pan : Math.Clamp(current.Pan + pan, limits.PanMin, limits.PanMax) - current.Pan;
                head.Pan = Travel(current.Pan, panStep, limits.PanSpeed * SpeedFraction(speed.Pan), now);
            }
            if (step.Tilt is { } tilt)
                head.Tilt = Travel(current.Tilt, Math.Clamp(current.Tilt + tilt, limits.TiltMin, limits.TiltMax) - current.Tilt, limits.TiltSpeed * SpeedFraction(speed.Tilt), now);
            if (step.Zoom is { } zoom)
                head.Zoom = Travel(current.Zoom, Math.Clamp(current.Zoom + zoom, 0, 1) - current.Zoom, limits.ZoomSpeed * SpeedFraction(speed.Zoom), now);
        });
    }

    // Moves at a velocity (fractions of the top speed, -1..1) until a limit or the timeout; zero stops
    // that axis.
    public void MoveContinuously(CameraModel camera, PtzAxes velocity, TimeSpan timeout)
    {
        long deadline = (long)(timeout.TotalSeconds * _time.TimestampFrequency);
        Command(camera, (head, limits, current, now) =>
        {
            if (velocity.Pan is { } pan)
            {
                float speed = limits.PanSpeed * Math.Clamp(pan, -1, 1);
                head.Pan = limits.EndlessPan
                    ? new AxisMotion(current.Pan, speed, now, speed == 0 ? now : now + deadline)
                    : Run(current.Pan, speed, limits.PanMin, limits.PanMax, now, deadline);
            }
            if (velocity.Tilt is { } tilt)
                head.Tilt = Run(current.Tilt, limits.TiltSpeed * Math.Clamp(tilt, -1, 1), limits.TiltMin, limits.TiltMax, now, deadline);
            if (velocity.Zoom is { } zoom)
                head.Zoom = Run(current.Zoom, limits.ZoomSpeed * Math.Clamp(zoom, -1, 1), 0, 1, now, deadline);
        });
    }

    public void Stop(CameraModel camera, bool panTilt, bool zoom)
    {
        Command(camera, (head, limits, current, now) =>
        {
            if (panTilt)
            {
                head.Pan = AxisMotion.Holding(current.Pan, now);
                head.Tilt = AxisMotion.Holding(current.Tilt, now);
            }
            if (zoom) head.Zoom = AxisMotion.Holding(current.Zoom, now);
        });
    }

    public void Dispose()
    {
        _store.Changed -= OnWorldChanged;
        lock (_gate)
        {
            foreach (var head in _heads.Values) head.Timer?.Dispose();
            _heads.Clear();
        }
    }

    // Applies a command to the camera's head, given its limits and where it is now, and arranges for the
    // position to be saved once the head stops. The world is written outside the lock: the store calls
    // OnWorldChanged under its own lock.
    private void Command(CameraModel camera, Action<Head, PtzModel, PtzPosition, long> change)
    {
        var limits = LimitsOf(camera);
        long now = _time.GetTimestamp();
        long generation;
        bool stopped;
        lock (_gate)
        {
            if (!_heads.TryGetValue(camera.Id, out var head))
            {
                head = new Head(limits.Position, now);
                _heads.Add(camera.Id, head);
            }
            change(head, limits, head.PositionAt(now, limits, _time.TimestampFrequency), now);
            generation = ++head.Generation;
            head.Timer?.Dispose();
            long end = head.EndOfMove;
            stopped = end <= now;
            head.Timer = stopped
                ? null
                : _time.CreateTimer(_ => SaveIfStopped(camera.Id, limits, generation), null, ElapsedTime(now, end), Timeout.InfiniteTimeSpan);
        }
        if (stopped) SaveIfStopped(camera.Id, limits, generation);
    }

    private void SaveIfStopped(string cameraId, PtzModel limits, long generation)
    {
        PtzPosition position;
        lock (_gate)
        {
            // A newer command superseded the move this was scheduled for; it saves when it ends.
            if (!_heads.TryGetValue(cameraId, out var head) || head.Generation != generation) return;
            long now = _time.GetTimestamp();
            // Timers keep a coarser clock than the timestamps moves are timed by and can fire a few
            // milliseconds early: wait out the rest of the move rather than save a head short of its target.
            if (head.EndOfMove > now)
            {
                head.Timer?.Dispose();
                var rest = TimeSpan.FromTicks(Math.Max(ElapsedTime(now, head.EndOfMove).Ticks, MinimumWait.Ticks));
                head.Timer = _time.CreateTimer(_ => SaveIfStopped(cameraId, limits, generation), null, rest, Timeout.InfiniteTimeSpan);
                return;
            }
            position = head.PositionAt(now, limits, _time.TimestampFrequency);
        }
        try
        {
            _store.Update(world =>
            {
                var ptz = world.Cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase))?.Ptz;
                if (ptz != null) ptz.Position = position;
            });
        }
        catch (IOException ex)
        {
            // Runs on a timer, where an exception would end the process; the camera keeps its position
            // in memory and the next move tries again.
            _logger.LogError(ex, "Camera {Camera}: could not save the PTZ position", cameraId);
        }
    }

    // A head at rest follows a world whose stored position differs from where the head is: the editor
    // (or anyone else) moved the camera. The simulator's own saves store exactly the head's position and
    // change nothing. Heads still moving keep going and save when they stop.
    private void OnWorldChanged(WorldModel world)
    {
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            foreach (var (id, head) in _heads.ToList())
            {
                var ptz = world.Cameras.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) && c.Kind == CameraKind.Ptz)?.Ptz;
                if (ptz == null)
                {
                    head.Timer?.Dispose();
                    _heads.Remove(id);
                }
                else if (head.EndOfMove <= now && head.PositionAt(now, ptz, _time.TimestampFrequency) != ptz.Position)
                {
                    head.Timer?.Dispose();
                    _heads[id] = new Head(ptz.Position, now);
                }
            }
        }
    }

    private static PtzModel LimitsOf(CameraModel camera) =>
        camera.Kind == CameraKind.Ptz && camera.Ptz != null
            ? camera.Ptz
            : throw new InvalidOperationException($"Camera {camera.Id} has no PTZ head.");

    // A missing or non-positive speed means full speed, as the configuration's default speed is 1.
    private static float SpeedFraction(float? fraction) => fraction is > 0 ? Math.Min(fraction.Value, 1) : 1;

    // Signed shortest turn from one heading to another, -180..180 degrees.
    private static float ShortestTurn(float from, float to)
    {
        float turn = (to - from) % 360;
        if (turn > 180) turn -= 360;
        if (turn < -180) turn += 360;
        return turn;
    }

    private AxisMotion Travel(float from, float step, float speed, long now)
    {
        if (step == 0 || speed <= 0) return AxisMotion.Holding(from, now);
        long duration = (long)(Math.Abs(step) / speed * _time.TimestampFrequency);
        return new AxisMotion(from, MathF.CopySign(speed, step), now, now + duration);
    }

    // Constant velocity until the limit ahead or the deadline, whichever comes first.
    private AxisMotion Run(float from, float velocity, float min, float max, long now, long deadline)
    {
        if (velocity == 0) return AxisMotion.Holding(from, now);
        float room = velocity > 0 ? max - from : from - min;
        long toLimit = (long)(room / Math.Abs(velocity) * _time.TimestampFrequency);
        return new AxisMotion(from, velocity, now, now + Math.Min(toLimit, deadline));
    }

    private TimeSpan ElapsedTime(long from, long to) => TimeSpan.FromSeconds((double)(to - from) / _time.TimestampFrequency);

    // One axis: at From when Start comes, moving at Velocity units per second until End, then holding.
    private readonly record struct AxisMotion(float From, float Velocity, long Start, long End)
    {
        public static AxisMotion Holding(float value, long now) => new(value, 0, now, now);

        public float ValueAt(long now, long frequency) =>
            From + Velocity * (float)((double)(Math.Clamp(now, Start, End) - Start) / frequency);

        public bool IsMovingAt(long now) => now < End;
    }

    private sealed class Head(PtzPosition position, long now)
    {
        public AxisMotion Pan = AxisMotion.Holding(position.Pan, now);
        public AxisMotion Tilt = AxisMotion.Holding(position.Tilt, now);
        public AxisMotion Zoom = AxisMotion.Holding(position.Zoom, now);
        // Bumped by every command, so a save scheduled for an earlier move can tell it is stale.
        public long Generation;
        public ITimer? Timer;

        public long EndOfMove => Math.Max(Pan.End, Math.Max(Tilt.End, Zoom.End));

        public bool PanTiltMovingAt(long now) => Pan.IsMovingAt(now) || Tilt.IsMovingAt(now);

        public bool ZoomMovingAt(long now) => Zoom.IsMovingAt(now);

        public PtzPosition PositionAt(long now, PtzModel limits, long frequency)
        {
            float pan = Pan.ValueAt(now, frequency);
            pan = limits.EndlessPan
                ? limits.PanMin + Modulo(pan - limits.PanMin, 360)
                : Math.Clamp(pan, limits.PanMin, limits.PanMax);
            return new PtzPosition(
                pan,
                Math.Clamp(Tilt.ValueAt(now, frequency), limits.TiltMin, limits.TiltMax),
                Math.Clamp(Zoom.ValueAt(now, frequency), 0, 1));
        }

        private static float Modulo(float value, float divisor) => ((value % divisor) + divisor) % divisor;
    }
}
