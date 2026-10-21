using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OnvifLib;
using OnvifSim.Onvif;
using OnvifSim.Rtsp;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The simulator on a clock the test moves: the cameras start recording when it starts, at Start (a
// whole second), and have as much footage as the test lets time pass. cam3 and cam4 are copies of cam1,
// so that tests changing a camera's recording each have one of their own.
public class RecordingSimFactory : SimFactory
{
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    public DateTimeOffset Start { get; }

    public RecordingSimFactory()
    {
        Start = Time.GetUtcNow();
        var store = new WorldStore(Data.Path);
        var world = store.Current;
        var cam1 = world.Cameras.Single(c => c.Id == "cam1");
        foreach (var id in new[] { "cam3", "cam4" })
        {
            var copy = JsonSerializer.Deserialize<CameraModel>(JsonSerializer.Serialize(cam1, WorldStore.Json), WorldStore.Json)!;
            copy.Id = id;
            copy.Name = id;
            world.Cameras.Add(copy);
        }
        store.Replace(world);
        // The host starts on first use, and the cameras start recording with it: now, at Start, not
        // whenever a test first asks for a service.
        _ = Services;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }
}

// The same with the ffmpeg found on PATH: replays in H.264.
public sealed class H264RecordingSimFactory : RecordingSimFactory
{
    protected override string Ffmpeg => "";
}

// Profile G: the cameras' own recordings found through Search and played back through Replay, the
// way a VMS recovers what it missed while a camera was cut off. Each test works on a camera of its
// own, as they share the simulator and its clock.
public class RecordingTests(RecordingSimFactory factory) : IClassFixture<RecordingSimFactory>
{
    private const int Fps = 10; // the default world's JPEG frame rate
    private const uint RtpTicksPerFrame = 90000 / Fps;

    private RtspServer Server => factory.Services.GetRequiredService<RtspServer>();

    private int OnvifPortOf(string cameraId) => factory.Services.GetRequiredService<OnvifHost>().PortOf(cameraId)!.Value;

    private Camera Connect(string cameraId) => Camera.Create("127.0.0.1", OnvifPortOf(cameraId), "admin", "admin", timeout: 10);

    private void Wait(TimeSpan time) => factory.Time.Advance(time);

    private static string Clock(DateTimeOffset time) => ReplayClient.Clock(time);

    [Fact]
    public async Task Services_OfferRecordingSearchAndReplay_OnTheCamerasOwnPort()
    {
        var services = await Connect("cam1").GetServicesAsync();
        Assert.NotNull(services);
        int port = OnvifPortOf("cam1");
        Assert.Equal($"http://127.0.0.1:{port}/onvif/recording_service", services["http://www.onvif.org/ver10/recording/wsdl"]);
        Assert.Equal($"http://127.0.0.1:{port}/onvif/search_service", services["http://www.onvif.org/ver10/search/wsdl"]);
        Assert.Equal($"http://127.0.0.1:{port}/onvif/replay_service", services["http://www.onvif.org/ver10/replay/wsdl"]);
    }

    [Fact]
    public async Task FindRecordings_FindsWhatTheCameraRecordedSinceItStarted_AndNothingBefore()
    {
        Wait(TimeSpan.FromMinutes(1));
        var search = (await Connect("cam1").GetSearchService())!;
        var now = factory.Time.GetUtcNow().UtcDateTime;

        var found = Assert.Single(await search.FindRecordingsAsync(now.AddSeconds(-30), now, null, 200));
        Assert.Equal("Recording_1", found.RecordingToken);
        var track = Assert.Single(found.Tracks);
        Assert.Equal("video", track.TrackType);
        Assert.Equal(factory.Start.UtcDateTime, track.DataFrom!.Value, TimeSpan.FromSeconds(1));
        Assert.True(track.DataTo >= now.AddSeconds(-1));

        var before = factory.Start.UtcDateTime;
        Assert.Empty(await search.FindRecordingsAsync(before.AddHours(-2), before.AddHours(-1), null, 200));
    }

    [Fact]
    public async Task ReplayUri_IsTheCamerasRecordingOnTheRtspServer()
    {
        var replay = (await Connect("cam1").GetReplayService())!;
        Assert.Equal($"rtsp://127.0.0.1:{Server.Port}/cam1/replay", await replay.GetReplayUriAsync("Recording_1"));
    }

    [Fact]
    public async Task Replay_StartsWithAFrameAtExactlyTheRequestedTime_AndEndsWithTheRange()
    {
        Wait(TimeSpan.FromSeconds(30));
        var from = factory.Start + TimeSpan.FromSeconds(10);
        var to = from + TimeSpan.FromSeconds(2);

        var (play, packets) = await ReplayAsync("cam1", from, to);
        Assert.Equal(200, play.Status);
        Assert.Equal($"clock={Clock(from)}-{Clock(to)}", play.Header("Range"));

        var frames = Frames(packets);
        Assert.Equal(2 * Fps, frames.Count);
        Assert.Equal(from, frames[0].Time);
        // One frame every 1/fps, by the extension and by the RTP clock alike, every one a JPEG clean point.
        Assert.Equal(Enumerable.Range(0, frames.Count).Select(i => from + TimeSpan.FromSeconds((double)i / Fps)), frames.Select(f => f.Time));
        Assert.All(frames.Zip(frames.Skip(1)), pair => Assert.Equal(RtpTicksPerFrame, pair.Second.RtpTime - pair.First.RtpTime));
        Assert.All(frames, f => Assert.True(f.CleanPoint));
        Assert.True(frames[^1].Terminal);
        Assert.False(frames[^2].Terminal);
        Assert.DoesNotContain(frames, f => f.Discontinuity);
    }

    [Fact]
    public async Task Replay_AcrossAHoleInTheRecording_JumpsTheClockAndSaysSo()
    {
        // cam2 records 5 s, is idle for 5 s (SetRecordingJobMode), then records again.
        var recording = (await Connect("cam2").GetRecordingService())!;
        var start = factory.Time.GetUtcNow();
        Wait(TimeSpan.FromSeconds(5));
        await recording.SetRecordingJobModeAsync("RecordingJob_1", "Idle");
        Wait(TimeSpan.FromSeconds(5));
        await recording.SetRecordingJobModeAsync("RecordingJob_1", "Active");
        Wait(TimeSpan.FromSeconds(5));

        var (_, packets) = await ReplayAsync("cam2", start + TimeSpan.FromSeconds(4), start + TimeSpan.FromSeconds(11));
        var frames = Frames(packets);
        int gap = frames.FindIndex(f => f.Discontinuity);
        Assert.Equal(Fps, gap); // 4 s .. 5 s before it
        Assert.Equal(Fps, frames.Count - gap); // 10 s .. 11 s after it
        Assert.True(frames[gap - 1].EndOfSection);
        Assert.Equal(start + TimeSpan.FromSeconds(10), frames[gap].Time);
        Assert.Equal((uint)(6 * 90000), frames[gap].RtpTime - frames[0].RtpTime);
    }

    [Fact]
    public async Task RecordingJob_Idle_ShowsAsStopped_AndActiveRecordsAgain()
    {
        var camera = Connect("cam3");
        var recording = (await camera.GetRecordingService())!;
        var search = (await camera.GetSearchService())!;

        await recording.SetRecordingJobModeAsync("RecordingJob_1", "Idle");
        Assert.Equal("Idle", Assert.Single(await recording.GetRecordingJobsAsync()).Mode);
        Assert.Equal("Stopped", (await search.GetRecordingInformationAsync("Recording_1"))!.RecordingStatus);

        await recording.SetRecordingJobModeAsync("RecordingJob_1", "Active");
        Assert.Equal("Recording", (await search.GetRecordingInformationAsync("Recording_1"))!.RecordingStatus);
    }

    [Fact]
    public async Task Retention_IsHowFarBackTheCardReaches()
    {
        var recording = (await Connect("cam4").GetRecordingService())!;
        TimeSpan Retention(string? duration) => XmlConvert.ToTimeSpan(duration!);
        Assert.Equal(TimeSpan.FromDays(30), Retention((await recording.GetRecordingConfigurationAsync("Recording_1"))!.MaximumRetentionTime));

        // Whole hours: ten minutes round up to one.
        await recording.SetRecordingConfigurationAsync("Recording_1", "Recording", "PT10M");
        Assert.Equal(TimeSpan.FromHours(1), Retention((await recording.GetRecordingConfigurationAsync("Recording_1"))!.MaximumRetentionTime));
        Wait(TimeSpan.FromHours(2));

        var information = (await (await Connect("cam4").GetSearchService())!.GetRecordingInformationAsync("Recording_1"))!;
        var now = factory.Time.GetUtcNow().UtcDateTime;
        Assert.Equal(now.AddHours(-1), information.EarliestRecording!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ACameraWithoutItsNetwork_AnswersNothing_ButGoesOnRecording()
    {
        using var api = factory.CreateClient();
        Assert.Equal(204, (int)(await api.PutAsJsonAsync("/api/cameras/cam1/network", new { online = false })).StatusCode);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await Assert.ThrowsAsync<HttpRequestException>(() => http.PostAsync(
                $"http://127.0.0.1:{OnvifPortOf("cam1")}/onvif/device_service", new StringContent("<x/>")));
            await using var rtsp = await RtspTestClient.ConnectAsync(Server.Port);
            rtsp.Username = rtsp.Password = "admin";
            Assert.Equal(404, (await rtsp.RequestAsync("DESCRIBE", RtspTestClient.Url(Server.Port, "cam1"))).Status);

            var state = await api.GetFromJsonAsync<RecordingState>("/api/cameras/cam1/recording");
            Assert.False(state!.Online);
            Assert.True(state.Recording);
        }
        finally
        {
            await api.PutAsJsonAsync("/api/cameras/cam1/network", new { online = true });
        }
        Assert.NotNull(await Connect("cam1").GetDeviceInformationAsync());
    }

    [Fact]
    public async Task Require_AnOptionOtherThanOnvifReplay_IsRefused()
    {
        await using var client = await RtspTestClient.ConnectAsync(Server.Port);
        var response = await client.RequestAsync("DESCRIBE", RtspTestClient.Url(Server.Port, "cam1/replay"), ("Require", "onvif-replay, funky"));
        Assert.Equal(551, response.Status);
        Assert.Equal("funky", response.Header("Unsupported"));
    }

    private sealed record RecordingState(bool Recording, bool Online);

    private Task<(RtspTestResponse Play, List<byte[]> Packets)> ReplayAsync(string cameraId, DateTimeOffset from, DateTimeOffset to) =>
        ReplayClient.PlayAsync(Server.Port, cameraId, from, to);

    private static List<ReplayFrame> Frames(List<byte[]> packets) => ReplayClient.Frames(packets);
}

// What a replay client sees, the way ta_vms's edge import asks for it.
internal static class ReplayClient
{
    public static string Clock(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss.fff'Z'", CultureInfo.InvariantCulture);

    // DESCRIBE, SETUP and PLAY over TCP with Require: onvif-replay, Range: clock= and Rate-Control: no;
    // then everything sent until the server closes the connection.
    public static async Task<(RtspTestResponse Play, List<byte[]> Packets)> PlayAsync(int port, string cameraId, DateTimeOffset from, DateTimeOffset to)
    {
        await using var client = await RtspTestClient.ConnectAsync(port);
        client.Username = client.Password = "admin";
        var url = RtspTestClient.Url(port, $"{cameraId}/replay");
        var describe = await client.RequestAsync("DESCRIBE", url, ("Require", "onvif-replay"));
        Assert.Equal(200, describe.Status);
        var setup = await client.RequestAsync("SETUP", url + "/track1",
            ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"), ("Require", "onvif-replay"));
        Assert.Equal(200, setup.Status);
        var play = await client.RequestAsync("PLAY", url, ("Session", setup.SessionId!), ("Require", "onvif-replay"),
            ("Range", $"clock={Clock(from)}-{Clock(to)}"), ("Rate-Control", "no"));
        return (play, await client.ReadUntilClosedAsync());
    }

    public static List<ReplayFrame> Frames(List<byte[]> packets)
    {
        var frames = new List<ReplayFrame>();
        var first = packets[0];
        foreach (var packet in packets)
        {
            Assert.Equal(0x90, packet[0] & 0xF0); // version 2 with the extension bit
            Assert.Equal(0xABAC, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12)));
            Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(14)));
            if (packet == first || frames.Count == 0 || RtpTime(packet) != frames[^1].RtpTime)
            {
                byte flags = packet[24];
                frames.Add(new ReplayFrame(RtpTime(packet), NtpTime(packet), (flags & 0x80) != 0, false, (flags & 0x20) != 0, false));
            }
            if ((packet[1] & 0x80) != 0)
                frames[^1] = frames[^1] with { EndOfSection = (packet[24] & 0x40) != 0, Terminal = (packet[24] & 0x10) != 0 };
        }
        return frames;
    }

    public static uint RtpTime(byte[] packet) => BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));

    private static DateTimeOffset NtpTime(byte[] packet)
    {
        ulong ntp = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(16));
        long ticks = (long)(ntp >> 32) * TimeSpan.TicksPerSecond + (long)((ntp & 0xFFFFFFFF) * (ulong)TimeSpan.TicksPerSecond >> 32);
        // Rounded to the millisecond: the 32-bit fraction does not keep ticks exactly.
        var time = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(ticks);
        return new DateTimeOffset((time.Ticks + TimeSpan.TicksPerMillisecond / 2) / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }
}

