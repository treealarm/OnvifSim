using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace OnvifSim.Rtsp;

// A request as the server read it.
public sealed class RtspRequest
{
    private readonly Dictionary<string, string> _headers;

    internal RtspRequest(string method, string url, Dictionary<string, string> headers, byte[] body)
    {
        Method = method;
        Url = url;
        _headers = headers;
        Body = body;
    }

    public string Method { get; }
    public string Url { get; }
    public byte[] Body { get; }

    public string? Header(string name) => _headers.GetValueOrDefault(name);
    public string CSeq => Header("CSeq") ?? "0";
    // The session id without the ";timeout=" parameter some clients echo back.
    public string? SessionId => Header("Session")?.Split(';')[0].Trim();
}

// Reads requests from an RTSP control connection. While a session plays over TCP the client also sends
// interleaved binary packets ('$', channel, 16-bit length, data — RTCP receiver reports) on the same
// stream; they are skipped, since the connection itself already proves the client is alive.
internal sealed class RtspRequestReader(Stream stream)
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly byte[] EndOfHeaders = "\r\n\r\n"u8.ToArray();

    private readonly PipeReader _reader = PipeReader.Create(stream);

    // Null when the client closed the connection. Throws InvalidDataException on a malformed request.
    public async ValueTask<RtspRequest?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;
            var request = TryParse(ref buffer);
            if (request != null)
            {
                // More requests may already be buffered (pipelining): leave them unexamined.
                _reader.AdvanceTo(buffer.Start);
                return request;
            }
            // What is left is incomplete, so wait for more data before looking at it again.
            _reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted) return null;
        }
    }

    private static RtspRequest? TryParse(ref ReadOnlySequence<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            byte first = buffer.FirstSpan[0];
            if (first is (byte)'\r' or (byte)'\n')
            {
                buffer = buffer.Slice(1);
                continue;
            }
            if (first == (byte)'$')
            {
                if (!TrySkipInterleavedPacket(ref buffer)) return null;
                continue;
            }

            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> head, EndOfHeaders))
            {
                if (buffer.Length > MaxHeaderBytes) throw new InvalidDataException("RTSP request headers are too long.");
                return null;
            }
            if (head.Length > MaxHeaderBytes) throw new InvalidDataException("RTSP request headers are too long.");

            var (method, url, headers) = ParseHead(Encoding.UTF8.GetString(head));
            int bodyLength = 0;
            if (headers.TryGetValue("Content-Length", out var lengthText)
                && (!int.TryParse(lengthText, out bodyLength) || bodyLength < 0 || bodyLength > MaxBodyBytes))
                throw new InvalidDataException($"Bad Content-Length '{lengthText}'.");
            if (reader.Remaining < bodyLength) return null;

            var body = reader.UnreadSequence.Slice(0, bodyLength).ToArray();
            buffer = buffer.Slice(reader.Position).Slice(bodyLength);
            return new RtspRequest(method, url, headers, body);
        }
        return null;
    }

    private static bool TrySkipInterleavedPacket(ref ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length < 4) return false;
        Span<byte> framing = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(framing);
        int length = framing[2] << 8 | framing[3];
        if (buffer.Length < 4 + length) return false;
        buffer = buffer.Slice(4 + length);
        return true;
    }

    private static (string Method, string Url, Dictionary<string, string> Headers) ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("RTSP/1.", StringComparison.Ordinal))
            throw new InvalidDataException($"Bad RTSP request line '{lines[0]}'.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? lastName = null;
        foreach (var line in lines.Skip(1))
        {
            // Obsolete line folding: a continuation line starts with whitespace.
            if (line.Length > 0 && char.IsWhiteSpace(line[0]) && lastName != null)
            {
                headers[lastName] += " " + line.Trim();
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon <= 0) throw new InvalidDataException($"Bad RTSP header line '{line}'.");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var previous) ? previous + ", " + value : value;
            lastName = name;
        }
        return (requestLine[0], requestLine[1], headers);
    }
}
