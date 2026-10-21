using System.Xml;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;

namespace OnvifSim.Onvif.Services;

// A camera's own recording as Profile G presents it: one recording with one video track, filled by one
// recording job from the camera's video source, the way a camera with an SD card has it. Recordings and
// tracks can be neither created nor deleted (DynamicRecordings is false), as on most cameras. What the
// recording holds is RecordingLog's: from where the card has been recorded over to now, with a hole
// wherever the camera did not record.
public static class CameraRecording
{
    public const string RecordingToken = "Recording_1";
    public const string VideoTrackToken = "VideoTrack_1";
    public const string JobToken = "RecordingJob_1";
    public const string Content = "Recording";

    public static void CheckRecording(string? token)
    {
        if (token != RecordingToken) throw OnvifFault.NoRecording(token ?? "");
    }

    public static void CheckJob(string? token)
    {
        if (token != JobToken) throw OnvifFault.NoRecordingJob(token ?? "");
    }

    public static RecordingSourceInformation Source(OnvifRequestContext context) => new()
    {
        SourceId = CameraMedia.VideoSourceToken,
        Name = context.Camera.Name.Length > 0 ? context.Camera.Name : context.Camera.Id,
        Location = "OnvifSim",
        Description = "The camera's own video source",
        Address = context.XAddr("device_service"),
    };

    // The card's depth as an xs:duration, PT720H for 30 days.
    public static string MaximumRetentionTime(OnvifRequestContext context) =>
        XmlConvert.ToString(TimeSpan.FromHours(context.Camera.RecordingRetentionHours));

    public static RecordingConfiguration Configuration(OnvifRequestContext context) => new()
    {
        Source = Source(context),
        Content = Content,
        MaximumRetentionTime = MaximumRetentionTime(context),
    };

    // Earliest and latest are those of the footage still on the card; the track's DataFrom and DataTo
    // span it whole, holes included: Profile G has no way to say where they are, only replay finds them.
    public static RecordingInformation Information(OnvifRequestContext context)
    {
        var spans = context.Recordings.SpansOf(context.Camera);
        var now = context.Recordings.Now.UtcDateTime;
        var information = new RecordingInformation
        {
            RecordingToken = RecordingToken,
            Source = Source(context),
            Content = Content,
            RecordingStatus = context.Recordings.IsRecording(context.Camera.Id) ? RecordingStatus.Recording : RecordingStatus.Stopped,
            Track =
            [
                new TrackInformation
                {
                    TrackToken = VideoTrackToken,
                    TrackType = TrackType.Video,
                    Description = "Video",
                    DataFrom = spans.Count > 0 ? spans[0].From.UtcDateTime : now,
                    DataTo = spans.Count > 0 ? spans[^1].To.UtcDateTime : now,
                },
            ],
        };
        if (spans.Count > 0)
        {
            information.EarliestRecording = spans[0].From.UtcDateTime;
            information.EarliestRecordingSpecified = true;
            information.LatestRecording = spans[^1].To.UtcDateTime;
            information.LatestRecordingSpecified = true;
        }
        return information;
    }
}