// A frame as its first packet's RTP header and ONVIF replay extension describe it; E and T come from
// its last packet.
internal sealed record ReplayFrame(uint RtpTime, DateTimeOffset Time, bool CleanPoint, bool EndOfSection, bool Discontinuity, bool Terminal);

public class H264ReplayTests(H264RecordingSimFactory factory) : IClassFixture<H264RecordingSimFactory>
{
    private const int Fps = 10; // the default world's H.264 frame rate
    private const int ExtendedHeader = 12 + 16;

    [FfmpegFact]
    public async Task Replay_StartsWithAKeyFrameAndItsParameterSets_AtTheRequestedTime()
    {
        factory.Time.Advance(TimeSpan.FromSeconds(30));
        var from = factory.Start + TimeSpan.FromSeconds(5);
        int port = factory.Services.GetRequiredService<RtspServer>().Port;
        var (_, packets) = await ReplayClient.PlayAsync(port, "cam1", from, from + TimeSpan.FromSeconds(3));

        var frames = ReplayClient.Frames(packets);
        Assert.Equal(3 * Fps, frames.Count);
        Assert.Equal(from, frames[0].Time);
        Assert.True(frames[0].CleanPoint);
        Assert.True(frames[^1].Terminal);
        Assert.All(frames.Zip(frames.Skip(1)), pair => Assert.Equal((uint)(90000 / Fps), pair.Second.RtpTime - pair.First.RtpTime));

        // NAL types of the first frame: single NAL units carry theirs, FU-A (28) fragments in the FU header.
        var firstFrame = packets.TakeWhile(p => ReplayClient.RtpTime(p) == frames[0].RtpTime)
            .Select(p => (p[ExtendedHeader] & 0x1F) == 28 ? p[ExtendedHeader + 1] & 0x1F : p[ExtendedHeader] & 0x1F)
            .ToHashSet();
        Assert.Superset(new HashSet<int> { 7, 8, 5 }, firstFrame);
    }
}

