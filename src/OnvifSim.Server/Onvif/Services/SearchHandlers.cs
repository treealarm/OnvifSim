using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using OnvifSim.Onvif.Wsdl.Search;
using static OnvifSim.Onvif.Services.CameraRecording;

namespace OnvifSim.Onvif.Services;

// Search (ver10, Profile G): what the camera's recording holds, found the ONVIF way. FindRecordings
// starts a search and GetRecordingSearchResults returns all of it at once, already Completed: there is
// one recording, so there is nothing to wait for. Event searches find nothing, as the recording keeps
// no events; metadata and PTZ position searches are not supported (GetServiceCapabilities says so).
[OnvifService(typeof(SearchPort), "search_service", 21, 12)]
public sealed partial class SearchHandlers(OnvifRequestContext context)
{
    // Searches still open, by token, with the camera they belong to and when they lapse.
    private sealed record Search(string CameraId, RecordingInformation[] Recordings, long ExpiresAt);

    private static readonly ConcurrentDictionary<string, Search> Searches = new();
    private static readonly TimeSpan DefaultKeepAlive = TimeSpan.FromSeconds(60);

    // Capabilities7 is tse:Capabilities.
    public GetServiceCapabilitiesResponse GetServiceCapabilities(GetServiceCapabilitiesRequest request) => new()
    {
        Capabilities = new Capabilities7
        {
            MetadataSearch = false, MetadataSearchSpecified = true,
            GeneralStartEvents = false, GeneralStartEventsSpecified = true,
        },
    };

    public GetRecordingSummaryResponse GetRecordingSummary(GetRecordingSummaryRequest request)
    {
        var spans = context.Recordings.SpansOf(context.Camera);
        var now = context.Recordings.Now.UtcDateTime;
        return new GetRecordingSummaryResponse
        {
            Summary = new RecordingSummary
            {
                DataFrom = spans.Count > 0 ? spans[0].From.UtcDateTime : now,
                DataUntil = spans.Count > 0 ? spans[^1].To.UtcDateTime : now,
                NumberRecordings = 1,
            },
        };
    }

    public GetRecordingInformationResponse GetRecordingInformation(GetRecordingInformationRequest request)
    {
        CheckRecording(request.RecordingToken);
        return new GetRecordingInformationResponse { RecordingInformation = Information(context) };
    }

    public FindRecordingsResponse FindRecordings(FindRecordingsRequest request)
    {
        var information = Information(context);
        var scope = request.Scope;
        bool included = scope?.IncludedRecordings is not { Length: > 0 } tokens || tokens.Contains(RecordingToken);
        bool matches = included && Matches(scope?.RecordingInformationFilter, information);
        var found = matches && request.MaxMatches != 0 ? [information] : Array.Empty<RecordingInformation>();
        return new FindRecordingsResponse { SearchToken = Start(found, request.KeepAliveTime) };
    }

    public GetRecordingSearchResultsResponse GetRecordingSearchResults(GetRecordingSearchResultsRequest request)
    {
        var search = Find(request.SearchToken);
        var results = request.MaxResults > 0 ? search.Recordings.Take(request.MaxResults).ToArray() : search.Recordings;
        return new GetRecordingSearchResultsResponse
        {
            ResultList = new FindRecordingResultList { SearchState = SearchState.Completed, RecordingInformation = results },
        };
    }

    public FindEventsResponse FindEvents(FindEventsRequest request) =>
        new() { SearchToken = Start([], request.KeepAliveTime) };

    public GetEventSearchResultsResponse GetEventSearchResults(GetEventSearchResultsRequest request)
    {
        Find(request.SearchToken);
        return new GetEventSearchResultsResponse { ResultList = new FindEventResultList { SearchState = SearchState.Completed, Result = [] } };
    }

    public GetSearchStateResponse GetSearchState(GetSearchStateRequest request)
    {
        Find(request.SearchToken);
        return new GetSearchStateResponse { State = SearchState.Completed };
    }

    public EndSearchResponse EndSearch(EndSearchRequest request)
    {
        Find(request.SearchToken);
        Searches.TryRemove(request.SearchToken, out _);
        return new EndSearchResponse { Endpoint = context.Recordings.Now.UtcDateTime };
    }

    private string Start(RecordingInformation[] recordings, string? keepAlive)
    {
        long now = Environment.TickCount64;
        foreach (var lapsed in Searches.Where(s => s.Value.ExpiresAt <= now).Select(s => s.Key).ToList()) Searches.TryRemove(lapsed, out _);
        var lifetime = DefaultKeepAlive;
        if (keepAlive is { Length: > 0 })
        {
            try
            {
                lifetime = XmlConvert.ToTimeSpan(keepAlive);
            }
            catch (FormatException)
            {
                throw OnvifFault.InvalidArgValue($"KeepAliveTime '{keepAlive}' is not a duration.");
            }
        }
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        Searches[token] = new Search(context.Camera.Id, recordings, now + (long)lifetime.TotalMilliseconds);
        return token;
    }

    private Search Find(string? token)
    {
        if (token == null || !Searches.TryGetValue(token, out var search) || search.ExpiresAt <= Environment.TickCount64
            || !string.Equals(search.CameraId, context.Camera.Id, StringComparison.OrdinalIgnoreCase))
            throw OnvifFault.InvalidToken(token ?? "");
        return search;
    }

    // The filter is an XPath over the recording information. Only the comparisons ONVIF's own examples
    // and clients use are understood: the track type, and DataFrom / DataTo against a time, all joined
    // with "and". Anything else matches everything rather than nothing: a client asking more than this
    // understands still finds the recording and sees for itself what it holds.
    internal static bool Matches(string? filter, RecordingInformation recording)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var track = recording.Track.FirstOrDefault(t => t.TrackType == TrackType.Video);
        if (track == null) return false;
        foreach (Match comparison in Comparison().Matches(filter))
        {
            string field = comparison.Groups["field"].Value, op = comparison.Groups["op"].Value, value = comparison.Groups["value"].Value;
            if (field == "TrackType")
            {
                if (op == "=" && !string.Equals(value, track.TrackType.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
                continue;
            }
            if (!System.DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
                continue;
            var actual = field == "DataFrom" ? track.DataFrom : track.DataTo;
            bool holds = op switch
            {
                "<=" => actual <= time,
                "<" => actual < time,
                ">=" => actual >= time,
                ">" => actual > time,
                _ => actual == time,
            };
            if (!holds) return false;
        }
        return true;
    }

    [GeneratedRegex("""(?<field>TrackType|DataFrom|DataTo)\s*(?<op><=|>=|<|>|=)\s*["'](?<value>[^"']*)["']""")]
    private static partial Regex Comparison();
}
