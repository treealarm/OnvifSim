using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnvifSim.Ptz;
using OnvifSim.Recording;
using OnvifSim.World;

namespace OnvifSim.Tests;

// What follows a world switch: the footage the user chose to keep or clear, the PTZ heads of the world
// being left, and the cameras' network state.
public class WorldSwitchTests
{
    [Fact]
    public void KeepingFootage_CarriesTheOpenSpan_ClearingStartsItAgain()
    {
        using var dir = new TempDir();
        var start = DateTimeOffset.UnixEpoch.AddDays(20000);
        var time = new FakeTimeProvider(start);
        var store = new WorldStore(dir.Path);
        using var recordings = new RecordingLog(new SimOptions { DataDirectory = dir.Path }, store, time, NullLogger<RecordingLog>.Instance);

        var initial = recordings.SpansOf(store.Current.Cameras.Single(c => c.Id == "cam1"));
        Assert.Single(initial);
        Assert.Equal(start, initial[0].From);

        // A copy of the village: the same camera ids, so keeping carries their cards over.
        File.Copy(Path.Combine(dir.Path, "worlds", "default.json"), Path.Combine(dir.Path, "worlds", "Copy.json"));
        time.Advance(TimeSpan.FromHours(1));
        store.Activate("Copy", clearRecordings: false);
        var kept = recordings.SpansOf(store.Current.Cameras.Single(c => c.Id == "cam1"));
        Assert.Contains(kept, span => span.From == start);

        // Clearing starts every camera's card again at the moment of the switch.
        var clearedAt = time.GetUtcNow();
        store.Activate("default", clearRecordings: true);
        var cleared = recordings.SpansOf(store.Current.Cameras.Single(c => c.Id == "cam1"));
        Assert.All(cleared, span => Assert.True(span.From >= clearedAt));
    }

    [Fact]
    public void ASwitchStopsAMovingHeadFromWritingIntoTheNewWorld()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20000));
        var store = new WorldStore(dir.Path);
        using var ptz = new PtzSimulator(store, time, NullLogger<PtzSimulator>.Instance);
        var camera = store.Current.Cameras.Single(c => c.Id == "cam2");
        ptz.MoveTo(camera, new PtzAxes(90, null, null), default);

        File.Copy(Path.Combine(dir.Path, "worlds", "default.json"), Path.Combine(dir.Path, "worlds", "Copy.json"));
        store.Activate("Copy", clearRecordings: true);
        bool written = false;
        store.Changed += _ => written = true;

        time.Advance(TimeSpan.FromSeconds(10));

        Assert.False(written, "the head of the old world saved its position into the new one");
    }

    [Fact]
    public void ResettingTheNetwork_BringsEveryCameraBackOnline()
    {
        var network = new CameraNetwork();
        network.SetOnline("cam1", online: false);
        Assert.False(network.IsOnline("cam1"));

        network.Reset();

        Assert.True(network.IsOnline("cam1"));
    }
}
