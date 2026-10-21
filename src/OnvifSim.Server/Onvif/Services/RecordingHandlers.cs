using System.Xml;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Recording;
using static OnvifSim.Onvif.Services.CameraRecording;

namespace OnvifSim.Onvif.Services;

// Recording (ver10, Profile G): the camera's one recording and the job that fills it. Switching the job
// between Active and Idle starts and stops recording, which leaves a hole in what replay finds; the
// recording's MaximumRetentionTime is how far back the card reaches. Everything that would create or
// delete recordings, tracks or jobs is not supported, as DynamicRecordings says.
[OnvifService(typeof(RecordingPort), "recording_service", 21, 12)]
public sealed class RecordingHandlers(OnvifRequestContext context)
{
    private const string Active = "Active";
    private const string Idle = "Idle";

    // Capabilities6 is trc:Capabilities: svcutil numbers classes whose names other namespaces took.
    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities6
        {
            DynamicRecordings = false, DynamicRecordingsSpecified = true,
            DynamicTracks = false, DynamicTracksSpecified = true,
            Encoding = [context.HasH264 ? "H264" : "JPEG"],
            MaxRecordings = 1, MaxRecordingsSpecified = true,
            MaxRecordingJobs = 1, MaxRecordingJobsSpecified = true,
            Options = false, OptionsSpecified = true,
            MetadataRecording = false, MetadataRecordingSpecified = true,
        },
    };

    public GetRecordingsResponse GetRecordings(GetRecordingsRequest request) => new()
    {
        RecordingItem =
        [
            new GetRecordingsResponseItem
            {
                RecordingToken = RecordingToken,
                Configuration = Configuration(context),
                Tracks = new GetTracksResponseList
                {
                    Track =
                    [
                        new GetTracksResponseItem
                        {
                            TrackToken = VideoTrackToken,
                            Configuration = new TrackConfiguration { TrackType = TrackType.Video, Description = "Video" },
                        },
                    ],
                },
            },
        ],
    };

    public GetRecordingConfigurationResponse GetRecordingConfiguration(GetRecordingConfigurationRequest request)
    {
        CheckRecording(request.RecordingToken);
        return new GetRecordingConfigurationResponse { RecordingConfiguration = Configuration(context) };
    }

    // Only MaximumRetentionTime can be changed, in whole hours; the source and content are the camera's
    // and are left as they are, so a client writing back what it read still succeeds.
    public SetRecordingConfigurationResponse SetRecordingConfiguration(SetRecordingConfigurationRequest request)
    {
        CheckRecording(request.RecordingToken);
        if (request.RecordingConfiguration?.MaximumRetentionTime is { Length: > 0 } text)
        {
            TimeSpan retention;
            try
            {
                retention = XmlConvert.ToTimeSpan(text);
            }
            catch (FormatException)
            {
                throw OnvifFault.InvalidArgValue($"MaximumRetentionTime '{text}' is not a duration.");
            }
            int hours = (int)Math.Ceiling(retention.TotalHours);
            if (hours is < 1 or > 8760) throw OnvifFault.ConfigModify("MaximumRetentionTime must be between 1 hour and a year.");
            context.Store.Update(world => world.Cameras.Single(c => c.Id == context.Camera.Id).RecordingRetentionHours = hours);
        }
        return new SetRecordingConfigurationResponse();
    }

    public GetRecordingJobsResponse GetRecordingJobs(GetRecordingJobsRequest request) => new()
    {
        JobItem = [new GetRecordingJobsResponseItem { JobToken = JobToken, JobConfiguration = JobConfiguration() }],
    };

    public GetRecordingJobConfigurationResponse GetRecordingJobConfiguration(GetRecordingJobConfigurationRequest request)
    {
        CheckJob(request.JobToken);
        return new GetRecordingJobConfigurationResponse { JobConfiguration = JobConfiguration() };
    }

    public SetRecordingJobModeResponse SetRecordingJobMode(SetRecordingJobModeRequest request)
    {
        CheckJob(request.JobToken);
        bool recording = request.Mode switch
        {
            Active => true,
            Idle => false,
            _ => throw OnvifFault.BadMode(request.Mode ?? ""),
        };
        context.Recordings.SetRecording(context.Camera.Id, recording);
        return new SetRecordingJobModeResponse();
    }

    public GetRecordingJobStateResponse GetRecordingJobState(GetRecordingJobStateRequest request)
    {
        CheckJob(request.JobToken);
        return new GetRecordingJobStateResponse
        {
            State = new RecordingJobStateInformation { RecordingToken = RecordingToken, State = Mode() },
        };
    }

    private string Mode() => context.Recordings.IsRecording(context.Camera.Id) ? Active : Idle;

    // Records the camera's own video source, through the profile its recording is encoded like.
    private RecordingJobConfiguration JobConfiguration() => new()
    {
        RecordingToken = RecordingToken,
        Mode = Mode(),
        Priority = 1,
        Source =
        [
            new RecordingJobSource
            {
                SourceToken = new SourceReference
                {
                    Token = context.HasH264 ? CameraMedia.H264ProfileToken : CameraMedia.JpegProfileToken,
                    Type = "http://www.onvif.org/ver10/schema/Profile",
                },
                Tracks = [new RecordingJobTrack { SourceTag = "V", Destination = VideoTrackToken }],
            },
        ],
    };
}
