using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnvifSim.Ptz;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The default world's cam2 is a PTZ camera with an endless pan (-180..180), tilt -90..20 and top speeds
// of 90 deg/s pan, 60 deg/s tilt and half the zoom range per second; it starts at pan 0, tilt -10.
public sealed class PtzSimulatorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly WorldStore _store;
    private readonly FakeTimeProvider _time = new();
    private readonly PtzSimulator _ptz;

    public PtzSimulatorTests()
    {
        _store = new WorldStore(_dir.Path);
        _ptz = new PtzSimulator(_store, _time, NullLogger<PtzSimulator>.Instance);
    }

    public void Dispose()
    {
        _ptz.Dispose();
        _dir.Dispose();
    }

    private CameraModel Cam2 => _store.Current.Cameras.Single(c => c.Id == "cam2");

    private void Wait(double seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void MoveTo_TravelsAtTopSpeed_ThenStopsAndSavesThePosition()
    {
        _ptz.MoveTo(Cam2, new PtzAxes(45, null, 1), new PtzAxes(null, null, null));

        Wait(0.25);
        var halfway = _ptz.StateOf(Cam2);
        Assert.Equal(22.5f, halfway.Position.Pan, 0.01f);
        Assert.True(halfway.PanTiltMoving);
        Assert.True(halfway.ZoomMoving);
        Assert.Equal(0, Cam2.Ptz!.Pan);

        // Pan arrives after 0.5 s; zoom 0 -> 1 at 0.5 per second takes 2 s.
        Wait(0.25);
        Assert.False(_ptz.StateOf(Cam2).PanTiltMoving);
        Assert.True(_ptz.StateOf(Cam2).ZoomMoving);
        Wait(1.5);
        var stopped = _ptz.StateOf(Cam2);
        Assert.False(stopped.ZoomMoving);
        Assert.Equal(new PtzPosition(45, -10, 1), Cam2.Ptz!.Position);
        Assert.Equal(-10, stopped.Position.Tilt);
    }

    [Fact]
    public void StateAt_ReplaysWhereTheHeadPointedThen()
    {
        var start = _time.GetUtcNow();
        _ptz.MoveTo(Cam2, new PtzAxes(45, null, null), default);
        Wait(1);
        _ptz.MoveTo(Cam2, new PtzAxes(0, null, null), default);
        Wait(1);

        Assert.Equal(0, _ptz.StateAt(Cam2, start - TimeSpan.FromSeconds(1)).Position.Pan, 0.01f);
        var halfway = _ptz.StateAt(Cam2, start + TimeSpan.FromSeconds(0.25));
        Assert.Equal(22.5f, halfway.Position.Pan, 0.01f);
        Assert.True(halfway.PanTiltMoving);
        Assert.Equal(45, _ptz.StateAt(Cam2, start + TimeSpan.FromSeconds(0.75)).Position.Pan, 0.01f);
        Assert.Equal(22.5f, _ptz.StateAt(Cam2, start + TimeSpan.FromSeconds(1.25)).Position.Pan, 0.01f);
        Assert.Equal(0, _ptz.StateOf(Cam2).Position.Pan, 0.01f);
    }

    [Fact]
    public void ATimerFiringEarly_DoesNotSaveTheHeadShortOfItsTarget()
    {
        using var dir = new TempDir();
        var store = new WorldStore(dir.Path);
        var time = new EarlyTimers(TimeSpan.FromMilliseconds(50));
        using var ptz = new PtzSimulator(store, time, NullLogger<PtzSimulator>.Instance);
        CameraModel Cam2() => store.Current.Cameras.Single(c => c.Id == "cam2");

        ptz.MoveTo(Cam2(), new PtzAxes(20, null, null), new PtzAxes(null, null, null));
        // The timer goes off 50 ms before the 0.22 s move ends.
        time.Advance(TimeSpan.FromSeconds(0.21));
        Assert.Equal(0, Cam2().Ptz!.Pan);
        time.Advance(TimeSpan.FromSeconds(0.02));
        Assert.Equal(20, Cam2().Ptz!.Pan, 0.001f);
    }

    // A fake clock whose longer timers go off early, as real ones may by the timestamp clock; one due
    // within the margin goes off on time.
    private sealed class EarlyTimers(TimeSpan early) : FakeTimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, dueTime > early ? dueTime - early : dueTime, period);
    }

    [Fact]
    public void SpeedFraction_SlowsTheAxisDown()
    {
        _ptz.MoveTo(Cam2, new PtzAxes(45, null, null), new PtzAxes(0.5f, null, null));
        Wait(0.5);
        Assert.Equal(22.5f, _ptz.PositionOf(Cam2).Pan, 0.01f);
    }

    [Fact]
    public void EndlessPan_TakesTheShorterWayRound()
    {
        _store.Update(w => w.Cameras.Single(c => c.Id == "cam2").Ptz!.Pan = 170);
        _ptz.MoveTo(Cam2, new PtzAxes(-170, null, null), new PtzAxes(null, null, null));

        // 20 degrees through 180 take 2/9 s; halfway the head points due 180, reported as -180.
        Wait(1.0 / 9);
        Assert.Equal(-180, _ptz.PositionOf(Cam2).Pan, 0.01f);
        Wait(1.0 / 9);
        Assert.Equal(-170, _ptz.PositionOf(Cam2).Pan, 0.01f);
        Assert.False(_ptz.StateOf(Cam2).PanTiltMoving);
    }

    [Fact]
    public void MoveBy_ClampsALimitedAxis()
    {
        _ptz.MoveBy(Cam2, new PtzAxes(null, 100, null), new PtzAxes(null, null, null));
        Wait(1);
        Assert.Equal(20, _ptz.PositionOf(Cam2).Tilt, 0.01f);
    }

    [Fact]
    public void MoveContinuously_StopsAtTheLimitOrTheTimeout_WhicheverComesFirst()
    {
        // Tilt reaches -90 after 80/60 s, well before the 5 s timeout; pan has no limit and runs 1 s.
        _ptz.MoveContinuously(Cam2, new PtzAxes(null, -1, null), TimeSpan.FromSeconds(5));
        Wait(1.4);
        Assert.Equal(-90, _ptz.PositionOf(Cam2).Tilt, 0.01f);
        Assert.False(_ptz.StateOf(Cam2).PanTiltMoving);

        _ptz.MoveContinuously(Cam2, new PtzAxes(0.5f, null, null), TimeSpan.FromSeconds(1));
        Wait(0.5);
        Assert.True(_ptz.StateOf(Cam2).PanTiltMoving);
        Wait(2);
        Assert.Equal(45, _ptz.PositionOf(Cam2).Pan, 0.01f);
        Assert.Equal(new PtzPosition(45, -90, 0), Cam2.Ptz!.Position);
    }

    [Fact]
    public void Stop_FreezesTheHeadWhereItIs_AndSavesAtOnce()
    {
        _ptz.MoveTo(Cam2, new PtzAxes(90, null, null), new PtzAxes(null, null, null));
        Wait(0.5);
        _ptz.Stop(Cam2, panTilt: true, zoom: true);
        Wait(1);
        Assert.Equal(45, _ptz.PositionOf(Cam2).Pan, 0.01f);
        Assert.Equal(45, Cam2.Ptz!.Pan, 0.01f);
    }

    [Fact]
    public void ANewCommand_ReplacesTheMoveInProgress()
    {
        _ptz.MoveTo(Cam2, new PtzAxes(90, null, null), new PtzAxes(null, null, null));
        Wait(0.5);
        _ptz.MoveTo(Cam2, new PtzAxes(30, null, null), new PtzAxes(null, null, null));
        // The first move would have ended (and saved 90) here; the second one ended at 30 before that.
        Wait(0.5);
        Assert.Equal(30, _ptz.PositionOf(Cam2).Pan, 0.01f);
        Assert.Equal(30, Cam2.Ptz!.Pan, 0.01f);
    }

    [Fact]
    public void AnEditedWorld_MovesAHeadAtRest()
    {
        _ptz.MoveTo(Cam2, new PtzAxes(45, null, null), new PtzAxes(null, null, null));
        Wait(1);
        _store.Update(w => w.Cameras.Single(c => c.Id == "cam2").Ptz!.Pan = -60);
        Assert.Equal(-60, _ptz.PositionOf(Cam2).Pan);
    }

    [Fact]
    public void ARequestHoldingTheWorldFromBeforeAMove_SeesWhereTheHeadIsNow()
    {
        var before = Cam2;
        _ptz.MoveTo(Cam2, new PtzAxes(45, null, null), new PtzAxes(null, null, null));
        Wait(1);
        Assert.Equal(45, Cam2.Ptz!.Pan, 0.01f);
        Assert.Equal(45, _ptz.PositionOf(before).Pan, 0.01f);
    }

    [Fact]
    public void AFixedCamera_HasNoHead()
    {
        var cam1 = _store.Current.Cameras.Single(c => c.Id == "cam1");
        Assert.Equal(default, _ptz.PositionOf(cam1));
        Assert.Throws<InvalidOperationException>(() => _ptz.MoveTo(cam1, new PtzAxes(10, null, null), default));
    }
}
