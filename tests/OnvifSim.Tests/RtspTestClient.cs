using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OnvifSim.Tests;

public sealed class RtspTestResponse(int status, List<(string Name, string Value)> headers, string body)
{
    public int Status => status;
    public string Body => body;
    public string? Header(string name) => headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public IEnumerable<string> Headers(string name) =>
        headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
    public string? SessionId => Header("Session")?.Split(';')[0];
}

// A deliberately small RTSP client for the tests: sends requests, answers Digest challenges and reads
// interleaved RTP from the same connection.
public sealed class RtspTestClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _buffer = new byte[256 * 1024];
    private readonly Queue<(int Channel, byte[] Data)> _interleaved = new();
    private int _start, _end, _cseq;
    private string? _digestNonce;

    private RtspTestClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseBasic { get; set; }

    public static async Task<RtspTestClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        return new RtspTestClient(tcp);
    }

    public static string Url(int port, string path) => $"rtsp://127.0.0.1:{port}/{path}";

    // Sends a request; on a 401 with credentials set, retries once with Digest (or Basic).
    public async Task<RtspTestResponse> RequestAsync(string method, string url, params (string Name, string Value)[] headers)
    {
        var response = await SendAsync(method, url, headers);
        if (response.Status != 401 || Username == null) return response;
        var digest = response.Headers("WWW-Authenticate").First(h => h.StartsWith("Digest "));
        _digestNonce = digest.Split("nonce=\"")[1].Split('"')[0];
        return await SendAsync(method, url, headers);
    }

    public async Task SendRawAsync(string text) => await _stream.WriteAsync(Encoding.ASCII.GetBytes(text));

    public async Task<RtspTestResponse> ReadResponseAsync()
    {
        while (true)
        {
            if (await PeekByteAsync() == '$')
            {
                _interleaved.Enqueue(await ReadInterleavedPacketAsync());
                continue;
            }
            var statusLine = await ReadLineAsync();
            var headers = new List<(string, string)>();
            for (var line = await ReadLineAsync(); line.Length > 0; line = await ReadLineAsync())
            {
                int colon = line.IndexOf(':');
                headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
            int length = int.Parse(headers.FirstOrDefault(h => h.Item1 == "Content-Length").Item2 ?? "0");
            var body = Encoding.UTF8.GetString(await ReadExactAsync(length));
            return new RtspTestResponse(int.Parse(statusLine.Split(' ')[1]), headers, body);
        }
    }

    // The RTP packets of the next complete frame on the given channel, starting at a frame boundary.
    // A JPEG frame starts at fragment offset 0 (RFC 2435); an H.264 stream starts at a frame boundary
    // anyway, so for it pass anyPacketStartsFrame.
    public async Task<List<byte[]>> ReadFrameAsync(int channel = 0, bool anyPacketStartsFrame = false)
    {
        var packets = new List<byte[]>();
        while (true)
        {
            var (packetChannel, data) = _interleaved.Count > 0 ? _interleaved.Dequeue() : await ReadInterleavedPacketAsync();
            if (packetChannel != channel) continue;
            bool startsFrame = anyPacketStartsFrame || (data[13] == 0 && data[14] == 0 && data[15] == 0);
            if (packets.Count == 0 && !startsFrame) continue;
            packets.Add(data);
            if ((data[1] & 0x80) != 0) return packets;
        }
    }

    // Every interleaved packet on the channel until the server closes the connection, as a replay ends.
    public async Task<List<byte[]>> ReadUntilClosedAsync(int channel = 0)
    {
        var packets = new List<byte[]>();
        while (true)
        {
            (int Channel, byte[] Data) packet;
            try
            {
                packet = _interleaved.Count > 0 ? _interleaved.Dequeue() : await ReadInterleavedPacketAsync();
            }
            catch (EndOfStreamException)
            {
                return packets;
            }
            if (packet.Channel == channel) packets.Add(packet.Data);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }

    private async Task<RtspTestResponse> SendAsync(string method, string url, (string Name, string Value)[] headers)
    {
        var request = new StringBuilder($"{method} {url} RTSP/1.0\r\nCSeq: {++_cseq}\r\n");
        foreach (var (name, value) in headers) request.Append($"{name}: {value}\r\n");
        if (Username != null && UseBasic)
            request.Append($"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"))}\r\n");
        else if (Username != null && _digestNonce != null)
            request.Append($"Authorization: {DigestHeader(method, url)}\r\n");
        request.Append("\r\n");
        await SendRawAsync(request.ToString());
        return await ReadResponseAsync();
    }

    private string DigestHeader(string method, string uri)
    {
        static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));
        string response = Md5($"{Md5($"{Username}:OnvifSim:{Password}")}:{_digestNonce}:{Md5($"{method}:{uri}")}");
        return $"Digest username=\"{Username}\", realm=\"OnvifSim\", nonce=\"{_digestNonce}\", uri=\"{uri}\", response=\"{response}\"";
    }

    private async Task<(int Channel, byte[] Data)> ReadInterleavedPacketAsync()
    {
        var framing = await ReadExactAsync(4);
        if (framing[0] != '$') throw new InvalidDataException("Expected an interleaved packet.");
        return (framing[1], await ReadExactAsync(framing[2] << 8 | framing[3]));
    }

    private async Task<string> ReadLineAsync()
    {
        var line = new StringBuilder();
        while (true)
        {
            byte b = (await ReadExactAsync(1))[0];
            if (b == '\n') return line.ToString().TrimEnd('\r');
            line.Append((char)b);
        }
    }

    private async Task<byte> PeekByteAsync()
    {
        await FillAsync(1);
        return _buffer[_start];
    }

    private async Task<byte[]> ReadExactAsync(int count)
    {
        await FillAsync(count);
        var result = _buffer.AsSpan(_start, count).ToArray();
        _start += count;
        return result;
    }

    private async Task FillAsync(int count)
    {
        if (_end - _start >= count) return;
        Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
        _end -= _start;
        _start = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_end < count)
        {
            int n = await _stream.ReadAsync(_buffer.AsMemory(_end), timeout.Token);
            if (n == 0) throw new EndOfStreamException("The server closed the connection.");
            _end += n;
        }
    }
}
