# OnvifSim.Rtsp

The RTSP server of OnvifSim and the RTP payload formats it sends, with nothing of the simulator in
them. It lives in this solution for now and may become a package of its own once something besides
OnvifSim uses it.

- RTSP 1.0 (RFC 2326): OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER and
  SET_PARAMETER; sessions with several tracks; RTP over TCP (interleaved) and UDP (one shared server
  port pair); RTP-Info, RTCP sender reports, session timeouts.
- RTP payload formats: JPEG (RFC 2435), H.264 (RFC 6184, packetization-mode 1) and ONVIF metadata
  (`vnd.onvif.metadata`).
- Server only: no client, no RTSPS, no RECORD.

The application tells the server what it streams through `IRtspMediaSource`: the media at a URL path,
with its tracks, whether a request may have it, and the authentication challenges. Each track sends
itself through a `TrackPlayback`, which packetizes its frames into an `RtpSender`:

```csharp
sealed class Source : IRtspMediaSource
{
    public RtspMedia? Find(IReadOnlyList<string> path) => path is ["live"]
        ? new RtspMedia("Live", [new RtspTrack("track1", "video", JpegPacketizer.PayloadType, "JPEG", PlayAsync)])
        : null;

    public bool IsAuthorized(RtspRequest request, IReadOnlyList<string> path) => true;
    public IEnumerable<string> Challenges() => [];

    static async Task PlayAsync(RtpSender sender, CancellationToken cancellationToken)
    {
        var batch = new RtpPacketBatch();
        await foreach (var (jpeg, captured) in Frames(cancellationToken)) // the application's own frames
        {
            batch.Clear();
            JpegPacketizer.Packetize(sender.Stream, jpeg, captured, batch);
            await sender.SendAsync(batch, cancellationToken);
        }
    }
}

var server = new RtspServer(new RtspServerOptions { Port = 8554 }, new Source(), logger);
await server.StartAsync(cancellationToken); // or register it as a hosted service
```

In OnvifSim the source is `Streaming/CameraStreams.cs`; `tests/OnvifSim.Tests/RtspLibraryTests.cs`
runs the server with a source of its own.
