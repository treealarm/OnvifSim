using System.Text;

namespace OnvifSim.Streaming.Rtsp;

public sealed class RtspResponse(int status)
{
    public static readonly string ServerName = "OnvifSim/" + Onvif.CameraIdentity.FirmwareVersion;

    private readonly List<(string Name, string Value)> _headers = [];
    private string _body = "";

    public int Status => status;

    public RtspResponse With(string name, string value)
    {
        _headers.Add((name, value));
        return this;
    }

    public RtspResponse WithBody(string contentType, string body)
    {
        _body = body;
        return With("Content-Type", contentType);
    }

    public byte[] Serialize(string cseq)
    {
        var body = Encoding.UTF8.GetBytes(_body);
        var text = new StringBuilder()
            .Append($"RTSP/1.0 {status} {ReasonPhrase(status)}\r\n")
            .Append($"CSeq: {cseq}\r\n")
            .Append($"Date: {DateTime.UtcNow:r}\r\n")
            .Append($"Server: {ServerName}\r\n");
        foreach (var (name, value) in _headers) text.Append($"{name}: {value}\r\n");
        if (body.Length > 0) text.Append($"Content-Length: {body.Length}\r\n");
        text.Append("\r\n");
        return [.. Encoding.UTF8.GetBytes(text.ToString()), .. body];
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        451 => "Parameter Not Understood",
        454 => "Session Not Found",
        459 => "Aggregate Operation Not Allowed",
        461 => "Unsupported Transport",
        501 => "Not Implemented",
        551 => "Option not supported",
        _ => "Error",
    };
}
