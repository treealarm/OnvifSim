namespace OnvifSim.Rtsp.Rtp;

// The packets of one frame in one buffer, each preceded by four spare bytes. Over UDP every packet is
// sent on its own; over RTSP-interleaved TCP the spare bytes take the '$', channel, length framing and
// the whole buffer goes out in a single write. The buffer is reused from frame to frame.
public sealed class RtpPacketBatch
{
    public const int FramingSize = 4;

    private byte[] _buffer = new byte[64 * 1024];
    private readonly List<(int Offset, int Length)> _packets = [];
    private int _used;

    public int Count => _packets.Count;

    // A packet without its framing bytes.
    public ReadOnlyMemory<byte> Packet(int index)
    {
        var (offset, length) = _packets[index];
        return _buffer.AsMemory(offset + FramingSize, length);
    }

    public void Clear()
    {
        _packets.Clear();
        _used = 0;
    }

    // Reserves room for a packet of the given size and returns it for the caller to fill.
    public Span<byte> Append(int length)
    {
        int needed = _used + FramingSize + length;
        if (needed > _buffer.Length) Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
        _packets.Add((_used, length));
        var packet = _buffer.AsSpan(_used + FramingSize, length);
        _used = needed;
        return packet;
    }

    // Writes the interleaved framing of every packet and returns the whole batch as one TCP payload.
    public ReadOnlyMemory<byte> FrameForInterleaved(byte rtpChannel)
    {
        foreach (var (offset, length) in _packets)
        {
            _buffer[offset] = (byte)'$';
            _buffer[offset + 1] = rtpChannel;
            _buffer[offset + 2] = (byte)(length >> 8);
            _buffer[offset + 3] = (byte)length;
        }
        return _buffer.AsMemory(0, _used);
    }
}
